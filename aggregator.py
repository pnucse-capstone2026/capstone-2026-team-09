from __future__ import annotations
import math
import numpy as np

GAZE_YAW_TOL = 15.0       # 캘리브레이션 정면 대비 좌우 허용 각도(deg)
GAZE_PITCH_TOL = 12.0     # 상하 허용 각도(deg)
EYE_DEV_TOL = 0.35        # 안구 이탈 블렌드셰이프 허용치(0~1)
BLINK_ON = 0.5            # 이 값을 상향 돌파하면 눈깜빡임 1회
FACE_TOUCH_RADIUS = 0.55  # 어깨너비 배수. 코 기준 이 반경 안에 손목이 오면 '얼굴 만짐'
WRIST_VIS_TOL = 0.5       # 손목 visibility 하한
MIN_FRAMES = 5            # 이보다 적으면 턴 집계 포기


class SessionAggregator:
    def __init__(self):
        self.baseline: dict | None = None
        self._cal: list[dict] = []
        self._frames: list[dict] = []
        self.calibrating = False
        self.in_turn = False
        self.stage = ""

    # ---------- 캘리브레이션 ----------
    def start_calibration(self):
        self.calibrating = True
        self._cal.clear()

    def end_calibration(self) -> dict | None:
        self.calibrating = False
        face = [f for f in self._cal if f.get("face")]
        pose = [f for f in self._cal if f.get("pose")]
        if not face:
            return None
        self.baseline = {
            "yaw":        float(np.mean([f["yaw"] for f in face])),
            "pitch":      float(np.mean([f["pitch"] for f in face])),
            "torso_x":    float(np.mean([f["torso_x"] for f in pose])) if pose else 0.5,
            "torso_y":    float(np.mean([f["torso_y"] for f in pose])) if pose else 0.5,
            "shoulder_w": float(np.mean([f["shoulder_w"] for f in pose])) if pose else 0.25,
            "samples":    len(face),
        }
        return self.baseline

    # ---------- 턴 ----------
    def start_turn(self, stage: str = ""):
        self.in_turn = True
        self.stage = stage
        self._frames.clear()

    def push(self, frame: dict | None):
        if frame is None:
            return
        if self.calibrating:
            self._cal.append(frame)
        elif self.in_turn:
            self._frames.append(frame)

    def end_turn(self) -> dict | None:
        self.in_turn = False
        fr = self._frames
        n = len(fr)
        if n < MIN_FRAMES:
            return None

        dur = max((fr[-1]["ts"] - fr[0]["ts"]) / 1000.0, 1e-6)
        face = [f for f in fr if f.get("face")]
        pose = [f for f in fr if f.get("pose")]
        b = self.baseline or {"yaw": 0.0, "pitch": 0.0,
                              "torso_x": 0.5, "torso_y": 0.5, "shoulder_w": 0.25}

        # ── 1. 시선 ───────────────────────────────────────────────
        on, yaws, pitches = 0, [], []
        for f in face:
            dy, dp = f["yaw"] - b["yaw"], f["pitch"] - b["pitch"]
            yaws.append(dy); pitches.append(dp)
            if (abs(dy) <= GAZE_YAW_TOL and abs(dp) <= GAZE_PITCH_TOL
                    and f["eye_h"] <= EYE_DEV_TOL and f["eye_v"] <= EYE_DEV_TOL):
                on += 1
        gaze_ratio = on / len(face) if face else 0.0

        # ── 2. 자세 ───────────────────────────────────────────────
        if pose:
            tilt_mean = float(np.mean([abs(f["tilt"]) for f in pose]))
            sw_ref = b["shoulder_w"]
            xs = np.array([f["torso_x"] for f in pose])
            ys = np.array([f["torso_y"] for f in pose])
            sway = float(math.hypot(np.std(xs) / sw_ref, np.std(ys) / sw_ref))
            drift = float(np.mean(np.hypot((xs - b["torso_x"]) / sw_ref,
                                           (ys - b["torso_y"]) / sw_ref)))
        else:
            tilt_mean = sway = drift = 0.0

        # ── 3. 손짓 ───────────────────────────────────────────────
        energy, touches, visible = 0.0, 0, 0
        was_touching, prev = False, None
        for f in pose:
            sw = f["shoulder_w"]
            if f["lw_vis"] >= WRIST_VIS_TOL or f["rw_vis"] >= WRIST_VIS_TOL:
                visible += 1
            if prev is not None:
                dt = max((f["ts"] - prev["ts"]) / 1000.0, 1e-3)
                d = (math.hypot(f["lw"][0] - prev["lw"][0], f["lw"][1] - prev["lw"][1]) +
                     math.hypot(f["rw"][0] - prev["rw"][0], f["rw"][1] - prev["rw"][1])) / 2
                energy += (d / sw) / dt
            r = FACE_TOUCH_RADIUS * sw
            touching = (
                math.hypot(f["lw"][0] - f["nose"][0], f["lw"][1] - f["nose"][1]) < r or
                math.hypot(f["rw"][0] - f["nose"][0], f["rw"][1] - f["nose"][1]) < r)
            if touching and not was_touching:
                touches += 1
            was_touching = touching
            prev = f
        energy /= max(len(pose) - 1, 1)

        # ── 4. 표정 ───────────────────────────────────────────────
        if face:
            mat = np.array([f["expr_vec"] for f in face])
            expr_var = float(np.mean(np.std(mat, axis=0)))
            smile_ratio = float(np.mean([f["smile"] > 0.15 for f in face]))
            frown_ratio = float(np.mean([f["frown"] > 0.30 for f in face]))
            blinks, was_blink = 0, False
            for f in face:
                is_blink = f["blink"] > BLINK_ON
                if is_blink and not was_blink:
                    blinks += 1
                was_blink = is_blink
            bpm = blinks / dur * 60.0
        else:
            expr_var = smile_ratio = frown_ratio = bpm = 0.0

        return {
            "stage": self.stage,
            "durationSec": round(dur, 2),
            "frameCount": n,
            "faceDetectedRatio": round(len(face) / n, 3),
            "poseDetectedRatio": round(len(pose) / n, 3),
            "calibrated": self.baseline is not None,
            "gazeOnTargetRatio": round(gaze_ratio, 3),
            "headYawStd": round(float(np.std(yaws)) if yaws else 0.0, 2),
            "headPitchStd": round(float(np.std(pitches)) if pitches else 0.0, 2),
            "shoulderTiltMean": round(tilt_mean, 2),
            "bodySwayStd": round(sway, 4),
            "torsoDriftMean": round(drift, 4),
            "handMotionEnergy": round(energy, 4),
            "handVisibleRatio": round(visible / len(pose), 3) if pose else 0.0,
            "faceTouchCount": touches,
            "expressionVariance": round(expr_var, 4),
            "smileRatio": round(smile_ratio, 3),
            "frownRatio": round(frown_ratio, 3),
            "blinkPerMinute": round(bpm, 1),
        }