using UnityEngine;

/// <summary>
/// 애니메이션 클립마다 캐릭터의 착석 위치를 보정하는 StateMachineBehaviour.
///
/// ActorCore 등에서 받은 모션은 클립마다 기준 원점이 조금씩 다르다.
/// 그대로 재생하면 클립이 바뀔 때 캐릭터가 의자에서 떠오르거나 파묻힌다.
/// 이 컴포넌트를 Animator State 에 붙이고 클립별 오프셋을 지정하면
/// 해당 State 로 진입하는 순간 위치를 맞춰 준다.
///
/// 사용법: Animator 창에서 State 선택 -> Add Behaviour -> SeatOffset
///
/// 주의: 이 파일은 원래 CP949(EUC-KR)로 저장돼 있었다. Unity 는 읽지만
/// git diff 와 타 플랫폼에서 깨지므로 UTF-8 로 바꿨다.
/// </summary>
public class SeatOffset : StateMachineBehaviour
{
    public Vector3 localPosition;      // 이 클립에 맞는 위치
    public Vector3 localEulerAngles;   // 필요하면 미세 회전

    public override void OnStateEnter(Animator animator, AnimatorStateInfo info, int layer)
    {
        animator.transform.localPosition = localPosition;
        animator.transform.localEulerAngles = localEulerAngles;
    }
}