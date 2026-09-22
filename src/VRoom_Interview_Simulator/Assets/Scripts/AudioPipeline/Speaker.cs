using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// 스트리밍 오디오 재생기 + 자막 페이싱.
    ///
    /// [왜 AudioSource.Play() 를 쓰지 않는가]
    ///   TTS 음성이 청크 단위로 계속 흘러들어오므로 클립이 완성되기를 기다릴 수 없다.
    ///   무음 더미 클립을 무한 반복 재생해 OnAudioFilterRead 를 항상 돌게 하고,
    ///   그 콜백 안에서 큐의 샘플을 직접 채워 넣는다.
    ///
    /// [★ 스레드 경계 — 이 파일에서 가장 중요한 규칙]
    ///   OnAudioFilterRead 는 **오디오 스레드**에서 돌고 나머지는 메인 스레드다.
    ///   두 스레드가 공유하는 값은 ConcurrentQueue 또는 Volatile 로만 주고받는다.
    ///   오디오 스레드에서 Unity API(Debug.Log 포함)를 부르면 안 된다.
    ///
    /// [립싱크]
    ///   uLipSync 가 이 컴포넌트의 OnAudioFilterRead 출력에 물려 있다.
    ///   그래서 로컬 클립도 별도 AudioSource 가 아니라 반드시 이 큐로 넣어야
    ///   입이 함께 움직인다.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class Speaker : MonoBehaviour
    {
        // ===================================================================
        // 1. 설정
        // ===================================================================
        [Header("Audio Settings")]
        [Tooltip("서버가 보내는 PCM 의 샘플레이트. 로컬 클립도 이 값과 같아야 피치가 맞는다.")]
        [SerializeField] private int serverSampleRate = 44100;
        [SerializeField] private float volume = 1.0f;
        [Tooltip("이 개수만큼 쌓이면 스트리밍이 안정화된 것으로 보고 로그를 남긴다.")]
        [SerializeField] private int bufferThresholdChunks = 3;

        [Header("LipSync")]
        [SerializeField] private uLipSync.uLipSync lipSync;

        [Header("BargeIn Cut-in")]
        [Tooltip("Type A(REDIRECT) 컷인 프리셋. '잠깐', '잠시만요' 처럼\n"
               + "말을 멈추게만 하고 다음으로 넘어가지 않는 문구여야 한다.\n"
               + "44100Hz Mono 필수.")]
        [SerializeField] private AudioClip[] redirectCutinClips;

        [Tooltip("Type B(CUTOFF) 컷인 프리셋. '네, 거기까지' 처럼\n"
               + "답변을 종료시키는 문구를 쓴다. 44100Hz Mono 필수.")]
        [SerializeField] private AudioClip[] cutoffCutinClips;

        [Header("Subtitle Settings")]
        [Tooltip("글자당 예상 발화 시간(초). 자막 진행률 추정에만 쓰인다.")]
        [SerializeField] private float secondsPerChar = 0.18f;
        [Tooltip("자막이 이 글자 수를 넘으면 앞부분을 잘라낸다.")]
        [SerializeField] private int maxSubtitleCharacters = 85;
        [Tooltip("한 번 자를 때 최소 이만큼은 지운다. 한 글자씩 밀리면 읽기 어렵다.")]
        [SerializeField] private int trimStepCharacters = 30;

        // ===================================================================
        // 2. 공개 이벤트 / 상태
        // ===================================================================
        /// <summary>모든 버퍼 재생이 완료되었을 때. 마이크 복귀의 트리거다.</summary>
        public Action OnPlaybackFinished;

        /// <summary>표시할 자막이 바뀌었을 때.</summary>
        public Action<string> OnSubtitleTextChanged;

        /// <summary>재생 중인지. InterviewerDriver 의 Animator Speaking 축 구동에 쓴다.</summary>
        public bool IsPlaying => !_audioChunkQueue.IsEmpty || _currentChunk != null;

        /// <summary>큐에 담기는 단위. 오디오와 그 구간의 자막을 함께 들고 다닌다.</summary>
        public class SubtitleChunk
        {
            public string subtitleText;         // 이 청크가 속한 문장 전체
            public float[] audioData;           // float PCM
            public int estimatedTotalSamples;   // 문장 전체의 예상 샘플 수(진행률 분모)
        }

        // ===================================================================
        // 3. 내부 상태
        // ===================================================================
        private AudioSource _audioSource;
        private int _outputSampleRate;

        // ── 공유: 메인 스레드가 쓰고 오디오 스레드가 읽는다 ────────
        private readonly ConcurrentQueue<SubtitleChunk> _audioChunkQueue = new ConcurrentQueue<SubtitleChunk>();

        // ── 오디오 스레드 전용 (메인 스레드에서 만지지 말 것) ──────
        private float[] _currentChunk = null;      // 지금 읽고 있는 청크
        private int _chunkIndex = 0;               // 그 안의 위치
        private float _lastSample = 0;             // 리샘플 보간용 이전 샘플
        private float _currentSample = 0;          // 리샘플 보간용 현재 샘플
        private float _t = 0;                      // 보간 위치 (0~1)
        private bool _hasCurrentSample = false;
        private string _activeSubtitleText = null; // 현재 재생 중인 문장
        private int _turnCumulativeSamples = 0;    // 이 문장에서 지금까지 재생한 샘플 수
        private int _turnEstimatedTotalSamples = 0;// 이 문장의 예상 총 샘플 수

        // ── 메인 스레드 전용 ──────────────────────────────────────
        private bool _isEndOfStream = false;
        private bool _playbackFinishedEventFired = true;   // 시작 시에는 완료된 상태로 간주
        private string _pendingSubtitleText = "";          // 다음 청크에 붙일 자막
        private int _pendingEstimatedTotalSamples = 0;

        // ── Volatile 로 주고받는 값 (오디오 스레드 -> 메인 스레드) ──
        private string _currentSubtitleText = "";
        private float _currentSubtitleProgress = 0.0f;

        // ── 자막 렌더링 캐시 (GC 할당 억제) ───────────────────────
        private string _lastSubtitleText = null;   // 마지막으로 Split 한 원문
        private string[] _cachedWords = null;      // 그 Split 결과
        private int _lastWordsToShowCount = -1;    // 마지막으로 Join 한 단어 수
        private string _displayedSubtitle = "";    // 현재 화면에 떠 있는 문자열
        private int _trimStartWordIndex = 0;       // 길어져서 잘라낸 시작 위치

        // ===================================================================
        // 4. 수명
        // ===================================================================
        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = true;

            _outputSampleRate = AudioSettings.outputSampleRate;

            // OnAudioFilterRead 를 항상 돌리기 위한 무음 더미 클립.
            // 이게 없으면 재생 중이 아닐 때 콜백이 멈춰 스트림을 받을 수 없다.
            _audioSource.clip = AudioClip.Create("DummyStream", _outputSampleRate, 1, _outputSampleRate, false);
            _audioSource.Play();
        }

        private void Update()
        {
            // 재생 완료 판정은 메인 스레드에서 한다(이벤트 구독자가 Unity API 를 쓰므로).
            if (_isEndOfStream && !_playbackFinishedEventFired
                && _audioChunkQueue.IsEmpty && _currentChunk == null)
            {
                _playbackFinishedEventFired = true;
                _isEndOfStream = false;

                // 자막 진행률 추정치가 얼마나 부정확했든, 실제 재생이 끝났으면
                // 남은 단어를 전부 노출시킨 뒤에 지운다.
                Volatile.Write(ref _currentSubtitleProgress, 1.0f);
                UpdateSubtitlePacing();   // 이번 프레임에 즉시 반영

                OnPlaybackFinished?.Invoke();
                Debug.Log("[Speaker] All audio playback finished. VAD can be re-enabled.");
            }

            UpdateSubtitlePacing();
        }

        // ===================================================================
        // 5. 입력 (메인 스레드 / 수신 스레드)
        // ===================================================================
        /// <summary>
        /// 다음에 도착할 오디오 청크들이 어느 문장에 속하는지 지정한다.
        /// 오디오보다 자막이 먼저 오므로 여기서 보관해 뒀다가 청크에 붙인다.
        /// </summary>
        public void HandleSubtitleReceived(string subtitleText)
        {
            _pendingSubtitleText = subtitleText;
            _pendingEstimatedTotalSamples =
                Mathf.RoundToInt(subtitleText.Length * secondsPerChar * serverSampleRate);
        }

        /// <summary>
        /// float32 PCM 바이트를 큐에 넣는다. STT 워커 수신 스레드에서도 호출된다.
        ///
        /// 청크 경계에서 파형이 갑자기 끊기면 '틱' 하는 클릭 노이즈가 난다.
        /// 앞뒤 2ms 를 페이드해 막는다. 그래서 한 발화를 여러 청크로 쪼개 넣으면
        /// 경계마다 페이드가 걸려 트레몰로가 생긴다(로컬 클립을 통째로 넣는 이유).
        /// </summary>
        public void HandleAudioChunkReceived(byte[] pcmData)
        {
            int validBytes = pcmData.Length - (pcmData.Length % 4);   // float 경계로 자름
            if (validBytes == 0) return;

            float[] floatArray = new float[validBytes / 4];
            Buffer.BlockCopy(pcmData, 0, floatArray, 0, validBytes);  // 개별 변환보다 훨씬 빠르다

            ApplyEdgeFade(floatArray);

            _audioChunkQueue.Enqueue(new SubtitleChunk
            {
                subtitleText = _pendingSubtitleText,
                audioData = floatArray,
                estimatedTotalSamples = _pendingEstimatedTotalSamples
            });
            // _pendingSubtitleText 는 다음 자막이 올 때까지 비우지 않고 유지한다.

            _playbackFinishedEventFired = false;
            _isEndOfStream = false;

            if (_audioChunkQueue.Count == bufferThresholdChunks)
                Debug.Log("[Speaker] Buffer threshold reached. Audio streaming stabilized.");
        }

        /// <summary>청크 시작/끝 약 2ms 를 페이드해 패킷 경계 클릭 노이즈를 막는다.</summary>
        private void ApplyEdgeFade(float[] samples)
        {
            int fadeLength = Mathf.Min(
                Mathf.RoundToInt(serverSampleRate * 0.002f),
                samples.Length / 2);
            if (fadeLength <= 0) return;

            for (int i = 0; i < fadeLength; i++)
            {
                float factor = (float)i / fadeLength;
                samples[i] *= factor;                              // Fade In
                samples[samples.Length - 1 - i] *= factor;         // Fade Out
            }
        }

        /// <summary>
        /// 로컬 AudioClip 을 스트림 큐에 통째로 밀어 넣는다.
        ///
        /// 별도 AudioSource 로 재생하면 uLipSync 가 이 컴포넌트의 필터 경로에 물려 있어
        /// 그 구간만 입이 움직이지 않는다. 반드시 이 경로를 쓴다.
        /// 쪼개지 않는 이유는 ApplyEdgeFade 주석 참조.
        /// </summary>
        public void EnqueueLocalClip(AudioClip clip, string subtitle = "")
        {
            if (clip == null) return;

            if (clip.frequency != serverSampleRate)
            {
                Debug.LogWarning($"[Speaker] 클립 샘플레이트 불일치: " +
                                 $"{clip.frequency}Hz (기대 {serverSampleRate}Hz). 피치가 틀어집니다.");
            }

            var raw = new float[clip.samples * clip.channels];
            clip.GetData(raw, 0);
            float[] mono = MixToMono(raw, clip.samples, clip.channels);

            HandleSubtitleReceived(subtitle ?? "");   // 빈 문자열이면 자막이 지워진다

            var bytes = new byte[mono.Length * 4];
            Buffer.BlockCopy(mono, 0, bytes, 0, bytes.Length);
            HandleAudioChunkReceived(bytes);

            Debug.Log($"[Speaker] 로컬 클립 큐잉 '{clip.name}' " +
                      $"({mono.Length} samples, {mono.Length / (float)clip.frequency:F2}s)");
        }

        private static float[] MixToMono(float[] raw, int sampleCount, int channels)
        {
            if (channels == 1) return raw;

            var mono = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += raw[i * channels + c];
                mono[i] = sum / channels;
            }
            return mono;
        }

        /// <summary>컷인 프리셋을 무작위로 하나 재생한다. 클립이 없으면 조용히 통과한다.</summary>
        public void EnqueueCutin(string bargeinType)
        {
            AudioClip[] pool = (bargeinType == "REDIRECT") ? redirectCutinClips : cutoffCutinClips;

            if (pool == null || pool.Length == 0)
            {
                Debug.Log($"[Speaker] 컷인 미설정 (type={bargeinType}) - 개입 대사로 직행");
                return;
            }

            EnqueueLocalClip(pool[UnityEngine.Random.Range(0, pool.Length)]);
        }

        // ===================================================================
        // 6. 재생 제어
        // ===================================================================
        /// <summary>
        /// 서버가 더 이상 오디오를 보내지 않음을 알린다.
        /// 큐가 비는 순간 OnPlaybackFinished 가 발생한다.
        ///
        /// ★ 반드시 오디오와 같은 채널로 온 신호(tts_end)로만 호출할 것.
        ///   제어 채널의 audio_end 는 오디오보다 먼저 도착해 뒷부분을 잘라낸다.
        /// </summary>
        public void SetEndOfStream()
        {
            _isEndOfStream = true;
            // 오디오가 아예 없었더라도 완료 이벤트가 발생하도록 false 로 되돌린다.
            _playbackFinishedEventFired = false;
            Debug.Log("[Speaker] End of stream signaled from server.");
        }

        /// <summary>
        /// 재생 중인 오디오와 자막을 전부 버린다. 사용자가 말을 시작할 때 호출된다.
        ///
        /// ★ 개입 중에는 호출하면 안 된다. 개입 대사가 재생 도중 삭제되고,
        ///   _playbackFinishedEventFired = true 가 되어 마이크 복귀까지 막힌다.
        ///   (PipelineController.HandleSpeakingStarted 에 가드가 있다)
        /// </summary>
        public void StopAndClear()
        {
            while (_audioChunkQueue.TryDequeue(out _)) { }

            _currentChunk = null;
            _chunkIndex = 0;
            _lastSample = 0;
            _currentSample = 0;
            _t = 0;
            _hasCurrentSample = false;
            _isEndOfStream = false;
            _playbackFinishedEventFired = true;

            _pendingSubtitleText = "";
            _pendingEstimatedTotalSamples = 0;
            Volatile.Write(ref _currentSubtitleText, "");
            Volatile.Write(ref _currentSubtitleProgress, 0.0f);

            _lastSubtitleText = null;
            _cachedWords = null;
            _lastWordsToShowCount = -1;
            _displayedSubtitle = "";
            _trimStartWordIndex = 0;
            OnSubtitleTextChanged?.Invoke("");

            Debug.Log("[Speaker] Audio buffer and subtitles cleared.");
        }

        // ===================================================================
        // 7. 자막 페이싱 (메인 스레드)
        // ===================================================================
        /// <summary>
        /// 재생 진행률에 맞춰 자막을 한 단어씩 드러낸다.
        ///
        /// 매 프레임 도는 경로라 GC 할당을 최대한 피한다.
        ///   - 원문이 바뀔 때만 Split
        ///   - 노출 단어 수가 바뀔 때만 Join
        /// </summary>
        private void UpdateSubtitlePacing()
        {
            string rawText = Volatile.Read(ref _currentSubtitleText);
            float progress = Volatile.Read(ref _currentSubtitleProgress);

            // 자막 없음 -> 화면을 비운다
            if (string.IsNullOrEmpty(rawText))
            {
                if (!string.IsNullOrEmpty(_displayedSubtitle))
                {
                    _displayedSubtitle = "";
                    _lastSubtitleText = null;
                    _cachedWords = null;
                    _lastWordsToShowCount = -1;
                    OnSubtitleTextChanged?.Invoke(_displayedSubtitle);
                }
                return;
            }

            // 문장이 바뀐 경우에만 Split (GC 급감)
            if (rawText != _lastSubtitleText)
            {
                _lastSubtitleText = rawText;
                _cachedWords = rawText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                _lastWordsToShowCount = -1;   // Join 강제
                _trimStartWordIndex = 0;
            }

            // 공백만 있는 문장 등: 그대로 보여준다
            if (_cachedWords == null || _cachedWords.Length == 0)
            {
                if (_displayedSubtitle != rawText)
                {
                    _displayedSubtitle = rawText;
                    OnSubtitleTextChanged?.Invoke(_displayedSubtitle);
                }
                return;
            }

            int wordsToShow = Mathf.Clamp(
                Mathf.CeilToInt(_cachedWords.Length * progress), 1, _cachedWords.Length);

            // 노출 단어 수가 그대로면 Join 하지 않는다
            if (wordsToShow == _lastWordsToShowCount) return;
            _lastWordsToShowCount = wordsToShow;

            TrimIfTooLong(wordsToShow);

            int count = wordsToShow - _trimStartWordIndex;
            _displayedSubtitle = count > 0
                ? string.Join(" ", _cachedWords, _trimStartWordIndex, count)
                : "";
            OnSubtitleTextChanged?.Invoke(_displayedSubtitle);
        }

        /// <summary>
        /// 자막이 길어지면 앞에서부터 잘라낸다.
        /// 한 글자씩 밀면 읽기 어려우므로 최소 trimStepCharacters 만큼 한 번에 지운다.
        /// </summary>
        private void TrimIfTooLong(int wordsToShow)
        {
            int projectedLength = 0;
            for (int i = _trimStartWordIndex; i < wordsToShow; i++)
            {
                projectedLength += _cachedWords[i].Length;
                if (i > _trimStartWordIndex) projectedLength += 1;   // 공백
            }

            if (projectedLength <= maxSubtitleCharacters) return;

            int removedLength = 0;
            while (_trimStartWordIndex < wordsToShow && removedLength < trimStepCharacters)
            {
                removedLength += _cachedWords[_trimStartWordIndex].Length + 1;
                _trimStartWordIndex++;
            }
        }

        // ===================================================================
        // 8. 오디오 스레드 — 여기서 Unity API 호출 금지
        // ===================================================================
        /// <summary>
        /// 오디오 출력 버퍼를 채운다. 서버 샘플레이트와 출력 샘플레이트가 달라
        /// 선형 보간으로 리샘플링한다.
        /// </summary>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_outputSampleRate == 0) return;

            float resampleRatio = (float)serverSampleRate / _outputSampleRate;

            for (int i = 0; i < data.Length; i += channels)
            {
                // 보간 위치가 다음 샘플을 넘어가면 큐에서 더 읽어온다
                while (_t >= 1.0f || !_hasCurrentSample)
                {
                    if (!AdvanceToNextSample()) break;   // 버퍼 언더런
                }

                float sample;
                if (_hasCurrentSample)
                {
                    sample = Mathf.Lerp(_lastSample, _currentSample, _t);
                    _t += resampleRatio;
                }
                else
                {
                    // 언더런: 0으로 뚝 떨어뜨리면 '팝' 노이즈가 난다. 빠르게 감쇠시킨다.
                    _lastSample = Mathf.Lerp(_lastSample, 0, 0.1f);
                    sample = _lastSample;
                }

                for (int c = 0; c < channels; c++)
                    data[i + c] = sample * volume;
            }

            lipSync?.OnDataReceived(data, channels);
        }

        /// <summary>
        /// 다음 원본 샘플 하나를 읽어 보간 상태를 전진시킨다.
        /// 큐가 비어 더 읽을 수 없으면 false.
        /// </summary>
        private bool AdvanceToNextSample()
        {
            if (_currentChunk == null || _chunkIndex >= _currentChunk.Length)
            {
                if (!_audioChunkQueue.TryDequeue(out var chunk))
                {
                    _currentChunk = null;
                    _hasCurrentSample = false;
                    return false;
                }

                _currentChunk = chunk.audioData;
                _chunkIndex = 0;

                // 문장 경계 감지: 자막이 바뀌면 진행률을 처음부터 다시 센다
                if (chunk.subtitleText != _activeSubtitleText)
                {
                    _activeSubtitleText = chunk.subtitleText;
                    _turnCumulativeSamples = 0;
                    _turnEstimatedTotalSamples = chunk.estimatedTotalSamples;
                }

                Volatile.Write(ref _currentSubtitleText, _activeSubtitleText);
            }

            float nextSample = _currentChunk[_chunkIndex++];
            _turnCumulativeSamples++;

            // 진행률. 추정치를 넘어서면 분모를 실제값으로 바꿔 100%를 넘지 않게 한다.
            int denom = Mathf.Max(_turnEstimatedTotalSamples, _turnCumulativeSamples);
            Volatile.Write(ref _currentSubtitleProgress,
                           denom > 0 ? (float)_turnCumulativeSamples / denom : 0f);

            if (!_hasCurrentSample)
            {
                _currentSample = nextSample;
                _lastSample = nextSample;
                _hasCurrentSample = true;
                _t = 0;
            }
            else
            {
                _lastSample = _currentSample;
                _currentSample = nextSample;
                _t -= 1.0f;
                if (_t < 0) _t = 0;
            }
            return true;
        }
    }
}