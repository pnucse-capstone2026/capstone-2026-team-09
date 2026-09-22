using System;
using UnityEngine;
using VRoom.Backend;

namespace VerbalProcess
{
    /// <summary>
    /// 오디오 파이프라인 전체를 조율하는 컨트롤러이자 **턴 상태 머신의 유일한 소유자**.
    ///
    /// [핵심 설계 — 상태 머신 단일 진실 원천]
    ///   마이크 모드 / Speaker 제어 / Vision 턴 경계가 전부 TurnState 하나에서 파생된다.
    ///   개입 기능이 들어오면서 상태 조합이 폭발했기 때문에, 각 컴포넌트가 자기 상태를
    ///   따로 들고 있으면 어긋난다. 그래서 VAD 는 스스로 켜고 끄지 않고,
    ///   BehaviorCollector 는 VAD/Speaker 를 직접 구독하지 않는다.
    ///   **모든 상태 변경은 SetTurnState() 만 통과한다.**
    ///
    /// [Speaker 로 오디오가 들어오는 경로]
    ///   정상 구성:  백엔드 -> STT 워커 -> STTManager -> Speaker   (에코 제거 때문)
    ///   폴백 구성:  백엔드 -> BackendControlClient -> Speaker
    ///   두 경로의 도착 시점이 다르므로, '재생 완료' 판정은 오디오와 같은 채널로 오는
    ///   tts_end 로만 한다. 제어 채널의 audio_end 를 쓰면 뒷부분이 잘린다.
    ///
    /// [담당 영역]
    ///   VAD 이벤트 중계 / STT 송수신 / 개입 트리거·수신 / 저신뢰 교정 흐름 / Vision 턴 통보
    /// </summary>
    public class PipelineController : MonoBehaviour
    {
        // ===================================================================
        // 1. 참조
        // ===================================================================
        [Header("오디오 파이프라인")]
        [SerializeField] private VoiceActivityDetector vad;
        [SerializeField] private STTManager sttManager;
        [SerializeField] private Speaker speaker;

        [Header("UI")]
        [SerializeField] private TMPro.TMP_Text subtitleText;              // 자막 표시 (선택)
        [SerializeField] private SubtitleCorrectionPanel correctionPanel;  // 저신뢰 교정 패널

        [Header("개입 참조")]
        [SerializeField] private BackendControlClient backend;
        [SerializeField] private InterviewerDriver interviewerDriver;
        [SerializeField] private InterviewerExpression expression;

        // ===================================================================
        // 2. 설정값
        // ===================================================================
        [Header("BargeIn 임계값")]
        [Tooltip("질문 재생 종료 후 이 시간까지 발화가 없으면 LONG_SILENCE 트리거")]
        [SerializeField] private float longSilenceThreshold = 20f;

        [Tooltip("개입 대사가 끝내 도착하지 않을 때 마이크를 되돌리기까지의 상한(초)")]
        [SerializeField] private float bargeInFallbackTimeout = 10f;

        [Tooltip("개입 대사 후 후속 질문이 도착하기까지 기다리는 상한(초)")]
        [SerializeField] private float postBargeInTimeout = 12f;

        [Tooltip("개입 확정 후 이 시간까지를 REACTION 구간으로 측정한다.\n"
               + "재생 완료를 기준으로 하면 개입 대사와 후속 질문이 하나의 오디오 스트림으로\n"
               + "합쳐져 구간이 20초까지 늘어난다. 조건 간 비교가 불가능해지므로 고정 창을 쓴다.")]
        [SerializeField] private float reactionWindowSec = 5f;

        [Tooltip("개입 후 이 시간 이상 무음이 이어지면 '양보했다'로 판정한다.\n"
               + "짧게 잡으면 숨 고르기를 양보로 오판한다.")]
        [SerializeField] private float yieldSilenceSec = 1.5f;

        [Header("STT 교정")]
        [Tooltip("체크하면 저신뢰 전사를 사용자에게 묻지 않고 그대로 채점에 넘긴다. 실험 세션에서는 반드시 체크.")]
        [SerializeField] private bool autoAcceptLowConfidence = true;

        // ===================================================================
        // 3. 상태
        // ===================================================================
        // ── 턴 상태 머신 ──────────────────────────────────────────
        private TurnState _state = TurnState.Idle;
        public TurnState State => _state;

