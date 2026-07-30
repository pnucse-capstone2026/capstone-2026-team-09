from __future__ import annotations
import asyncio, csv, json, os, pathlib, time
from concurrent.futures import ThreadPoolExecutor

import httpx
from fastapi import FastAPI, WebSocket, WebSocketDisconnect

from analyzer import FrameAnalyzer
from aggregator import SessionAggregator

BACKEND_URL = os.getenv("BACKEND_URL", "http://127.0.0.1:8080")
LOG_DIR = pathlib.Path("logs"); LOG_DIR.mkdir(exist_ok=True)

app = FastAPI(title="VRoom Vision Worker", version="1.0")
_pool = ThreadPoolExecutor(max_workers=2)   # MediaPipe 추론을 이벤트 루프 밖으로


@app.get("/health")
async def health():
    return {"ok": True, "service": "vision-worker", "backend": BACKEND_URL}


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
        print(f"[{session_id}] 백엔드 전송 실패: {e}")


@app.websocket("/ws/vision")
async def ws_vision(ws: WebSocket):
    await ws.accept()
    sid = ws.query_params.get("session_id", "default")
    print(f"[Vision] 연결됨: {sid}")

    analyzer = FrameAnalyzer()
    agg = SessionAggregator()
    loop = asyncio.get_running_loop()
    t0 = time.time()
    busy = False
    dropped = 0
    saved_debug = False

    try:
        while True:
            msg = await ws.receive()

            if "bytes" in msg:
                if not saved_debug:
                    (LOG_DIR / f"debug_frame_{sid}.jpg").write_bytes(msg["bytes"])
                    print(f"[{sid}] 첫 프레임 저장: logs/debug_frame_{sid}.jpg "
                          f"({len(msg['bytes'])/1024:.1f}KB)")
                    saved_debug = True

                if busy:
                    dropped += 1
                    continue
                if not (agg.in_turn or agg.calibrating):
                    continue

                busy = True
                ts = int((time.time() - t0) * 1000)
                try:
                    frame = await loop.run_in_executor(
                        _pool, analyzer.analyze_jpeg, msg["bytes"], ts)
                    agg.push(frame)
                finally:
                    busy = False

            elif "text" in msg:
                data = json.loads(msg["text"])
                t = data.get("type")

                if t == "calibrate_start":
                    agg.start_calibration()
                    print(f"[{sid}] 캘리브레이션 시작")

                elif t == "calibrate_end":
                    base = agg.end_calibration()
                    await ws.send_json({"type": "calibrated",
                                        "ok": base is not None,
                                        "samples": (base or {}).get("samples", 0)})
                    print(f"[{sid}] 캘리브레이션 결과: {base}")

                elif t == "turn_start":
                    agg.start_turn(data.get("stage", ""))
                    print(f"[{sid}] 턴 시작 stage={data.get('stage','')}")

                elif t == "turn_end":
                    feats = agg.end_turn()
                    if feats:
                        print(f"[{sid}] 턴 피쳐: {feats}")
                        asyncio.create_task(_post_features(sid, feats))
                    else:
                        print(f"[{sid}] 프레임 부족 -> 턴 집계 스킵")

    except WebSocketDisconnect:
        print(f"[Vision] 연결 종료: {sid} (drop={dropped})")
    except Exception:
        import traceback; traceback.print_exc()
    finally:
        analyzer.close()