import os
import uvicorn
import io
import json
import re
import asyncio
import math
import threading
import numpy as np
import httpx
import websockets
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from faster_whisper import WhisperModel
from pydantic import BaseModel
from dotenv import load_dotenv

load_dotenv()
app = FastAPI()

# TTS Worker WebSocket URL
TTS_WORKER_WS_URL = os.getenv("TTS_WORKER_WS_URL", "ws://host.docker.internal:8001/ws/tts")

# Checkpoint 전사 설정. 기존 최종 전사 경로와 분리한다.
CHECKPOINT_ENABLED = os.getenv("CHECKPOINT_ENABLED", "true").lower() == "true"
CHECKPOINT_TRIGGER_BYTES = int(os.getenv("CHECKPOINT_TRIGGER_BYTES", "16000"))  # 0.5초
CHECKPOINT_MIN_BYTES = int(os.getenv("CHECKPOINT_MIN_BYTES", "25600"))          # 0.8초
CHECKPOINT_WINDOW_BYTES = int(os.getenv("CHECKPOINT_WINDOW_BYTES", "96000"))   # 3초

# correction_request는 호환성을 위해 코드와 메시지 형식은 유지하되 완전 비활성화한다.
# 신뢰도 확률은 0~1 범위이므로 threshold=0.0이면 조건을 만족할 수 없다.
CORRECTION_CONFIDENCE_THRESHOLD = 0.0

# checkpoint 확정과 Unity VAD 종료 신호를 분리한다.
# 기존 동작이 필요한 실험에서는 환경변수로 true를 줄 수 있다.
EMIT_ADAPTIVE_VAD_TRIGGER = os.getenv(
    "EMIT_ADAPTIVE_VAD_TRIGGER", "false"
).lower() == "true"

# Faster-Whisper 모델 로드
base_dir = os.path.dirname(os.path.abspath(__file__))
whisper_model_path = os.path.join(base_dir, "model", "whisper")
model = WhisperModel(whisper_model_path, device="cuda", compute_type="float16", local_files_only=True)

class InterviewFeatures(BaseModel):
    speakingTime: float
    meaningfulPauseCount: int
    volumeVariance: float
    lowVolumeRatio: float
    averageVolume: float
    responseTime: float

# [수정됨] 면접 상황에 맞춰 존댓말(하십시오체, 해요체) 및 다양한 종결 어미 패턴 보강
KOREAN_EOS_PATTERN = re.compile(
    r'(?:'
    r'습니다|습니까|입니다|입니까|랍니다|합니다|합니까|'     # 하십시오체 (격식)
    r'요|죠|지요|네요|데요|대요|나요|까요|게요|군요|'      # 해요체 (비격식 존대)
    r'다|까|오|시오|시요'                                  # 기타 종결
    r')[\.\?\!\s]*$'
)

def is_sentence_completed(text: str) -> bool:
    """부분 전사 결과의 마지막 토큰이 한국어 문장 종결 형태인지 판별합니다."""
    cleaned = text.strip()
    
    # 방어 로직: 할루시네이션(ex: "네.", "아.") 방지를 위해 실질 글자수가 너무 적으면 무시
    text_without_spaces = cleaned.replace(" ", "")
    if len(text_without_spaces) < 3:
        return False
        
    return bool(cleaned and KOREAN_EOS_PATTERN.search(cleaned))