        /// <summary>턴 경계 통보. BehaviorCollector 가 구독한다(VAD/Speaker 직접 구독 금지).</summary>
        public Action<TurnState, TurnState> OnTurnStateChanged;

        /// <summary>VAD 정상 발화 종료를 Vision 쪽에 중계. 개입 중에는 발생하지 않는다.</summary>
        public Action OnUtteranceEndedForVision;

        /// <summary>REACTION 측정 창 종료 통보. BehaviorCollector 가 턴을 닫는다.</summary>
        public Action OnReactionWindowElapsed;

        /// <summary>최근 개입 유형. "REDIRECT" | "CUTOFF" | ""</summary>
        public string LastBargeInType { get; private set; } = "";

        /// <summary>백엔드 패킷의 stage. BehaviorCollector 가 턴을 열 때 참조한다.</summary>
        public string CurrentStage { get; private set; } = "";

        // ── 반응 시간 측정 ────────────────────────────────────────
        private float _interviewerFinishedTime = -1f;   // 면접관 발화가 끝난 시각
        private float _currentResponseTime = 0f;        // 질문 종료 -> 답변 시작까지 걸린 시간

        // ── 개입 진행 상태 ────────────────────────────────────────
        private float _silenceTimerStart = -1f;             // LONG_SILENCE 계측 시작 시각
        private float _bargeInAt = -1f;                     // 개입이 확정된 시각
        private float _bargeInDeadline = float.MaxValue;    // 개입 대사 미도착 방어
        private float _postBargeInDeadline = float.MaxValue;// 후속 질문 미도착 방어
        private bool _awaitingCutoffQuestion = false;       // Type B 후속 질문을 기다리는 중인가
        private bool _reactionClosed = false;               // REACTION 창을 이미 닫았는가

        // 개입 순간 사용자가 실제로 발화 중이었는가.
        // LONG_SILENCE 개입은 애초에 발화가 없어 '양보 시간'이 정의되지 않는다.
        // 이 플래그가 없으면 무응답 개입에서 0초가 기록되어 '즉시 멈췄다'로 오독된다.
        private bool _yieldMeasurable = false;
        private bool _yieldReported = false;

        // 백엔드 컷인 이후 현재 발화를 utterance_abort 로 닫을지 여부
        private bool _abortRequested = false;
        private string _abortReason = "";

        // ── 저신뢰 교정 상태 ──────────────────────────────────────
        private bool _isCorrectionMode = false;        // 재발화 수신을 기다리는 중인가
        private bool _isCorrectionPanelOpen = false;   // 패널이 화면에 떠 있는가
        private int _correctionStartIdx = -1;          // 교정 대상 단어 범위 시작
        private int _correctionEndIdx = -1;            // 교정 대상 단어 범위 끝
        private string[] _originalWords;               // 원본 단어 목록
        private FeatureData _originalFeatures;         // 원본 발화의 음성 피쳐
        private string _originalTextForCorrection = "";// 원본 전사 텍스트

        // ===================================================================
        // 4. 수명 / 구독
        // ===================================================================
        private void OnEnable()
        {
            if (vad != null)
            {
                vad.OnUtteranceEnded += HandleUtteranceEnded;
                vad.OnAudioChunkCaptured += HandleOnAudioChunkCaptured;
                vad.OnSpeakingStarted += HandleSpeakingStarted;
                vad.OnBargeInTrigger += HandleBargeInTrigger;
                vad.OnUtteranceAborted += HandleUtteranceAborted;
            }
            else
            {
                Debug.LogWarning("PipelineController: VoiceActivityDetector is not assigned!");
            }

            if (sttManager != null)
            {
                sttManager.OnTranscriptionReceived += HandleTranscriptionReceived;
                sttManager.OnAudioStreamEnded += HandleAudioStreamEnded;
                sttManager.OnCorrectionRequested += HandleOnCorrectionRequested;
                sttManager.OnSentenceCompletedFlag += HandleSentenceCompletedFlag;
                sttManager.OnSttSkipped += HandleSttSkipped;

                // 오디오/자막의 정상 경로: STT 워커 -> Speaker
                if (speaker != null)
                {
                    sttManager.OnAudioChunkReceived += speaker.HandleAudioChunkReceived;
                    sttManager.OnSubtitleReceived += speaker.HandleSubtitleReceived;
                    speaker.OnPlaybackFinished += HandlePlaybackFinished;
                    speaker.OnSubtitleTextChanged += HandleSubtitleTextChanged;
                }
            }

            if (backend != null)
            {
                backend.OnBehaviorPacket += HandleBackendPacket;
                backend.OnBargeInCutin += OnBargeInCutin;
            }
        }

