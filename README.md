# VRoom Vision Worker (`vision_process`)

졸업과제 **VRoom**(AI 모의면접 시뮬레이터)의 **웹캠 기반 행동 데이터 수집 모듈**입니다.

이 워커는 웹캠 영상에서 **시선 / 손 사용 / 자세 / 표정** 네 가지 비언어적 지표를 추출해
백엔드로 전달합니다. 중간보고서 5.2에 기술한 **행동 지표(시선 안정도, 자세 변화)** 의
자동 측정을 담당하며, 결과 UI 10개 평가 항목 중 **1~4번**을 채웁니다.

> 이 문서 하나로 **어느 팀원이든 자기 집 PC에서 이 워커를 띄우고 검증**할 수 있도록
> 모든 절차를 담았습니다. GPU가 필요 없으므로 노트북에서도 그대로 돌아갑니다.

---

## 0. 4개 레포에서의 위치

| 레포 | 담당 | 실행 프로그램 | 포트 |
|:--|:--|:--|:--|
| `VR_Interview_Simulator` | 프론트 | Unity 클라이언트 | — |
| `VRoom_Backend` | 프론트/백엔드 | FastAPI 백엔드 | 8080 |
| `verbal_process` | 음성 처리 | STT 워커 / TTS 워커 (Docker, GPU) | 8000 / 8001 |
| `vision_process` | 프론트/백엔드 | Vision 워커 (venv, CPU) | 8002 |

```
   Unity 클라이언트                          Vision 워커 (이 레포)
┌────────────────────────┐              ┌──────────────────────────┐
│  BehaviorCollector     │  ws 8002     │  CameraPump (웹캠 직접)   │
│   └ 턴 마커(JSON) ─────┼─────────────▶│   └ MediaPipe Face+Pose  │
│                        │◀─ 캘리브 결과 │      ↓ 프레임별 지표      │
│  ※ 영상은 보내지 않음   │              │   SessionAggregator      │
└────────────────────────┘              │      ↓ 턴 단위 집계       │
                                        └──────────┬───────────────┘
   FastAPI 백엔드 :8080                            │ POST /vision
┌────────────────────────┐                         │ (턴당 1회, ~1KB)
│  InterviewSession      │◀────────────────────────┘
│   ├ collect_vision_features()                     
│   └ _score_vision()  ← VisionScoringConfig 로 0~10점 환산
└────────────────────────┘
```

### 설계 원칙

- **웹캠은 워커가 직접 엽니다.** Unity는 영상 프레임을 전혀 다루지 않고,
  `turn_start` / `turn_end` / `calibrate_*` 텍스트 마커만 보냅니다.
  → 워커를 Unity와 **다른 PC**에 둘 수 있습니다(원격 실험 구성).
- **워커는 원시 피쳐만 1차 가공**합니다. 점수 환산은 전부 백엔드
  `VisionScoringConfig`가 담당합니다. 음성 파이프라인에서 VAD가 특징값만 뽑고
  백엔드가 채점하는 것과 동일한 역할 분담입니다.
- **Docker를 쓰지 않습니다.** MediaPipe는 CPU 전용이라 CUDA 격리가 불필요합니다.
  `pip install` 한 줄이면 끝이고, WSL2 포트 프록시(`netsh portproxy`)도 필요 없습니다.

---

## 1. 준비물

| 항목 | 요구사항 |
|:--|:--|
| Python | **3.11 / 3.12 / 3.13** (3.13에서 동작 확인 완료) |
| 웹캠 | 내장캠 또는 USB 웹캠. **어깨가 프레임에 들어와야 함**(자세·손짓 측정에 필수) |
| GPU | **불필요** (MediaPipe CPU 추론) |
| 부하 | 코어 1개의 약 25%, RAM 약 700MB, 디스크 약 200MB |
| 네트워크 | 백엔드(8080)와 통신. 같은 PC면 `127.0.0.1`, 다른 PC면 Tailscale |

