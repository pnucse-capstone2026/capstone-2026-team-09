using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace VRoom.Backend
{
    /// <summary>
    /// SetupScene 전용 프리웜. 면접 씬에 들어가기 전에 백엔드에서 이것들을 미리 끝내 둔다.
    ///   1) 세션 선생성
    ///   2) TTS 워커 소켓 선연결
    ///   3) 첫 질문(템플릿) 음성 선합성 캐시
    ///   4) LLM 커넥션 예열
    ///
    /// 이 과정을 건너뛰면 씬 진입 후 면접관이 말하기까지 수 초간 침묵한다.
    /// 첫인상이 '시스템이 멈췄다'가 되므로 실험 품질에 직접 영향을 준다.
    ///
    /// 실패해도 면접은 진행할 수 있다. 첫 발화가 느려질 뿐이다.
    /// </summary>
    public class BackendWarmup : MonoBehaviour
    {
        // ===================================================================
        // 1. 설정 / 상태 / 이벤트
        // ===================================================================
        [Header("백엔드 (BackendControlClient 와 같은 호스트)")]
        [Tooltip("예: http://127.0.0.1:8080  (뒤에 / 붙이지 말 것)")]
        public string backendHttp = "http://127.0.0.1:8080";

        [Tooltip("STT 워커가 백엔드에 붙을 때 쓰는 session_id 와 동일해야 한다.")]
        public string sessionId = "default";

        [Header("타임아웃(초)")]
        public int healthTimeout = 5;      // 연결 확인. 짧게 잡아 빨리 실패를 알린다
        public int prepareTimeout = 90;    // TTS 선합성 포함. 길게 잡아야 한다

        /// <summary>프리웜 완료 여부. InterviewSetup 이 시작 버튼 활성화 판단에 쓴다.</summary>
        public bool IsReady { get; private set; }

        /// <summary>(성공 여부, 사용자에게 보여줄 메시지)</summary>
        public event Action<bool, string> OnFinished;

        /// <summary>진행 상황 안내 문구.</summary>
        public event Action<string> OnProgress;

        // ===================================================================
        // 2. 실행
        // ===================================================================
        /// <summary>프리웜을 시작한다. 이미 돌고 있으면 중단하고 새로 시작한다.</summary>
        public void Prepare(string company, string jobTitle, string resume)
        {
            StopAllCoroutines();
            IsReady = false;
            StartCoroutine(PrepareRoutine(company, jobTitle, resume));
        }

        private IEnumerator PrepareRoutine(string company, string jobTitle, string resume)
        {
            string baseUrl = backendHttp.TrimEnd('/');

            // ── 1단계: 백엔드가 살아 있는지 먼저 확인한다 ──────────
            // prepare 는 최대 90초를 기다리므로, 서버가 아예 꺼져 있을 때
            // 그만큼 붙잡혀 있으면 사용자가 원인을 알 수 없다.
            OnProgress?.Invoke("백엔드 연결 확인 중...");
            using (var health = UnityWebRequest.Get(baseUrl + "/health"))
            {
                health.timeout = healthTimeout;
                yield return health.SendWebRequest();

                if (health.result != UnityWebRequest.Result.Success)
                {
                    OnFinished?.Invoke(false, $"백엔드({baseUrl})에 연결할 수 없습니다: {health.error}");
                    yield break;
                }
                Debug.Log($"[Warmup] /health OK: {health.downloadHandler.text}");
            }

            // ── 2단계: 세션 생성 + 첫 질문 음성 선합성 ─────────────
            OnProgress?.Invoke("면접관 첫 질문 음성을 준비하는 중입니다...");

            string json =
                "{\"session_id\":\"" + Escape(sessionId) + "\"," +
                "\"company\":\"" + Escape(company) + "\"," +
                "\"job_title\":\"" + Escape(jobTitle) + "\"," +
                "\"resume\":\"" + Escape(resume) + "\"}";

            using (var req = new UnityWebRequest(baseUrl + "/session/prepare", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = prepareTimeout;

                float t0 = Time.realtimeSinceStartup;
                yield return req.SendWebRequest();
                float elapsed = Time.realtimeSinceStartup - t0;

                if (req.result != UnityWebRequest.Result.Success)
                {
                    // 보낸 JSON 까지 남긴다. 이력서에 이스케이프되지 않은 문자가 섞이면
                    // 여기서 422 가 나는데 본문을 봐야 원인을 알 수 있다.
                    string detail = req.downloadHandler != null ? req.downloadHandler.text : "";
                    Debug.LogError($"[Warmup] prepare 실패 ({req.responseCode})\n" +
                                   $"error={req.error}\ndetail={detail}\n보낸 JSON={json}");
                    OnFinished?.Invoke(false, $"첫 질문 사전 준비 실패: {req.error}");
                    yield break;
                }

                Debug.Log($"[Warmup] prepare 완료 ({elapsed:F1}s): {req.downloadHandler.text}");
            }

            // ── 3단계: 면접 씬이 이 세션을 이어받도록 표시한다 ─────
            InterviewConfig.SessionId = sessionId;
            InterviewConfig.Prewarmed = true;
            IsReady = true;
            OnFinished?.Invoke(true, "면접 준비 완료. 시작을 눌러주세요.");
        }

        // ===================================================================
        // 3. 유틸
        // ===================================================================
        /// <summary>
        /// JSON 문자열 값에 넣기 안전하게 이스케이프한다.
        /// 이력서 원문에 줄바꿈과 탭이 섞여 있어 제어문자 처리가 특히 중요하다.
        /// </summary>
        private static string Escape(string s)
            => (s ?? "")
               .Replace("\\", "\\\\")
               .Replace("\"", "\\\"")
               .Replace("\r", "")
               .Replace("\n", "\\n")
               .Replace("\t", " ");
    }
}