        private void OnDisable()
        {
            if (vad != null)
            {
                vad.OnUtteranceEnded -= HandleUtteranceEnded;
                vad.OnAudioChunkCaptured -= HandleOnAudioChunkCaptured;
                vad.OnSpeakingStarted -= HandleSpeakingStarted;
                vad.OnBargeInTrigger -= HandleBargeInTrigger;
                vad.OnUtteranceAborted -= HandleUtteranceAborted;
            }

            if (sttManager != null)
            {
                sttManager.OnTranscriptionReceived -= HandleTranscriptionReceived;
                sttManager.OnAudioStreamEnded -= HandleAudioStreamEnded;
                sttManager.OnCorrectionRequested -= HandleOnCorrectionRequested;
                sttManager.OnSentenceCompletedFlag -= HandleSentenceCompletedFlag;
                sttManager.OnSttSkipped -= HandleSttSkipped;

                if (speaker != null)
                {
                    sttManager.OnAudioChunkReceived -= speaker.HandleAudioChunkReceived;
                    sttManager.OnSubtitleReceived -= speaker.HandleSubtitleReceived;
                    speaker.OnPlaybackFinished -= HandlePlaybackFinished;
                    speaker.OnSubtitleTextChanged -= HandleSubtitleTextChanged;
                }
            }

            if (backend != null)
            {
                backend.OnBehaviorPacket -= HandleBackendPacket;
                backend.OnBargeInCutin -= OnBargeInCutin;
            }
        }

        // ===================================================================
        // 5. 턴 상태 머신
        // ===================================================================
        /// <summary>
        /// 턴 상태를 바꾸고 그에 딸린 부수 효과를 일괄 적용한다.
        /// **마이크 모드를 여기 밖에서 바꾸면 안 된다.** 어긋나는 순간 개입이 깨진다.
        /// </summary>
        public void SetTurnState(TurnState next)
        {
            if (_state == next) return;
            var prev = _state;
            _state = next;

            switch (next)
            {
                case TurnState.Idle:
                case TurnState.Correcting:
                    vad?.SetMicMode(MicMode.Off);
                    vad?.ExitEchoGuard();
                    break;

                case TurnState.Finished:
                    vad?.SetMicMode(MicMode.Off);
                    vad?.ExitEchoGuard();
                    // CloseAsync -> CTS cancel 순서를 보장해 씬 전환 시 abort 에러를 막는다.
                    _ = sttManager?.CloseGracefullyAsync();
                    break;

                case TurnState.InterviewerSpeaking:
                    vad?.SetMicMode(MicMode.Monitoring);
                    vad?.ExitEchoGuard();
                    break;

                case TurnState.UserAnswering:
                    vad?.SetMicMode(MicMode.Transmitting);
                    vad?.ExitEchoGuard();
                    // 교정 패널에서 복귀한 경우는 '질문 직후'가 아니므로
                    // LONG_SILENCE 타이머를 다시 돌리지 않는다.
                    // (돌리면 교정 취소 후 10초 만에 유령 개입이 터진다)
                    _silenceTimerStart = (prev == TurnState.Correcting) ? -1f : Time.time;
                    _bargeInDeadline = float.MaxValue;
                    break;

                case TurnState.BargeInPending:
                    vad?.SetMicMode(MicMode.Monitoring);
                    vad?.EnterEchoGuard();
                    _silenceTimerStart = -1f;
                    _bargeInDeadline = Time.time + bargeInFallbackTimeout;
                    break;

                case TurnState.Interrupting:
                    vad?.SetMicMode(MicMode.Monitoring);
                    vad?.EnterEchoGuard();   // 스피커 소리를 사용자 발화로 오인하지 않게
                    _bargeInDeadline = float.MaxValue;
                    break;
            }

            Debug.Log($"[Turn] {prev} -> {next}");
            OnTurnStateChanged?.Invoke(prev, next);
        }

