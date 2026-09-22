using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// 단어 칩을 왼쪽부터 채우며 줄바꿈하는 간이 레이아웃.
    ///
    /// [왜 HorizontalLayoutGroup 을 쓰지 않는가]
    ///   Unity 기본 레이아웃 그룹은 자동 줄바꿈(flow)을 지원하지 않는다.
    ///   칩 너비가 단어마다 제각각이므로 직접 계산해 배치한다.
    ///
    /// [UI 좌표계 주의]
    ///   아래로 갈수록 Y 가 **작아진다**. 줄을 내릴 때 마이너스 연산을 쓴다.
    ///
    /// [호출 순서]
    ///   Initialize() -> AddChips() * N -> UpdateHeight()
    ///   AddChips 전에 Canvas.ForceUpdateCanvases() 로 칩 너비가 확정되어야 한다.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class WordLayout : MonoBehaviour
    {
        [SerializeField] private float verticalGap = 50;      // 줄 간격 (= 한 줄 높이)
        [SerializeField] private float horizontalGap = 10;    // 칩 사이 간격
        [SerializeField] private float leftPadding = 0;
        [SerializeField] private float rightPadding = 0;
        [SerializeField] private float verticalPadding = 0;

        private Vector2 positionToInstantiate;   // 다음 칩을 놓을 위치
        private float layoutWidth;
        private RectTransform rectTransform;
        private float initialHeight;             // 에디터에 설정된 기본 높이 (복원용)

        private void Start()
        {
            rectTransform = GetComponent<RectTransform>();
            initialHeight = rectTransform.rect.height;
            Initialize();
        }

        /// <summary>배치 좌표와 높이를 초기 상태로 되돌린다. 칩을 깔기 전에 호출한다.</summary>
        public void Initialize()
        {
            if (rectTransform == null)
                rectTransform = GetComponent<RectTransform>();

            // UI 좌표계는 아래로 갈수록 Y 가 작아지므로 패딩을 뺀다.
            positionToInstantiate = new Vector2(leftPadding, -verticalPadding);

            // 이전 교정에서 늘어난 높이를 원래대로 복원한다.
            if (initialHeight > 0)
                rectTransform.sizeDelta = new Vector2(rectTransform.sizeDelta.x, initialHeight);
        }

        /// <summary>칩 하나를 다음 자리에 놓는다. 줄이 넘치면 자동으로 줄바꿈한다.</summary>
        public void AddChips(WordChip chip)
        {
            // Start 시점에는 레이아웃이 확정되지 않아 0 이 나올 수 있어 매번 읽는다.
            layoutWidth = rectTransform.rect.width;

            RectTransform chipRect = chip.GetComponent<RectTransform>();
            float chipWidth = chipRect.rect.width;

            // 오른쪽 끝을 넘으면 줄바꿈
            if ((positionToInstantiate.x + chipWidth) > (layoutWidth - rightPadding))
            {
                positionToInstantiate.x = leftPadding;
                positionToInstantiate.y -= verticalGap;
            }

            chipRect.localPosition = positionToInstantiate;

            // 배치한 뒤에 다음 자리를 계산한다 (순서가 바뀌면 한 칸씩 밀린다).
            positionToInstantiate.x += (chipWidth + horizontalGap);
        }

        /// <summary>
        /// 배치가 끝난 뒤 Content 높이를 실제 필요한 만큼 늘린다.
        /// 이걸 호출하지 않으면 칩이 영역 밖으로 나가도 스크롤이 생기지 않는다.
        /// </summary>
        public void UpdateHeight()
        {
            // Y 는 음수이므로 절대값으로 바꾼 뒤 마지막 줄 높이와 하단 여백을 더한다.
            float neededHeight = Mathf.Abs(positionToInstantiate.y) + verticalGap + verticalPadding;

            if (neededHeight > rectTransform.rect.height)
                rectTransform.sizeDelta = new Vector2(rectTransform.sizeDelta.x, neededHeight);
        }

        /// <summary>칩을 지운 뒤 좌표를 초기 상태로 되돌린다.</summary>
        public void ClearChips() => Initialize();
    }
}