"""웹캠 지표 검증 도구 — 서버 없이 MediaPipe 출력을 눈으로 확인한다.

임계값(GAZE_YAW_TOL, HAND_HEIGHT_LIMIT 등)을 실제 하드웨어에 맞춰 조정할 때,
그리고 analyzer.TRANSPOSE_HEAD_MATRIX 부호를 확인할 때 쓴다.

  [c] 3초 캘리브레이션   [s] 턴 시작   [e] 턴 종료(피쳐 출력)   [q] 종료

주의: Unity 나 server.py 가 웹캠을 잡고 있으면 열리지 않는다. 먼저 종료할 것.
운영(server.py)과 동일하게 10fps 로 스로틀하므로 실측 fps 도 함께 비교할 수 있다.
"""
import time

import cv2

from aggregator import SessionAggregator
from analyzer import FrameAnalyzer

# ── 설정 ────────────────────────────────────────────────────────────
CAM_INDEX = 0           # 카메라 인덱스. 안 열리면 list_cameras.py 로 확인
SHOW_WINDOW = True      # 미리보기 창 표시 여부 (끄면 키 입력도 안 받는다)
FPS = 10.0              # 분석 FPS. server.py 의 CAM_FPS 와 맞춰야 의미가 있다
INTERVAL = 1.0 / FPS
CALIBRATION_SEC = 3.0   # 캘리브레이션 수집 시간


def open_camera(index: int):
    """웹캠을 열고 해상도를 서버와 동일하게 맞춘다."""
    cap = cv2.VideoCapture(index, cv2.CAP_DSHOW)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    if not cap.isOpened():
        raise SystemExit(f"웹캠 {index} 를 열 수 없습니다. CAM_INDEX 를 1, 2 로 바꿔보세요.")
    return cap


def print_frame(mode: str, f: dict):
    """프레임 원시값을 한 줄로 출력한다. 각도 부호 검증에 쓴다."""
    if f.get("face"):
        print(f"[{mode}] yaw={f['yaw']:+6.1f} pitch={f['pitch']:+6.1f} "
              f"eye_h={f['eye_h']:.2f} blink={f['blink']:.2f} "
              f"smile={f['smile']:.2f} pose={f.get('pose')}")
    else:
        print(f"[{mode}] 얼굴 미검출")


def run_calibration(cap, analyzer, agg, t0: float):
    """정면 응시 기준값을 수집한다. 본 루프와 동일한 스로틀을 적용한다."""
    print(f">>> 캘리브레이션 {CALIBRATION_SEC:.0f}초. 화면 중앙을 응시하세요.")
    agg.start_calibration()

    end = time.time() + CALIBRATION_SEC
    last = 0.0
    while time.time() < end:
        ok, frame = cap.read()
        if not ok:
            continue

        now = time.time()
        if now - last >= INTERVAL:
            last = now
            agg.push(analyzer.analyze_bgr(frame, int((now - t0) * 1000)))

        if SHOW_WINDOW:
            cv2.imshow("VRoom Vision smoke test", frame)
            cv2.waitKey(1)

    print(">>> baseline:", agg.end_calibration())


def print_turn_features(feats: dict | None):
    """턴 집계 결과를 항목별로 출력하고 실측 fps 를 함께 보여준다."""
    print(">>> 턴 피쳐:")
    if not feats:
        print("     프레임 부족")
        return

    for k, v in feats.items():
        print(f"     {k:22s} {v}")

    # 실측 fps 가 목표(10)에 크게 못 미치면 CPU 가 못 따라가고 있다는 뜻이다.
    fps_actual = feats["frameCount"] / max(feats["durationSec"], 1e-6)
    print(f"     {'[실측 fps]':22s} {fps_actual:.1f}")


def main():
    cap = open_camera(CAM_INDEX)
    analyzer = FrameAnalyzer()
    agg = SessionAggregator()

    t0 = time.time()
    last_analyze = 0.0      # 마지막 분석 시각
    last_print = 0.0        # 마지막 콘솔 출력 시각
    frame_data = None       # 최근 분석 결과 (첫 프레임 전 None)
    mode = "idle"           # idle | calib | TURN

    print("=== [c] 3초 캘리브레이션  [s] 턴 시작  [e] 턴 종료  [q] 종료 ===")

    try:
        while True:
            ok, bgr = cap.read()
            if not ok:
                continue

            now = time.time()

            # 분석 (스로틀)
            if now - last_analyze >= INTERVAL:
                last_analyze = now
                frame_data = analyzer.analyze_bgr(bgr, int((now - t0) * 1000))
                agg.push(frame_data)

            # 콘솔 출력 (0.5초마다)
            if frame_data and now - last_print > 0.5:
                last_print = now
                print_frame(mode, frame_data)

            # 미리보기 + 키 입력
            if SHOW_WINDOW:
                label = (f"{mode}  face={bool(frame_data and frame_data.get('face'))}"
                         f"  pose={bool(frame_data and frame_data.get('pose'))}")
                cv2.putText(bgr, label, (10, 28), cv2.FONT_HERSHEY_SIMPLEX,
                            0.7, (0, 255, 0), 2)
                cv2.imshow("VRoom Vision smoke test", bgr)
                key = cv2.waitKey(1) & 0xFF
            else:
                key = 255   # 입력 없음

            if key == ord('q'):
                break

            elif key == ord('c'):
                mode = "calib"
                run_calibration(cap, analyzer, agg, t0)
                mode = "idle"

            elif key == ord('s'):
                agg.start_turn("TEST")
                mode = "TURN"
                print(">>> 턴 시작. 움직여 보세요.")

            elif key == ord('e'):
                mode = "idle"
                print_turn_features(agg.end_turn())

    finally:
        cap.release()
        cv2.destroyAllWindows()
        analyzer.close()


if __name__ == "__main__":
    main()