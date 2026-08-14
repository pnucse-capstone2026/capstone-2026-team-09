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

    def reset(self):
        """부분 전사 태스크를 무효화하고 현재 발화 상태를 초기화합니다."""
        self.epoch += 1
        if self.inference_task is not None and not self.inference_task.done():
            self.inference_task.cancel()
        self.inference_task = None
        self.full_pcm_buffer.clear()
        self.last_partial_trigger_byte = 0
        self.last_eos_trigger_byte = 0


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

async def detect_sentence_completion(state, websocket, session_id):
    """[수정됨] Sliding Window 방식: 전체 오디오가 아닌 최근 N초 구간만 추출하여 종결 감지"""
    current_epoch = state.epoch
    buffer_len = len(state.full_pcm_buffer)
    
    # 윈도우 크기: 최근 3초 (16000Hz * 2bytes * 3sec = 96,000 bytes)
    SLIDING_WINDOW_BYTES = 96000
    
    # 전체 버퍼에서 최근 3초 분량만 잘라냄 (앞부분은 버리고 뒷부분 유지 = 문맥 유지 및 연산량 고정)
    start_idx = max(0, buffer_len - SLIDING_WINDOW_BYTES)
    audio_slice = bytes(state.full_pcm_buffer[start_idx:])
    audio_np = np.frombuffer(audio_slice, dtype=np.int16).astype(np.float32) / 32768.0

    detected_text, _ = await asyncio.to_thread(
        transcribe_for_sentence_detection,
        audio_np,
    )

    # 추론 도중 새로운 발화(reset)가 시작되었거나 텍스트가 없으면 종료
    if state.epoch != current_epoch or not detected_text:
        return

    if is_sentence_completed(detected_text):
        # 중복 트리거 방지: 이전에 종결 신호를 보낸 후 1초(32000 bytes) 분량의 새로운 오디오가 없다면 무시
        if buffer_len - state.last_eos_trigger_byte > 32000:
            state.last_eos_trigger_byte = buffer_len
            print(f"[{session_id}] Sentence completion detected: '{detected_text}'")
            await websocket.send_json({"type": "adaptive_vad_trigger", "is_completed": True})

@app.websocket("/ws/interview")
async def websocket_endpoint(websocket: WebSocket):
    await websocket.accept()
    session_id = websocket.query_params.get("session_id", "default")
    print(f"WebSocket Connected (Dual-Track STT Mode) - Session ID: {session_id}")

    state = AudioState()
    tts_ws = None

    async def get_or_connect_tts_ws():
        nonlocal tts_ws
        if tts_ws is None or tts_ws.state != websockets.State.OPEN:
            try:
                ws_url_with_sid = f"{TTS_WORKER_WS_URL}?session_id={session_id}"
                tts_ws = await websockets.connect(ws_url_with_sid, max_size=None)
            except Exception as e:
                tts_ws = None
        return tts_ws

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
        task = asyncio.create_task(detect_sentence_completion(state, websocket, session_id))
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

                # [수정됨] 0.5초(16000 바이트)마다 부분 전사 트리거 실행
                unprocessed_for_partial = len(state.full_pcm_buffer) - state.last_partial_trigger_byte
                if (
                    unprocessed_for_partial >= 16000 
                    and (state.inference_task is None or state.inference_task.done())
                ):
                    state.last_partial_trigger_byte = len(state.full_pcm_buffer)
                    spawn_sentence_detection_task()

            elif "text" in message:
                data = json.loads(message["text"])
                msg_type = data.get("type")

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

                elif msg_type == "utterance_end":
                    features = data.get("features")
                    if not isinstance(features, dict):
                        features = {}
                    try:
                        speaking_time = float(features.get("speakingTime", 0.0))
                    except (TypeError, ValueError):
                        speaking_time = 0.0

                    if not math.isfinite(speaking_time) or speaking_time <= 0.0:
                        state.reset()
                        try: await websocket.send_json({"type": "stt_skip", "reason": "invalid_speaking_time"})
                        except: pass
                        continue

                    if len(state.full_pcm_buffer) == 0:
                        state.reset()
                        try: await websocket.send_json({"type": "stt_skip", "reason": "empty_buffer"})
                        except: pass
                        continue

                    if state.inference_task is not None:
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

                        if not is_correction and avg_confidence < 0.75:
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
                                await ws.send(json.dumps({
                                    "text": final_text,
                                    "session_id": session_id,
                                    "features": features if features else {}
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