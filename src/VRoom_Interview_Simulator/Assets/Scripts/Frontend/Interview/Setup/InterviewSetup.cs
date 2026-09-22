using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace VRoom.Backend
{
    /// <summary>
    /// SetupScene 컨트롤러. 면접 정보 txt 를 읽어 파싱하고, 백엔드 프리웜을 마친 뒤
    /// 면접 씬으로 넘어간다.
    ///
    /// [txt 형식] 대괄호 헤더로 섹션을 구분한다. 섹션 이름은 부분 일치로 인식한다.
    ///     [기업]
    ///     카카오
    ///     [직무]
    ///     백엔드 개발자
    ///     [이력서]
    ///     ...
    ///
    /// [시작 버튼이 열리는 조건]
    ///   파일 파싱 성공 AND 프리웜 완료. 프리웜 중에 넘어가면 첫 질문 음성이
    ///   준비되지 않아 씬 진입 후 수 초간 침묵한다.
    /// </summary>
    public class InterviewSetup : MonoBehaviour
    {
        // ===================================================================
        // 1. 참조 / 상태
        // ===================================================================
        [Header("파일 불러오기 버튼")]
        public Button loadFileButton;

        [Header("미리보기 텍스트")]
        public TMP_Text companyPreview;
        public TMP_Text jobPreview;
        public TMP_Text resumePreview;
        public TMP_Text conditionPreview;

        [Header("씬 이름")]
        public string interviewSceneName = "InterviewRoomScene";

        [Header("시작 버튼 / 안내")]
        public Button startButton;
        public TMP_Text warningText;

        [Header("백엔드 프리웜")]
        public BackendWarmup warmup;

        // ── 파싱 결과 ─────────────────────────────────────────────
        private string _company = "";
        private string _job = "";
        private string _resume = "";
        private string _condition = "";    // 실험 조건 A, B, C
        private bool _loaded = false;      // 파일 파싱에 성공했는가
        private bool _preparing = false;   // 프리웜 진행 중인가

        // ── 안내 문구 (두 줄을 따로 관리해 서로 덮어쓰지 않게 한다) ──
        private string _fileLine = "";     // 파일 불러오기 관련 메시지
        private string _statusLine = "";   // 프리웜 진행 상황 메시지

        // ===================================================================
        // 2. 수명
        // ===================================================================
        void Start()
        {
            loadFileButton.onClick.AddListener(OnLoadFile);
            startButton.onClick.AddListener(OnStart);
            startButton.interactable = false;

            _fileLine = "먼저 면접 정보 txt 파일을 불러와주세요.";
            _statusLine = "";
            Redraw();
            ClearPreview();

            if (warmup != null)
            {
                warmup.OnProgress += OnWarmupProgress;
                warmup.OnFinished += OnWarmupFinished;
            }
        }

        void OnDestroy()
        {
            if (warmup != null)
            {
                warmup.OnProgress -= OnWarmupProgress;
                warmup.OnFinished -= OnWarmupFinished;
            }
        }

        // ===================================================================
        // 3. 버튼 핸들러
        // ===================================================================
        void OnLoadFile()
        {
            string path = WindowsFileDialog.Open("면접 정보 txt 선택");
            if (string.IsNullOrEmpty(path))
                return;   // 사용자가 취소

            string raw;
            try
            {
                raw = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (System.Exception e)
            {
                SetWarning($"파일을 읽지 못했습니다: {e.Message}");
                return;
            }

            if (!TryParse(raw, out _company, out _job, out _resume, out _condition, out string err))
            {
                OnParseFailed(err);
                return;
            }

            OnParseSucceeded(Path.GetFileName(path));
        }

        void OnStart()
        {
            if (_preparing)
            {
                SetWarning("면접관 준비 중입니다. 잠시만 기다려주세요.");
                return;
            }

            if (!_loaded || string.IsNullOrEmpty(_company) || string.IsNullOrEmpty(_job))
            {
                SetWarning("기업과 직무가 채워진 파일을 먼저 불러오세요.");
                return;
            }

            // 씬 전환에도 값이 유지되도록 static 홀더에 옮겨 담는다.
            InterviewConfig.Company = _company;
            InterviewConfig.JobTitle = _job;
            InterviewConfig.Resume = _resume;
            InterviewConfig.Condition = _condition;
            InterviewConfig.IsReady = true;

            SceneManager.LoadScene(interviewSceneName);
        }

        // ===================================================================
        // 4. 파싱 결과 반영
        // ===================================================================
        private void OnParseFailed(string err)
        {
            _loaded = false;
            startButton.interactable = false;
            ClearPreview();
            SetWarning(err);
            _statusLine = "";
        }

        private void OnParseSucceeded(string fileName)
        {
            _loaded = true;
            ApplyPreview();
            SetWarning($"불러오기 완료: {fileName}");

            InterviewConfig.Company = _company;
            InterviewConfig.JobTitle = _job;
            InterviewConfig.Resume = _resume;
            InterviewConfig.Condition = _condition;
            InterviewConfig.IsReady = true;
            InterviewConfig.Prewarmed = false;   // 새 파일이므로 이전 프리웜 결과는 무효

            // 프리웜이 붙어 있으면 그게 끝날 때까지 시작 버튼을 잠근다.
            if (warmup != null)
            {
                _preparing = true;
                startButton.interactable = false;
                _statusLine = "";
                warmup.Prepare(_company, _job, _resume);
            }
            else
            {
                startButton.interactable = true;
            }
        }

        // ===================================================================
        // 5. txt 파싱 (순수 함수 — 테스트 가능)
        // ===================================================================
        private enum Section { None, Company, Job, Resume, Condition }

        /// <summary>
        /// 면접 정보 txt 를 파싱한다. [기업] [직무] [조건] 이 모두 있어야 성공이다.
        /// MonoBehaviour 상태를 건드리지 않으므로 단위 테스트에서 그대로 호출할 수 있다.
        /// </summary>
        public static bool TryParse(string raw, out string company, out string job,
                                    out string resume, out string condition, out string error)
        {
            company = job = resume = condition = "";
            error = "";

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "빈 파일입니다.";
                return false;
            }

            var sb = new Dictionary<Section, StringBuilder>
            {
                { Section.Company,   new StringBuilder() },
                { Section.Job,       new StringBuilder() },
                { Section.Resume,    new StringBuilder() },
                { Section.Condition, new StringBuilder() },
            };

            Section cur = Section.None;
            foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
            {
                string t = line.Trim();

                // [헤더] 줄이면 섹션을 전환하고 내용으로는 쓰지 않는다.
                if (t.StartsWith("[") && t.EndsWith("]") && t.Length >= 3)
                {
                    cur = Classify(t.Substring(1, t.Length - 2).Trim());
                    continue;
                }

                if (cur == Section.None)
                    continue;   // 첫 헤더 이전의 내용은 버린다

                if (sb[cur].Length > 0)
                    sb[cur].Append('\n');
                sb[cur].Append(line);   // Trim 하지 않은 원본을 넣어 들여쓰기를 보존한다
            }

            company = sb[Section.Company].ToString().Trim();
            job = sb[Section.Job].ToString().Trim();
            resume = sb[Section.Resume].ToString().Trim();
            condition = NormalizeCondition(sb[Section.Condition].ToString());

            if (string.IsNullOrEmpty(company) || string.IsNullOrEmpty(job))
            {
                error = "[기업]과 [직무] 섹션이 모두 필요합니다. 파일 형식을 확인하세요.";
                return false;
            }
            if (string.IsNullOrEmpty(condition))
            {
                error = "[조건] 섹션에 실험 조건 A / B / C 중 하나를 적어야 합니다.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// [조건] 섹션에서 A/B/C 한 글자를 뽑는다. "C", "조건 C", "C (개입)" 등을 허용한다.
        ///
        /// 단어 경계를 요구하는 이유: "CONDITION: B" 같은 표기에서 CONDITION 의 C 를
        /// 조건 C 로 오인식하면 개입 조건이 아닌 참가자에게 개입이 발동한다.
        /// </summary>
        private static string NormalizeCondition(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            string first = raw.Replace("\r\n", "\n").Split('\n')[0].ToUpperInvariant();
            var m = Regex.Match(first, @"(?<![A-Z])[ABC](?![A-Z])");
            return m.Success ? m.Value : "";
        }

        /// <summary>실험자 확인용 조건 설명. 참가자에게 보여선 안 된다.</summary>
        private static string ConditionLabel(string c) => c switch
        {
            "A" => "A — 정적 턴제",
            "B" => "B — 가변 페르소나",
            "C" => "C — 가변 페르소나 및 능동 개입",
            _ => "-",
        };

        /// <summary>헤더 문자열을 섹션으로 분류한다. 부분 일치라 표기 흔들림을 허용한다.</summary>
        private static Section Classify(string header)
        {
            if (header.Contains("기업") || header.Contains("회사"))
                return Section.Company;
            if (header.Contains("직무") || header.Contains("직군") || header.Contains("포지션"))
                return Section.Job;
            if (header.Contains("이력") || header.Contains("자기소개") || header.Contains("경력"))
                return Section.Resume;
            if (header.Contains("조건") || header.Contains("실험"))
                return Section.Condition;

            return Section.None;
        }

        // ===================================================================
        // 6. 프리웜 콜백
        // ===================================================================
        private void OnWarmupProgress(string msg)
        {
            _statusLine = msg ?? "";
            Redraw();
        }

        private void OnWarmupFinished(bool ok, string msg)
        {
            _preparing = false;
            _statusLine = msg;
            Redraw();

            // 프리웜에 실패해도 시작은 가능하다. 첫 발화가 느려질 뿐이다.
            startButton.interactable = _loaded;
            if (!ok)
                Debug.LogWarning($"[InterviewSetup] 프리웜 실패: {msg}");
        }

        // ===================================================================
        // 7. UI 표시
        // ===================================================================
        private void ApplyPreview()
        {
            if (companyPreview) companyPreview.text = _company;
            if (jobPreview) jobPreview.text = _job;
            if (resumePreview)
                resumePreview.text = string.IsNullOrEmpty(_resume) ? "(이력서 없음)" : _resume;
            if (conditionPreview) conditionPreview.text = ConditionLabel(_condition);
        }

        private void ClearPreview()
        {
            if (companyPreview) companyPreview.text = "-";
            if (jobPreview) jobPreview.text = "-";
            if (resumePreview) resumePreview.text = "-";
            if (conditionPreview) conditionPreview.text = "-";
        }

        private void SetWarning(string msg)
        {
            _fileLine = msg ?? "";
            Redraw();
        }

        /// <summary>파일 메시지와 프리웜 메시지를 합쳐 한 텍스트에 표시한다.</summary>
        private void Redraw()
        {
            if (warningText == null) return;

            if (string.IsNullOrEmpty(_statusLine))
                warningText.text = _fileLine;
            else if (string.IsNullOrEmpty(_fileLine))
                warningText.text = _statusLine;
            else
                warningText.text = $"{_fileLine}\n{_statusLine}";
        }
    }
}