class AudioState:
    """세션별 전체 PCM과 문장 종결 감지용 부분 전사 상태입니다."""
    def __init__(self):
        self.full_pcm_buffer = bytearray()
        self.inference_task = None
        self.epoch = 0
        
        # [수정됨] Sliding Window 및 트리거 관리를 위한 변수
        self.last_partial_trigger_byte = 0  # 마지막으로 부분 전사를 실행했을 때의 버퍼 크기
        self.last_eos_trigger_byte = 0      # 마지막으로 문장 종결 신호를 보냈을 때의 버퍼 크기

        # 마지막으로 확정한 checkpoint 이후의 전사 구간
        self.checkpoint_start_byte = 0
        self.last_checkpoint_sent_byte = 0
        self.partial_seq = 0
        self.checkpoint_texts = []
        self.partial_enabled = True

    def invalidate_inference(self):
        """진행 중인 후보/checkpoint 추론 결과가 도착해도 무효화한다."""
        self.epoch += 1
        if self.inference_task is not None and not self.inference_task.done():
            self.inference_task.cancel()
        self.inference_task = None

    def reset(self):
        """부분 전사 태스크를 무효화하고 현재 발화 상태를 초기화합니다."""
        self.invalidate_inference()
        self.full_pcm_buffer.clear()
        self.last_partial_trigger_byte = 0
        self.last_eos_trigger_byte = 0
        self.checkpoint_start_byte = 0
        self.last_checkpoint_sent_byte = 0
        self.partial_seq = 0
        self.checkpoint_texts.clear()
        self.partial_enabled = True


# Faster-Whisper 모델 객체에 대한 부분 전사와 최종 batch 전사의 동시 접근 방지용 Lock
model_inference_lock = threading.Lock()

def _transcribe_segments(audio_np, **kwargs):
    with model_inference_lock:
        segments, _ = model.transcribe(audio_np, **kwargs)
        texts = []
        words_info = []
        for segment in segments:
            text = segment.text.strip()
            if text:
                texts.append(text)
            if segment.words:
                for word in segment.words:
                    words_info.append({
                        "word": word.word.strip(),
                        "probability": word.probability,
                        "start": word.start,
                        "end": word.end,
                    })
        return " ".join(texts).strip(), words_info

def transcribe_full_batch(audio_np):
    text, words_info = _transcribe_segments(
        audio_np,
        language="ko",
        beam_size=5,
        vad_filter=True,
        condition_on_previous_text=True,
        word_timestamps=True,
    )
    for word in words_info:
        word.pop("start", None)
        word.pop("end", None)
    return text, words_info

def transcribe_for_sentence_detection(audio_np):
    """부분 전사는 속도가 중요하므로 beam_size를 낮추고 최근 오디오만 추론합니다."""
    return _transcribe_segments(
        audio_np,
        language="ko",
        beam_size=2,
        condition_on_previous_text=False,
        vad_filter=True, # 할루시네이션 방지를 위해 VAD 활성화 권장
        word_timestamps=False, # 종결 감지용이므로 타임스탬프 생략(속도 향상)
    )

def transcribe_checkpoint(audio_np):
    """마지막 checkpoint 이후 구간을 batch 전사한다.

    결과는 주제 이탈 판정용 초안이며, 최종 채점에는 사용하지 않는다.
    """
    return _transcribe_segments(
        audio_np,
        language="ko",
        beam_size=2,
        condition_on_previous_text=False,
        vad_filter=True,
        word_timestamps=False,
    )

