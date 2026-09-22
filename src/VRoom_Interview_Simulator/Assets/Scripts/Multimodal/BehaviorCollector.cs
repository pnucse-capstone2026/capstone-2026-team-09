using System.Collections;
using UnityEngine;
using VerbalProcess;
using VRoom.Backend;

namespace VRoom.Multimodal
{
    /// <summary>
    /// 원격 Vision 워커(노트북)에 '턴 경계'만 알려주는 오케스트레이터.
    /// 웹캠 캡처와 분석은 워커가 로컬에서 직접 수행하므로 Unity 는 프레임을 다루지 않는다.
    ///
    /// [핵심 원칙] VAD 나 Speaker 를 직접 구독하지 않는다.
    ///   개입이 발생하면 VAD 는 정상 종료 경로를 타지 않고, Speaker 의 재생 완료도
    ///   두 발화가 합쳐져 한 번만 발생한다. 직접 구독하면 턴이 열린 채 남거나
    ///   엉뚱한 시점에 닫힌다. 그래서 PipelineController 의 상태 전이만 신뢰한다.
    ///
    /// [위상(phase)]
    ///   NORMAL     개입 없는 일반 답변 구간          -> 채점 대상
    ///   TRUNCATED  개입으로 잘린 답변 구간            -> 로그 전용
    ///   REACTION   개입 직후 반응 구간                -> 로그 전용, 논문 핵심 종속변인
    ///   REANSWER   Type A 재답변 구간                 -> 채점 대상
    ///
    ///   turn_start 시점에는 개입이 일어날지 알 수 없으므로 일단 NORMAL 로 열고,
    ///   개입이 확정되면 닫을 때 TRUNCATED 로 정정한다.
    /// </summary>
    public class BehaviorCollector : MonoBehaviour
    {
        // ===================================================================
        // 1. 참조 / 설정 / 상태
        // ===================================================================
        [Header("참조")]
        public VisionStreamClient client;      // Vision 워커 웹소켓
        public PipelineController pipeline;    // 턴 상태 전이의 유일한 출처
        public BackendControlClient backend;   // 세션 ID 폴백용

        [Header("캘리브레이션")]
        [Tooltip("면접 시작 전 '정면 응시' 기준을 잡는 시간(초)")]
        public float calibrationSeconds = 3f;
        public bool calibrateOnStart = true;

        [Header("상태(읽기 전용)")]
        [SerializeField] private bool _turnOpen;                        // 턴이 열려 있는가
        [SerializeField] private string _openStage = "";                // 열린 턴의 면접 단계
        [SerializeField] private string _openPhase = TurnPhase.Normal;  // 열린 턴의 위상
        [SerializeField] private bool _calibrated;                      // 기준 자세 확보 성공 여부

        private bool _ready;             // 워커 연결 성공 여부. false 면 아무것도 보내지 않는다
        private bool _calibrating;       // 캘리브레이션 진행 중
        private bool _turnStartPending;  // 캘리브레이션 중 도착한 턴 시작 요청을 보류

        // ===================================================================
        // 2. 수명
        // ===================================================================
        private IEnumerator Start()
        {
            // 프리웜 세션 ID 를 우선 사용하고, 없으면 백엔드 클라이언트 값을 따른다.
            if (!string.IsNullOrEmpty(InterviewConfig.SessionId))
                client.SessionId = InterviewConfig.SessionId;
            else if (backend != null && !string.IsNullOrEmpty(backend.sessionId))
                client.SessionId = backend.sessionId;

            var connect = client.ConnectAsync();
            while (!connect.IsCompleted) yield return null;

            // 워커가 없어도 면접은 정상 진행된다. 시각 4항목만 채점에서 빠진다.
            if (!connect.Result)
            {
                Debug.LogWarning("[Behavior] Vision 워커 연결 실패. 시각 4항목은 채점에서 제외됩니다.");
                yield break;
            }

            client.OnCalibrated += (ok, n) =>
            {
                _calibrated = ok;
                Debug.Log($"[Behavior] 캘리브레이션 {(ok ? "성공" : "실패")} (샘플 {n})");
            };
            _ready = true;

            Subscribe();

            if (calibrateOnStart) yield return Calibrate();

            // 캘리브레이션 중에 첫 턴이 시작됐다면 지금 연다.
            if (_turnStartPending)
            {
                _turnStartPending = false;
                OpenTurn(pipeline.CurrentStage, TurnPhase.Normal);
            }
        }

        private void OnDestroy() => Unsubscribe();

        private void Subscribe()
        {
            if (pipeline == null) return;
            pipeline.OnTurnStateChanged += HandleTurnStateChanged;
            pipeline.OnUtteranceEndedForVision += HandleUtteranceEnded;
            pipeline.OnReactionWindowElapsed += HandleReactionWindowElapsed;
        }