---

## 2. 설치 (최초 1회)

```bash
git clone https://github.com/VRoomInterviewSimulator/vision_process.git
cd vision_process
mkdir logs

python -m venv .venv
.venv\Scripts\activate          # Windows
# source .venv/bin/activate     # macOS / Linux

python -m pip install --upgrade pip
pip install mediapipe fastapi uvicorn[standard] httpx python-dotenv
```

> **PowerShell에서는** 대괄호를 와일드카드로 해석하므로 `"uvicorn[standard]"` 처럼
> 따옴표를 붙이세요. cmd에서는 불필요합니다.

### OpenCV 주의

MediaPipe가 `opencv-contrib-python`을 의존성으로 함께 설치합니다.
여기에 `opencv-python` 이나 `opencv-python-headless` 를 **추가로 설치하면 cv2가 충돌**해
import가 깨집니다. `import cv2` 가 실패할 때만 아래를 실행하세요.

```bash
pip install opencv-contrib-python
```

### 모델 파일

`models/` 폴더에 두 개의 `.task` 파일이 레포에 포함되어 있습니다(합계 약 9MB).
없다면 아래로 받으세요.

```bash
curl -L -o models/face_landmarker.task \
  https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task
curl -L -o models/pose_landmarker_lite.task \
  https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/1/pose_landmarker_lite.task
```

`face_landmarker.task` 약 3.7MB, `pose_landmarker_lite.task` 약 5.5MB.
몇 KB짜리면 HTML 에러 페이지를 받은 것이니 다시 받으세요.

### 설치 검증

```bash
python check_env.py
```

`✅ 전부 통과` 가 나와야 합니다. Python 버전, mediapipe/cv2/numpy 버전,
Tasks API 존재 여부, 모델 로드, 더미 프레임 추론까지 한 번에 확인합니다.

---

## 3. 설정 (`.env`)

레포 루트에 `.env` 파일을 만듭니다. **PC마다 값이 다르므로 커밋하지 않습니다**(`.gitignore` 포함).

```ini
# 백엔드 주소. 같은 PC면 127.0.0.1, 다른 PC면 백엔드 PC의 Tailscale IP
BACKEND_URL=http://127.0.0.1:8080

# 웹캠. list_cameras.py 결과로 채울 것
CAM_INDEX=0
CAM_BACKEND=DSHOW

# 분석 프레임레이트. CPU가 부족하면 6으로 낮출 것
CAM_FPS=10

# 1이면 OpenCV 창으로 웹캠 프리뷰 표시 (카메라 위치 잡을 때만)
SHOW_PREVIEW=0

# CSV 로그 경로 (선택). 미지정 시 ./logs
# VISION_LOG_DIR=C:\Users\me\OneDrive\vroom_logs
```

| 변수 | 기본값 | 설명 |
|:--|:--|:--|
| `BACKEND_URL` | `http://127.0.0.1:8080` | 턴 피쳐를 POST할 백엔드 주소 |
| `CAM_INDEX` | `0` | OpenCV 카메라 인덱스 |
| `CAM_BACKEND` | `DSHOW` | `DSHOW` / `MSMF` / `ANY` |
| `CAM_FPS` | `10` | MediaPipe 추론 주기 |
| `SHOW_PREVIEW` | `0` | OpenCV 프리뷰 창 |
| `VISION_LOG_DIR` | `logs` | CSV 저장 위치 |

### 웹캠 인덱스 찾기

```bash
python list_cameras.py
```

`CAP_DSHOW` / `CAP_MSMF` / `CAP_ANY` 세 백엔드에서 인덱스 0~5를 훑어
실제로 프레임이 나오는 조합을 알려줍니다. 그 값을 `.env` 에 넣으세요.

> 실행 전에 **웹캠을 점유하는 앱을 모두 종료**하세요.

---

## 4. 실행

