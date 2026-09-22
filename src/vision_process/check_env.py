"""설치 환경과 MediaPipe Tasks API 표면을 한 번에 검증한다.

새 PC 에 vision_process 를 세팅했을 때 가장 먼저 실행할 스크립트.
여기서 통과하면 smoke_test.py -> server.py 순서로 넘어간다.

검사 순서: 패키지 버전 -> Tasks API 심볼 -> 모델 파일 로드 -> 더미 추론 -> 결과 속성
"""
import sys

import numpy as np


def check_packages():
    """필수 패키지 버전을 출력한다. cv2 가 없으면 여기서 종료."""
    print("python   :", sys.version.split()[0])

    import mediapipe as mp
    print("mediapipe:", mp.__version__)
    print("numpy    :", np.__version__)

    try:
        import cv2
        print("cv2      :", cv2.__version__)
    except ImportError:
        print("cv2      : ❌ 없음 -> pip install opencv-contrib-python")
        sys.exit(1)


def check_tasks_api():
    """Tasks API 심볼이 전부 있는지 확인한다.

    mediapipe 구버전은 solutions API 만 있고 Tasks API 가 없어 여기서 걸린다.
    """
    from mediapipe.tasks.python import vision as mp_vision
    print("Tasks API import OK")

    need = ["FaceLandmarker", "FaceLandmarkerOptions",
            "PoseLandmarker", "PoseLandmarkerOptions", "RunningMode"]
    missing = [n for n in need if not hasattr(mp_vision, n)]

    for n in need:
        print(f"  {n:24s} {'OK' if n not in missing else '❌ MISSING'}")
    if missing:
        sys.exit(1)


def check_models():
    """실제 모델을 로드하고 더미 프레임 1장을 추론해 본다.

    models/ 폴더에 .task 파일이 없으면 여기서 예외가 난다.
    """
    import mediapipe as mp
    from mediapipe.tasks import python as mp_python
    from mediapipe.tasks.python import vision as mp_vision

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

    # 회색 더미 프레임. 얼굴이 없어도 호출 자체가 되는지만 본다.
    dummy = np.full((480, 640, 3), 128, dtype=np.uint8)
    img = mp.Image(image_format=mp.ImageFormat.SRGB, data=dummy)

    face_result = face.detect_for_video(img, 0)
    pose_result = pose.detect_for_video(img, 0)
    print("detect_for_video 호출 OK")

    # analyzer.py 가 실제로 참조하는 속성들이 존재하는지 확인
    for attr in ["face_landmarks", "face_blendshapes", "facial_transformation_matrixes"]:
        print(f"  FaceResult.{attr:32s} {'OK' if hasattr(face_result, attr) else '❌ MISSING'}")
    ok_pose = hasattr(pose_result, "pose_landmarks")
    print(f"  PoseResult.pose_landmarks{' ':21s}{'OK' if ok_pose else '❌ MISSING'}")

    face.close()
    pose.close()


def main():
    check_packages()
    check_tasks_api()
    check_models()
    print("\n✅ 전부 통과. smoke_test.py 로 넘어가세요.")


if __name__ == "__main__":
    main()