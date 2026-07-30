"""웹캠을 파이썬에서 직접 열어 MediaPipe 지표를 검증한다.
운영(server.py)과 동일하게 10fps 로 스로틀한다.
Unity 나 다른 프로그램이 웹캠을 잡고 있으면 실패하므로 먼저 종료할 것.
"""
import time, cv2
from analyzer import FrameAnalyzer
from aggregator import SessionAggregator

CAM_INDEX = 0
SHOW_WINDOW = True
FPS = 10.0                      # ✅ 루프 밖
INTERVAL = 1.0 / FPS

cap = cv2.VideoCapture(CAM_INDEX, cv2.CAP_DSHOW)
cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
if not cap.isOpened():
    raise SystemExit(f"웹캠 {CAM_INDEX} 를 열 수 없습니다. CAM_INDEX 를 1,2 로 바꿔보세요.")

an, agg = FrameAnalyzer(), SessionAggregator()
t0 = time.time()
last_analyze = 0.0              # ✅ 루프 밖
last_print = 0.0
f = None                        # ✅ 첫 프레임 전 참조 방지
mode = "idle"
print("=== [c] 3초 캘리브레이션  [s] 턴 시작  [e] 턴 종료  [q] 종료 ===")

try:
    while True:
        ok, bgr = cap.read()
        if not ok:
            continue

        now = time.time()
        if now - last_analyze >= INTERVAL:
            last_analyze = now
            f = an.analyze_bgr(bgr, int((now - t0) * 1000))
            agg.push(f)

        if f and now - last_print > 0.5:
            last_print = now
            if f.get("face"):
                print(f"[{mode}] yaw={f['yaw']:+6.1f} pitch={f['pitch']:+6.1f} "
                      f"eye_h={f['eye_h']:.2f} blink={f['blink']:.2f} "
                      f"smile={f['smile']:.2f} pose={f.get('pose')}")
            else:
                print(f"[{mode}] 얼굴 미검출")

        if SHOW_WINDOW:
            label = f"{mode}  face={bool(f and f.get('face'))}  pose={bool(f and f.get('pose'))}"
            cv2.putText(bgr, label, (10, 28), cv2.FONT_HERSHEY_SIMPLEX,
                        0.7, (0, 255, 0), 2)
            cv2.imshow("VRoom Vision smoke test", bgr)
            key = cv2.waitKey(1) & 0xFF
        else:
            key = 255

        if key == ord('q'):
            break

        elif key == ord('c'):
            print(">>> 캘리브레이션 3초. 화면 중앙을 응시하세요.")
            agg.start_calibration(); mode = "calib"
            end = time.time() + 3.0
            cal_last = 0.0
            while time.time() < end:
                ok, b2 = cap.read()
                if not ok:
                    continue
                n2 = time.time()
                if n2 - cal_last >= INTERVAL:      # ✅ 캘리브레이션도 동일 스로틀
                    cal_last = n2
                    agg.push(an.analyze_bgr(b2, int((n2 - t0) * 1000)))
                if SHOW_WINDOW:
                    cv2.imshow("VRoom Vision smoke test", b2); cv2.waitKey(1)
            print(">>> baseline:", agg.end_calibration())
            mode = "idle"

        elif key == ord('s'):
            agg.start_turn("TEST"); mode = "TURN"
            print(">>> 턴 시작. 움직여 보세요.")

        elif key == ord('e'):
            mode = "idle"
            feats = agg.end_turn()
            print(">>> 턴 피쳐:")
            if feats:
                for k, v in feats.items():
                    print(f"     {k:22s} {v}")
                fps_actual = feats["frameCount"] / max(feats["durationSec"], 1e-6)
                print(f"     [실측 fps]              {fps_actual:.1f}")   # ✅ 검증용
            else:
                print("     프레임 부족")
finally:
    cap.release()
    cv2.destroyAllWindows()
    an.close()