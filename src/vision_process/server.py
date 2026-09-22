"""VRoom Vision Worker — 웹캠 행동 분석 서버.

Unity 와 별도 프로세스로 돌며, 웹캠 프레임을 직접 읽어 턴 단위 행동 지표를
만들어 백엔드로 보낸다.

[왜 별도 프로세스인가]
  Unity 에서 MediaPipe 를 돌리면 Windows 에서 SIGABRT 로 죽는 사례가 있고,
  Moonlight 원격 환경에서는 웹캠이 노트북에, Unity 가 데스크탑에 있어
  물리적으로 분리해야 한다.

[구조]
  CameraPump   백그라운드 스레드에서 웹캠을 계속 읽고 분석해 집계기에 밀어넣는다
  /ws/vision   Unity 가 캘리브레이션/턴 경계를 알려주는 제어 채널
  /snapshot    현재 프레임 1장(JPEG). 프레이밍 확인용
  /preview     위 두 개를 묶어 보여주는 간이 웹 페이지

[데이터 흐름]
  웹캠 -> CameraPump -> FrameAnalyzer -> SessionAggregator -> 백엔드 POST /vision
                            (프레임 단위)      (턴 단위)
"""
from __future__ import annotations

import asyncio
import csv
import json
import os
import pathlib
import threading
import time
from contextlib import asynccontextmanager

import cv2
import httpx
from dotenv import load_dotenv
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import HTMLResponse, JSONResponse, Response

from aggregator import SessionAggregator
from analyzer import FrameAnalyzer

load_dotenv()


# ===========================================================================
# 1. 환경 설정
# ===========================================================================
BACKEND_URL = os.getenv("BACKEND_URL", "http://127.0.0.1:8080")  # 턴 피쳐를 POST 할 백엔드
CAM_INDEX = int(os.getenv("CAM_INDEX", "0"))                     # 카메라 인덱스 (list_cameras.py 로 확인)
CAM_BACKEND = os.getenv("CAM_BACKEND", "DSHOW")                  # 캡처 백엔드 DSHOW | MSMF | ANY
TARGET_FPS = float(os.getenv("CAM_FPS", "10"))                   # 분석 목표 FPS. 높이면 CPU 부하가 급증한다
SHOW_PREVIEW = os.getenv("SHOW_PREVIEW", "0") == "1"             # 로컬 OpenCV 창을 띄울지
LOG_DIR = pathlib.Path(os.getenv("VISION_LOG_DIR", "logs"))      # CSV 로그 / 디버그 프레임 저장 위치
LOG_DIR.mkdir(parents=True, exist_ok=True)

# OpenCV 캡처 백엔드 이름 -> 상수.
# Windows 에서 DSHOW 가 가장 안정적이고, MSMF 는 초기화가 느린 경우가 있다.
_BACKENDS = {"DSHOW": cv2.CAP_DSHOW, "MSMF": cv2.CAP_MSMF, "ANY": cv2.CAP_ANY}