        // ===================================================================
        // 6. 매 프레임 계측 / 안전장치
        // ===================================================================
        private void Update()
        {
            TickLongSilence();
            TickYieldTime();
            TickReactionWindow();
            TickSafetyTimeouts();
        }

        /// <summary>질문 재생이 끝난 뒤 무응답이 길어지면 LONG_SILENCE 개입을 요청한다.</summary>
        private void TickLongSilence()
        {
            if (_state != TurnState.UserAnswering || vad == null) return;

            if (vad.IsSpeaking)
            {
                _silenceTimerStart = -1f;   // 말을 시작했으면 계측 중단
                return;
            }

            if (_silenceTimerStart <= 0f) return;

            float idle = Time.time - _silenceTimerStart;
            if (idle > longSilenceThreshold)
            {
                _silenceTimerStart = -1f;
                HandleBargeInTrigger("LONG_SILENCE", idle);
            }
        }

        /// <summary>
        /// 개입 후 사용자가 입을 다물기까지의 시간을 잰다. 논문 종속변인이다.
        ///
        /// 측정 창을 상태가 아니라 시간으로 잡는 이유: 사용자가 개입 대사 내내 계속
        /// 말하면 무음이 오기 전에 상태가 UserAnswering 으로 넘어가 측정이 통째로 누락된다.
        /// 창 안에 안 멈추면 미측정으로 남는데, 그것도 '5초 내 양보하지 않음'이라는 데이터다.
        /// </summary>
        private void TickYieldTime()
        {
            if (!_yieldMeasurable || _yieldReported || _bargeInAt <= 0f || vad == null) return;
            if (Time.time - _bargeInAt > reactionWindowSec) return;

            if (vad.LastVoicedTime > 0f && Time.time - vad.LastVoicedTime > yieldSilenceSec)
            {
                _yieldReported = true;
                _yieldMeasurable = false;

                float yieldTime = Mathf.Max(0f, vad.LastVoicedTime - _bargeInAt);
                _ = backend.SendBargeInYield(yieldTime);
                Debug.Log($"[개입] 양보 시간 {yieldTime:F2}s");
            }
        }

        /// <summary>개입 확정 기준 고정 시간이 지나면 REACTION 턴을 닫도록 통보한다.</summary>
        private void TickReactionWindow()
        {
            if (_reactionClosed || _bargeInAt <= 0f) return;
            if (_state != TurnState.BargeInPending && _state != TurnState.Interrupting) return;
            if (Time.time - _bargeInAt < reactionWindowSec) return;

            _reactionClosed = true;
            Debug.Log($"[개입] REACTION 측정 창 종료 ({reactionWindowSec}s)");
            OnReactionWindowElapsed?.Invoke();
        }

        /// <summary>
        /// 백엔드 응답이 유실됐을 때 세션이 멈추지 않게 하는 두 개의 안전장치.
        /// 둘 다 마이크를 되돌려 사용자가 계속 말할 수 있게 만든다.
        /// </summary>
        private void TickSafetyTimeouts()
        {
            // 개입 대사(발화 1)가 끝내 오지 않은 경우
            if (_state == TurnState.BargeInPending && Time.time >= _bargeInDeadline)
            {
                Debug.LogWarning("[개입] 대사 미도착 - 타임아웃으로 마이크 복귀");
                if (speaker != null) speaker.SetEndOfStream();
                SetTurnState(TurnState.UserAnswering);
                return;
            }

            // 후속 질문(발화 2)이 끝내 오지 않은 경우 (백엔드 LLM/TTS 실패, stt_skip 등)
            if (_state == TurnState.InterviewerSpeaking && Time.time >= _postBargeInDeadline)
            {
                Debug.LogWarning("[개입] 후속 질문 미도착 - 타임아웃으로 마이크 복귀");
                _awaitingCutoffQuestion = false;
                _postBargeInDeadline = float.MaxValue;
                SetTurnState(TurnState.UserAnswering);
            }
        }