```bash
.venv\Scripts\activate
python -m uvicorn server:app --host 0.0.0.0 --port 8002
```

성공 시:

```
[Vision] MediaPipe 모델 로드 완료
[Camera] ✅ index=0 backend=DSHOW 640x480 target=10.0fps
INFO:     Uvicorn running on http://0.0.0.0:8002
```

### 실행 순서

Vision 워커는 **Unity보다 먼저** 떠 있어야 합니다. 나중에 뜨면 `BehaviorCollector` 가
연결 실패로 시각 4항목을 통째로 건너뜁니다.

```
1. TTS 워커     (8001)   → verbal_process
2. STT 워커     (8000)   → verbal_process
3. Vision 워커  (8002)   → 이 레포
4. 백엔드       (8080)   → VRoom_Backend
5. Unity Play (SetupScene)
```

### 카메라 프레이밍 확인 — 이 단계를 건너뛰지 마세요

브라우저에서 `http://127.0.0.1:8002/preview` 를 열고,
**평소 앉는 자세로 양쪽 어깨가 프레임에 들어오는지** 확인하세요.

어깨가 잘리면 `poseDetectedRatio` 가 0.5 미만이 되어 **손짓·자세 두 항목이
중립 5점으로 고정**됩니다. 카메라를 위로 올리거나 뒤로 물러나세요.

---

## 5. 배치 구성 (두 가지)

### A. 단독 구성 — 개발·디버깅용

Unity, 백엔드, Vision 워커, 웹캠이 모두 같은 PC.

```ini
# .env
BACKEND_URL=http://127.0.0.1:8080
```
```
Unity Inspector → MultimodalRig → Vision Stream Client → Ws Url
  ws://127.0.0.1:8002/ws/vision
```

방화벽·Tailscale 설정이 전혀 필요 없습니다. 팀원이 각자 집에서 검증할 때는 이 구성입니다.

### B. 원격 구성 — 실험·시연용

클라이언트 노트북(웹캠 + Vision 워커) ↔ 호스트 데스크탑(Unity + 백엔드 + STT/TTS).
사용자는 노트북에서 Moonlight로 데스크탑 화면을 보고, 마이크는 SonoBus로 전달합니다.

```
노트북 (참가자)                          데스크탑 (호스트)
├─ Moonlight 클라이언트 ◀─ 화면/음성 ───┤ Unity + 백엔드 + STT + TTS
├─ 마이크 → SonoBus ────── 음성 ───────▶│
├─ 웹캠                                 │
└─ Vision 워커 :8002 ◀── 턴 마커 ───────┤ Unity
                     └── POST /vision ─▶│ 백엔드
```

```ini
# 노트북의 .env
BACKEND_URL=http://<데스크탑 Tailscale IP>:8080
```
```
Unity Ws Url = ws://<노트북 Tailscale IP>:8002/ws/vision
백엔드 기동  = uvicorn app.main:app --host 0.0.0.0 --port 8080
```

**방화벽** (각 PC의 관리자 PowerShell):

```powershell
# 노트북
New-NetFirewallRule -DisplayName "VRoom Vision 8002" -Direction Inbound -LocalPort 8002 -Protocol TCP -Action Allow
# 데스크탑
New-NetFirewallRule -DisplayName "VRoom Backend 8080" -Direction Inbound -LocalPort 8080 -Protocol TCP -Action Allow
```

**Tailscale 직결 확인** — 이게 릴레이로 떨어지면 Moonlight/SonoBus가 못 버팁니다.

```bash
tailscale ping <상대 머신 이름>
# via 192.168.x.x 또는 via 100.x  → 직결 ✅
# via DERP(...)  가 계속되면      → 릴레이 ❌
```

**웹캠 프레임은 네트워크를 건너지 않습니다.** 노트북 로컬에서 MediaPipe가 돌고
결과 숫자 20개만 턴당 1회 전송되므로, 네트워크 부담은 사실상 0입니다.

