using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace VRoom.Backend
{
    /// <summary>
    /// [테스트 전용] STT 없이 사용자 답변을 흉내 내는 디버그 패널.
    /// 화면에 입력칸과 버튼이 떠서, 타이핑한 답변을 백엔드 /process 로 POST 한다.
    /// 그러면 백엔드가 채점 -> 다음 질문 패킷을 /ws/control 로 내려보내고,
    /// InterviewerDriver 가 면접관 Animator(Expression_ID/Gesture_ID)를 움직인다.
    ///
    /// 사용법: 아무 GameObject 에 이 컴포넌트를 부착하고 Play. (씬 UI 설정 불필요)
    ///
    /// 주의: /process 는 레거시 HTTP 경로다. 실제 STT 워커는 /ws/tts 를 쓰므로,
    /// 이 패널로 보낸 답변은 개입(barge-in) 경로를 타지 않는다.
    /// 개입 검증에는 쓸 수 없고 페르소나 전이 확인 용도로만 유효하다.
    ///
    /// 실험 세션에서는 반드시 비활성화할 것. OnGUI 패널이 화면에 남으면
    /// 참가자 시선이 그쪽으로 쏠려 시선 지표가 오염된다.
    /// </summary>
    public class InterviewDebugInput : MonoBehaviour
    {
        public string backendHttp = "http://127.0.0.1:8080/process";
        public string sessionId = "default";   // BackendControlClient 의 sessionId 와 동일하게

        private string _answer = "안녕하세요, 저는 3년차 백엔드 개발자입니다.";
        private string _status = "";

        private void OnGUI()
        {
            // 화면 좌상단에 간단한 입력 패널
            GUILayout.BeginArea(new Rect(20, 20, 460, 160), GUI.skin.box);
            GUILayout.Label("면접 답변 테스트 입력 (STT 대체)");
            _answer = GUILayout.TextField(_answer, GUILayout.Height(50));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("답변 전송", GUILayout.Height(30)))
                StartCoroutine(SendAnswer(_answer));
            if (GUILayout.Button("낮은 점수용 답변", GUILayout.Height(30)))
                StartCoroutine(SendAnswer("음... 잘 모르겠습니다. 그냥 그렇게 했습니다."));
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_status))
                GUILayout.Label(_status);
            GUILayout.EndArea();
        }

        private IEnumerator SendAnswer(string text)
        {
            _status = "전송 중...";
            // features 는 STT 워커가 보낼 값을 흉내 낸 더미
            string json =
                "{\"session_id\":\"" + sessionId + "\"," +
                "\"text\":\"" + Escape(text) + "\"," +
                "\"features\":{\"speakingTime\":12.0,\"pauseCount\":1,\"averageVolume\":0.05}}";

            using var req = new UnityWebRequest(backendHttp, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
                _status = "응답: " + req.downloadHandler.text;   // {"ok":true,"stage":...}
            else
                _status = "에러: " + req.error + " / " + req.downloadHandler.text;
        }

        private static string Escape(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
    }
}