        // ===================================================================
        // 7. VAD 이벤트
        // ===================================================================
        private void HandleSpeakingStarted()
        {
            // ★ 개입 중에는 면접관 오디오/자막을 절대 지우지 않는다.
            //   개입은 정의상 '사용자가 말하는 중'에 일어나므로 이 가드가 없으면
            //   StopAndClear() 가 개입 대사를 재생 도중 삭제하고,
            //   _playbackFinishedEventFired = true 로 만들어 OnPlaybackFinished 도
            //   오지 않아 마이크 복귀까지 막힌다.
            if (_state == TurnState.BargeInPending || _state == TurnState.Interrupting)
            {
                // 다시 말하기 시작했다 = 아직 양보하지 않았다. 측정을 재무장한다.
                _yieldReported = false;
                Debug.Log("[Pipeline] 개입 중 사용자 발화 감지 - Speaker 유지 (양보 대기)");
                return;
            }

            // InterviewerSpeaking/Monitoring 중의 마이크 감지는 에코 또는
            // 관찰 대상 입력이다. 실제 사용자 답변이 시작된 것이 아니므로
            // STT와 Speaker 상태를 건드리지 않는다.
            if (_state != TurnState.UserAnswering)
            {
                Debug.Log($"[Pipeline] 사용자 발화 시작 무시 (state={_state})");
                return;
            }

            if (_interviewerFinishedTime > 0)
            {
                _currentResponseTime = Time.time - _interviewerFinishedTime;
                _interviewerFinishedTime = -1f;
            }

            if (sttManager != null) sttManager.ResetUtteranceState();
            if (speaker != null) speaker.StopAndClear();
            if (backend != null) backend.SendUtteranceStarted();
        }

        private async void HandleOnAudioChunkCaptured(AudioClip clip)
        {
            if (sttManager == null) return;
            try
            {
                await sttManager.SendAudioChunkAsync(clip);
            }
            catch (Exception e)
            {
                Debug.LogError($"Pipeline Error (Chunk): {e.Message}");
            }
        }

        private async void HandleUtteranceEnded(VoiceActivityDetector.VoiceFeatures features)
        {
            if (sttManager == null)
            {
                ClearAbortRequest();
                return;
            }

            features.responseTime = _currentResponseTime;

            try
            {
                if (_abortRequested)
                {
                    Debug.Log($"[Pipeline] Barge-in utterance aborted (reason={_abortReason}).");
                    await sttManager.SendUtteranceAbortAsync(
                        new FeatureData(features), _abortReason);
                    ClearAbortRequest();
                }
                else if (_isCorrectionMode)
                {
                    Debug.Log("[Pipeline] Re-speak completed. Sending Correction Feature via WebSocket...");
                    await sttManager.SendCorrectionEndUtteranceAsync(
                        new FeatureData(features), _correctionStartIdx, _correctionEndIdx, _originalWords);

                    if (correctionPanel != null) correctionPanel.Close();
                    ResetCorrectionState();
                }
                else
                {
                    Debug.Log("Pipeline: Utterance ended. Sending Feature via WebSocket...");
                    await sttManager.SendEndUtteranceAsync(new FeatureData(features));

                    // 개입으로 강제 확정된 발화는 Vision 턴을 닫지 않는다.
                    // 이 메서드는 async 라서 await 뒤 몇 프레임 지연 실행되는데,
                    // 그 사이 OnBargeInCutin 이 이미 TRUNCATED 로 닫고 REACTION 을 열어둔다.
                    // 여기서 또 닫으면 REACTION 이 0프레임으로 소멸해
                    // '개입 직후 반응'이라는 핵심 종속변인이 아예 수집되지 않는다.
                    if (_state != TurnState.BargeInPending && _state != TurnState.Interrupting)
                        OnUtteranceEndedForVision?.Invoke();
                    else
                        Debug.Log("[Pipeline] 개입 중 발화 종료 - Vision REACTION 턴 유지");
                }
            }
            catch (Exception e)
            {
                ClearAbortRequest();
                Debug.LogError($"Pipeline Error (End): {e.Message}");
            }
        }

        /// <summary>
        /// VAD가 강제 종료를 알린다.
        /// 실제 음성이 있었으면 HandleUtteranceEnded가 feature와 함께 abort를
        /// 전송하므로 여기서는 중복 전송하지 않는다. 음성이 없었던 경우에는
        /// STT에 보낼 발화가 없으므로 대기 상태만 정리한다.
        /// </summary>
        private void HandleUtteranceAborted(bool hasAudio)
        {
            if (!hasAudio && _abortRequested)
            {
                Debug.Log("[Pipeline] Barge-in with no active audio; no STT abort payload needed.");
                ClearAbortRequest();
            }
        }

