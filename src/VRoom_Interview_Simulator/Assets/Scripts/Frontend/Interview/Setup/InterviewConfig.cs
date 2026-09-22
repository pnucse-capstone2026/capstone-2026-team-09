namespace VRoom.Backend
{
    /// <summary>
    /// SetupScene 에서 입력한 지원 정보를 면접 씬으로 넘기기 위한 static 홀더.
    ///
    /// static 이라 씬 전환에도 값이 유지된다. MonoBehaviour 가 아니므로
    /// 씬에 붙이지 않는다(파일만 존재하면 된다).
    ///
    /// 주의: 애플리케이션이 살아 있는 동안 값이 남는다. SetupScene 으로 되돌아와
    /// 다른 파일을 불러오면 Prewarmed 를 false 로 되돌려야 한다(InterviewSetup 이 처리).
    /// </summary>
    public static class InterviewConfig
    {
        public static string Company = "";            // 지원 기업 (원문. 여러 줄일 수 있다)
        public static string JobTitle = "";           // 지원 직무 (원문)
        public static string Resume = "";             // 이력서 원문
        public static bool IsReady = false;           // 파일 파싱에 성공했는가
        public static string SessionId = "default";   // 백엔드 세션 식별자
        public static bool Prewarmed = false;         // 첫 질문 음성이 백엔드에 캐시돼 있는가
        public static string Condition = ""; // 실험 조건 A, B, C

        /// <summary>여러 줄로 입력된 기업명의 첫 줄만. 화면 라벨 표시용.</summary>
        public static string CompanyShort => FirstLine(Company);

        /// <summary>여러 줄로 입력된 직무의 첫 줄만. 화면 라벨 표시용.</summary>
        public static string JobTitleShort => FirstLine(JobTitle);

        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return s.Trim().Split('\n')[0].Trim();
        }
    }
}