from __future__ import annotations
import math
import numpy as np
import cv2
import mediapipe as mp
from mediapipe.tasks import python as mp_python
from mediapipe.tasks.python import vision as mp_vision

FACE_MODEL = "models/face_landmarker.task"
POSE_MODEL = "models/pose_landmarker_lite.task"

# Pose 랜드마크 인덱스
NOSE, L_SHOULDER, R_SHOULDER = 0, 11, 12
L_WRIST, R_WRIST = 15, 16

# 표정 '경직도' 산출에 쓸 블렌드셰이프 축 (52개 전부 쓰면 노이즈만 커짐)
EXPR_KEYS = [
    "browDownLeft", "browDownRight", "browInnerUp",
    "eyeSquintLeft", "eyeSquintRight", "eyeWideLeft", "eyeWideRight",
    "mouthSmileLeft", "mouthSmileRight", "mouthFrownLeft", "mouthFrownRight",
    "mouthPressLeft", "mouthPressRight", "mouthPucker", "jawOpen",
]

# 두부 회전 행렬 전치 여부. smoke_test.py 로 검증 후 필요하면 True 로 바꾼다.
TRANSPOSE_HEAD_MATRIX = False


def _euler_from_matrix(m: np.ndarray) -> tuple[float, float, float]:
    """4x4 변환 행렬 -> (pitch, yaw, roll) 도(degree)."""
    R = m[:3, :3]
    if TRANSPOSE_HEAD_MATRIX:
        R = R.T
    sy = math.sqrt(R[0, 0] ** 2 + R[1, 0] ** 2)
    if sy > 1e-6:
        pitch = math.atan2(R[2, 1], R[2, 2])
        yaw = math.atan2(-R[2, 0], sy)
        roll = math.atan2(R[1, 0], R[0, 0])
    else:
        pitch = math.atan2(-R[1, 2], R[1, 1])
        yaw = math.atan2(-R[2, 0], sy)
        roll = 0.0
    return math.degrees(pitch), math.degrees(yaw), math.degrees(roll)


def _vis(lm) -> float:
    v = getattr(lm, "visibility", None)
    return float(v) if v is not None else 1.0


class FrameAnalyzer:
    def __init__(self):
        self.face = mp_vision.FaceLandmarker.create_from_options(
            mp_vision.FaceLandmarkerOptions(
                base_options=mp_python.BaseOptions(model_asset_path=FACE_MODEL),
                running_mode=mp_vision.RunningMode.VIDEO,
                num_faces=1,
                output_face_blendshapes=True,
                output_facial_transformation_matrixes=True,
            )
        )
        self.pose = mp_vision.PoseLandmarker.create_from_options(
            mp_vision.PoseLandmarkerOptions(
                base_options=mp_python.BaseOptions(model_asset_path=POSE_MODEL),
                running_mode=mp_vision.RunningMode.VIDEO,
                num_poses=1,
                output_segmentation_masks=False,
            )
        )
        self._last_ts = -1

    def close(self):
        for m in (self.face, self.pose):
            try:
                m.close()
            except Exception:
                pass

    def analyze_bgr(self, bgr: np.ndarray, ts_ms: int) -> dict | None:
        if bgr is None:
            return None
        if ts_ms <= self._last_ts:
            ts_ms = self._last_ts + 1
        self._last_ts = ts_ms

        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
        mp_img = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)

        out: dict = {"ts": ts_ms, "face": False, "pose": False}

        # ---------- 얼굴 ----------
        f = self.face.detect_for_video(mp_img, ts_ms)
        if f.facial_transformation_matrixes:
            pitch, yaw, roll = _euler_from_matrix(
                np.array(f.facial_transformation_matrixes[0]))
            bs = ({c.category_name: c.score for c in f.face_blendshapes[0]}
                  if f.face_blendshapes else {})

            eye_h = max(bs.get("eyeLookOutLeft", 0), bs.get("eyeLookInLeft", 0),
                        bs.get("eyeLookOutRight", 0), bs.get("eyeLookInRight", 0))
            eye_v = max(bs.get("eyeLookUpLeft", 0), bs.get("eyeLookDownLeft", 0),
                        bs.get("eyeLookUpRight", 0), bs.get("eyeLookDownRight", 0))

            out.update(
                face=True, pitch=pitch, yaw=yaw, roll=roll,
                eye_h=float(eye_h), eye_v=float(eye_v),
                blink=float(max(bs.get("eyeBlinkLeft", 0), bs.get("eyeBlinkRight", 0))),
                smile=float((bs.get("mouthSmileLeft", 0) + bs.get("mouthSmileRight", 0)) / 2),
                frown=float(max(
                    (bs.get("browDownLeft", 0) + bs.get("browDownRight", 0)) / 2,
                    (bs.get("mouthFrownLeft", 0) + bs.get("mouthFrownRight", 0)) / 2)),
                expr_vec=[float(bs.get(k, 0.0)) for k in EXPR_KEYS],
            )

        # ---------- 자세 ----------
        p = self.pose.detect_for_video(mp_img, ts_ms)
        if p.pose_landmarks:
            lm = p.pose_landmarks[0]
            ls, rs = lm[L_SHOULDER], lm[R_SHOULDER]
            sw = max(math.hypot(ls.x - rs.x, ls.y - rs.y), 1e-4)

            tilt = math.degrees(math.atan2(rs.y - ls.y, rs.x - ls.x))
            if tilt > 90:  tilt -= 180
            if tilt < -90: tilt += 180

            lw, rw = lm[L_WRIST], lm[R_WRIST]
            out.update(
                pose=True,
                shoulder_w=float(sw),
                torso_x=float((ls.x + rs.x) / 2),
                torso_y=float((ls.y + rs.y) / 2),
                tilt=float(tilt),
                lw=(float(lw.x), float(lw.y)), rw=(float(rw.x), float(rw.y)),
                lw_vis=_vis(lw), rw_vis=_vis(rw),
                nose=(float(lm[NOSE].x), float(lm[NOSE].y)),
            )
        return out