using UnityEngine;
using VerbalProcess;

namespace VRoom.Backend
{
    /// <summary>
    /// 백엔드 이벤트를 실제 면접관 캐릭터에 연결하는 드라이버.
    ///
    /// [Animator 2축 구동]
    ///   Emotion  = persona_value          (-1 부정 ~ +1 긍정)
    ///   Speaking = Speaker.IsPlaying      ( 0 경청 ~  1 말하기)
    ///
    ///   Speaking 을 오디오 수신 이벤트가 아니라 '실제 재생 여부'로 판단하는 이유:
    ///   정상 구성에서는 음성이 STT 워커를 경유해 오므로 backend.OnAudioChunk 가
    ///   아예 호출되지 않는다. 그 콜백에서 축을 세우면 몸이 영영 움직이지 않는다.
    ///
    /// [표정] expression_id 는 Animator 가 아니라 InterviewerExpression(BlendShape)이 처리한다.
    /// [제스처] gesture_id 는 현재 사용하지 않는다(애니메이션 리소스 미확보).
    /// </summary>
    public class InterviewerDriver : MonoBehaviour
    {
        // ===================================================================
        // 1. 참조 / 설정 / 상태
        // ===================================================================
        [Header("참조")]
        public BackendControlClient backend;
        public Animator animator;
        public Speaker speaker;
        public InterviewerExpression expression;
        public ResultUI resultUI;
        public PipelineController pipeline;
        public STTManager stt;

        [Header("면접 설정 (InterviewConfig 가 있으면 덮어쓴다)")]
        public string company = "";
        public string jobTitle = "";
        [TextArea] public string resume = "";

        [Header("블렌드 반응 속도")]
        [SerializeField] float emotionLerp = 6f;    // 감정 축 전이 속도. 낮을수록 부드럽다
        [SerializeField] float speakingLerp = 10f;  // 발화 축 전이 속도

        [Header("결과 화면")]
        [Tooltip("재생 완료 신호가 오지 않을 때 강제로 피드백을 요청하기까지의 대기 시간(초)")]
        [SerializeField] float feedbackFallbackTimeout = 8f;

        private static readonly int EmotionHash = Animator.StringToHash("Emotion");
        private static readonly int SpeakingHash = Animator.StringToHash("Speaking");

        // ── Animator 목표값 (Update 에서 현재값을 여기로 보간한다) ──
        private float _targetEmotion = 0f;    // -1(부정) ~ +1(긍정)
        private float _targetSpeaking = 0f;   //  0(경청) ~  1(말하기)

        // ── 개입 연출 ─────────────────────────────────────────────
        private float _speakingLerpOverride = 0f;   // 0 이 아니면 speakingLerp 대신 이 값을 쓴다
        private InterviewerSpeechState _speechState = InterviewerSpeechState.Listening;

        // ── 피드백 요청 제어 (중복 요청 방지) ─────────────────────
        private bool _feedbackPending = false;      // is_final 패킷을 받았는가
        private bool _feedbackRequested = false;    // 이미 요청했는가
        private float _feedbackDeadline = float.MaxValue;   // 타임아웃 폴백 시각

        // ===================================================================
        // 2. 수명 / 구독
        // ===================================================================
        private void OnEnable()
        {
            backend.OnBehaviorPacket += HandlePacket;
            backend.OnAudioChunk += HandleAudio;
            backend.OnAudioEnd += HandleAudioEnd;
            backend.OnFeedback += HandleFeedback;

            if (speaker != null)
                speaker.OnPlaybackFinished += HandlePlaybackFinished;
        }

        private void OnDisable()
        {
            backend.OnBehaviorPacket -= HandlePacket;
            backend.OnAudioChunk -= HandleAudio;
            backend.OnAudioEnd -= HandleAudioEnd;
            backend.OnFeedback -= HandleFeedback;

            if (speaker != null)
                speaker.OnPlaybackFinished -= HandlePlaybackFinished;
        }

        private void Start()
        {
            // SetupScene 에서 넘어온 값이 있으면 인스펙터 값을 덮어쓴다.
            if (InterviewConfig.IsReady)
            {
                company = InterviewConfig.Company;
                jobTitle = InterviewConfig.JobTitle;
                resume = InterviewConfig.Resume;
            }
            backend.StartInterview(company, jobTitle, resume);
        }