async def detect_sentence_completion(state, websocket, session_id, send_partial):
    """문장 종결 후보를 찾고, 마지막 확정 지점 이후 구간을 batch checkpoint 전사한다."""
    if not CHECKPOINT_ENABLED or not state.partial_enabled:
        return

    current_epoch = state.epoch
    buffer_len = len(state.full_pcm_buffer)

    # 전체 버퍼에서 최근 3초 분량만 잘라냄 (앞부분은 버리고 뒷부분 유지 = 문맥 유지 및 연산량 고정)
    start_idx = max(0, buffer_len - CHECKPOINT_WINDOW_BYTES)
    audio_slice = bytes(state.full_pcm_buffer[start_idx:])
    audio_np = np.frombuffer(audio_slice, dtype=np.int16).astype(np.float32) / 32768.0

    detected_text, _ = await asyncio.to_thread(
        transcribe_for_sentence_detection,
        audio_np,
    )

    # 추론 도중 새로운 발화(reset)가 시작되었거나 텍스트가 없으면 종료
    if (
        state.epoch != current_epoch
        or not state.partial_enabled
        or not detected_text
    ):
        return

    if not is_sentence_completed(detected_text):
        return

    # 같은 지점의 반복 후보를 막는다.
    if state.last_eos_trigger_byte and buffer_len - state.last_eos_trigger_byte <= 32000:
        return
    state.last_eos_trigger_byte = buffer_len

    segment_start = state.checkpoint_start_byte
    segment_bytes = buffer_len - segment_start
    if segment_bytes < CHECKPOINT_MIN_BYTES:
        return

    checkpoint_slice = bytes(state.full_pcm_buffer[segment_start:buffer_len])
    checkpoint_np = np.frombuffer(checkpoint_slice, dtype=np.int16).astype(np.float32) / 32768.0
    checkpoint_text, _ = await asyncio.to_thread(
        transcribe_checkpoint,
        checkpoint_np,
    )

    if (
        state.epoch != current_epoch
        or not state.partial_enabled
        or not checkpoint_text
        or not is_sentence_completed(checkpoint_text)
        or buffer_len <= state.last_checkpoint_sent_byte
    ):
        return

    cumulative = " ".join((*state.checkpoint_texts, checkpoint_text)).strip()
    payload = {
        "type": "partial_transcript",
        "session_id": session_id,
        "seq": state.partial_seq + 1,
        "sentence": checkpoint_text,
        "cumulative": cumulative,
    }

    if not await send_partial(payload):
        print(f"[{session_id}] partial_transcript 전송 실패 - checkpoint 보류")
        return

    state.partial_seq += 1
    state.checkpoint_texts.append(checkpoint_text)
    state.checkpoint_start_byte = buffer_len
    state.last_checkpoint_sent_byte = buffer_len
    print(
        f"[{session_id}] Checkpoint #{state.partial_seq}: "
        f"'{checkpoint_text}'"
    )

    # 기존 Unity VAD 종료 임계값 변경은 checkpoint와 분리한다.
    if EMIT_ADAPTIVE_VAD_TRIGGER:
        await websocket.send_json({"type": "adaptive_vad_trigger", "is_completed": True})

