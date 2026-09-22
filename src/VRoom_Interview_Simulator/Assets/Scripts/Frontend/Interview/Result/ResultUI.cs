using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

namespace VRoom.Backend
{
    /// <summary>
    /// 면접 결과 화면. 10개 항목을 좌우 두 열(5개씩)에 셀로 깔고 총점과 총평을 표시한다.
    ///
    /// 셀은 프리팹을 매번 Instantiate 한다. 항목 수가 10개로 고정이라
    /// 오브젝트 풀을 쓸 이유가 없고, 재진입 시 이전 셀만 지우면 된다.
    /// </summary>
    public class ResultUI : MonoBehaviour
    {
        // ===================================================================
        // 1. 참조 / 상수
        // ===================================================================
        [Header("Root")]
        public GameObject panelRoot;
        public TMP_Text titleText;

        [Header("점수 열")]
        public Transform leftColumn;          // 1~5번 항목
        public Transform rightColumn;         // 6~10번 항목
        public GameObject scoreCellPrefab;    // ScoreCellView 가 붙어 있어야 한다

        [Header("하단")]
        public TMP_Text totalText;
        public TMP_Text summaryText;
        public Button homeButton;

        [Header("씬")]
        public string homeSceneName = "SetupScene";

        /// <summary>
        /// 화면 표시용 항목 이름. 순서가 ToArray() 의 반환 순서와 정확히 대응해야 한다.
        /// 백엔드 InterviewScore 의 필드 순서와도 맞춰 두었다.
        /// </summary>
        private static readonly string[] Labels =
        {
            "시선 처리", "손 사용", "자세 안정성", "표정 변화", "답변 목소리 크기",
            "답변 목소리 빠르기", "답변 길이", "답변 내 필러 단어 여부", "답변의 정확도", "답변 반응 속도"
        };

        // ===================================================================
        // 2. 수명
        // ===================================================================
        void Awake()
        {
            // 면접 중에는 숨어 있다가 Show() 로만 나타난다.
            if (panelRoot) panelRoot.SetActive(false);
            if (homeButton) homeButton.onClick.AddListener(GoHome);
        }

        // ===================================================================
        // 3. 표시
        // ===================================================================
        /// <summary>피드백 리포트를 화면에 반영한다. InterviewerDriver 가 호출한다.</summary>
        public void Show(FeedbackReport r)
        {
            if (r == null) return;

            if (panelRoot) panelRoot.SetActive(true);
            if (titleText) titleText.text = "면접 결과";

            int[] values = ToArray(r.scores);

            // 백엔드가 총점을 못 보냈을 때만 항목 합으로 대체한다.
            // (웹캠 미사용 세션은 백엔드가 60점을 100점으로 환산해 보내므로 그 값을 써야 한다)
            int total = r.overall_score;
            if (total <= 0)
            {
                total = 0;
                foreach (var v in values) total += v;
            }

            ClearCells();
            for (int i = 0; i < Labels.Length; i++)
            {
                Transform parent = (i < 5) ? leftColumn : rightColumn;
                if (parent == null || scoreCellPrefab == null) continue;

                var go = Instantiate(scoreCellPrefab, parent);
                go.SetActive(true);   // 프리팹이 비활성 상태로 저장돼 있을 수 있다

                var cell = go.GetComponent<ScoreCellView>();
                if (cell) cell.Set(i + 1, Labels[i], values[i], 10);
            }

            if (totalText) totalText.text = $"총점: {total}/100";
            if (summaryText) summaryText.text = string.IsNullOrEmpty(r.summary) ? "" : r.summary;
        }

        public void Hide()
        {
            if (panelRoot) panelRoot.SetActive(false);
        }

        private void GoHome()
        {
            if (!string.IsNullOrEmpty(homeSceneName))
                SceneManager.LoadScene(homeSceneName);
        }

        // ===================================================================
        // 4. 내부 유틸
        // ===================================================================
        /// <summary>InterviewScore 를 Labels 와 같은 순서의 배열로 편다.</summary>
        private static int[] ToArray(InterviewScore s)
        {
            if (s == null) return new int[10];

            return new[]
            {
                s.gaze, s.gesture, s.posture, s.expression, s.voiceVolume,
                s.voiceSpeed, s.answerLength, s.fillerWords, s.accuracy, s.responseTime
            };
        }

        private void ClearCells()
        {
            ClearChildren(leftColumn);
            ClearChildren(rightColumn);
        }

        /// <summary>자식을 역순으로 지운다. 순방향으로 지우면 인덱스가 밀린다.</summary>
        private static void ClearChildren(Transform parent)
        {
            if (parent == null) return;

            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);   // 편집 모드에서는 즉시 삭제해야 한다
            }
        }

        /// <summary>[에디터 전용] 면접을 돌리지 않고 레이아웃만 확인할 때 쓴다.</summary>
        [ContextMenu("Show Sample")]
        private void ShowSample()
        {
            Show(new FeedbackReport
            {
                scores = new InterviewScore
                {
                    gaze = 10,
                    gesture = 8,
                    posture = 9,
                    expression = 7,
                    voiceVolume = 10,
                    voiceSpeed = 6,
                    answerLength = 8,
                    fillerWords = 5,
                    accuracy = 9,
                    responseTime = 7
                },
                summary = "전반적으로 안정적이었으나 필러 단어와 발화 속도에서 개선이 필요합니다."
            });
        }
    }
}