"""사용 가능한 카메라 인덱스와 백엔드를 훑는다."""
import os
os.environ["OPENCV_LOG_LEVEL"] = "SILENT"   # 실패 인덱스마다 뜨는 경고 억제
import cv2

BACKENDS = [("CAP_DSHOW", cv2.CAP_DSHOW),
            ("CAP_MSMF",  cv2.CAP_MSMF),
            ("CAP_ANY",   cv2.CAP_ANY)]

found = []
for name, backend in BACKENDS:
    print(f"\n=== {name} ===")
    for i in range(6):
        cap = cv2.VideoCapture(i, backend)
        if not cap.isOpened():
            print(f"  index {i}: 열기 실패")
            cap.release()
            continue
        ok, frame = cap.read()
        if ok and frame is not None:
            h, w = frame.shape[:2]
            print(f"  index {i}: ✅ 동작  {w}x{h}")
            found.append((name, i, w, h))
        else:
            print(f"  index {i}: 열렸지만 프레임 없음")
        cap.release()

print("\n--- 결과 ---")
if found:
    for name, i, w, h in found:
        print(f"  {name}  CAM_INDEX = {i}   ({w}x{h})")
    print("\n위 조합을 smoke_test.py 에 반영하세요.")
else:
    print("  ❌ 동작하는 카메라 없음")