@app.websocket("/ws/interview")
async def websocket_endpoint(websocket: WebSocket):
    await websocket.accept()
    session_id = websocket.query_params.get("session_id", "default")
    print(f"WebSocket Connected (Dual-Track STT Mode) - Session ID: {session_id}")

    state = AudioState()
    tts_ws = None
    tts_send_lock = asyncio.Lock()

    async def get_or_connect_tts_ws():
        nonlocal tts_ws
        if tts_ws is None or tts_ws.state != websockets.State.OPEN:
            try:
                ws_url_with_sid = f"{TTS_WORKER_WS_URL}?session_id={session_id}"
                tts_ws = await websockets.connect(ws_url_with_sid, max_size=None)
            except Exception as e:
                tts_ws = None
        return tts_ws

    async def send_partial_to_backend(payload):
        """기존 /ws/tts 연결로 checkpoint만 전달한다. TTS 생성은 백엔드가 하지 않는다."""
        nonlocal tts_ws
        for _ in range(2):
            try:
                ws = await get_or_connect_tts_ws()
                if ws is None:
                    raise RuntimeError("TTS backend WebSocket is unavailable")
                async with tts_send_lock:
                    await ws.send(json.dumps(payload, ensure_ascii=False))
                return True
            except Exception as e:
                print(f"[{session_id}] partial_transcript 전송 실패: {e}")
                tts_ws = None
        return False

    async def tts_relay_loop():
        nonlocal tts_ws
        while True:
            try:
                ws = await get_or_connect_tts_ws()
                if ws is None:
                    await asyncio.sleep(1.0)
                    continue

                async for msg in ws:
                    if isinstance(msg, str):
                        try:
                            event = json.loads(msg)
                            if event.get("type") == "end":
                                await websocket.send_json({"type": "tts_end"})
                            elif event.get("type") == "subtitle":
                                await websocket.send_text(msg)
                        except json.JSONDecodeError:
                            pass
                    else:
                        await websocket.send_bytes(msg)
            except (websockets.exceptions.ConnectionClosed, Exception):
                tts_ws = None
                await asyncio.sleep(1.0)

    relay_task = asyncio.create_task(tts_relay_loop())

    def spawn_sentence_detection_task():
        task = asyncio.create_task(
            detect_sentence_completion(
                state,
                websocket,
                session_id,
                send_partial_to_backend,
            )
        )
        state.inference_task = task
        def clear_task(done_task):
            if state.inference_task is done_task:
                state.inference_task = None
        task.add_done_callback(clear_task)

    try:
        while True:
            message = await websocket.receive()

            if "bytes" in message:
                chunk = message["bytes"]

                if chunk[:4] == b'RIFF':
                    if len(state.full_pcm_buffer) > 0:
                        state.reset()
                    pcm_payload = chunk[44:]
                else:
                    pcm_payload = chunk

                state.full_pcm_buffer.extend(pcm_payload)

                # 0.5초마다 문장 종결 후보를 확인한다.
                unprocessed_for_partial = len(state.full_pcm_buffer) - state.last_partial_trigger_byte
                if (
                    CHECKPOINT_ENABLED
                    and state.partial_enabled
                    and unprocessed_for_partial >= CHECKPOINT_TRIGGER_BYTES
                    and (state.inference_task is None or state.inference_task.done())
                ):
                    state.last_partial_trigger_byte = len(state.full_pcm_buffer)
                    spawn_sentence_detection_task()

            elif "text" in message:
                data = json.loads(message["text"])
                msg_type = data.get("type")
                final_text = ""
                features = {}
                truncated = False

                if msg_type == "discard":
                    state.reset()
                    try: await websocket.send_json({"type": "tts_end"})
                    except: pass
                    continue

                # ... (send_anyway, utterance_end 처리 로직은 기존과 100% 동일하므로 이하 생략 없이 그대로 유지) ...
                elif msg_type == "send_anyway":
                    text_to_send = data.get("text", "")
                    features = data.get("features")
                    pause_cnt = features.get("meaningfulPauseCount", features.get("pauseCount", 0)) if features else 0
                    final_dto = {
                        "sttText": text_to_send,
                        "speakingTime": features.get("speakingTime", 0.0) if features else 0.0,
                        "pauseCount": pause_cnt,
                        "meaningfulPauseCount": pause_cnt,
                        "volumeVariance": features.get("volumeVariance", 0.0) if features else 0.0,
                        "lowVolumeRatio": features.get("lowVolumeRatio", 0.0) if features else 0.0,
                        "averageVolume": features.get("averageVolume", 0.0) if features else 0.0,
                        "responseTime": features.get("responseTime", 0.0) if features else 0.0
                    }

                    state.reset()
                    await websocket.send_json({"type": "final", "data": final_dto})

                    if text_to_send:
                        final_text = text_to_send
                    else:
                        try: await websocket.send_json({"type": "stt_skip", "reason": "empty_text"})
                        except: pass
                        continue

                elif msg_type in ("utterance_end", "utterance_abort"):
                    is_abort = msg_type == "utterance_abort"
                    truncated = is_abort
                    features = data.get("features")
                    if not isinstance(features, dict):
                        features = {}
                    try:
                        speaking_time = float(features.get("speakingTime", 0.0))
                    except (TypeError, ValueError):
                        speaking_time = 0.0

                    if (
                        not is_abort
                        and (not math.isfinite(speaking_time) or speaking_time <= 0.0)
                    ):
                        state.reset()
                        try: await websocket.send_json({"type": "stt_skip", "reason": "invalid_speaking_time"})
                        except: pass
                        continue

                    if len(state.full_pcm_buffer) == 0:
                        state.reset()
                        try: await websocket.send_json({"type": "stt_skip", "reason": "empty_buffer"})
                        except: pass
                        continue

                    if is_abort:
                        # 개입 확정 이후에는 늦게 끝난 checkpoint가 결과를 보내지 못하게 한다.
                        state.partial_enabled = False
                        state.invalidate_inference()
                    elif state.inference_task is not None:
                        partial_task = state.inference_task
                        try:
                            await asyncio.gather(partial_task, return_exceptions=True)
                        finally:
                            if state.inference_task is partial_task:
                                state.inference_task = None

                    total_dur = len(state.full_pcm_buffer) / 32000.0
                    try:
                        raw_pcm = state.full_pcm_buffer
                        audio_np = np.frombuffer(raw_pcm, dtype=np.int16).astype(np.float32) / 32768.0
                        final_text, words_info = transcribe_full_batch(audio_np)
                        is_correction = data.get("mode") == "correction"
                        avg_confidence = sum([w["probability"] for w in words_info]) / len(words_info) if words_info else 1.0

                        if is_correction:
                            original_words = data.get("original_words", [])
                            target_range = data.get("target_range", [0, 0])
                            new_words = [w["word"] for w in words_info]
                            start_idx, end_idx = target_range[0], target_range[1]
                            if 0 <= start_idx <= end_idx < len(original_words):
                                merged_words = original_words[:start_idx] + new_words + original_words[end_idx+1:]
                            else:
                                merged_words = new_words if new_words else original_words
                            final_text = " ".join(merged_words).strip()

                        if not final_text:
                            state.reset()
                            try: await websocket.send_json({"type": "stt_skip", "reason": "empty_transcription"})
                            except: pass
                            continue

                        pause_cnt = features.get("meaningfulPauseCount", features.get("pauseCount", 0)) if features else 0
                        final_dto = {
                            "sttText": final_text,
                            "speakingTime": features.get("speakingTime", 0.0) if features else 0.0,
                            "pauseCount": pause_cnt,
                            "meaningfulPauseCount": pause_cnt,
                            "volumeVariance": features.get("volumeVariance", 0.0) if features else 0.0,
                            "lowVolumeRatio": features.get("lowVolumeRatio", 0.0) if features else 0.0,
                            "averageVolume": features.get("averageVolume", 0.0) if features else 0.0,
                            "responseTime": features.get("responseTime", 0.0) if features else 0.0
                        }

                        # correction_request는 호환성을 위해 로직을 보존하지만
                        # 임계값 0.0에서는 절대 발생하지 않는다.
                        if (
                            not is_correction
                            and CORRECTION_CONFIDENCE_THRESHOLD > 0.0
                            and avg_confidence < CORRECTION_CONFIDENCE_THRESHOLD
                        ):
                            words_list = [w["word"] for w in words_info]
                            confidences_list = [w["probability"] for w in words_info]
                            state.reset()
                            await websocket.send_json({
                                "type": "correction_request",
                                "data": final_dto,
                                "words": words_list,
                                "word_confidences": confidences_list
                            })
                            continue

                        final_dto["truncated"] = is_abort
                        state.reset()
                        await websocket.send_json({"type": "final", "data": final_dto})

                    except Exception as e:
                        state.reset()
                        try: await websocket.send_json({"type": "stt_skip", "reason": "transcription_error"})
                        except: pass
                        continue

                # ==================== TTS / LLM Streaming Pipeline ====================
                if not final_text:
                    try: await websocket.send_json({"type": "stt_skip", "reason": "empty_final_text"})
                    except: pass
                else:
                    sent_successfully = False
                    for attempt in range(2):
                        try:
                            ws = await get_or_connect_tts_ws()
                            if ws is not None:
                                async with tts_send_lock:
                                    await ws.send(json.dumps({
                                        "text": final_text,
                                        "session_id": session_id,
                                        "features": features if features else {},
                                        "truncated": truncated,
                                    }))
                                sent_successfully = True
                                break
                            else:
                                raise Exception("TTS WebSocket is None")
                        except Exception as tts_e:
                            tts_ws = None

                    if not sent_successfully:
                        try: await websocket.send_json({"type": "tts_end"})
                        except: pass

                state.reset()

    except WebSocketDisconnect:
        pass
    except asyncio.CancelledError:
        raise
    except Exception as e:
        try: await websocket.close()
        except: pass
    finally:
        relay_task.cancel()
        if tts_ws is not None:
            try:
                if tts_ws.state == websockets.State.OPEN:
                    await tts_ws.close()
            except: pass

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)
