from __future__ import annotations
import asyncio, csv, json, os, pathlib, threading, time
from contextlib import asynccontextmanager

import cv2
import httpx
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import HTMLResponse, JSONResponse, Response

from analyzer import FrameAnalyzer
from aggregator import SessionAggregator

# ── 환경변수 설정 ────────────────────────────────────────────────
BACKEND_URL  = os.getenv("BACKEND_URL", "http://127.0.0.1:8080")
CAM_INDEX    = int(os.getenv("CAM_INDEX", "0"))
CAM_BACKEND  = os.getenv("CAM_BACKEND", "DSHOW")
TARGET_FPS   = float(os.getenv("CAM_FPS", "10"))
SHOW_PREVIEW = os.getenv("SHOW_PREVIEW", "0") == "1"
LOG_DIR      = pathlib.Path(os.getenv("VISION_LOG_DIR", "logs"))
LOG_DIR.mkdir(parents=True, exist_ok=True)

_BACKENDS = {"DSHOW": cv2.CAP_DSHOW, "MSMF": cv2.CAP_MSMF, "ANY": cv2.CAP_ANY}


class CameraPump:
    def __init__(self, analyzer: FrameAnalyzer):
        self.analyzer = analyzer
        self.cap = None
        self._run = False
        self._t = None
        self._session = None
        self.last_frame = None
        self.analyzed = 0
        self.read_fail = 0
        self.width = self.height = 0

    # ---------- 수명 ----------
    def open(self) -> bool:
        backend = _BACKENDS.get(CAM_BACKEND, cv2.CAP_ANY)
        self.cap = cv2.VideoCapture(CAM_INDEX, backend)
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
        self.cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        if not self.cap.isOpened():
            print(f"[Camera] ❌ index={CAM_INDEX} backend={CAM_BACKEND} 열기 실패")
            return False
        self.width = int(self.cap.get(cv2.CAP_PROP_FRAME_WIDTH))
        self.height = int(self.cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        print(f"[Camera] ✅ index={CAM_INDEX} backend={CAM_BACKEND} "
              f"{self.width}x{self.height} target={TARGET_FPS}fps")
        return True

    def start(self):
        self._run = True
        self._t = threading.Thread(target=self._loop, daemon=True)
        self._t.start()

    def stop(self):
        self._run = False
        if self._t:
            self._t.join(timeout=2.0)
        if self.cap:
            self.cap.release()
        if SHOW_PREVIEW:
            cv2.destroyAllWindows()

    # ---------- 세션 바인딩 ----------
    def bind(self, agg: SessionAggregator, lock: threading.Lock):
        self._session = (agg, lock)
        self.analyzed = 0

    def unbind(self):
        self._session = None

    # ---------- 캡처 루프 ----------
    def _loop(self):
        interval = 1.0 / max(TARGET_FPS, 1.0)
        t0, last = time.time(), 0.0
        saved_debug = False

        while self._run:
            ok, bgr = self.cap.read()
            if not ok or bgr is None:
                self.read_fail += 1
                if self.read_fail % 50 == 1:
                    print(f"[Camera] ⚠️ 프레임 읽기 실패 누적 {self.read_fail}")
                time.sleep(0.02)
                continue

            self.last_frame = bgr

            if SHOW_PREVIEW:
                cv2.imshow("VRoom webcam (q=close window)", bgr)
                cv2.waitKey(1)

            sess = self._session
            if sess is None:
                continue
            agg, lock = sess
            if not (agg.in_turn or agg.calibrating):
                continue

            now = time.time()
            if (now - last) < interval:
                continue
            last = now

            if not saved_debug:
                cv2.imwrite(str(LOG_DIR / "debug_frame.jpg"), bgr)
                print("[Camera] 첫 분석 프레임 저장: logs/debug_frame.jpg")
                saved_debug = True

            frame = self.analyzer.analyze_bgr(bgr, int((now - t0) * 1000))
            with lock:
                agg.push(frame)
            self.analyzed += 1


# ── 전역 인스턴스 (기동 시 1회 생성) ─────────────────────────────
_analyzer: FrameAnalyzer | None = None
_camera: CameraPump | None = None


@asynccontextmanager
async def lifespan(app: FastAPI):
    global _analyzer, _camera
    _analyzer = FrameAnalyzer()
    print("[Vision] MediaPipe 모델 로드 완료")
    _camera = CameraPump(_analyzer)
    if _camera.open():
        _camera.start()
    else:
        print("[Vision] ⚠️ 웹캠 없이 기동. CAM_INDEX / CAM_BACKEND 를 확인하세요.")
    yield
    if _camera:
        _camera.stop()
    if _analyzer:
        _analyzer.close()
    print("[Vision] 종료")


app = FastAPI(title="VRoom Vision Worker", version="2.0", lifespan=lifespan)


# ── 상태 / 프레이밍 확인용 엔드포인트 ────────────────────────────
@app.get("/health")
async def health():
    return {"ok": True, "service": "vision-worker", "backend": BACKEND_URL,
            "camera": _camera.cap is not None and _camera.cap.isOpened()
                      if _camera else False}


@app.get("/status")
async def status():
    if _camera is None:
        return JSONResponse({"error": "not started"}, status_code=503)
    sess = _camera._session
    agg = sess[0] if sess else None
    return {
        "camera": {"index": CAM_INDEX, "backend": CAM_BACKEND,
                   "size": f"{_camera.width}x{_camera.height}",
                   "target_fps": TARGET_FPS, "read_fail": _camera.read_fail},
        "session_bound": sess is not None,
        "in_turn": bool(agg and agg.in_turn),
        "calibrating": bool(agg and agg.calibrating),
        "calibrated": bool(agg and agg.baseline is not None),
        "analyzed_this_turn": _camera.analyzed,
    }


@app.get("/snapshot")
async def snapshot():
    """현재 웹캠 프레임 1장 (JPEG). 데스크탑 브라우저에서 프레이밍 확인용."""
    if _camera is None or _camera.last_frame is None:
        return JSONResponse({"error": "no frame"}, status_code=503)
    ok, buf = cv2.imencode(".jpg", _camera.last_frame,
                           [int(cv2.IMWRITE_JPEG_QUALITY), 70])
    if not ok:
        return JSONResponse({"error": "encode failed"}, status_code=500)
    return Response(content=buf.tobytes(), media_type="image/jpeg")


@app.get("/preview")
async def preview():
    return HTMLResponse("""<!doctype html><meta charset=utf-8>
<title>VRoom webcam preview</title>
<body style="margin:0;background:#111;color:#eee;font-family:sans-serif;text-align:center">
<h3>Vision Worker preview</h3>
<img id=v style="max-width:96vw;border:1px solid #444">
<p id=s style="font-size:13px;color:#9ad"></p>
<script>
setInterval(async()=>{
  document.getElementById('v').src='/snapshot?t='+Date.now();
  try{ const r=await fetch('/status'); const j=await r.json();
    document.getElementById('s').textContent=
      `${j.camera.size} | turn=${j.in_turn} | calibrated=${j.calibrated} | analyzed=${j.analyzed_this_turn} | read_fail=${j.camera.read_fail}`;
  }catch(e){}
},500);
</script></body>""")


# ── 실험 로그 + 백엔드 전송 ──────────────────────────────────────
def _dump_csv(session_id: str, features: dict):
    path = LOG_DIR / f"{session_id}.csv"
    new = not path.exists()
    with path.open("a", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=list(features.keys()))
        if new:
            w.writeheader()
        w.writerow(features)


async def _post_features(session_id: str, features: dict):
    _dump_csv(session_id, features)
    try:
        async with httpx.AsyncClient(timeout=5.0) as c:
            r = await c.post(f"{BACKEND_URL}/vision",
                             json={"session_id": session_id, "features": features})
            print(f"[{session_id}] POST /vision -> {r.status_code} {r.text[:120]}")
    except Exception as e:
        print(f"[{session_id}] ❌ 백엔드 전송 실패: {e}")


# ── Unity 제어 채널 (텍스트 전용) ────────────────────────────────
@app.websocket("/ws/vision")
async def ws_vision(ws: WebSocket):
    await ws.accept()
    sid = ws.query_params.get("session_id", "default")
    print(f"[Vision] Unity 연결됨: {sid}")

    if _camera is None or _camera.cap is None or not _camera.cap.isOpened():
        await ws.send_json({"type": "camera_error"})
        await ws.close()
        print(f"[Vision] 웹캠 없음 -> 연결 거부 ({sid})")
        return

    agg = SessionAggregator()
    lock = threading.Lock()
    _camera.bind(agg, lock)

    try:
        while True:
            data = json.loads(await ws.receive_text())
            t = data.get("type")

            if t == "calibrate_start":
                with lock:
                    agg.start_calibration()
                print(f"[{sid}] 캘리브레이션 시작")

            elif t == "calibrate_end":
                with lock:
                    base = agg.end_calibration()
                await ws.send_json({"type": "calibrated", "ok": base is not None,
                                    "samples": (base or {}).get("samples", 0)})
                print(f"[{sid}] 캘리브레이션 결과: {base}")

            elif t == "turn_start":
                with lock:
                    agg.start_turn(data.get("stage", ""))
                _camera.analyzed = 0
                print(f"[{sid}] 턴 시작 stage={data.get('stage','')}")

            elif t == "turn_end":
                with lock:
                    feats = agg.end_turn()
                if feats:
                    print(f"[{sid}] 턴 피쳐: {feats}")
                    asyncio.create_task(_post_features(sid, feats))
                else:
                    print(f"[{sid}] ⚠️ 프레임 부족 -> 턴 집계 스킵 "
                          f"(분석 {_camera.analyzed}장)")

    except WebSocketDisconnect:
        print(f"[Vision] Unity 연결 종료: {sid}")
    except Exception:
        import traceback; traceback.print_exc()
    finally:
        _camera.unbind()