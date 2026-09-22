using System;

namespace VRoom.Backend
{
    // =======================================================================
    // 백엔드 <-> Unity 통신 스키마 모음
    //
    // 이 파일은 '계약서'다. 모든 필드는 백엔드 app/domain.py 와 1:1로 맞춰야 한다.
    //
    // ★ JsonUtility 는 클래스에 없는 필드를 예외 없이 조용히 무시한다.
    //   백엔드에만 필드를 추가하면 값이 사라지는데 에러도 로그도 나지 않는다.
    //   백엔드 domain.py 를 고칠 때는 반드시 이 파일도 함께 고칠 것.
    //
    // 대응 관계:
    //   BehaviorPacket   <-> domain.py  BehaviorPacket
    //   InterviewScore   <-> domain.py  InterviewScore
    //   FeedbackReport   <-> domain.py  FeedbackReport
    //   BargeInCutin     <-> bargein.py build_cutin_message()
    // =======================================================================

    /// <summary>
    /// 백엔드 -> Unity 행동 지시 패킷. 면접관의 한 턴을 통째로 기술한다.
    ///
    /// 도착 순서: 이 패킷이 먼저 오고, 잠시 뒤 같은 대사의 음성이 흘러온다.
    /// 몸짓과 표정을 소리보다 앞서 세우기 위한 순서다.
    /// </summary>
    [Serializable]
    public class BehaviorPacket
    {
        /// <summary>
        /// "interviewer_turn" = 정상 발화 (대사 있음)
        /// "thinking"         = 생각 중 더미 모션 (대사 없음, 재생 안 함)
        /// "ignored"          = 처리하지 말 것 (Type A 잘린 답변 흡수 등)
        /// </summary>
        public string type;

        public string session_id;
        public string stage;         // SELF_INTRO / TECH_Q1 / FOLLOWUP_1 / FOLLOWUP_2 / BEHAVIORAL / CLOSING / DONE
        public string persona;       // POSITIVE / NEUTRAL / NEGATIVE
        public float persona_value;  // 연속 감정 강도 -1.0(부정) ~ +1.0(긍정) -> Animator Emotion 축
        public string dialogue;      // 면접관 대사. 자막 표시 + TTS 입력으로 함께 쓰인다
        public int expression_id;    // 얼굴 표정 ID -> InterviewerExpression.Apply()
        public int gesture_id;       // 제스처 ID. 현재 Unity 에서는 사용하지 않는다(리소스 미확보)
        public int score;            // 직전 답변 점수 0~100 (-1 = 채점 대상 아님)
        public bool is_final;        // true 면 마무리 멘트. 재생 완료 후 피드백을 요청한다

        /// <summary>
        /// 개입 관련 태그. 빈 문자열이면 일반 턴이다.
        ///   "CUTOFF"            = Type B 발화 1 (개입 대사)
        ///   "CUTOFF_QUESTION"   = Type B 발화 2 (개입 후 다음 질문)
        ///   "REDIRECT"          = Type A 개입 대사
        ///   "REDIRECT_REANSWER" = Type A 재답변 이후의 다음 질문
        /// </summary>
        public string bargein_type;
    }

    /// <summary>
    /// 백엔드 -> Unity 컷인 반사 명령. 대사가 없고 '즉시 도착'만이 목적이다.
    ///
    /// 개입 대사는 TTS 합성에 1초 남짓 걸리는데, 그동안 화면에 아무 변화가 없으면
    /// 시스템이 멈춘 것처럼 보인다. 이 메시지는 대사 생성을 기다리지 않고 먼저 나가서
    /// Unity 의 상태 전이 / 발화 강제 확정 / 표정 변경을 트리거한다.
    /// </summary>
    [Serializable]
    public class BargeInCutin
    {
        public string type;
        public string session_id;
        public string bargein_type;   // "REDIRECT"(주제 이탈) | "CUTOFF"(길이/침묵)
        public string reason;         // "OFF_TOPIC" | "LONG_ANSWER" | "LONG_SILENCE"
        public int expression_id;     // 개입 표정 (5 = FIRM_STOP)
        public int gesture_id;        // 개입 제스처 (3 = ARMS_CROSSED). 현재 미사용
    }

    /// <summary> 10개 평가 항목, 각 0~10점. ResultUI 의 셀 순서와 대응한다. </summary>
    [Serializable]
    public class InterviewScore
    {
        // ── 시각 4항목 (웹캠. Vision 워커 미연결 시 0) ────────────
        public int gaze;         // 1. 시선 처리
        public int gesture;      // 2. 손 사용
        public int posture;      // 3. 자세 안정성
        public int expression;   // 4. 표정 변화

        // ── 음성 6항목 ────────────────────────────────────────────
        public int voiceVolume;  // 5. 답변 목소리 크기
        public int voiceSpeed;   // 6. 답변 목소리 빠르기
        public int answerLength; // 7. 답변 길이
        public int fillerWords;  // 8. 답변 내 필러 단어 여부
        public int accuracy;     // 9. 답변의 정확도 (LLM 채점 평균)
        public int responseTime; // 10. 답변 반응 속도
    }

    /// <summary> 단계별 답변 점수 한 건. </summary>
    [Serializable]
    public class StageScore
    {
        public string stage;   // SELF_INTRO / TECH_Q1 / ...
        public int score;      // 0~100
    }

    /// <summary>
    /// 면접 종료 후 결과 UI 시각화용 종합 피드백.
    ///
    /// 주의: 아래 다섯 필드(stage_scores, strengths, improvements,
    /// avg_speaking_time, total_pauses)는 백엔드가 계속 보내고 있었는데
    /// 이 클래스에 선언이 없어 JsonUtility 가 조용히 버리고 있었다.
    /// 지금은 받아둔다. ResultUI 가 아직 화면에 쓰지는 않는다.
    /// </summary>
    [Serializable]
    public class FeedbackReport
    {
        public string type;
        public string session_id;
        public InterviewScore scores;        // 10항목 세부 점수
        public int overall_score;            // 종합 점수 (100점 만점)
        public StageScore[] stage_scores;    // 단계별 답변 점수
        public string strengths;             // 강점 (LLM 총평)
        public string improvements;          // 개선점 (LLM 총평)
        public string summary;               // 총평 2문장
        public float avg_speaking_time;      // 평균 발화 시간(초)
        public int total_pauses;             // 총 의미 있는 침묵 횟수
    }

    /// <summary>
    /// type 필드만 먼저 읽어 어떤 패킷인지 판별하기 위한 경량 구조체.
    /// 한 채널로 여러 종류의 JSON 이 오기 때문에 2단계 파싱이 필요하다.
    /// </summary>
    [Serializable]
    public class ServerMessage
    {
        public string type;
    }
}