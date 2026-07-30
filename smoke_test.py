import time, cv2
from analyzer import FrameAnalyzer
from aggregator import SessionAggregator

CAM_INDEX = 0
SHOW_WINDOW = True

cap = cv2.VideoCapture(CAM_INDEX, cv2.CAP_DSHOW)
cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
if not cap.isOpened():
    raise SystemExit(f"웹캠 {CAM_INDEX} 를 열 수 없습니다. CAM_INDEX 를 1,2 로 바꿔보세요.")

an, agg = FrameAnalyzer(), SessionAggregator()
t0 = time.time()
last_print = 0.0
mode = "idle"
print("=== [c] 3초 캘리브레이션  [s] 턴 시작  [e] 턴 종료  [q] 종료 ===")

try:
    while True:
        ok, bgr = cap.read()
        if not ok:
            continue
        ts = int((time.time() - t0) * 1000)
        FPS = 10.0
        _last = 0.0
        now = time.time()
        if now - _last >= 1.0 / FPS:
            _last = now
            f = an.analyze_bgr(bgr, int((now - t0) * 1000))
            agg.push(f)

        now = time.time()
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
            while time.time() < end:
                ok, b2 = cap.read()
                if ok:
                    agg.push(an.analyze_bgr(b2, int((time.time() - t0) * 1000)))
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
            else:
                print("     프레임 부족")
finally:
    cap.release()
    cv2.destroyAllWindows()
    an.close()