---

## 6. 파일 구조

```
vision_process/
├── server.py           # FastAPI 앱 + CameraPump(웹캠 캡처 스레드) + WS/HTTP 엔드포인트
├── analyzer.py         # 프레임 1장 → MediaPipe 추론 → 원시 지표 dict
├── aggregator.py       # 턴 구간 프레임들 → 백엔드로 보낼 피쳐 dict (+ 캘리브레이션)
├── models/
│   ├── face_landmarker.task
│   └── pose_landmarker_lite.task
├── logs/               # 턴 피쳐 CSV, 디버그 프레임 (gitignore)
├── check_env.py        # 설치 검증
├── list_cameras.py     # 카메라 인덱스/백엔드 탐색
├── smoke_test.py       # Unity 없이 지표 검증 + 임계값 측정
├── audit_worker.py     # 코드 적용 상태 자동 점검
├── diag_hands.py       # 손목 랜드마크 진단 (손짓 지표 문제 시)
├── .env                # PC별 설정 (gitignore)
└── README.md
```

### 모듈 역할

| 파일 | 역할 |
|:--|:--|
| `analyzer.py` | `FrameAnalyzer` — Face Landmarker(블렌드셰이프 52종 + 두부 변환행렬)와 Pose Landmarker(33 랜드마크)를 VIDEO 모드로 실행. 프레임당 두부 각도, 안구 이탈, 눈깜빡임, 표정 벡터, 어깨/손목/코 좌표를 반환 |
| `aggregator.py` | `SessionAggregator` — 캘리브레이션 기준값 산출, 턴 구간 프레임 누적, 턴 종료 시 20개 피쳐로 집계. **피쳐 추출 임계값**(정면 허용 각도 등)이 여기 상수로 있음 |
| `server.py` | `CameraPump` — 웹캠을 워커 기동 시 열어 끝까지 유지하고, 세션이 바인딩된 상태에서 턴/캘리브레이션 중일 때만 추론 수행(별도 스레드). WebSocket 제어 채널과 진단용 HTTP 엔드포인트 제공 |

---

## 7. 통신 명세

### 엔드포인트

| 경로 | 종류 | 용도 |
|:--|:--|:--|
| `/ws/vision` | WebSocket | Unity → 워커 (턴 마커 **텍스트 전용**) |
| `/health` | HTTP GET | 상태 확인. `backend` 주소와 `camera` 개방 여부 |
| `/status` | HTTP GET | 카메라 설정, 세션 바인딩, 턴 진행 상태, 분석 프레임 수, 읽기 실패 누적 |
| `/snapshot` | HTTP GET | 현재 프레임 1장 (JPEG) |
| `/preview` | HTTP GET | 0.5초마다 갱신되는 프리뷰 페이지 |

### Unity → 워커 (텍스트 프레임)

```jsonc
{"type":"calibrate_start"}                  // 캘리브레이션 시작
{"type":"calibrate_end"}                    // 종료 → 기준값 산출
{"type":"turn_start","stage":"SELF_INTRO"}  // 사용자 차례 시작
{"type":"turn_end"}                         // 발화 종료 → 집계 후 백엔드 POST
```

### 워커 → Unity

```jsonc
{"type":"calibrated","ok":true,"samples":23}  // 캘리브레이션 결과
{"type":"camera_error"}                        // 웹캠 개방 실패 → 연결 종료
```

### 워커 → 백엔드 (`POST /vision`)

```jsonc
{
  "session_id": "default",
  "features": {
    "stage": "SELF_INTRO", "durationSec": 11.2, "frameCount": 85,
    "faceDetectedRatio": 1.0, "poseDetectedRatio": 1.0, "calibrated": true,
    "gazeOnTargetRatio": 0.635, "headYawStd": 0.84, "headPitchStd": 1.86,
    "shoulderTiltMean": 4.97, "bodySwayStd": 0.0103, "torsoDriftMean": 0.0131,
    "handUsageRatio": 0.0, "handExtent": 0.0, "faceTouchCount": 0,
    "handMotionEnergy": 0.0,
    "expressionVariance": 0.0168, "smileRatio": 0.0, "frownRatio": 0.0,
    "blinkPerMinute": 0.0
  }
}
```