# ===========================================================================
# 2. 카메라 캡처 스레드
# ===========================================================================
class CameraPump:
    """웹캠을 백그라운드 스레드에서 계속 읽어 분석 결과를 집계기에 밀어넣는다.

    카메라는 서버 기동 시 한 번만 열고 종료까지 유지한다.
    턴마다 열고 닫으면 초기화에 수백 ms 가 걸려 앞부분 프레임을 놓친다.

    분석은 '캘리브레이션 중이거나 턴 진행 중'일 때만 수행한다.
    나머지 시간에는 프레임만 읽어 버려 CPU 를 아낀다.
    """

    def __init__(self, analyzer: FrameAnalyzer):
        self.analyzer = analyzer                 # 프레임 분석기 (프로세스 공용)
        self.cap = None                          # OpenCV VideoCapture
        self.last_frame = None                   # 최근 프레임 (/snapshot 용)
        self.analyzed = 0                        # 현재 턴에서 분석한 프레임 수
        self.read_fail = 0                       # 누적 프레임 읽기 실패 횟수
        self.width = self.height = 0             # 실제 캡처 해상도

        self._run = False                        # 루프 유지 플래그
        self._t: threading.Thread | None = None  # 캡처 스레드
        self._session = None                     # (집계기, 락) 튜플. 없으면 유휴 상태

    # -----------------------------------------------------------------
    # 2-1. 수명
    # -----------------------------------------------------------------
    def open(self) -> bool:
        """카메라를 연다. 실패해도 서버는 계속 뜨고, 시각 항목만 채점에서 빠진다."""
        backend = _BACKENDS.get(CAM_BACKEND, cv2.CAP_ANY)
        self.cap = cv2.VideoCapture(CAM_INDEX, backend)
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
        self.cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)   # 버퍼를 최소화해 최신 프레임을 읽는다

        if not self.cap.isOpened():
            print(f"[Camera] ❌ index={CAM_INDEX} backend={CAM_BACKEND} 열기 실패")
            return False

        self.width = int(self.cap.get(cv2.CAP_PROP_FRAME_WIDTH))
        self.height = int(self.cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        print(f"[Camera] ✅ index={CAM_INDEX} backend={CAM_BACKEND} "
              f"{self.width}x{self.height} target={TARGET_FPS}fps")
        return True

    def start(self):
        """캡처 스레드를 띄운다. daemon 이라 서버가 죽으면 함께 종료된다."""
        self._run = True
        self._t = threading.Thread(target=self._loop, daemon=True)
        self._t.start()

    def stop(self):
        """캡처 스레드를 멈추고 카메라를 반납한다."""
        self._run = False
        if self._t:
            self._t.join(timeout=2.0)
        if self.cap:
            self.cap.release()
        if SHOW_PREVIEW:
            cv2.destroyAllWindows()

    # -----------------------------------------------------------------
    # 2-2. 세션 바인딩
    # -----------------------------------------------------------------
    def bind(self, agg: SessionAggregator, lock: threading.Lock):
        """Unity 세션의 집계기를 연결한다. 이때부터 분석 결과가 쌓이기 시작한다.

        락이 필요한 이유: 캡처 스레드가 push 하는 동안 웹소켓 코루틴이
        end_turn 으로 버퍼를 읽을 수 있다.
        """
        self._session = (agg, lock)
        self.analyzed = 0

    def unbind(self):
        """세션 연결을 끊는다. 이후 프레임은 읽기만 하고 분석하지 않는다."""
        self._session = None

    # -----------------------------------------------------------------
    # 2-3. 캡처 루프 (별도 스레드)
    # -----------------------------------------------------------------
    def _loop(self):
        interval = 1.0 / max(TARGET_FPS, 1.0)   # 분석 최소 간격(초)
        t0 = time.time()                        # 타임스탬프 기준점
        last = 0.0                              # 마지막 분석 시각
        saved_debug = False                     # 첫 분석 프레임 저장 여부

        while self._run:
            ok, bgr = self.cap.read()
            if not ok or bgr is None:
                self.read_fail += 1
                # 실패가 이어질 때 로그가 폭주하지 않도록 50회마다 한 번만 알린다.
                if self.read_fail % 50 == 1:
                    print(f"[Camera] ⚠️ 프레임 읽기 실패 누적 {self.read_fail}")
                time.sleep(0.02)
                continue

            self.last_frame = bgr   # /snapshot 이 읽어간다

            if SHOW_PREVIEW:
                cv2.imshow("VRoom webcam (q=close window)", bgr)
                cv2.waitKey(1)

            # 세션이 없거나 수집 구간이 아니면 분석하지 않는다.
            sess = self._session
            if sess is None:
                continue
            agg, lock = sess
            if not (agg.in_turn or agg.calibrating):
                continue

            # 목표 FPS 로 스로틀. 카메라는 30fps 로 들어와도 분석은 10fps 만 한다.
            now = time.time()
            if (now - last) < interval:
                continue
            last = now

            # 첫 분석 프레임 1장을 저장해 프레이밍을 나중에 확인할 수 있게 한다.
            if not saved_debug:
                cv2.imwrite(str(LOG_DIR / "debug_frame.jpg"), bgr)
                print("[Camera] 첫 분석 프레임 저장: logs/debug_frame.jpg")
                saved_debug = True

            frame = self.analyzer.analyze_bgr(bgr, int((now - t0) * 1000))
            with lock:
                agg.push(frame)
            self.analyzed += 1


# ===========================================================================
# 3. 서버 수명
# ===========================================================================
_analyzer: FrameAnalyzer | None = None   # 프로세스 전역 분석기 (모델 로딩이 무겁다)
_camera: CameraPump | None = None        # 프로세스 전역 캡처 스레드


@asynccontextmanager
async def lifespan(app: FastAPI):
    """기동 시 모델과 카메라를 준비하고, 종료 시 정리한다."""
    global _analyzer, _camera

    _analyzer = FrameAnalyzer()
    print("[Vision] MediaPipe 모델 로드 완료")

    _camera = CameraPump(_analyzer)
    if _camera.open():
        _camera.start()
    else:
        # 카메라가 없어도 서버는 뜬다. Unity 연결 시점에 거부하면 되고,
        # 면접 자체는 시각 항목 없이 진행할 수 있다.
        print("[Vision] ⚠️ 웹캠 없이 기동. CAM_INDEX / CAM_BACKEND 를 확인하세요.")

    yield

    if _camera:
        _camera.stop()
    if _analyzer:
        _analyzer.close()
    print("[Vision] 종료")


app = FastAPI(title="VRoom Vision Worker", version="2.0", lifespan=lifespan)


# ===========================================================================
# 4. 상태 / 프레이밍 확인 엔드포인트
# ===========================================================================
@app.get("/health")
async def health():
    """기동 확인용. 카메라가 실제로 열려 있는지까지 알려준다."""
    return {
        "ok": True,
        "service": "vision-worker",
        "backend": BACKEND_URL,
        "camera": (_camera.cap is not None and _camera.cap.isOpened()
                   if _camera else False),
    }


@app.get("/status")
async def status():
    """현재 수집 상태. 턴이 도는데 analyzed 가 안 늘면 카메라 문제다."""
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
    """현재 웹캠 프레임 1장 (JPEG). 브라우저에서 프레이밍을 확인할 때 쓴다."""
    if _camera is None or _camera.last_frame is None:
        return JSONResponse({"error": "no frame"}, status_code=503)

    ok, buf = cv2.imencode(".jpg", _camera.last_frame,
                           [int(cv2.IMWRITE_JPEG_QUALITY), 70])
    if not ok:
        return JSONResponse({"error": "encode failed"}, status_code=500)
    return Response(content=buf.tobytes(), media_type="image/jpeg")


@app.get("/preview")
async def preview():
    """/snapshot 과 /status 를 0.5초마다 갱신해 보여주는 간이 페이지.

    실험 전 카메라 높이와 프레이밍을 맞출 때 이 화면을 보면 된다.
    """
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


# ===========================================================================
# 5. 결과 출력 (CSV 로그 + 백엔드 전송)
# ===========================================================================
def _dump_csv(session_id: str, features: dict):
    """실험 분석용 원시 피쳐를 CSV 에 한 줄 덧붙인다.

    백엔드는 채점에 쓰는 항목만 보관하므로, 비활성 지표(torsoDrift, blinkPerMinute 등)까지
    남기려면 이 CSV 가 필요하다. 회차 구분을 위해 기록 시각을 맨 앞에 붙인다.
    utf-8-sig 를 쓰는 이유는 Excel 에서 한글이 깨지지 않게 하기 위함이다.
    """
    row = {"recordedAt": time.strftime("%Y-%m-%d %H:%M:%S"), **features}
    path = LOG_DIR / f"{session_id}.csv"
    is_new = not path.exists()

    with path.open("a", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=list(row.keys()))
        if is_new:
            w.writeheader()
        w.writerow(row)


async def _post_features(session_id: str, features: dict):
    """턴 피쳐를 CSV 에 남기고 백엔드로 보낸다.

    전송에 실패해도 예외를 밖으로 던지지 않는다. 시각 항목은 없어도 면접이
    진행되어야 하고, 원자료는 이미 CSV 에 남았기 때문이다.
    """
    _dump_csv(session_id, features)
    try:
        async with httpx.AsyncClient(timeout=5.0) as c:
            r = await c.post(f"{BACKEND_URL}/vision",
                             json={"session_id": session_id, "features": features})
            print(f"[{session_id}] POST /vision -> {r.status_code} {r.text[:120]}")
    except Exception as e:
        print(f"[{session_id}] ❌ 백엔드 전송 실패: {e}")


# ===========================================================================
# 6. Unity 제어 채널
# ===========================================================================
@app.websocket("/ws/vision")
async def ws_vision(ws: WebSocket):
    """Unity 가 캘리브레이션과 턴 경계를 알려주는 채널 (텍스트 전용).

    영상은 이 채널로 오가지 않는다. 웹캠은 이 워커가 직접 읽는다.
    """
    await ws.accept()
    sid = ws.query_params.get("session_id", "default")
    print(f"[Vision] Unity 연결됨: {sid}")

    # 카메라가 없으면 즉시 알리고 끊는다. Unity 는 시각 항목 없이 면접을 진행한다.
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
                _on_calibrate_start(sid, agg, lock)

            elif t == "calibrate_end":
                await _on_calibrate_end(ws, sid, agg, lock)

            elif t == "turn_start":
                _on_turn_start(sid, agg, lock, data)

            elif t == "turn_end":
                _on_turn_end(sid, agg, lock, data)

    except WebSocketDisconnect:
        print(f"[Vision] Unity 연결 종료: {sid}")
    except Exception:
        import traceback
        traceback.print_exc()
    finally:
        _camera.unbind()


def _on_calibrate_start(sid: str, agg: SessionAggregator, lock: threading.Lock):
    """기준 자세 수집 시작. Unity 가 사용자에게 정면 응시를 안내한 뒤 보낸다."""
    with lock:
        agg.start_calibration()
    print(f"[{sid}] 캘리브레이션 시작")


async def _on_calibrate_end(ws: WebSocket, sid: str,
                            agg: SessionAggregator, lock: threading.Lock):
    """기준 자세를 확정하고 성공 여부를 Unity 에 알린다.

    실패해도 면접은 진행된다. 기본 베이스라인으로 대체되며 정확도만 떨어진다.
    """
    with lock:
        base = agg.end_calibration()

    await ws.send_json({"type": "calibrated", "ok": base is not None,
                        "samples": (base or {}).get("samples", 0)})
    print(f"[{sid}] 캘리브레이션 결과: {base}")


def _on_turn_start(sid: str, agg: SessionAggregator,
                   lock: threading.Lock, data: dict):
    """턴 수집 시작. Unity 는 개입 여부를 아직 모르므로 대개 NORMAL 로 연다."""
    with lock:
        agg.start_turn(data.get("stage", ""), data.get("phase", "NORMAL"))
    _camera.analyzed = 0
    print(f"[{sid}] 턴 시작 stage={data.get('stage','')} "
          f"phase={data.get('phase','NORMAL')}")


def _on_turn_end(sid: str, agg: SessionAggregator,
                 lock: threading.Lock, data: dict):
    """턴을 닫고 집계 결과를 백엔드로 보낸다.

    data 에 phase 가 있으면 turn_start 때의 위상을 그 값으로 정정한다.
    (개입이 확정되면 NORMAL 로 열었던 구간이 TRUNCATED 가 된다.)
    """
    with lock:
        feats = agg.end_turn(data.get("phase", ""))

    if feats:
        print(f"[{sid}] 턴 피쳐 phase={feats['phase']}: {feats}")
        # 전송은 백그라운드로. 여기서 await 하면 다음 메시지 수신이 늦어진다.
        asyncio.create_task(_post_features(sid, feats))
    else:
        print(f"[{sid}] ⚠️ 프레임 부족 -> 턴 집계 스킵 (분석 {_camera.analyzed}장)")