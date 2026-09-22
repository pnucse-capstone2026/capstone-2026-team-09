using System;

namespace VerbalProcess
{
    /// <summary>
    /// VAD 가 추출한 음성 피쳐를 STT 워커로 보내기 위한 DTO.
    ///
    /// VoiceFeatures 는 struct 라 JsonUtility 로 직렬화할 수 없어 이 클래스로 옮겨 담는다.
    /// 필드명은 백엔드 session._collect_features() 가 읽는 키와 정확히 일치해야 한다.
    /// </summary>
    [Serializable]
    public class FeatureData
    {
        public float speakingTime;         // 순수 발화 시간(초)
        public int meaningfulPauseCount;   // 의미 있는 침묵 횟수
        public float volumeVariance;       // 볼륨 분산
        public float lowVolumeRatio;       // 작은 목소리 구간 비율
        public float averageVolume;        // 평균 RMS
        public float responseTime;         // 질문 종료 -> 답변 시작까지 걸린 시간

        public FeatureData(VoiceActivityDetector.VoiceFeatures features)
        {
            speakingTime = features.speakingTime;
            meaningfulPauseCount = features.meaningfulPauseCount;
            volumeVariance = features.volumeVariance;
            lowVolumeRatio = features.lowVolumeRatio;
            averageVolume = features.averageVolume;
            responseTime = features.responseTime;
        }
    }
}