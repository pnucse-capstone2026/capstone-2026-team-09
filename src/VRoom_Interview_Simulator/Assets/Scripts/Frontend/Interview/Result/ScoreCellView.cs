using TMPro;
using UnityEngine;

namespace VRoom.Backend
{
    /// <summary>
    /// 결과 화면의 점수 셀 하나. "1. 시선 처리   8/10" 형태로 표시하고
    /// 점수 구간에 따라 색을 바꾼다.
    ///
    /// 파일명은 ScrollCellView.cs 인데 클래스명은 ScoreCellView 다.
    /// Unity 는 MonoBehaviour 의 파일명과 클래스명이 같아야 하는 것이 원칙이므로
    /// 언젠가 맞춰야 하지만, 프리팹 참조가 깨질 수 있어 별도 작업으로 남겨 둔다.
    /// </summary>
    public class ScoreCellView : MonoBehaviour
    {
        public TMP_Text labelText;   // "1. 시선 처리"
        public TMP_Text valueText;   // "8/10" (색상도 여기서 바뀐다)

        /// <summary>셀 내용을 채운다. ResultUI 가 항목마다 한 번씩 호출한다.</summary>
        public void Set(int index, string label, int value, int max = 10)
        {
            value = Mathf.Clamp(value, 0, max);

            if (labelText)
                labelText.text = $"{index}. {label}";

            if (valueText)
            {
                valueText.text = $"{value}/{max}";
                valueText.color = ColorFor(value, max);
            }
        }

        /// <summary>80% 이상 초록, 50% 이상 주황, 그 미만 빨강.</summary>
        private static Color ColorFor(int v, int max)
        {
            float r = max <= 0 ? 0f : (float)v / max;

            if (r >= 0.8f) return new Color(0.20f, 0.60f, 0.25f);   // 초록
            if (r >= 0.5f) return new Color(0.85f, 0.55f, 0.00f);   // 주황
            return new Color(0.80f, 0.20f, 0.18f);                  // 빨강
        }
    }
}