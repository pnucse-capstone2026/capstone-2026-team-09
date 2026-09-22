using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// STT 워커와의 웹소켓 클라이언트. 오디오를 올려보내고 전사 결과와 면접관 음성을 받는다.
    ///
    /// [한 채널로 두 방향]
    ///   Unity -> 워커 : 오디오 청크(Binary) + 제어 메시지(Text)
    ///   워커 -> Unity : 전사 결과 / 자막 / **면접관 음성 PCM**(Binary) / tts_end
    ///
    ///   면접관 음성이 왜 STT 워커를 거치는가: 워커가 자기가 내보낸 음성을 알고 있어야
    ///   마이크로 되돌아온 에코를 걸러낼 수 있다. 그래서 백엔드가 이 소켓으로 릴레이한다.
    ///
    /// [전송 큐]
    ///   오디오 청크는 0.3초마다 쏟아지는데 웹소켓 SendAsync 는 동시 호출이 불가능하다.
    ///   그래서 모든 전송을 큐에 넣고 SendLoopAsync 한 곳에서 직렬 처리한다.
    ///   호출자는 Task 를 받아 완료/실패를 알 수 있다.
    ///
    /// [WAV 헤더]
    ///   한 발화의 **첫 청크에만** 헤더를 붙이고 이후는 raw PCM 을 보낸다.
    ///   발화가 바뀌면 ResetUtteranceState() 로 다시 헤더를 붙이게 해야 한다.
    ///   이걸 놓치면 워커가 스트림을 해석하지 못해 전사가 통째로 실패한다.
    /// </summary>
    public class STTManager : MonoBehaviour
    {
        // ===================================================================
        // 1. 설정 / 이벤트
        // ===================================================================
        [SerializeField] private string wsUrl = "ws://127.0.0.1:8000/ws/interview";
        [SerializeField] private string sessionId = "default";

        public string SessionId
        {
            get => sessionId;
            set => sessionId = value;
        }

        public Action<FinalResponse> OnTranscriptionReceived;           // 최종 전사 결과
        public Action<byte[]> OnAudioChunkReceived;                     // 면접관 음성 PCM -> Speaker
        public Action OnAudioStreamEnded;                               // tts_end. 재생 완료 판정의 근거
        public Action<string> OnSubtitleReceived;                       // 자막 텍스트 -> Speaker
        public Action<CorrectionRequestMessage> OnCorrectionRequested;  // 저신뢰 교정 요청
        public Action OnSentenceCompletedFlag;                          // 부분 전사의 문장 종결 감지
        public Action OnSttSkipped;                                     // 빈 전사로 처리가 스킵됨

        // ===================================================================
        // 2. 내부 상태
        // ===================================================================
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cts;                      // 현재 소켓 전용
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();  // 컴포넌트 수명

        private bool _shuttingDown = false;   // 세션 종료로 인한 정상 해제. 재연결을 막는다
        private bool _isFirstChunk = true;    // 이번 발화의 첫 청크인가 (WAV 헤더 부착 여부)
        private int _reconnectScheduled = 0;  // 재연결 루프 중복 실행 방지 (Interlocked)

        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);  // 동시 연결 시도 직렬화
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);      // 전송 큐 소비자 깨우기
        private readonly object _utteranceStateLock = new object();             // _isFirstChunk 보호
        private readonly ConcurrentQueue<OutgoingPacket> _outgoingPackets = new ConcurrentQueue<OutgoingPacket>();
        private Task _sendLoopTask;

        // ===================================================================
        // 3. 수명
        // ===================================================================
        private async void Start()
        {
            _sendLoopTask = SendLoopAsync();
            try
            {
                await ConnectAsync();
            }
            catch (Exception e)
            {
                // 워커가 아직 안 떴을 수 있다. 큐에 쌓아 두고 재연결 후 보낸다.
                Debug.LogWarning($"[STT] Initial connection unavailable. " +
                                 $"Queued packets will retry after reconnect: {e.Message}");
                _ = ScheduleReconnectAsync();
            }
        }

        private void OnDestroy()
        {
            _lifetimeCts.Cancel();
            _cts?.Cancel();
            _webSocket?.Dispose();
            FailQueuedPackets(new OperationCanceledException("STTManager was destroyed."));
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// 씬 전환 전 정상 종료. CloseAsync -> CTS cancel 순서를 보장한다.
        ///
        /// _shuttingDown 을 먼저 세우는 이유: 소켓이 닫히면 ReceiveLoop 이 예외를 받는데,
        /// 이 플래그가 없으면 그걸 장애로 오인해 에러를 찍고 재연결까지 시도한다.
        /// 결과 화면에서 STT 워커가 되살아나 GPU 를 점유하게 된다.
        /// </summary>
        public async Task CloseGracefullyAsync()
        {
            _shuttingDown = true;
            try
            {
                if (_webSocket?.State == WebSocketState.Open)
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { /* 종료 중 예외는 무시 */ }
            finally { _cts?.Cancel(); }
        }

        // ===================================================================
        // 4. 연결 / 재연결
        // ===================================================================
        /// <summary>
        /// 워커에 연결한다. 여러 곳에서 동시에 불러도 lock 으로 직렬화되어
        /// 하나의 연결 결과를 공유한다.
        /// </summary>
        public async Task ConnectAsync()
        {
            if (_webSocket?.State == WebSocketState.Open) return;

            await _connectLock.WaitAsync(_lifetimeCts.Token);

            ClientWebSocket socket = null;
            CancellationTokenSource socketCts = null;
            try
            {
                // lock 을 기다리는 동안 다른 호출이 이미 연결했을 수 있다.
                if (_webSocket?.State == WebSocketState.Open) return;

                _cts?.Cancel();
                _webSocket?.Dispose();

                socket = new ClientWebSocket();
                socketCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

                // 버퍼를 8KB 로 잡아 오디오 스트리밍 레이턴시를 줄인다.
                socket.Options.SetBuffer(8192, 8192);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

                await socket.ConnectAsync(new Uri(BuildUrl()), socketCts.Token);

                // 연결 중에 컴포넌트가 파괴됐으면 정리하고 빠진다.
                if (_lifetimeCts.IsCancellationRequested)
                {
                    socket.Dispose();
                    socketCts.Dispose();
                    return;
                }

                _webSocket = socket;
                _cts = socketCts;
                Debug.Log($"[STT] WebSocket Connected to: {BuildUrl()}");
                _ = ReceiveLoop(socket, socketCts);
            }
            catch (Exception e)
            {
                // 실패한 소켓이 현재 소켓으로 승격되지 않았을 때만 정리한다.
                if (socket != null && !ReferenceEquals(_webSocket, socket))
                    socket.Dispose();
                if (socketCts != null && !ReferenceEquals(_cts, socketCts))
                    socketCts.Dispose();

                if (!_lifetimeCts.IsCancellationRequested)
                    Debug.LogError($"[STT] Connection Error: {e.Message}");
                throw;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private string BuildUrl()
        {
            if (wsUrl.Contains("session_id=")) return wsUrl;
            string separator = wsUrl.Contains("?") ? "&" : "?";
            return wsUrl + $"{separator}session_id={Uri.EscapeDataString(sessionId)}";
        }

        /// <summary>3초 간격으로 재연결을 시도한다. Interlocked 로 중복 실행을 막는다.</summary>
        private async Task ScheduleReconnectAsync()
        {
            if (_shuttingDown) return;
            if (_lifetimeCts.IsCancellationRequested ||
                Interlocked.Exchange(ref _reconnectScheduled, 1) == 1)
                return;

            try
            {
                while (!_lifetimeCts.IsCancellationRequested && _webSocket?.State != WebSocketState.Open)
                {
                    await Task.Delay(3000, _lifetimeCts.Token);
                    if (_webSocket?.State == WebSocketState.Open) break;

                    try
                    {
                        Debug.Log("[STT] Attempting to reconnect to STT Worker...");
                        await ConnectAsync();
                    }
                    catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[STT] Reconnect failed: {e.Message}");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectScheduled, 0);
            }
        }

        // ===================================================================
        // 5. 전송 큐
        // ===================================================================
        private Task EnqueuePacket(byte[] payload, WebSocketMessageType messageType,
                                   string description, bool resetUtteranceStateAfterSend = false)
        {
            if (_lifetimeCts.IsCancellationRequested)
                return Task.FromException(new OperationCanceledException("STTManager is shutting down."));

            var packet = new OutgoingPacket(payload, messageType, description, resetUtteranceStateAfterSend);
            _outgoingPackets.Enqueue(packet);
            _sendSignal.Release();
            return packet.Completion.Task;
        }

        /// <summary>큐를 하나씩 꺼내 순서대로 보낸다. 컴포넌트 수명 내내 돈다.</summary>
        private async Task SendLoopAsync()
        {
            try
            {
                while (!_lifetimeCts.IsCancellationRequested)
                {
                    await _sendSignal.WaitAsync(_lifetimeCts.Token);

                    while (_outgoingPackets.TryDequeue(out var packet))
                    {
                        try
                        {
                            await SendPacketAsync(packet);
                            packet.Completion.TrySetResult(true);
                        }
                        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                        {
                            packet.Completion.TrySetCanceled();
                            FailQueuedPackets(new OperationCanceledException("STT send loop was cancelled."));
                            return;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[STT] Failed to send {packet.Description}: {e.Message}");
                            packet.Completion.TrySetException(e);

                            // 현재 발화의 남은 청크를 새 연결에 이어 보내면 오디오가 중간부터
                            // 시작되어 전사가 망가진다. 큐를 비우고 다음 발화부터 새로 시작한다.
                            FailQueuedPackets(e);
                            _cts?.Cancel();
                            _webSocket?.Dispose();
                            _ = ScheduleReconnectAsync();
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                FailQueuedPackets(new OperationCanceledException("STT send loop was cancelled."));
            }
        }

        private async Task SendPacketAsync(OutgoingPacket packet)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                await ConnectAsync();

            // 로컬로 잡아둔다. 전송 중에 재연결이 일어나면 필드가 바뀔 수 있다.
            var socket = _webSocket;
            var socketCts = _cts;
            if (socket == null || socket.State != WebSocketState.Open || socketCts == null)
                throw new InvalidOperationException("STT WebSocket is not open.");

            await socket.SendAsync(
                new ArraySegment<byte>(packet.Payload),
                packet.MessageType,
                true,
                socketCts.Token);

            // utterance_end 계열은 전송이 끝난 뒤에 다음 발화용 헤더 플래그를 세운다.
            if (packet.ResetUtteranceStateAfterSend)
            {
                lock (_utteranceStateLock) { _isFirstChunk = true; }
            }
        }

        private void FailQueuedPackets(Exception error)
        {
            while (_outgoingPackets.TryDequeue(out var queuedPacket))
                queuedPacket.Completion.TrySetException(error);
        }

        // ===================================================================
        // 6. 송신 API
        // ===================================================================
        /// <summary>새 발화가 시작됨을 알린다. 다음 청크에 WAV 헤더가 붙는다.</summary>
        public void ResetUtteranceState()
        {
            lock (_utteranceStateLock) { _isFirstChunk = true; }
        }

        /// <summary>오디오 청크를 보낸다. 발화의 첫 청크만 WAV 헤더를 포함한다.</summary>
        public Task SendAudioChunkAsync(AudioClip clip)
        {
            if (clip == null) return Task.CompletedTask;

            byte[] audioBytes;
            lock (_utteranceStateLock)
            {
                if (_isFirstChunk)
                {
                    audioBytes = AudioUtils.GetWavBytes(clip);
                    _isFirstChunk = false;
                }
                else
                {
                    audioBytes = AudioUtils.GetRawPcmBytes(clip);
                }
            }

            return EnqueuePacket(audioBytes, WebSocketMessageType.Binary, "audio chunk");
        }

        /// <summary>발화 종료 신호 + 음성 피쳐. 워커가 최종 전사를 시작한다.</summary>
        public Task SendEndUtteranceAsync(FeatureData features)
        {
            string featuresJson = features != null ? JsonUtility.ToJson(features) : "{}";
            string json = $"{{\"type\":\"utterance_end\"," +
                          $"\"session_id\":\"{EscapeJson(sessionId)}\"," +
                          $"\"features\":{featuresJson}}}";

            return EnqueuePacket(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text,
                                 "utterance_end", resetUtteranceStateAfterSend: true);
        }

        /// <summary>
        /// 개입으로 현재 발화를 강제 종료한다.
        /// 오디오 청크 큐 뒤에 들어가므로 마지막 청크가 먼저 전송된다.
        /// 워커는 최종 전사 결과에 truncated=true를 붙인다.
        /// </summary>
        public Task SendUtteranceAbortAsync(FeatureData features, string reason = "")
        {
            string featuresJson = features != null ? JsonUtility.ToJson(features) : "{}";
            string json = $"{{\"type\":\"utterance_abort\"," +
                          $"\"session_id\":\"{EscapeJson(sessionId)}\"," +
                          $"\"reason\":\"{EscapeJson(reason)}\"," +
                          $"\"features\":{featuresJson}}}";

            return EnqueuePacket(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text,
                                 "utterance_abort", resetUtteranceStateAfterSend: true);
        }

        /// <summary>
        /// 재발화 교정 종료 신호. 어느 단어 범위를 다시 말한 것인지 함께 보낸다.
        /// 워커가 원본 전사의 해당 구간만 교체한다.
        /// </summary>
        public Task SendCorrectionEndUtteranceAsync(FeatureData features, int startIdx, int endIdx,
                                                    string[] originalWords)
        {
            string featuresJson = features != null ? JsonUtility.ToJson(features) : "{}";
            string json = $"{{\"type\":\"utterance_end\"," +
                          $"\"session_id\":\"{EscapeJson(sessionId)}\"," +
                          $"\"mode\":\"correction\"," +
                          $"\"target_range\":[{startIdx},{endIdx}]," +
                          $"\"original_words\":{BuildWordsJson(originalWords)}," +
                          $"\"features\":{featuresJson}}}";

            return EnqueuePacket(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text,
                                 "correction utterance_end", resetUtteranceStateAfterSend: true);
        }

        /// <summary>교정 없이 원본 전사를 그대로 채점에 넘긴다.</summary>
        public Task SendAnywayAsync(string text, FeatureData features)
        {
            string featuresJson = features != null ? JsonUtility.ToJson(features) : "{}";
            string json = $"{{\"type\":\"send_anyway\"," +
                          $"\"session_id\":\"{EscapeJson(sessionId)}\"," +
                          $"\"text\":\"{EscapeJson(text)}\"," +
                          $"\"features\":{featuresJson}}}";

            return EnqueuePacket(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, "send_anyway");
        }

        /// <summary>교정을 포기하고 이번 발화를 통째로 버린다.</summary>
        public Task SendDiscardAsync()
        {
            string json = $"{{\"type\":\"discard\",\"session_id\":\"{EscapeJson(sessionId)}\"}}";
            return EnqueuePacket(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, "discard");
        }

        private static string BuildWordsJson(string[] words)
        {
            if (words == null || words.Length == 0) return "[]";

            var sb = new StringBuilder("[");
            for (int i = 0; i < words.Length; i++)
            {
                // 보간 문자열 안에 문자열 리터럴을 중첩하면 읽기 어려우므로 풀어 쓴다.
                string escaped = words[i].Replace("\"", "\\\"");
                sb.Append('"').Append(escaped).Append('"');
                if (i < words.Length - 1) sb.Append(',');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string EscapeJson(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\n", "\\n").Replace("\r", "\\r");

        // ===================================================================
        // 7. 수신
        // ===================================================================
        /// <summary>
        /// 소켓 하나에 대한 수신 루프. 재연결 시 새 루프가 시작되므로
        /// 소켓과 CTS 를 인자로 받아 '자기 소켓'만 정리하도록 한다.
        /// </summary>
        private async Task ReceiveLoop(ClientWebSocket socket, CancellationTokenSource socketCts)
        {
            byte[] buffer = new byte[1024 * 32];
            using var ms = new System.IO.MemoryStream();

            while (socket.State == WebSocketState.Open && !socketCts.IsCancellationRequested)
            {
                WebSocketReceiveResult result = null;
                try
                {
                    // 한 메시지가 여러 프레임으로 쪼개져 올 수 있다.
                    ms.SetLength(0);
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), socketCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, socketCts.Token);
                        _ = ScheduleReconnectAsync();
                        break;
                    }

                    if (ms.Length == 0) continue;
                    byte[] data = ms.ToArray();

                    if (result.MessageType == WebSocketMessageType.Binary)
                        OnAudioChunkReceived?.Invoke(data);   // 면접관 음성 PCM
                    else
                        DispatchTextMessage(Encoding.UTF8.GetString(data));
                }
                catch (OperationCanceledException) when (socketCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    // 세션 종료로 인한 정상 해제와 실제 장애를 구분한다.
                    if (_shuttingDown)
                    {
                        Debug.Log("[STT] Receive loop closed (session finished).");
                        return;
                    }

                    Debug.LogError($"[STT] Receive Loop Error: {e.Message}");

                    // 이미 다른 소켓으로 교체된 상태라면 건드리지 않는다.
                    if (ReferenceEquals(_webSocket, socket))
                    {
                        _cts?.Cancel();
                        _webSocket?.Dispose();
                    }
                    _ = ScheduleReconnectAsync();
                    break;
                }
            }
        }

        /// <summary>type 필드로 분기해 해당 이벤트를 발생시킨다.</summary>
        private void DispatchTextMessage(string message)
        {
            Debug.Log($"[STT] Received JSON: {message}");

            try
            {
                ServerMessage msg = JsonUtility.FromJson<ServerMessage>(message);
                switch (msg.type)
                {
                    case "final":
                        OnTranscriptionReceived?.Invoke(JsonUtility.FromJson<FinalResponse>(message));
                        break;

                    case "correction_request":
                        OnCorrectionRequested?.Invoke(
                            JsonUtility.FromJson<CorrectionRequestMessage>(message));
                        break;

                    case "subtitle":
                        OnSubtitleReceived?.Invoke(JsonUtility.FromJson<SubtitleMessage>(message).text);
                        break;

                    case "tts_end":
                        OnAudioStreamEnded?.Invoke();
                        break;

                    case "adaptive_vad_trigger":
                        OnSentenceCompletedFlag?.Invoke();
                        break;

                    case "stt_skip":
                        OnSttSkipped?.Invoke();
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to parse server message: {e.Message}");
            }
        }

        // ===================================================================
        // 8. 데이터 구조
        // ===================================================================
        /// <summary>전송 큐에 담기는 단위. Completion 으로 호출자가 결과를 기다린다.</summary>
        private sealed class OutgoingPacket
        {
            public readonly byte[] Payload;
            public readonly WebSocketMessageType MessageType;
            public readonly string Description;                 // 실패 로그용
            public readonly bool ResetUtteranceStateAfterSend;  // 전송 후 WAV 헤더 플래그를 세울지

            public readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public OutgoingPacket(byte[] payload, WebSocketMessageType messageType,
                                  string description, bool resetUtteranceStateAfterSend)
            {
                Payload = payload;
                MessageType = messageType;
                Description = description;
                ResetUtteranceStateAfterSend = resetUtteranceStateAfterSend;
            }
        }

        /// <summary>type 만 먼저 읽기 위한 경량 구조체.</summary>
        [Serializable]
        public class ServerMessage
        {
            public string type;
        }

        [Serializable]
        public class SubtitleMessage
        {
            public string type;
            public string text;
        }

        [Serializable]
        public class FinalResponse
        {
            public string type;
            public TranscriptionData data;
        }

        /// <summary>전사 텍스트 + 워커가 되돌려주는 음성 피쳐.</summary>
        [Serializable]
        public class TranscriptionData
        {
            public string sttText;
            public bool truncated;
            public string utterance_id;
            public float speakingTime;
            public int pauseCount;
            public int meaningfulPauseCount;
            public float volumeVariance;
            public float lowVolumeRatio;
            public float averageVolume;
            public float responseTime;
        }

        /// <summary>저신뢰 교정 요청. 단어별 신뢰도가 함께 온다.</summary>
        [Serializable]
        public class CorrectionRequestMessage
        {
            public string type;
            public TranscriptionData data;
            public string[] words;
            public float[] word_confidences;
        }
    }
}
