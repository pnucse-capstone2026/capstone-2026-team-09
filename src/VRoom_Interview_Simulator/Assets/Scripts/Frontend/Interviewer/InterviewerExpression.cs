using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRoom.Backend
{
    /// <summary>
    /// expression_id -> 얼굴 BlendShape 매핑. Animator 레이어와 독립.
    /// 여러 메시를 동시에 제어
    /// 입/턱 셰이프(Jaw_/V_)는 건드리지 않는다(uLipSync 담당).
    /// </summary>
    public class InterviewerExpression : MonoBehaviour
    {
        [System.Serializable]
        public class ShapeWeight
        {
            public string name; 
            [Range(0, 100)] public float weight;
        }

        [System.Serializable]
        public class Preset { public ShapeWeight[] shapes; }

        [Header("얼굴 관련 메시")]
        public SkinnedMeshRenderer[] faceMeshes;

        [Header("프리셋")]
        public Preset neutral;
        public Preset positive;
        public Preset negative;
        public Preset firmStop;

        [SerializeField] float lerpSpeed = 6f;

        private readonly List<Dictionary<string, int>> _index = new();
        private readonly List<Dictionary<int, float>> _current = new();
        private readonly List<Dictionary<int, float>> _target = new();

        void Awake()
        {
            for (int m = 0; m < faceMeshes.Length; m++)
            {
                var idx = new Dictionary<string, int>();
                var smr = faceMeshes[m];
                if (smr != null)
                {
                    var mesh = smr.sharedMesh;
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                        idx[mesh.GetBlendShapeName(i)] = i;
                }
                _index.Add(idx);
                _current.Add(new Dictionary<int, float>());
                _target.Add(new Dictionary<int, float>());
            }

            ValidatePreset(nameof(neutral), neutral);
            ValidatePreset(nameof(positive), positive);
            ValidatePreset(nameof(negative), negative);
            ValidatePreset(nameof(firmStop), firmStop);
        }

        private void ValidatePreset(string label, Preset p)
        {
            if (p?.shapes == null || p.shapes.Length == 0)
            {
                Debug.LogWarning($"[표정] 프리셋 '{label}' 이 비어 있습니다.");
                return;
            }
            foreach (var s in p.shapes)
            {
                bool found = false;
                for (int m = 0; m < _index.Count; m++)
                    if (_index[m].ContainsKey(s.name)) { found = true; break; }
                if (!found)
                    Debug.LogError($"[표정] 프리셋 '{label}' 의 셰이프 '{s.name}' 을 " +
                                   $"어떤 faceMeshes 에서도 찾을 수 없습니다. 오타 확인.");
            }
        }

        public void Apply(int expressionId)
        {
            Preset p = expressionId switch
            {
                1 or 3 => positive,
                2 => negative,
                5 => firmStop,
                _ => neutral,   // 0, 4
            };

            for (int m = 0; m < faceMeshes.Length; m++)
            {
                _target[m].Clear();
                if (p?.shapes == null) continue;
                foreach (var s in p.shapes)
                    if (_index[m].TryGetValue(s.name, out int idx))
                        _target[m][idx] = s.weight;
            }
        }

        void Update()
        {
            for (int m = 0; m < faceMeshes.Length; m++)
            {
                var smr = faceMeshes[m];
                if (smr == null) continue;

                var keys = new HashSet<int>(_current[m].Keys);
                foreach (var k in _target[m].Keys) keys.Add(k);

                foreach (int idx in keys)
                {
                    float cur = _current[m].TryGetValue(idx, out var c) ? c : 0f;
                    float tgt = _target[m].TryGetValue(idx, out var t) ? t : 0f;
                    float next = Mathf.Lerp(cur, tgt, Time.deltaTime * lerpSpeed);
                    _current[m][idx] = next;
                    smr.SetBlendShapeWeight(idx, next);
                }
            }
        }
    }
}