        private void ClearAbortRequest()
        {
            _abortRequested = false;
            _abortReason = "";
        }

        /// <summary>
        /// Unity 로컬 타이머가 잡은 개입 트리거를 백엔드로 보낸다.
        /// 허가 여부는 백엔드가 게이팅으로 정하며, 거부는 통보되지 않는다.
        /// </summary>
        private void HandleBargeInTrigger(string reason, float elapsed)
        {
            if (_state != TurnState.UserAnswering) return;   // 이중 방어

            Debug.Log($"[개입] 신호 전송 reason={reason} elapsed={elapsed:F1}s");
            _ = backend.SendBargeInSignal(reason, elapsed);
        }

        // ===================================================================
        // 8. STT 이벤트
        // ===================================================================
        private void HandleTranscriptionReceived(STTManager.FinalResponse response)
        {
            // 전사 결과는 로그/UI 용이다. 마이크 복귀는 재생 완료 시점에 한다.
            Debug.Log($"<color=cyan>[Pipeline] Final STT Result: {response.data.sttText}</color>");
            Debug.Log($"[Pipeline] Stats - Time: {response.data.speakingTime:F2}s, " +
                      $"Pauses: {response.data.pauseCount}, Vol: {response.data.averageVolume:F4}");
        }

        /// <summary>
        /// tts_end 수신. **재생 완료 판정의 유일한 근거다.**
        /// 제어 채널의 audio_end 는 오디오보다 먼저 도착하므로 쓰면 안 된다.
        /// </summary>
        private void HandleAudioStreamEnded()
        {
            Debug.Log($"[Pipeline] tts_end 수신 (state={_state})");
            if (speaker != null) speaker.SetEndOfStream();
        }

        /// <summary>부분 전사에서 문장 종결이 감지되면 VAD 의 침묵 임계를 줄여 응답을 앞당긴다.</summary>
        private void HandleSentenceCompletedFlag()
        {
            vad?.SetSentenceCompleted();
        }

        /// <summary>빈 전사로 STT 가 스킵된 경우. Speaker 를 건드리지 않고 마이크만 되돌린다.</summary>
        private void HandleSttSkipped()
        {
            if (_isCorrectionPanelOpen)
            {
                Debug.Log("[Pipeline] STT skipped, but correction panel is open. VAD remains disabled.");
                return;
            }

            if (_state == TurnState.BargeInPending || _state == TurnState.Interrupting)
            {
                Debug.Log($"[Pipeline] STT skipped during barge-in (state={_state}) - 상태 유지");
                return;
            }

            Debug.Log("[Pipeline] STT skipped (empty transcription). Re-enabling VAD without touching Speaker.");
            SetTurnState(TurnState.UserAnswering);
        }

        // ===================================================================
        // 9. Speaker 이벤트
        // ===================================================================
        /// <summary>Speaker 버퍼가 완전히 비워져 실제 재생이 끝났을 때.</summary>
        private void HandlePlaybackFinished()
        {
            if (_isCorrectionPanelOpen)
            {
                Debug.Log("[Pipeline] Speaker finished, but correction panel is open. VAD remains disabled.");
                return;
            }

            if (_state == TurnState.Finished) return;

            _interviewerFinishedTime = Time.time;   // 반응 시간 계측 기준점

            if (_state == TurnState.Interrupting)
            {
                if (_awaitingCutoffQuestion)
                {
                    // Type B: 개입 대사만 끝났고 후속 질문 음성이 곧 이어진다.
                    // 여기서 마이크를 열면 면접관 음성이 STT 로 들어간다.
                    Debug.Log("[개입] 개입 대사 완료 - 후속 질문 대기 (마이크 Monitoring 유지)");
                    _postBargeInDeadline = Time.time + postBargeInTimeout;
                    SetTurnState(TurnState.InterviewerSpeaking);
                    return;
                }

                // Type A: 재답변을 받아야 하므로 즉시 마이크 복귀
                Debug.Log($"[개입] 개입 대사 완료 (type={LastBargeInType}) - 마이크 복귀");
                SetTurnState(TurnState.UserAnswering);
                return;
            }

            SetTurnState(TurnState.UserAnswering);
        }

        private void HandleSubtitleTextChanged(string text)
        {
            if (subtitleText != null) subtitleText.text = text;
        }

