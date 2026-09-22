namespace VerbalProcess
{
    /// <summary>
    /// 턴 진행 상태. **이 열거형 하나가 마이크·Speaker·Vision 턴 경계를 전부 결정한다.**
    ///
    /// 개입 기능이 들어오면서 상태 조합이 폭발했기 때문에, 각 컴포넌트가 자기 상태를
    /// 따로 들고 있으면 반드시 어긋난다. PipelineController.SetTurnState() 만이
    /// 이 값을 바꿀 수 있고, 나머지는 전부 여기서 파생된다.
    /// </summary>
    public enum TurnState
    {
        Idle,                // 세션 시작 전
        InterviewerSpeaking, // 질문 / 개입 후속 질문 재생 중
        UserAnswering,       // 사용자 답변 수신 중 (유일하게 STT 로 청크를 보내는 상태)
        BargeInPending,      // 개입 확정, 컷인 재생 중, 개입 대사 도착 대기
        Interrupting,        // 개입 대사 재생 중
        Correcting,          // 저신뢰 STT 교정 패널 열림
        Finished             // 결과 화면
    }

    /// <summary>
    /// 마이크 3-모드.
    ///
    /// Monitoring 이 핵심이다. 개입 중에도 마이크를 완전히 끄면 사용자가 언제
    /// 입을 다물었는지 알 수 없어 '양보 시간'을 측정할 수 없다.
    /// 그래서 관측은 계속하되 STT 로는 보내지 않는 중간 모드가 필요하다.
    /// </summary>
    public enum MicMode
    {
        Off,           // 아무것도 하지 않음
        Monitoring,    // RMS 는 계산하되 STT 로 보내지 않음
        Transmitting   // 정상 답변 수신. 청크를 STT 로 전송
    }

    /// <summary>중간보고서의 "경청/말하기/끼어들기" 3축과 1:1 대응. 로그 기록용.</summary>
    public enum InterviewerSpeechState { Listening, Speaking, Interrupting }

    /// <summary>
    /// 웹캠 턴 위상. **vision_process 및 백엔드의 문자열과 정확히 일치해야 한다.**
    /// 오타가 나면 백엔드 SCORED_PHASES 필터에 걸려 해당 턴이 채점에서 통째로 빠진다.
    /// </summary>
    public static class TurnPhase
    {
        public const string Normal = "NORMAL";        // 개입 없는 일반 답변 -> 채점 대상
        public const string Truncated = "TRUNCATED";  // 개입으로 잘린 답변   -> 로그 전용
        public const string Reaction = "REACTION";    // 개입 직후 반응 구간   -> 로그 전용, 핵심 종속변인
        public const string Reanswer = "REANSWER";    // Type A 재답변        -> 채점 대상
    }
}