백엔드에 활성 세션이 없으면 `409`를 반환합니다(Unity가 먼저 `init`을 보내야 함).

### 턴 구간 정의

**면접관 질문 오디오 재생 완료 → 사용자 발화 종료**

즉 *생각하는 동안의 행동*까지 포함합니다. 시선 회피와 자세 흔들림은 답변 중보다
생각하는 구간에서 더 크게 나타나므로, 능동 개입 조건의 압박 효과를 잡아내려면
이 구간이 반드시 포함돼야 합니다.

이 때문에 `durationSec` 은 음성의 `speakingTime` 보다 항상 깁니다. **두 값을 섞어 쓰지 마세요.**

---

## 8. 피쳐 명세

| 키 | 의미 | 대응 평가 항목 |
|:--|:--|:--|
| `gazeOnTargetRatio` | 정면(캘리브레이션 기준) 응시 프레임 비율 | 시선 처리 |
| `headYawStd` / `headPitchStd` | 두부 각도 표준편차(deg) — 시선 흔들림 | 시선 처리 |
| `shoulderTiltMean` | 어깨선 기울기 평균(deg) | 자세 안정성 |
| `bodySwayStd` | 어깨너비 정규화 상체 흔들림 표준편차 | 자세 안정성 |
| `torsoDriftMean` | 캘리브레이션 대비 몸통 중심 이탈 | **채점 미사용** (아래 참조) |
| `handUsageRatio` | 손목이 프레임 안 + 가슴 높이 이상인 프레임 비율 | 손 사용 |
| `handExtent` | 손목 좌표의 공간 확산 | 손 사용 |
| `faceTouchCount` | 얼굴 만지기 횟수 (0.6초 이상 체류 시 1회) | 손 사용 |
| `expressionVariance` | 표정 블렌드셰이프 축별 표준편차의 평균 | 표정 변화 |
| `smileRatio` / `frownRatio` | 미소 / 찌푸림 프레임 비율 | 표정 변화 |
| `blinkPerMinute` | 분당 눈깜빡임 | **채점 미사용** (아래 참조) |
| `faceDetectedRatio` / `poseDetectedRatio` | 검출률 — 0.5 미만이면 해당 항목 채점 제외 | 신뢰도 가드 |
| `frameCount` / `durationSec` | 집계 구간 크기 | 품질 확인 |
| `handMotionEnergy` | (폐기) 손목 이동 속도 p80 | 분석 기록용 |

### 채점에서 제외한 두 지표

**`torsoDriftMean`** — 실측에서 턴 순서를 따라 단조 증가했습니다
(0.013 → 0.015 → 0.044 → 0.075 → 0.091). 자세가 나빠진 것이 아니라
**캘리브레이션 이후 경과 시간**을 재고 있습니다. 사람은 시간이 지나면 자연히
자세를 조정하기 때문입니다. 켜두면 면접 뒤쪽 턴만 체계적으로 감점되어
실험 조건 비교가 오염됩니다. CSV에는 계속 기록됩니다.

**`blinkPerMinute`** — 눈을 감은 구간은 100~150ms인데 10fps에서는 1~1.5프레임이라
상향 돌파를 놓칩니다. 개인차도 커서(`eyeBlink` 최대값이 0.34인 피험자 확인)
신뢰할 수 없습니다. CSV 기록용으로만 씁니다.

