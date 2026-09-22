using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// 마이크 오디오를 STT 워커가 받을 형식으로 변환하는 유틸리티.
    ///
    /// [변환 파이프라인]
    ///   AudioClip (Unity, float, 16kHz)
    ///     -> 구간 잘라내기 (TrimAudio)
    ///     -> 모노 믹싱 -> 16kHz 리샘플 -> int16 PCM
    ///     -> (첫 청크만) WAV 헤더 부착
    ///
    /// [왜 16kHz 인가] Whisper 가 16kHz 를 입력으로 쓴다. 더 높여도 정확도가 오르지 않고
    /// 전송량만 늘어난다. VAD 도 같은 값으로 녹음하므로 실제로는 리샘플이 일어나지 않는다.
    ///
    /// [왜 첫 청크에만 헤더인가] 워커는 스트림을 하나의 WAV 파일로 취급한다.
    /// 매 청크에 헤더를 붙이면 파일이 여러 개로 쪼개져 전사가 실패한다.
    /// </summary>
    public static class AudioUtils
    {
        private const int TargetSampleRate = 16000;   // Whisper 입력 규격

        // ===================================================================
        // 1. 구간 잘라내기
        // ===================================================================
        /// <summary>
        /// 원본 AudioClip 에서 [startSample, endSample) 구간을 새 클립으로 잘라낸다.
        ///
        /// 마이크 클립은 **링버퍼**라 end 가 start 보다 작을 수 있다(한 바퀴 돈 경우).
        /// 그때는 뒤쪽 + 앞쪽 두 조각을 이어 붙인다.
        /// </summary>
        public static AudioClip TrimAudio(AudioClip source, int startSample, int endSample)
        {
            int sourceSamples = source.samples;
            bool wrapped = endSample < startSample;

            int length = wrapped
                ? (sourceSamples - startSample) + endSample
                : endSample - startSample;

            if (length <= 0) return null;

            float[] data = new float[length];

            if (!wrapped)
            {
                source.GetData(data, startSample);
            }
            else
            {
                // 뒤쪽 조각 (startSample ~ 버퍼 끝)
                int firstPartLength = sourceSamples - startSample;
                float[] firstPart = new float[firstPartLength];
                source.GetData(firstPart, startSample);
                Array.Copy(firstPart, 0, data, 0, firstPartLength);

                // 앞쪽 조각 (버퍼 시작 ~ endSample)
                float[] secondPart = new float[endSample];
                source.GetData(secondPart, 0);
                Array.Copy(secondPart, 0, data, firstPartLength, endSample);
            }

            AudioClip result = AudioClip.Create(
                "TrimmedAudio", length, source.channels, source.frequency, false);
            result.SetData(data, 0);

            return result;
        }

        // ===================================================================
        // 2. 포맷 변환
        // ===================================================================
        /// <summary>AudioClip -> 16kHz Mono WAV 바이트 (헤더 포함). 발화의 첫 청크용.</summary>
        public static byte[] GetWavBytes(AudioClip clip)
        {
            byte[] pcmData = GetRawPcmBytes(clip);

            using var memoryStream = new MemoryStream();
            WriteWavHeader(memoryStream, pcmData.Length / 2, TargetSampleRate, 1);
            memoryStream.Write(pcmData, 0, pcmData.Length);
            return memoryStream.ToArray();
        }

        /// <summary>AudioClip -> 16kHz Mono int16 PCM 바이트 (헤더 없음). 두 번째 이후 청크용.</summary>
        public static byte[] GetRawPcmBytes(AudioClip clip)
        {
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);

            float[] monoSamples = MixToMono(samples, clip.channels);
            float[] resampled = Resample(monoSamples, clip.frequency, TargetSampleRate);
            return ConvertToPcmBytes(resampled);
        }

        // ===================================================================
        // 3. 내부 변환 단계
        // ===================================================================
        private static float[] MixToMono(float[] input, int channels)
        {
            if (channels == 1) return input;

            float[] output = new float[input.Length / channels];
            for (int i = 0; i < output.Length; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += input[i * channels + c];
                output[i] = sum / channels;
            }
            return output;
        }

        /// <summary>선형 보간 리샘플. 마이크가 이미 16kHz 라 실제로는 거의 통과만 한다.</summary>
        private static float[] Resample(float[] samples, int fromRate, int toRate)
        {
            if (fromRate == toRate) return samples;

            float ratio = (float)fromRate / toRate;
            int newLength = Mathf.FloorToInt(samples.Length / ratio);
            float[] result = new float[newLength];

            for (int i = 0; i < newLength; i++)
            {
                float index = i * ratio;
                int i1 = Mathf.FloorToInt(index);
                int i2 = Mathf.Min(i1 + 1, samples.Length - 1);
                float t = index - i1;
                result[i] = Mathf.Lerp(samples[i1], samples[i2], t);
            }
            return result;
        }

        /// <summary>float(-1~1) -> int16 little-endian 바이트.</summary>
        private static byte[] ConvertToPcmBytes(float[] samples)
        {
            byte[] bytesData = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short value = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32767f);
                bytesData[i * 2] = (byte)(value & 0xff);
                bytesData[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }
            return bytesData;
        }

        /// <summary>
        /// 44바이트 WAV 헤더(RIFF/fmt/data)를 쓴다.
        /// 필드 순서와 크기가 규격에 고정되어 있으므로 임의로 바꾸면 안 된다.
        /// </summary>
        private static void WriteWavHeader(MemoryStream stream, int samplesCount, int hz, int channels)
        {
            stream.Write(Encoding.UTF8.GetBytes("RIFF"), 0, 4);
            stream.Write(BitConverter.GetBytes(36 + samplesCount * 2), 0, 4);   // 전체 크기 - 8
            stream.Write(Encoding.UTF8.GetBytes("WAVE"), 0, 4);

            stream.Write(Encoding.UTF8.GetBytes("fmt "), 0, 4);
            stream.Write(BitConverter.GetBytes(16), 0, 4);                      // fmt 청크 크기
            stream.Write(BitConverter.GetBytes((ushort)1), 0, 2);               // PCM
            stream.Write(BitConverter.GetBytes((ushort)channels), 0, 2);
            stream.Write(BitConverter.GetBytes(hz), 0, 4);                      // 샘플레이트
            stream.Write(BitConverter.GetBytes(hz * channels * 2), 0, 4);       // 바이트/초
            stream.Write(BitConverter.GetBytes((ushort)(channels * 2)), 0, 2);  // 블록 정렬
            stream.Write(BitConverter.GetBytes((ushort)16), 0, 2);              // 비트 심도

            stream.Write(Encoding.UTF8.GetBytes("data"), 0, 4);
            stream.Write(BitConverter.GetBytes(samplesCount * 2), 0, 4);        // 데이터 크기
        }
    }
}