        // ===================================================================
        // 10. 개입 (백엔드 -> Unity)
        // ===================================================================
        /// <summary>
        /// 백엔드 bargein_cutin 수신. **순서가 중요하다**(설계서 6.7).
        /// 이 메시지는 대사 생성을 기다리지 않고 먼저 오므로, 표정이 음성보다 앞서 바뀐다.
        /// </summary>
        public void OnBargeInCutin(BargeInCutin msg)
        {
            if (_state != TurnState.UserAnswering)
            {
                Debug.LogWarning($"[개입] 상태 불일치로 무시 (state={_state})");
                return;
            }

            LastBargeInType = msg.bargein_type;
            _bargeInAt = Time.time;
            _yieldReported = false;
            _reactionClosed = false;

            // ForceEndUtterance() 가 isSpeaking 을 지우기 전에 기록해야 한다.
            _yieldMeasurable = (vad != null && vad.IsSpeaking);

            // ForceEndUtterance()가 발생시키는 OnUtteranceEnded가
            // utterance_end 대신 utterance_abort를 선택하도록 먼저 표시한다.
            _abortRequested = true;
            _abortReason = msg.reason ?? "";

            // 1·2. 상태 전이 (SetTurnState 가 MicMode 를 Monitoring 으로 내린다)
            SetTurnState(TurnState.BargeInPending);

            // 3. STT 에 발화 강제 확정.
            //    마지막 tail chunk가 먼저 큐에 들어간 뒤 utterance_abort가 전송된다.
            if (vad != null)
                vad.ForceEndUtterance();
            else
                ClearAbortRequest();

            // 4. 애니메이터 트리거 — 몸짓이 소리보다 먼저(수백 ms 선행).
            //    지연 은폐용 편법이 아니라 인간 개입 행동의 실제 시간 구조 모사다.
            interviewerDriver.TriggerBargeIn();

            // 5. 표정 적용. 제스처와 컷인이 빠진 현재, 이것이 가장 강한 즉시 신호다.
            expression?.Apply(msg.expression_id);

            // 6. 컷인 프리셋 재생. 클립이 없으면 조용히 통과하고 개입 대사로 직행한다.
            speaker.EnqueueCutin(msg.bargein_type);

            // Type B 는 개입 대사(발화 1) 뒤에 후속 질문(발화 2)이 따라온다.
            _awaitingCutoffQuestion = (msg.bargein_type == "CUTOFF");

            Debug.Log($"[개입] 컷인 시작 type={msg.bargein_type} reason={msg.reason}");
        }

        /// <summary>
        /// 백엔드 행동 패킷 수신. bargein_type 태그로 개입 단계를 구분한다.
        ///
        ///   "CUTOFF" / "REDIRECT"  = 발화 1 (개입 대사)
        ///   "CUTOFF_QUESTION"      = 발화 2 (개입 후 다음 질문)
        ///   ""                     = 일반 턴
        /// </summary>
        private void HandleBackendPacket(BehaviorPacket p)
        {
            if (p.type == "thinking" || p.type == "ignored") return;
            if (!string.IsNullOrEmpty(p.stage)) CurrentStage = p.stage;

            // 발화 1: 개입 대사 도착
            if ((p.bargein_type == "CUTOFF" || p.bargein_type == "REDIRECT")
                && _state == TurnState.BargeInPending)
            {
                LastBargeInType = p.bargein_type;
                SetTurnState(TurnState.Interrupting);
                return;
            }

            // 발화 2: Type B 후속 질문 도착
            if (p.bargein_type == "CUTOFF_QUESTION")
            {
                _awaitingCutoffQuestion = false;
                _postBargeInDeadline = float.MaxValue;

                if (_state == TurnState.UserAnswering || _state == TurnState.Idle)
                    SetTurnState(TurnState.InterviewerSpeaking);

                return;   // 이미 InterviewerSpeaking 이면 그대로 둔다
            }

            // 일반 턴
            if (_state == TurnState.UserAnswering || _state == TurnState.Idle)
                SetTurnState(TurnState.InterviewerSpeaking);
        }

