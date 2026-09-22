using System;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRoom.Backend
{
    /// <summary>
    /// 백엔드 /ws/control 채널에 연결하는 Unity 클라이언트.
    ///
    /// 한 채널로 두 종류의 프레임이 오간다.
    ///   Text   = JSON 행동 패킷 / 피드백 리포트 / 개입 명령  -> 이벤트로 전달
    ///   Binary = 44.1kHz Mono 32-bit Float PCM 면접관 음성   -> Speaker 로 전달
    ///
    /// 다만 STT 워커가 연결된 정상 구성에서는 음성이 이 채널로 오지 않는다.
    /// 백엔드가 STT 워커를 경유해 보내기 때문이다(에코 제거를 위해).
    /// 따라서 OnAudioChunk 는 STT 워커가 없을 때만 발생한다.
    ///
    /// 사용법:
    ///   1) 씬의 GameObject 에 부착하고 backendUrl 설정
    ///   2) 이벤트를 InterviewerDriver / PipelineController 에 연결
    ///   3) StartInterview(company, jobTitle, resume) 로 면접 시작
    /// </summary>
    public class BackendControlClient : MonoBehaviour
    {
        // ===================================================================
        // 1. 설정 / 이벤트 / 내부 상태
        // ===================================================================
        [Header("Backend")]
        public string backendUrl = "ws://127.0.0.1:8080/ws/control";
        public string sessionId = "default";

        // ── 수신 이벤트 (전부 Unity 메인 스레드에서 호출된다) ────────
        public event Action<BehaviorPacket> OnBehaviorPacket;  // interviewer_turn / thinking / ignored
        public event Action<byte[]> OnAudioChunk;              // PCM 음성 청크 (STT 워커 부재 시에만)
        public event Action OnAudioEnd;                        // 한 발화의 합성 종료 신호
        public event Action<FeedbackReport> OnFeedback;        // 최종 피드백 리포트
        public event Action<BargeInCutin> OnBargeInCutin;      // 개입 컷인 명령

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private SynchronizationContext _main;   // 수신 스레드 -> 메인 스레드 마샬링용

        // JSON 에 float 를 넣을 때 로케일에 따라 소수점이 ',' 가 되면 파싱이 깨진다.
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private void Awake() => _main = SynchronizationContext.Current;

        // ===================================================================
        // 2. 연결 수명
        // ===================================================================
        /// <summary>연결 후 init 메시지를 보내 면접을 시작한다.</summary>
        public async void StartInterview(string company, string jobTitle, string resume)
        {
            // 프리웜에서 세션을 미리 만들어 뒀다면 그 ID 를 그대로 이어받는다.
            if (!string.IsNullOrEmpty(InterviewConfig.SessionId))
                sessionId = InterviewConfig.SessionId;

            // 실험 조건은 셋업 txt 에서만 온다. 비어 있으면 파싱 단계가 뚫린 것이므로
            // 조용히 기본값으로 넘기지 않고 오류로 남긴다 (데이터 오염 방지).
            string condition = InterviewConfig.Condition;
            if (string.IsNullOrEmpty(condition))
            {
                condition = "C";
                Debug.LogError("[Backend] 실험 조건이 비어 있습니다. C 로 진행하지만 "
                             + "이 세션의 데이터는 조건 미상으로 취급해야 합니다.");
            }

            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(backendUrl), _cts.Token);
            _ = ReceiveLoop();

            string init =
                "{\"type\":\"init\"," +
                $"\"session_id\":\"{Escape(sessionId)}\"," +
                $"\"company\":\"{Escape(company)}\"," +
                $"\"job_title\":\"{Escape(jobTitle)}\"," +
                $"\"resume\":\"{Escape(resume)}\"," +
                $"\"condition\":\"{Escape(condition)}\"," +
                $"\"prewarmed\":{(InterviewConfig.Prewarmed ? "true" : "false")}}}";
            await SendText(init);

            Debug.Log($"[Backend] init 전송 (session={sessionId}, condition={condition}, "
                    + $"prewarmed={InterviewConfig.Prewarmed})");
        }

        private async void OnDestroy()
        {
            try
            {
                _cts?.Cancel();
                if (_ws?.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { /* 종료 중 예외는 무시 */ }
        }

        // ===================================================================
        // 3. 송신 (Unity -> 백엔드)
        // ===================================================================
        /// <summary>면접 종료 시 최종 피드백 리포트를 요청한다.</summary>
        public Task RequestFeedback()
            => SendText($"{{\"type\":\"request_feedback\",\"session_id\":\"{Escape(sessionId)}\"}}");

        /// <summary>VAD 발화 시작 시 1회. 백엔드 개입 유예(G6) 계산의 기준 시각이 된다.</summary>
        public Task SendUtteranceStarted()
            => SendText($"{{\"type\":\"utterance_started\"," +
                        $"\"session_id\":\"{Escape(sessionId)}\"}}");

        /// <summary>개입 트리거 발생을 알린다. 백엔드가 게이팅해 허가 여부를 정한다.</summary>
        /// <param name="reason">"LONG_ANSWER" | "LONG_SILENCE"</param>
        /// <param name="elapsed">트리거까지의 경과 시간(초)</param>
        public Task SendBargeInSignal(string reason, float elapsed)
            => SendText($"{{\"type\":\"bargein_signal\"," +
                        $"\"session_id\":\"{Escape(sessionId)}\"," +
                        $"\"reason\":\"{Escape(reason)}\"," +
                        $"\"elapsed\":{elapsed.ToString("0.###", Inv)}}}");

        /// <summary>개입 후 사용자가 입을 다물기까지 걸린 시간. 논문 종속변인이다.</summary>
        public Task SendBargeInYield(float yieldTime)
            => SendText($"{{\"type\":\"bargein_yield\"," +
                        $"\"session_id\":\"{Escape(sessionId)}\"," +
                        $"\"yield_time\":{yieldTime.ToString("0.###", Inv)}}}");

        private async Task SendText(string json)
        {
            if (_ws?.State != WebSocketState.Open) return;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, _cts.Token);
        }

        // ===================================================================
        // 4. 수신 (백엔드 -> Unity)
        // ===================================================================
        private async Task ReceiveLoop()
        {
            byte[] buffer = new byte[1024 * 32];
            using var ms = new System.IO.MemoryStream();

            while (_ws.State == WebSocketState.Open)
            {
                // 한 메시지가 여러 프레임으로 쪼개져 올 수 있으므로 EndOfMessage 까지 모은다.
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                byte[] data = ms.ToArray();
                if (data.Length == 0) continue;

                if (result.MessageType == WebSocketMessageType.Binary)
                    Post(() => OnAudioChunk?.Invoke(data));
                else
                    Dispatch(Encoding.UTF8.GetString(data));
            }
        }

        /// <summary>
        /// JSON 을 type 으로 분기해 해당 이벤트로 넘긴다.
        ///
        /// 주의: default 분기가 알 수 없는 타입을 전부 BehaviorPacket 으로 파싱한다.
        /// 새 메시지 타입을 추가할 때 case 를 빠뜨리면 행동 패킷으로 오인되어
        /// 면접관 표정이 엉뚱하게 초기화된다.
        /// </summary>
        private void Dispatch(string json)
        {
            string type = JsonUtility.FromJson<ServerMessage>(json)?.type;
            switch (type)
            {
                case "audio_end":
                    Post(() => OnAudioEnd?.Invoke());
                    break;

                case "feedback_report":
                    var report = JsonUtility.FromJson<FeedbackReport>(json);
                    Post(() => OnFeedback?.Invoke(report));
                    break;

                case "bargein_cutin":
                    var cutin = JsonUtility.FromJson<BargeInCutin>(json);
                    Post(() => OnBargeInCutin?.Invoke(cutin));
                    break;

                default:   // interviewer_turn / thinking / ignored
                    var packet = JsonUtility.FromJson<BehaviorPacket>(json);
                    Post(() => OnBehaviorPacket?.Invoke(packet));
                    break;
            }
        }

        // ===================================================================
        // 5. 유틸
        // ===================================================================
        /// <summary>백그라운드 수신 스레드에서 Unity 메인 스레드로 넘긴다.
        /// Unity API 는 메인 스레드에서만 호출할 수 있다.</summary>
        private void Post(Action a) => _main.Post(_ => a(), null);

        /// <summary>JSON 문자열 값에 넣기 안전하게 이스케이프한다.</summary>
        private static string Escape(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }
}