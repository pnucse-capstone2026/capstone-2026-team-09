using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// 마이크 입력을 캡처해 RMS 기반으로 발화/침묵을 판정하고, 음성 피쳐를 추출한다.
    /// 말하는 도중에도 0.3초마다 청크를 내보내 STT 지연을 줄인다.
    ///
    /// [★ 마이크는 끄지 않는다]
    ///   Microphone.Start 는 Start() 에서 한 번만 부르고 끝까지 유지한다.
    ///   대신 MicMode 로 '무엇을 할지'만 바꾼다.
    ///     Off          아무것도 하지 않는다
    ///     Monitoring   RMS 는 계산하되 STT 로 보내지 않는다 (개입 중 양보 시간 계측용)
    ///     Transmitting 정상 답변 수신. 청크를 STT 로 보낸다
    ///
    ///   MicMode 는 이 컴포넌트가 스스로 정하지 않는다.
    ///   PipelineController 의 턴 상태 머신만이 SetMicMode() 를 호출한다.
    ///
    /// [읽기 포인터]
    ///   마이크는 계속 녹음 중이므로 Off/Monitoring 구간의 오디오도 링버퍼에 쌓인다.
    ///   Transmitting 으로 들어갈 때 ResyncMicPointer() 로 포인터를 현재로 당겨
    ///   그 구간을 버려야 한다. 안 그러면 이전 구간이 답변에 섞인다.
    /// </summary>
    public class VoiceActivityDetector : MonoBehaviour
    {
        // ===================================================================
        // 1. 설정
        // ===================================================================
        [Header("VAD 판정")]
        [Tooltip("이 RMS 를 넘으면 '말하는 중'으로 본다. 마이크마다 조정 필요.")]
        [SerializeField] private float threshold = 0.02f;

        [Tooltip("이만큼 침묵이 이어지면 발화가 끝난 것으로 본다.")]
        [SerializeField] private float defaultSilenceThreshold = 3.0f;

        [Tooltip("STT 가 문장 종결을 감지했을 때 쓰는 짧은 임계값. 응답을 앞당긴다.")]
        [SerializeField] private float shortSilenceThreshold = 1.0f;

        [Header("캡처")]
        [Tooltip("말하는 도중 이 간격마다 부분 청크를 STT 로 보낸다.")]
        [SerializeField] private float chunkSendInterval = 0.3f;
        [Tooltip("Whisper 가 16kHz 를 쓰므로 여기서 맞춰 녹음한다.")]
        [SerializeField] private int sampleRate = 16000;
        [Tooltip("마이크 링버퍼 길이(초). 최대 샘플 수 = sampleRate * 이 값.")]
        [SerializeField] private int bufferLengthSeconds = 300;

        [Header("채점 임계값")]
        [Tooltip("이 시간 이상 침묵하면 '의미 있는 퍼즈' 1회로 센다.")]
        public float meaningfulPauseThreshold = 0.4f;
        [Tooltip("평균 볼륨의 이 비율 이하를 '작은 목소리' 구간으로 센다.")]
        public float lowVolumeRatioThreshold = 0.3f;

        [Header("BargeIn")]
        [Tooltip("발화 시작 후 이 시간을 넘으면 LONG_ANSWER 트리거")]
        [SerializeField] private float longAnswerThreshold = 90f;

        [Tooltip("개입 대사 재생 중 RMS 임계값 배수 (스피커 사용 시 에코 방어)")]
        [SerializeField] private float echoGuardMultiplier = 2.5f;

        [Tooltip("개입 시작 후 이 시간 동안은 발화 판정 자체를 차단")]
        [SerializeField] private float echoGuardBlindSec = 1.0f;

        // ===================================================================
        // 2. 이벤트
        // ===================================================================
        /// <summary>발화 종료 + 음성 피쳐. PipelineController 가 STT 로 넘긴다.</summary>
        public Action<VoiceFeatures> OnUtteranceEnded;

        /// <summary>부분 음성 청크. 말하는 도중에도 계속 발생한다.</summary>
        public Action<AudioClip> OnAudioChunkCaptured;

        /// <summary>발화 시작. Speaker 정지와 반응 시간 계측의 트리거다.</summary>
        public Action OnSpeakingStarted;

        /// <summary>개입 트리거. ("LONG_ANSWER", 경과초)</summary>
        public Action<string, float> OnBargeInTrigger;

        /// <summary>개입으로 발화가 강제 종료됨. bool = 전사할 오디오가 있었는지.</summary>
        public Action<bool> OnUtteranceAborted;

        /// <summary>한 발화에서 추출한 음성 피쳐. 백엔드 채점의 원자료다.</summary>
        public struct VoiceFeatures
        {
            public float speakingTime;         // 순수 발화 시간(초). 뒤쪽 침묵은 제외
            public int meaningfulPauseCount;   // 의미 있는 침묵 횟수
            public float volumeVariance;       // 볼륨 분산. 클수록 들쭉날쭉
            public float lowVolumeRatio;       // 작은 목소리 구간 비율
            public float averageVolume;        // 평균 RMS
            public float responseTime;         // 질문 종료 -> 답변 시작 (Pipeline 이 채운다)
        }

        // ===================================================================
        // 3. 내부 상태
        // ===================================================================
        // ── 마이크 ────────────────────────────────────────────────
        private AudioClip micClip;
        private string micDevice;
        private int lastSamplePosition = 0;        // 마지막으로 읽은 링버퍼 위치
        private float[] reusableSampleBuffer;      // GC 억제용 재사용 버퍼
        private MicMode _micMode = MicMode.Off;

        // ── 발화 판정 ─────────────────────────────────────────────
        private bool isSpeaking = false;
        private float silenceTimer = 0f;               // 현재 침묵이 이어진 시간
        private float silenceDurationThreshold = 3.0f; // 지금 적용 중인 침묵 임계값
        private float chunkTimer = 0f;                 // 마지막 청크 전송 이후 경과
        private int lastChunkEndSample = 0;            // 마지막으로 보낸 청크의 끝 지점
        private float utteranceStartTime;

        // ── 피쳐 집계 ─────────────────────────────────────────────
        private int meaningfulPauseCount = 0;
        private bool wasMeaningfulSilence = false;     // 이번 침묵을 이미 셌는가
        private readonly List<float> rmsSamples = new List<float>();

        // ── 개입 ──────────────────────────────────────────────────
        private bool _bargeInFired = false;      // 한 발화당 1회로 제한
        private float _echoGuardUntil = -1f;     // 이 시각까지는 판정 자체를 차단
        private float _thresholdMul = 1f;        // 에코 방어 시 임계값 배수
        private float _lastVoicedTime = -1f;     // 마지막으로 음성이 감지된 시각

        // ===================================================================
        // 4. 공개 프로퍼티
        // ===================================================================
        public MicMode Mode => _micMode;
        public bool IsSpeaking => isSpeaking;
        public float UtteranceElapsed => isSpeaking ? Time.time - utteranceStartTime : 0f;

        /// <summary>마지막으로 음성이 감지된 시각. 양보 시간 계측에 쓴다.</summary>
        public float LastVoicedTime => _lastVoicedTime;

        private float EffectiveThreshold => threshold * _thresholdMul;

        // ===================================================================
        // 5. 수명
        // ===================================================================
        void Start()
        {
            InitializeMicrophone();

            // 한 프레임에 들어올 최대 샘플 수(약 0.1초 분량)를 미리 잡아 둔다.
            reusableSampleBuffer = new float[sampleRate / 10];

            // 컴포넌트는 항상 켜두고 MicMode 가 동작을 결정한다.
            // enabled 로 껐다 켜면 상태 머신이 정한 모드를 덮어쓴다.
            _micMode = MicMode.Off;
            Debug.Log("[VAD] Initialized. MicMode=Off until the first question playback finishes.");
        }

        private void InitializeMicrophone()
        {
            if (Microphone.devices.Length == 0)
            {
                Debug.LogError("No microphone detected!");
                return;
            }

            micDevice = Microphone.devices[0];
            micClip = Microphone.Start(micDevice, true, bufferLengthSeconds, sampleRate);

            if (micClip == null)
            {
                Debug.LogError("Failed to initialize Microphone Clip!");
                return;
            }
            Debug.Log($"Microphone started: {micDevice}");
        }

        // ===================================================================
        // 6. 마이크 모드 제어 (PipelineController 전용)
        // ===================================================================
        public void SetMicMode(MicMode mode)
        {
            if (_micMode == mode) return;
            var prev = _micMode;
            _micMode = mode;

            // Off 에서 나올 때: 꺼져 있던 동안 쌓인 오디오를 버린다.
            // Monitoring -> Transmitting: isSpeaking 을 반드시 리셋해야 한다.
            //   그러지 않으면 StartSpeaking() 이 호출되지 않아
            //   STTManager.ResetUtteranceState() 가 안 돌고, 첫 청크에 WAV 헤더가 빠진다.
            //   -> 개입 후 재답변이 전사되지 않는다.
            if (prev == MicMode.Off || mode == MicMode.Transmitting)
                ResyncMicPointer();

            _thresholdMul = 1f;
            Debug.Log($"[VAD] MicMode {prev} -> {mode}");
        }

        /// <summary>비활성/관측 구간의 오디오를 무시하기 위해 읽기 포인터를 현재로 당긴다.</summary>
        private void ResyncMicPointer()
        {
            if (micClip == null || !Microphone.IsRecording(micDevice)) return;

            lastSamplePosition = Microphone.GetPosition(micDevice);
            lastChunkEndSample = lastSamplePosition;
            silenceTimer = 0f;
            chunkTimer = 0f;
            isSpeaking = false;
            silenceDurationThreshold = defaultSilenceThreshold;
            rmsSamples.Clear();

            Debug.Log("[VAD] Mic pointer resynced.");
        }

        /// <summary>
        /// 개입 대사 재생 구간의 에코 방어를 켠다.
        /// 스피커로 나가는 면접관 음성을 사용자 발화로 오인하지 않기 위한 것이다.
        /// 헤드셋을 쓰면 필요 없지만, 켜져 있어도 임계값만 올라갈 뿐 해롭지 않다.
        /// </summary>
        public void EnterEchoGuard()
        {
            _thresholdMul = echoGuardMultiplier;
            _echoGuardUntil = Time.time + echoGuardBlindSec;
            Debug.Log($"[VAD] 에코 방어 진입 (x{echoGuardMultiplier}, blind {echoGuardBlindSec}s)");
        }

        public void ExitEchoGuard() => _thresholdMul = 1f;

        /// <summary>
        /// STT 부분 전사에서 문장 종결이 감지되면 침묵 임계값을 줄여 응답을 앞당긴다.
        /// 최종 전사 결과나 채점 데이터에는 관여하지 않는다.
        /// </summary>
        public void SetSentenceCompleted()
        {
            if (!isSpeaking) return;

            silenceDurationThreshold = shortSilenceThreshold;
            Debug.Log($"[VAD] Sentence completed detected. " +
                      $"Silence threshold reduced to {silenceDurationThreshold}s.");
        }

        // ===================================================================
        // 7. 캡처 루프
        // ===================================================================
        void Update()
        {
            if (_micMode == MicMode.Off) return;
            if (micClip == null || !Microphone.IsRecording(micDevice)) return;

            int currentPosition = Microphone.GetPosition(micDevice);
            if (currentPosition < 0 || currentPosition == lastSamplePosition) return;

            ProcessMicSamples(currentPosition);
            lastSamplePosition = currentPosition;
        }

        /// <summary>지난 프레임 이후 들어온 샘플의 RMS 를 구해 VAD 로 넘긴다.</summary>
        private void ProcessMicSamples(int currentPosition)
        {
            // 링버퍼이므로 모듈러 연산으로 실제 개수를 구한다.
            int sampleCount = (currentPosition - lastSamplePosition + micClip.samples) % micClip.samples;
            if (sampleCount <= 0) return;

            // 프레임 시간이 아니라 실제 샘플 수로 시간을 재야 정확하다.
            float audioDuration = (float)sampleCount / sampleRate;

            if (reusableSampleBuffer.Length < sampleCount)
                reusableSampleBuffer = new float[sampleCount];   // 드문 경우에만 재할당

            micClip.GetData(reusableSampleBuffer, lastSamplePosition);

            float sum = 0f;
            for (int i = 0; i < sampleCount; i++)
                sum += reusableSampleBuffer[i] * reusableSampleBuffer[i];
            float rms = Mathf.Sqrt(sum / sampleCount);

            ProcessVAD(rms, audioDuration, currentPosition);
        }

        /// <summary>
        /// 발화/침묵 상태를 갱신하고 필요한 이벤트를 발생시킨다.
        ///
        /// 청크 전송 지점이 두 곳이라는 점에 주의:
        ///   (1) 말하는 도중 chunkSendInterval 마다
        ///   (2) 침묵이 시작되는 첫 프레임 (남은 구간 flush)
        /// Monitoring 모드에서는 둘 다 건너뛴다.
        /// </summary>
        private void ProcessVAD(float rms, float duration, int currentPosition)
        {
            // 에코 방어 블라인드 구간: 판정 자체를 하지 않는다.
            if (Time.time < _echoGuardUntil) return;

            if (rms > EffectiveThreshold)
                HandleVoicedFrame(rms, duration, currentPosition);
            else if (isSpeaking)
                HandleSilentFrame(duration, currentPosition);
        }

        private void HandleVoicedFrame(float rms, float duration, int currentPosition)
        {
            _lastVoicedTime = Time.time;   // 양보 시간 계측용

            if (!isSpeaking)
            {
                StartSpeaking(currentPosition);
            }
            else
            {
                // (1) 주기적 청크 전송
                chunkTimer += duration;
                if (chunkTimer >= chunkSendInterval)
                {
                    if (_micMode == MicMode.Transmitting)
                        SendChunk(currentPosition);
                    lastChunkEndSample = currentPosition;
                    chunkTimer = 0f;
                }

                // LONG_ANSWER 개입 트리거 (한 발화당 1회)
                if (_micMode == MicMode.Transmitting && !_bargeInFired)
                {
                    float elapsed = Time.time - utteranceStartTime;
                    if (elapsed > longAnswerThreshold)
                    {
                        _bargeInFired = true;
                        OnBargeInTrigger?.Invoke("LONG_ANSWER", elapsed);
                    }
                }
            }

            // 문장 종결로 짧아진 임계값은 발화가 재개되면 기본값으로 되돌린다.
            if (silenceTimer > 0f)
            {
                silenceDurationThreshold = defaultSilenceThreshold;
                wasMeaningfulSilence = false;
                silenceTimer = 0f;
            }

            rmsSamples.Add(rms);
        }

        private void HandleSilentFrame(float duration, int currentPosition)
        {
            // (2) 침묵 첫 프레임: 남은 구간을 flush
            if (silenceTimer == 0f)
            {
                if (_micMode == MicMode.Transmitting)
                    SendChunk(currentPosition);
                lastChunkEndSample = currentPosition;
                chunkTimer = 0f;
            }

            silenceTimer += duration;

            if (silenceTimer >= meaningfulPauseThreshold && !wasMeaningfulSilence)
            {
                meaningfulPauseCount++;
                wasMeaningfulSilence = true;
            }

            if (silenceTimer < silenceDurationThreshold) return;

            // Monitoring 중의 발화 종료는 STT 로 보내지 않는다. 상태만 되돌린다.
            if (_micMode != MicMode.Transmitting)
            {
                isSpeaking = false;
                silenceDurationThreshold = defaultSilenceThreshold;
                rmsSamples.Clear();
                Debug.Log("[VAD] (Monitoring) 발화 종료 관측 - STT 전송 없음");
                return;
            }

            EndSpeaking(currentPosition, silenceDurationThreshold);
        }

        // ===================================================================
        // 8. 발화 시작 / 종료
        // ===================================================================
        private void StartSpeaking(int currentPosition)
        {
            isSpeaking = true;
            utteranceStartTime = Time.time;
            _bargeInFired = false;                 // 새 발화마다 트리거 재무장
            lastChunkEndSample = currentPosition;  // 청크 시작 지점 초기화
            silenceDurationThreshold = defaultSilenceThreshold;
            meaningfulPauseCount = 0;
            wasMeaningfulSilence = false;
            rmsSamples.Clear();
            
            OnSpeakingStarted?.Invoke();
            Debug.Log("Speaking Started");
            
        }

        private void SendChunk(int currentPosition)
        {
            AudioClip trimmedClip = AudioUtils.TrimAudio(micClip, lastChunkEndSample, currentPosition);
            OnAudioChunkCaptured?.Invoke(trimmedClip);
        }

        /// <summary>
        /// 발화를 종료하고 피쳐를 산출해 이벤트로 넘긴다.
        /// </summary>
        /// <param name="silenceDuration">
        /// 뒤쪽 침묵 길이. 이만큼 앞을 실제 발화 종료 지점으로 본다.
        /// 개입으로 강제 종료할 때는 0 을 넘겨 '지금까지 말한 전부'를 확정 구간으로 삼는다.
        /// </param>
        private void EndSpeaking(int currentPosition, float silenceDuration, bool aborted = false)
        {
            isSpeaking = false;
            silenceDurationThreshold = defaultSilenceThreshold;

            AudioClip tailClip = BuildTailClip(currentPosition, silenceDuration);
            VoiceFeatures features = BuildFeatures(silenceDuration);

            if (tailClip != null)
            {
                OnAudioChunkCaptured?.Invoke(tailClip);
                Debug.Log($"[VAD] Final tail chunk sent. Length: {tailClip.length:F2}s");
            }

            OnUtteranceEnded?.Invoke(features);

            // tail clip 이 null 인 것은 정상이다.
            // 침묵 첫 프레임에서 이미 flush 하고 lastChunkEndSample 을 앞당겨 두므로
            // 잔여 샘플이 800개(0.05초) 미만이 되어 생성 조건을 통과하지 못한다.
            // 피쳐는 tail clip 유무와 무관하게 rmsSamples 로 독립 계산된다.
            string tailInfo = tailClip != null
                ? $"Tail chunk sent: {tailClip.length:F2}s"
                : "No tail chunk (already flushed at silence start)";
            Debug.Log($"Speaking Ended. {tailInfo}, " +
                      $"Avg Volume: {features.averageVolume:F4}, " +
                      $"Speaking Time: {features.speakingTime:F2}s");

            if (aborted)
                OnUtteranceAborted?.Invoke(true);
        }

        /// <summary>
        /// 마지막 잔여 구간을 클립으로 만든다. 유의미한 길이가 아니면 null.
        ///
        /// 방어가 필요한 이유: 반올림 오차나 프레임 지연으로 종료 지점이 미세하게
        /// 역전되면, 모듈러 연산 결과가 버퍼 전체(300초)를 한 바퀴 도는 값이 되어
        /// 거대한 클립이 만들어진다.
        /// </summary>
        private AudioClip BuildTailClip(int currentPosition, float silenceDuration)
        {
            int silenceSamples = (int)(silenceDuration * sampleRate);
            int utteranceEndSample = (currentPosition - silenceSamples + micClip.samples) % micClip.samples;

            int diff = (utteranceEndSample - lastChunkEndSample + micClip.samples) % micClip.samples;

            // 0.05초(800샘플) 이상 남았고, 한 바퀴 돈 값이 아닐 때만 만든다.
            if (diff <= 800 || diff >= micClip.samples - 800) return null;

            return AudioUtils.TrimAudio(micClip, lastChunkEndSample, utteranceEndSample);
        }

        /// <summary>수집한 RMS 표본으로 음성 피쳐를 계산한다.</summary>
        private VoiceFeatures BuildFeatures(float silenceDuration)
        {
            float duration = Time.time - utteranceStartTime - silenceDuration;
            float avgRms = rmsSamples.Count > 0 ? rmsSamples.Average() : 0f;

            float volumeVariance = 0f;
            float lowVolumeRatio = 0f;

            if (rmsSamples.Count > 0)
            {
                float sumSq = 0f;
                int lowCount = 0;
                float lowThresh = avgRms * lowVolumeRatioThreshold;

                foreach (var r in rmsSamples)
                {
                    float diff = r - avgRms;
                    sumSq += diff * diff;
                    if (r < lowThresh) lowCount++;
                }

                volumeVariance = sumSq / rmsSamples.Count;
                lowVolumeRatio = (float)lowCount / rmsSamples.Count;
            }

            return new VoiceFeatures
            {
                speakingTime = Mathf.Max(0, duration),
                meaningfulPauseCount = meaningfulPauseCount,
                volumeVariance = volumeVariance,
                lowVolumeRatio = lowVolumeRatio,
                averageVolume = avgRms,
                responseTime = 0f   // PipelineController 가 채운다
            };
        }

        /// <summary>
        /// 개입 확정 시 호출. 정상 종료 경로를 타지 않고 발화를 강제로 끝낸다.
        /// 침묵 보정을 0 으로 두어 사용자가 방금 말한 부분까지 전사에 포함시킨다.
        /// </summary>
        public void ForceEndUtterance()
        {
            if (!isSpeaking)
            {
                // LONG_SILENCE 개입: 애초에 발화가 없었으므로 전사할 것이 없다.
                Debug.Log("[VAD] 강제 종료 요청 - 진행 중 발화 없음 (무응답 개입)");
                OnUtteranceAborted?.Invoke(false);
                return;
            }

            int pos = Microphone.GetPosition(micDevice);
            EndSpeaking(pos, 0f, aborted: true);
            Debug.Log("[VAD] 발화 강제 확정 (개입)");
        }
    }
}
