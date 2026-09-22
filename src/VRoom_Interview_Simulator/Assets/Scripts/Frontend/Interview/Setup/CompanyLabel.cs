using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace VRoom.Backend
{
    /// <summary>
    /// TV/안내판 등에 표시되는 "XX 회사 면접" 텍스트를
    /// SetupScene 에서 불러온 txt 의 [기업]/[직무] 값으로 치환한다.
    /// TMP_Text(월드 스페이스 TextMeshPro / UI TextMeshProUGUI) 모두 지원한다.
    ///
    /// 기업명 길이가 제각각이라 줄바꿈이 생기면 안내판 레이아웃이 무너진다.
    /// 그래서 Wrapping 을 끄고 AutoSize 를 켜서 '한 줄 유지 + 폰트 축소'로 처리한다.
    /// 인스펙터에서 잊고 설정하지 않는 일이 잦아 코드로 강제한다.
    /// </summary>
    [DisallowMultipleComponent]
    public class CompanyLabel : MonoBehaviour
    {
        [Header("대상 (비우면 같은 오브젝트에서 자동 탐색)")]
        [SerializeField] private TMP_Text target;

        [Header("표시 형식  {0}=기업명, {1}=직무")]
        [Tooltip("예: \"{0} 면접\"  /  \"{0} {1} 직무 면접\"  /  \"{0}\\n{1} 직무 면접\"")]
        [SerializeField] private string format = "{0} {1} 신입 공채 면접";

        [Header("값이 없을 때 대체 문구")]
        [SerializeField] private string fallbackCompany = "XX 회사";
        [SerializeField] private string fallbackJob = "지원";

        [Header("한 줄 자동 축소 (인스펙터 설정을 코드로 강제)")]
        [Tooltip("켜면 Start 시 Wrapping=Off + AutoSize=On 을 강제 적용한다.")]
        [SerializeField] private bool enforceSingleLineAutoFit = true;
        [SerializeField] private float fontSizeMin = 8f;
        [SerializeField] private float fontSizeMax = 72f;

        // 기업명이나 직무가 비었을 때 format 결과에 공백이 겹치는 것을 정리한다.
        private static readonly Regex MultiSpace = new Regex(@"\s{2,}");

        private void Awake()
        {
            if (target == null)
                target = GetComponent<TMP_Text>();
        }

        // Awake 가 아니라 Start 에서 Apply 하는 이유:
        // InterviewConfig 값이 씬 로드 직후에 채워지므로 한 프레임 뒤에 읽어야 안전하다.
        private void Start()
        {
            if (enforceSingleLineAutoFit)
                ApplyAutoFitSettings();
            Apply();
        }

        /// <summary>Wrapping 끄고 Auto Size 켜기 = 길어지면 한 줄에서 폰트만 작아짐.</summary>
        public void ApplyAutoFitSettings()
        {
            if (target == null)
                return;

            target.overflowMode = TextOverflowModes.Overflow;
            target.enableAutoSizing = true;
            target.fontSizeMin = fontSizeMin;
            target.fontSizeMax = fontSizeMax;
        }

        /// <summary>런타임 중 값이 바뀌면 외부에서 다시 호출할 수 있다.</summary>
        public void Apply()
        {
            if (target == null)
            {
                Debug.LogWarning("[CompanyLabel] TMP_Text 대상이 없습니다.", this);
                return;
            }

            string company = InterviewConfig.CompanyShort;
            if (string.IsNullOrWhiteSpace(company))
                company = fallbackCompany;

            string job = InterviewConfig.JobTitleShort;
            if (string.IsNullOrWhiteSpace(job))
                job = fallbackJob;

            string text = string.Format(format, company, job);
            text = MultiSpace.Replace(text, " ").Trim();

            target.text = text;
            Debug.Log($"[CompanyLabel] \"{text}\"");
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (target == null)
                target = GetComponent<TMP_Text>();
        }
#endif
    }
}