using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerbalProcess
{
    /// <summary>
    /// 교정 패널의 단어 칩 하나. 클릭으로 선택하고, 신뢰도에 따라 색이 달라진다.
    ///
    /// [4가지 시각 상태]
    ///   일반 / 미선택      흰 배경 + 검은 글자
    ///   일반 / 선택        파란 배경 + 흰 글자
    ///   저신뢰 / 미선택    붉은 반투명 배경 + 붉은 글자   <- 여기를 고치라는 신호
    ///   저신뢰 / 선택      주황 배경 + 흰 글자
    ///
    /// 선택 판단은 스스로 하지 않고 패널에 위임한다. 범위 선택 규칙이
    /// 다른 칩들의 상태에 의존하기 때문이다.
    /// </summary>
    public class WordChip : MonoBehaviour, IPointerDownHandler
    {
        [Header("UI References")]
        [SerializeField] private TextMeshProUGUI wordText;
        [SerializeField] private Image background;

        [Header("Colors")]
        [SerializeField] private Color normalColor = Color.white;
        [SerializeField] private Color selectedColor = new Color(0.2f, 0.6f, 1.0f, 1.0f);
        [SerializeField] private Color lowConfidenceColor = new Color(1.0f, 0.3f, 0.3f, 0.3f);
        [SerializeField] private Color lowConfidenceSelectedColor = new Color(1.0f, 0.5f, 0.2f, 1.0f);

        /// <summary>전사 결과에서의 단어 순번. 선택 범위 계산의 기준이다.</summary>
        public int Index { get; private set; }
        public string Word { get; private set; }
        public float Confidence { get; private set; }
        public bool IsSelected { get; private set; }

        private SubtitleCorrectionPanel _panel;
        private bool _isLowConfidence = false;

        /// <summary>패널이 칩을 생성한 직후 1회 호출한다.</summary>
        public void Setup(SubtitleCorrectionPanel panel, int index, string word,
                          float confidence, float lowConfidenceThreshold = 0.6f)
        {
            _panel = panel;
            Index = index;
            Word = word;
            Confidence = confidence;
            IsSelected = false;

            if (wordText != null) wordText.text = word;

            _isLowConfidence = confidence < lowConfidenceThreshold;
            UpdateVisuals();
        }

        /// <summary>패널이 범위를 계산한 뒤 각 칩에 결과를 알려준다.</summary>
        public void SetSelected(bool isSelected)
        {
            IsSelected = isSelected;
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            if (background == null) return;

            if (IsSelected)
            {
                background.color = _isLowConfidence ? lowConfidenceSelectedColor : selectedColor;
                if (wordText != null) wordText.color = Color.white;
            }
            else
            {
                background.color = _isLowConfidence ? lowConfidenceColor : normalColor;
                if (wordText != null) wordText.color = _isLowConfidence ? Color.red : Color.black;
            }
        }

        // OnPointerClick 이 아니라 OnPointerDown 을 쓰는 이유:
        // 스크롤 가능한 영역이라 드래그가 섞이면 클릭이 씹힌다.
        public void OnPointerDown(PointerEventData eventData)
        {
            _panel?.OnChipPointerDown(this);
        }
    }
}