        // ===================================================================
        // 11. 저신뢰 STT 교정
        // ===================================================================
        /// <summary>
        /// STT 신뢰도가 낮을 때 사용자에게 교정 기회를 준다.
        /// 개입 중에는 무시한다 — 잘린 발화는 원래 신뢰도가 낮게 나오는데,
        /// 여기서 교정 UI 가 뜨면 개입 흐름과 완전히 모순된다.
        /// </summary>
        private void HandleOnCorrectionRequested(STTManager.CorrectionRequestMessage msg)
        {
            // 실험 모드 / 개입 중에는 교정 UI 를 띄우지 않는다.
            // 다만 '무시'하면 워커가 final 을 보내지 않아 답변이 통째로 유실되므로,
            // 반드시 send_anyway 로 원본 전사를 그대로 확정시켜야 한다.
            bool bypass = autoAcceptLowConfidence
                          || _state == TurnState.BargeInPending
                          || _state == TurnState.Interrupting;

            if (bypass)
            {
                Debug.Log($"[Pipeline] 저신뢰 전사 자동 수용 (state={_state})");

                _originalTextForCorrection = msg.data != null ? msg.data.sttText : "";
                if (msg.data != null)
                {
                    _originalFeatures = new FeatureData(new VoiceActivityDetector.VoiceFeatures
                    {
                        speakingTime = msg.data.speakingTime,
                        meaningfulPauseCount = msg.data.meaningfulPauseCount,
                        averageVolume = msg.data.averageVolume,
                        volumeVariance = msg.data.volumeVariance,
                        lowVolumeRatio = msg.data.lowVolumeRatio,
                        responseTime = msg.data.responseTime
                    });
                }

                HandleSendAnyway();   // 내부에서 ResetCorrectionState() 까지 처리
                return;
            }

            // 이하 기존 패널 오픈 경로 (개발 중 디버그용으로만 남긴다)
            Debug.Log("[Pipeline] Low confidence STT detected. Opening correction panel.");
            SetTurnState(TurnState.Correcting);
            _isCorrectionPanelOpen = true;

            _originalTextForCorrection = msg.data != null ? msg.data.sttText : "";
            if (msg.data != null)
            {
                // 교정 후에도 원본 발화의 음성 피쳐를 그대로 쓴다.
                // 재발화는 일부 구간만 다시 말하는 것이므로 전체 피쳐를 대체할 수 없다.
                _originalFeatures = new FeatureData(new VoiceActivityDetector.VoiceFeatures
                {
                    speakingTime = msg.data.speakingTime,
                    meaningfulPauseCount = msg.data.meaningfulPauseCount,
                    averageVolume = msg.data.averageVolume,
                    volumeVariance = msg.data.volumeVariance,
                    lowVolumeRatio = msg.data.lowVolumeRatio,
                    responseTime = msg.data.responseTime
                });
            }

            correctionPanel?.Open(msg, HandleSendAnyway, HandleDiscardCorrection, HandleReSpeakStart);
        }

        /// <summary>[그대로 전송] 신뢰도가 낮아도 원본 전사를 그대로 채점에 넘긴다.</summary>
        private async void HandleSendAnyway()
        {
            if (sttManager != null && _originalFeatures != null)
                await sttManager.SendAnywayAsync(_originalTextForCorrection, _originalFeatures);

            ResetCorrectionState();
        }

        /// <summary>[폐기] 이번 발화를 버리고 다시 답변을 받는다.</summary>
        private async void HandleDiscardCorrection()
        {
            if (sttManager != null)
                await sttManager.SendDiscardAsync();

            SetTurnState(TurnState.UserAnswering);
            ResetCorrectionState();
        }

        /// <summary>[재발화] 선택한 단어 범위만 다시 말하도록 마이크를 연다.</summary>
        private void HandleReSpeakStart(int startIdx, int endIdx, string[] words)
        {
            _isCorrectionMode = true;
            _correctionStartIdx = startIdx;
            _correctionEndIdx = endIdx;
            _originalWords = words;

            SetTurnState(TurnState.UserAnswering);
            sttManager.ResetUtteranceState();   // 새 WAV 헤더가 붙도록
        }

        private void ResetCorrectionState()
        {
            _isCorrectionMode = false;
            _isCorrectionPanelOpen = false;
            _correctionStartIdx = -1;
            _correctionEndIdx = -1;
            _originalWords = null;
            _originalFeatures = null;
            _originalTextForCorrection = "";

            correctionPanel?.ResetButtonInteractions();
        }
    }
}