        private void Unsubscribe()
        {
            if (pipeline == null) return;
            pipeline.OnTurnStateChanged -= HandleTurnStateChanged;
            pipeline.OnUtteranceEndedForVision -= HandleUtteranceEnded;
            pipeline.OnReactionWindowElapsed -= HandleReactionWindowElapsed;
        }

        /// <summary>정면 응시 기준 자세를 수집한다. 실패해도 면접은 진행된다.</summary>
        public IEnumerator Calibrate()
        {
            Debug.Log($"[Behavior] 캘리브레이션 {calibrationSeconds}초 — 정면을 응시하세요.");
            _calibrating = true;
            yield return client.SendCalibrateStart();
            yield return new WaitForSeconds(calibrationSeconds);
            yield return client.SendCalibrateEnd();
            _calibrating = false;
        }

        // ===================================================================
        // 3. 턴 경계 판정 (PipelineController 상태 전이에 종속)
        // ===================================================================
        private void HandleTurnStateChanged(TurnState prev, TurnState next)
        {
            if (!_ready) return;

            switch (next)
            {
                case TurnState.UserAnswering:
                    OnEnterUserAnswering(prev);
                    break;

                case TurnState.BargeInPending:
                    // 개입 발동: 지금까지의 구간을 TRUNCATED 로 정정해 닫고 REACTION 을 연다.
                    CloseTurn(TurnPhase.Truncated);
                    OpenTurn(pipeline.CurrentStage, TurnPhase.Reaction);
                    break;

                case TurnState.InterviewerSpeaking:
                    // Type B: 개입 대사(발화 1) 종료 -> REACTION 을 닫는다.
                    // 후속 질문 재생 구간은 어느 턴에도 속하지 않는다(일반 질문 재생과 동일).
                    if (prev == TurnState.Interrupting) CloseTurn();
                    break;

                case TurnState.Finished:
                    CloseTurn();   // 열린 턴 강제 닫기 (DONE 턴 누수 방지)
                    break;
            }
        }

        private void OnEnterUserAnswering(TurnState prev)
        {
            // 캘리브레이션 중이면 끝난 뒤에 연다.
            if (_calibrating)
            {
                _turnStartPending = true;
                return;
            }

            if (prev == TurnState.Interrupting)
            {
                // Type A: 개입 대사 재생 완료 -> 같은 단계의 재답변을 받는다.
                // (Type B 는 Interrupting -> InterviewerSpeaking 을 거치므로 여기 오지 않는다)
                CloseTurn();
                OpenTurn(pipeline.CurrentStage,
                         pipeline.LastBargeInType == "REDIRECT"
                             ? TurnPhase.Reanswer     // 같은 단계
                             : TurnPhase.Normal);     // 다음 단계
            }
            else if (prev == TurnState.InterviewerSpeaking || prev == TurnState.Idle)
            {
                CloseTurn();                                    // 누수 방어 (이미 닫혔으면 무시된다)
                OpenTurn(pipeline.CurrentStage, TurnPhase.Normal);
            }
            // Correcting -> UserAnswering 은 턴을 새로 열지 않는다(같은 답변의 연속).
        }

        /// <summary>VAD 정상 발화 종료. PipelineController 가 중계한다(개입 중에는 오지 않는다).</summary>
        private void HandleUtteranceEnded() => CloseTurn();

        /// <summary>
        /// 개입 확정 후 고정 시간이 지나면 REACTION 턴을 닫는다.
        ///
        /// 재생 완료를 기준으로 닫으면 개입 대사와 후속 질문이 하나의 오디오 스트림으로
        /// 합쳐져 REACTION 이 20초까지 늘어난다. 조건 간 비교가 불가능해지므로
        /// 고정 시간 창을 쓴다.
        /// </summary>
        private void HandleReactionWindowElapsed() => CloseTurn();

        // ===================================================================
        // 4. 워커 통신
        // ===================================================================
        private void OpenTurn(string stage, string phase)
        {
            _turnOpen = true;
            _openStage = stage ?? "";
            _openPhase = phase;
            _ = client.SendTurnStart(_openStage, phase);
            Debug.Log($"[Behavior] 턴 시작 {_openStage}/{phase}");
        }

        /// <param name="phaseOverride">
        /// 지정하면 워커가 turn_start 때의 위상을 이 값으로 정정한다.
        /// null 이면 열 때의 위상을 그대로 쓴다.
        /// </param>
        private void CloseTurn(string phaseOverride = null)
        {
            if (!_turnOpen) return;   // 이미 닫혔으면 무시 (중복 호출이 잦은 구조다)

            _turnOpen = false;
            _ = client.SendTurnEnd(phaseOverride ?? "");
            Debug.Log($"[Behavior] 턴 종료 {_openStage}/{phaseOverride ?? _openPhase}" +
                      (phaseOverride != null ? " (위상 정정)" : ""));
        }
    }
}