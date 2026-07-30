"""설치 환경과 Tasks API 표면을 한 번에 검증한다."""
import sys
import numpy as np

print("python  :", sys.version.split()[0])

import mediapipe as mp
print("mediapipe:", mp.__version__)
print("numpy   :", np.__version__)

try:
    import cv2
    print("cv2     :", cv2.__version__)
except ImportError:
    print("cv2     : ❌ 없음 -> pip install opencv-contrib-python")
    sys.exit(1)

from mediapipe.tasks import python as mp_python
from mediapipe.tasks.python import vision as mp_vision
print("Tasks API import OK")

need = ["FaceLandmarker", "FaceLandmarkerOptions",
        "PoseLandmarker", "PoseLandmarkerOptions", "RunningMode"]
missing = [n for n in need if not hasattr(mp_vision, n)]
for n in need:
    print(f"  {n:24s} {'OK' if n not in missing else '❌ MISSING'}")
if missing:
    sys.exit(1)

# 실제 모델 로드 + 더미 프레임 1장 추론
face = mp_vision.FaceLandmarker.create_from_options(
    mp_vision.FaceLandmarkerOptions(
        base_options=mp_python.BaseOptions(
            model_asset_path="models/face_landmarker.task"),
        running_mode=mp_vision.RunningMode.VIDEO,
        num_faces=1,
        output_face_blendshapes=True,
        output_facial_transformation_matrixes=True,
    ))
pose = mp_vision.PoseLandmarker.create_from_options(
    mp_vision.PoseLandmarkerOptions(
        base_options=mp_python.BaseOptions(
            model_asset_path="models/pose_landmarker_lite.task"),
        running_mode=mp_vision.RunningMode.VIDEO,
        num_poses=1,
        output_segmentation_masks=False,
    ))
print("모델 로드 OK (face_landmarker / pose_landmarker_lite)")

dummy = np.full((480, 640, 3), 128, dtype=np.uint8)
img = mp.Image(image_format=mp.ImageFormat.SRGB, data=dummy)

fr = face.detect_for_video(img, 0)
pr = pose.detect_for_video(img, 0)
print("detect_for_video 호출 OK")

for attr in ["face_landmarks", "face_blendshapes", "facial_transformation_matrixes"]:
    print(f"  FaceResult.{attr:32s} {'OK' if hasattr(fr, attr) else '❌ MISSING'}")
print(f"  PoseResult.pose_landmarks{' ':21s}{'OK' if hasattr(pr, 'pose_landmarks') else '❌ MISSING'}")

face.close(); pose.close()
print("\n✅ 전부 통과. smoke_test.py 로 넘어가세요.")