        // ===================================================================
        // 3. Animator 구동
        // ===================================================================
        private void Update()
        {
            // 재생 완료 신호가 유실됐을 때의 폴백. 이게 없으면 결과 화면으로 못 간다.
            if (_feedbackPending && !_feedbackRequested && Time.time >= _feedbackDeadline)
            {
                Debug.LogWarning("[면접관] 재생 완료 신호 미수신 - 타임아웃으로 피드백 요청");
                RequestFeedbackOnce();
            }

            // 어느 경로로 오디오가 들어오든 '실제 재생 여부'로 발화 축을 구동한다.
            if (speaker != null)
                _targetSpeaking = speaker.IsPlaying ? 1f : 0f;

            if (animator == null) return;

            // 개입 중에는 전이 속도를 올려 '끊고 들어오는' 느낌을 만든다.
            float spLerp = _speakingLerpOverride > 0f ? _speakingLerpOverride : speakingLerp;

            animator.SetFloat(EmotionHash,
                Mathf.Lerp(animator.GetFloat(EmotionHash), _targetEmotion,
                           Time.deltaTime * emotionLerp));
            animator.SetFloat(SpeakingHash,
                Mathf.Lerp(animator.GetFloat(SpeakingHash), _targetSpeaking,
                           Time.deltaTime * spLerp));
        }

        /// <summary>
        /// 개입 시 발화 축 전이를 가속한다. PipelineController 가 컷인 수신 시 호출.
        ///
        /// 제지 제스처(UpperBody 레이어)는 리소스 제약으로 미구현이라,
        /// 현재 이 전이 속도가 개입의 유일한 '동작' 신호다(설계서 6.6).
        /// </summary>
        public void TriggerBargeIn()
        {
            _speechState = InterviewerSpeechState.Interrupting;
            if (animator == null) return;

            _speakingLerpOverride = speakingLerp * 3f;
        }

        // ===================================================================
        // 4. 백엔드 이벤트 핸들러
        // ===================================================================
        private void HandlePacket(BehaviorPacket p)
        {
            Debug.Log($"[면접관/{p.stage}/{p.persona}] {p.dialogue} " +
                      $"(점수 {p.score}, emo={p.persona_value:F2}, expr={p.expression_id}" +
                      (string.IsNullOrEmpty(p.bargein_type) ? "" : $", bargein={p.bargein_type}") + ")");

            // thinking = 더미 모션, ignored = 처리하지 말 것.
            // 둘 다 expression_id 가 0 이라 그냥 통과시키면 개입 직후 표정이 중립으로 풀린다.
            if (p.type == "thinking" || p.type == "ignored")
                return;

            _targetEmotion = p.persona_value;
            expression?.Apply(p.expression_id);

            // 마무리 멘트 도착. 실제 재생이 끝난 뒤에 피드백을 요청해야 하므로 예약만 한다.
            if (p.is_final && !_feedbackRequested)
            {
                _feedbackPending = true;
                _feedbackDeadline = float.MaxValue;
            }
        }

        /// <summary>
        /// /ws/control 로 PCM 이 직접 올 때만 호출된다(STT 워커 부재 시).
        /// 정상 구성에서는 STTManager -> Speaker 경로를 타므로 여기는 실행되지 않는다.
        /// </summary>
        private void HandleAudio(byte[] pcm)
        {
            if (speaker != null)
                speaker.HandleAudioChunkReceived(pcm);
        }

        /// <summary>
        /// 백엔드의 합성 종료 신호(audio_end).
        ///
        /// 주의: 여기서 Speaker.SetEndOfStream() 을 부르면 안 된다.
        /// audio_end 는 제어 채널로 직행하지만 오디오는 STT 워커를 한 홉 더 거치므로,
        /// 아직 도착하지 않은 뒷부분을 버리고 재생이 끊긴다.
        /// 재생 완료 판정은 오디오와 같은 채널로 오는 tts_end 로만 한다.
        /// </summary>
        private void HandleAudioEnd()
        {
            _targetSpeaking = 0f;
            if (_feedbackPending && !_feedbackRequested)
                _feedbackDeadline = Time.time + feedbackFallbackTimeout;
        }

        /// <summary>Speaker 버퍼가 완전히 비워져 실제 재생이 끝났을 때 호출된다.</summary>
        private void HandlePlaybackFinished()
        {
            _speakingLerpOverride = 0f;
            _speechState = InterviewerSpeechState.Listening;

            if (_feedbackPending)
                RequestFeedbackOnce();
        }

        private void HandleFeedback(FeedbackReport r)
        {
            pipeline.SetTurnState(TurnState.Finished);

            if (resultUI != null)
                resultUI.Show(r);
            else
                Debug.LogWarning("[InterviewerDriver] resultUI 미연결");
        }

        /// <summary>피드백 요청은 정확히 1회만 나가야 한다(재생 완료와 타임아웃이 겹칠 수 있다).</summary>
        private void RequestFeedbackOnce()
        {
            if (_feedbackRequested) return;

            _feedbackRequested = true;
            _feedbackPending = false;
            _feedbackDeadline = float.MaxValue;

            pipeline.SetTurnState(TurnState.Finished);
            Debug.Log("[면접관] 마무리 멘트 재생 완료 - 피드백 요청");
            _ = backend.RequestFeedback();
        }
    }
}