**`handMotionEnergy`** — 초기 설계에서는 손목 이동 속도를 손짓 지표로 썼으나,
MediaPipe가 **프레임 밖 관절도 추정값을 반환**하기 때문에 가만히 있을 때와
손짓할 때가 5 vs 6으로 거의 구분되지 않았습니다(둘 다 모델 노이즈).
`handUsageRatio`(손이 올라와 있는 프레임 비율)로 재정의한 뒤 **0.0 vs 0.52~0.86** 으로
명확히 분리됐습니다.

---

## 9. 캘리브레이션

시선 채점은 **캘리브레이션 없이는 무의미합니다.** 웹캠이 모니터 위에 있느냐 아래에
있느냐, 사용자가 화면 정중앙에 앉았느냐에 따라 "정면"의 절대 각도가 사람마다
10~20도씩 다릅니다.

- Unity `BehaviorCollector` 가 면접 씬 진입 직후 **3초간** 자동 수행합니다.
- 이 3초 동안 사용자가 **실제로 화면 중앙(면접관 얼굴)을 응시**해야 합니다.
- 워커 콘솔에 `캘리브레이션 결과: {'yaw': ..., 'pitch': ..., 'samples': 23}` 이 뜨면 성공,
  `None` 이면 얼굴 미검출입니다(조명 확인 후 재시작).
- 실패해도 면접은 정상 진행되며, `calibrated: false` 로 전송되어 기준값 의존 감점만
  건너뜁니다.

---

## 10. 검증 스크립트

각자 집에서 아래 순서로 돌리면 워커가 정상인지 확인할 수 있습니다.

| 스크립트 | 용도 | 언제 |
|:--|:--|:--|
| `check_env.py` | 설치·모델·API 검증 | 설치 직후 |
| `list_cameras.py` | 카메라 인덱스/백엔드 탐색 | `.env` 작성 전 |
| `smoke_test.py` | 지표 검증 + 임계값 측정 | 카메라 배치 확정 후 |

### `smoke_test.py` — 지표 검증

Unity와 백엔드 없이 웹캠만으로 전체 지표를 확인합니다.
운영과 동일하게 10fps로 스로틀합니다.

```bash
python smoke_test.py
```

| 키 | 동작 |
|:--|:--|
| `c` | 3초 캘리브레이션 (화면 중앙 응시) |
| `s` | 턴 시작 |
| `e` | 턴 종료 → 피쳐 출력 + 실측 fps |
| `q` | 종료 |

> 키 입력은 **OpenCV 창이 포커스를 가진 상태**에서 받습니다. 콘솔이 아니라 영상 창을
> 클릭하고 누르세요.

**검증 절차** — `c` 로 캘리브레이션 후 아래 4회를 각 10초씩:

| # | 행동 | 확인할 값 | 기대 |
|:-:|:--|:--|:--|
| 1 | 정면 응시 | `gazeOnTargetRatio` | 0.85 이상 |
| 2 | 계속 딴 데 보기 | `gazeOnTargetRatio` | 0.3 이하 |
| 3 | 바른 자세 | `bodySwayStd` | 0.01 ~ 0.02 |
| 4 | 좌우로 몸 흔들기 + 손짓 | `bodySwayStd` / `handUsageRatio` | 0.06 이상 / 0.3 이상 |

1·2가 구분되지 않으면 `aggregator.py` 의 `GAZE_YAW_TOL`(15.0) / `GAZE_PITCH_TOL`(12.0)을,
`handUsageRatio` 가 둘 다 0이면 `HAND_HEIGHT_LIMIT`(1.2)을 조정하세요.

고개를 **좌우로 돌릴 때 `yaw`**, **위아래로 들 때 `pitch`** 가 변해야 합니다.
반대로 반응하면 `analyzer.py` 의 `TRANSPOSE_HEAD_MATRIX = True` 로 바꾸세요.

---

## 11. 임계값 튜닝

**두 층위의 상수가 있습니다. 역할이 다르니 헷갈리지 마세요.**

