using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRoom.Multimodal
{
    /// <summary>
    /// Vision 워커(포트 8002)와의 웹소켓 클라이언트. 텍스트 메시지만 오간다.
    ///
    /// 영상은 이 채널로 흐르지 않는다. 웹캠은 워커가 로컬에서 직접 읽으므로,
    /// Unity 는 '언제부터 언제까지가 한 턴인가'만 알려주면 된다.
    /// 그래서 대역폭이 거의 들지 않고, 원격 데스크탑 환경에서도 안정적이다.
    ///
    ///   Unity -> 워커 : calibrate_start / calibrate_end / turn_start / turn_end
    ///   워커 -> Unity : calibrated(결과) / camera_error(웹캠 열기 실패)
    /// </summary>
    public class VisionStreamClient : MonoBehaviour
    {
        // ===================================================================
        // 1. 설정 / 상태
        // ===================================================================
        [SerializeField] private string wsUrl = "ws://127.0.0.1:8002/ws/vision";
        [SerializeField] private string sessionId = "default";

        public string SessionId { get => sessionId; set => sessionId = value; }
        public bool IsOpen => _ws?.State == WebSocketState.Open;

        /// <summary>캘리브레이션 결과. (성공 여부, 수집 샘플 수)</summary>
        public Action<bool, int> OnCalibrated;

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private SynchronizationContext _main;   // 수신 스레드 -> 메인 스레드 마샬링용

        // 여러 코루틴이 동시에 SendTurnStart/End 를 부를 수 있어 전송을 직렬화한다.
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private void Awake() => _main = SynchronizationContext.Current;

        private void OnDestroy()
        {
            _cts?.Cancel();
            _ws?.Dispose();
        }

        // ===================================================================
        // 2. 연결
        // ===================================================================
        /// <summary>
        /// 워커에 연결한다. 실패해도 예외를 던지지 않고 false 를 돌려준다.
        /// 웹캠이 없어도 면접은 진행되어야 하기 때문이다.
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            if (IsOpen) return true;

            try
            {
                _cts?.Cancel();
                _ws?.Dispose();

                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();
                _ws.Options.SetBuffer(4096, 4096);
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

                // 세션 ID 를 쿼리로 붙인다. 이미 들어 있으면 그대로 둔다.
                string url = wsUrl.Contains("session_id=")
                    ? wsUrl
                    : wsUrl + (wsUrl.Contains("?") ? "&" : "?") + "session_id=" + sessionId;

                await _ws.ConnectAsync(new Uri(url), _cts.Token);
                _ = ReceiveLoop();

                Debug.Log($"[Vision] 연결됨: {url}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Vision] 연결 실패: {e.Message}");
                return false;
            }
        }

        // ===================================================================
        // 3. 송신
        // ===================================================================
        /// <summary>기준 자세 수집 시작. 사용자에게 정면 응시를 안내한 뒤 보낸다.</summary>
        public Task SendCalibrateStart() => SendJson("{\"type\":\"calibrate_start\"}");

        /// <summary>기준 자세 수집 종료. 워커가 평균을 내어 calibrated 로 회신한다.</summary>
        public Task SendCalibrateEnd() => SendJson("{\"type\":\"calibrate_end\"}");

        /// <summary>턴 수집 시작. 개입 여부를 아직 모르므로 대개 NORMAL 로 연다.</summary>
        public Task SendTurnStart(string stage, string phase = "NORMAL")
            => SendJson($"{{\"type\":\"turn_start\"," +
                        $"\"stage\":\"{Escape(stage)}\"," +
                        $"\"phase\":\"{Escape(phase)}\"}}");

        /// <summary>
        /// 턴 수집 종료.
        /// phase 를 넘기면 워커가 turn_start 때의 위상을 그 값으로 정정한다
        /// (개입이 확정되면 NORMAL 로 열었던 구간이 TRUNCATED 가 된다).
        /// </summary>
        public Task SendTurnEnd(string phase = "")
            => string.IsNullOrEmpty(phase)
                ? SendJson("{\"type\":\"turn_end\"}")
                : SendJson($"{{\"type\":\"turn_end\",\"phase\":\"{Escape(phase)}\"}}");

        private async Task SendJson(string json)
        {
            if (!IsOpen) return;

            await _sendLock.WaitAsync();
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(new ArraySegment<byte>(b),
                    WebSocketMessageType.Text, true, _cts.Token);
            }
            catch (Exception e)
            {
                // 전송 실패로 면접을 멈추지 않는다. 해당 턴의 시각 지표만 빠진다.
                Debug.LogWarning($"[Vision] 텍스트 전송 실패: {e.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ===================================================================
        // 4. 수신
        // ===================================================================
        private async Task ReceiveLoop()
        {
            var buf = new byte[4096];
            while (IsOpen)
            {
                try
                {
                    var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;

                    var m = JsonUtility.FromJson<CalibMsg>(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (m == null) continue;

                    if (m.type == "calibrated")
                        _main.Post(_ => OnCalibrated?.Invoke(m.ok, m.samples), null);
                    else if (m.type == "camera_error")
                        _main.Post(_ => Debug.LogError("[Vision] 노트북 웹캠을 열 수 없습니다."), null);
                }
                catch
                {
                    break;   // 연결 종료 또는 취소. 재연결하지 않는다
                }
            }
        }

        /// <summary>워커가 보내는 두 메시지를 모두 담을 수 있는 최소 구조체.</summary>
        [Serializable]
        private class CalibMsg
        {
            public string type;
            public bool ok;
            public int samples;
        }

        // ===================================================================
        // 5. 유틸
        // ===================================================================
        private static string Escape(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}