| 위치 | 역할 | 예시 |
|:--|:--|:--|
| `aggregator.py` (이 레포) | **피쳐 추출 임계값** — 무엇을 "정면 응시"로 볼 것인가 | `GAZE_YAW_TOL`, `HAND_HEIGHT_LIMIT`, `BLINK_ON` |
| `VRoom_Backend/app/config.py` `VisionScoringConfig` | **감점 계수** — 그 값이 몇 점인가 | `GAZE_RATIO_FULL`, `POSTURE_SWAY_TOLERANCE` |

실험 조건별 튜닝은 백엔드 상수 한 곳에서만 하면 됩니다.

### ⚠️ 카메라를 바꾸면 다시 재야 합니다

`bodySwayStd`, `handUsageRatio`, `shoulderTiltMean`, `expressionVariance` 는
**카메라 거리·각도에 따라 스케일이 2배까지 달라집니다.**
노트북 내장캠에서 잰 값은 데스크탑 USB 웹캠에서 맞지 않습니다.

**실험에 쓸 장비 구성 그대로** 측정한 값으로 `VisionScoringConfig` 를 잡으세요.
그리고 실험 중에는 카메라 위치를 고정하세요(테이블 테이프 표시, 힌지 각도 메모).

### 현재 상수의 근거 (2026-08-07, 노트북 내장캠 실측)

| 지표 | 정상 | 이상 행동 | 설정된 허용치 |
|:--|:--|:--|:--|
| `gazeOnTargetRatio` | 1.0 | 0.033 (시선 회피) | 0.80 만점 / 0.30 이하 0점 |
| `bodySwayStd` | 0.009~0.019 | 0.097 (몸 흔들기) | 0.030 초과부터 감점 |
| `handUsageRatio` | 0.0 (미사용) | 0.52~0.86 (손짓) | 0.15~0.75 만점 |
| `shoulderTiltMean` | 1.5~5.3 | — | 8.0 초과부터 감점 |
| `expressionVariance` | 0.007~0.017 | — | 0.012 미만이면 경직 감점 |

---

## 12. 실험 데이터 로깅

턴 피쳐는 백엔드 전송과 별도로 `logs/<session_id>.csv` 에 누적됩니다.
**여기 저장되는 원시 피쳐가 곧 논문의 종속변수입니다.** 0~10 점수만 남기면
통계 분석을 할 수 없습니다.

`session_id` 에 참가자와 실험 조건을 인코딩하면 파일이 자동으로 분리됩니다.

```
logs/P03_condB_run1.csv
logs/P03_condC_run1.csv
```

Unity의 `InterviewConfig.SessionId` 를 SetupScene의 txt 설정에서 조립하면 됩니다.

**주의**
- CSV는 **회차마다 누적**됩니다. `recordedAt` 컬럼으로 구분하거나, 회차별로
  `session_id` 를 다르게 두세요.
- 원격 구성에서는 CSV가 **워커가 도는 PC**(노트북)에 쌓입니다. 실험 후 회수를
  잊지 마세요. `VISION_LOG_DIR` 를 클라우드 동기화 폴더로 지정해 두면 안전합니다.

---

## 13. 트러블슈팅

| 증상 | 원인 / 해결 |
|:--|:--|
| `ModuleNotFoundError: cv2` | venv 활성화 확인 → `pip install mediapipe` 재실행 |
| `import cv2` ImportError | opencv 패키지 중복. `pip uninstall opencv-python opencv-python-headless` 후 `pip install --force-reinstall opencv-contrib-python` |
| `pip install mediapipe` 실패 | Python 3.10 이하. 3.11~3.13으로 venv 재생성 |
| `uvicorn server:app` 임포트 실패 | 파일명이 `server.py` 인지, venv 활성화됐는지, 레포 루트에서 실행했는지. `python -m uvicorn` 권장 |
| 모델 로드 에러 | `models/` 상대경로 문제. 반드시 레포 루트에서 실행 |
| `[Camera] ❌ ... 열기 실패` | 다른 앱이 웹캠 점유 / `list_cameras.py` 로 인덱스·백엔드 재확인 |
| `camera_error` 를 Unity가 수신 | 위와 동일 |
| `캘리브레이션 결과: None` | 얼굴 미검출. 조명 확인, `logs/debug_frame.jpg` 로 상하 반전 여부 확인 |
| `poseDetectedRatio` 0 근처 | 어깨가 프레임 밖. `/preview` 로 확인 후 뒤로 물러나기 |
| `gazeOnTargetRatio` 항상 0 | 캘리브레이션 실패, 또는 화면이 아닌 다른 곳을 보고 있었음 |
| `프레임 부족 -> 턴 집계 스킵` | 턴이 1초 미만이거나 `turn_start` 누락. `/status` 의 `analyzed_this_turn` 확인 |
| `frameCount` 가 예상의 절반 | CPU 경합/스로틀링. 전원 연결 + 고성능 모드, 안 되면 `CAM_FPS=6` |
| `read_fail` 급증 | 웹캠 드라이버 문제 또는 절전. 워커 재시작, 노트북 절전 끄기 |
| `POST /vision -> 409` | 백엔드에 활성 세션 없음. Unity가 `init` 을 보냈는지, `session_id` 가 일치하는지 |
| `❌ 백엔드 전송 실패` | `.env` 의 `BACKEND_URL` 오타, 백엔드 `--host 0.0.0.0` 누락, 방화벽 |
| Unity: `Vision 워커 연결 실패` | 워커를 Unity보다 먼저 띄웠는지, `Ws Url` 의 IP/포트 확인 |
| 결과 UI 시각 4칸이 0 | `faceDetectedRatio` 가 낮아 전 턴 채점 제외. CSV 확인 |
| yaw/pitch가 반대로 반응 | `analyzer.py` 의 `TRANSPOSE_HEAD_MATRIX = True` |

---

## 14. 알려진 한계

**노트북 근거리 착석에서 손짓 측정이 제한적입니다.** 화면이 가까우면 손이 프레임
아래로 벗어나 `handUsageRatio` 가 낮게 나옵니다. `poseDetectedRatio` 가 0.5 미만이면
손짓·자세 두 항목이 중립 5점으로 고정되므로, 카메라 배치로 해결해야 합니다.

**심한 시선 회피 시 얼굴 검출이 끊깁니다.** 고개를 40도 이상 돌리면 Face Landmarker가
놓칩니다. 실측에서 턴 중에는 발생하지 않았으나(전 턴 `faceDetectedRatio` 1.0),
회피가 길게 이어지면 `MIN_FACE_RATIO` 가드에 걸려 해당 턴이 채점에서 제외될 수 있습니다.

**빈 전사 결과나 자막 교정 재발화 시 한 단계가 두 번 집계될 수 있습니다.**
백엔드 `_score_vision()` 에 단계별 중복 제거(프레임 수가 가장 많은 턴만 사용) 로직으로
방어했습니다. 근본 원인은 음성 파이프라인 측에 있습니다.

**끼어들기 기능과의 접점.** `gazeOnTargetRatio` 는 실시간으로도 산출 가능한 신호이지만,
현재 개입 트리거는 "의미 이탈" 하나로 확정되어 있습니다. 여기에 시선 신호를 추가하면
개입 조건의 독립변수가 둘이 되어 실험 설계가 무너지므로, 지금은 손대지 않습니다.

---

## 15. 참고

- 백엔드 채점 로직: `VRoom_Backend/app/session.py` 의 `_score_vision()`
- 채점 상수: `VRoom_Backend/app/config.py` 의 `VisionScoringConfig`
- Unity 연동: `VR_Interview_Simulator/Assets/Scripts/Multimodal/`
  (`VisionStreamClient.cs`, `BehaviorCollector.cs`)
- MediaPipe Tasks: https://ai.google.dev/edge/mediapipe/solutions/vision
