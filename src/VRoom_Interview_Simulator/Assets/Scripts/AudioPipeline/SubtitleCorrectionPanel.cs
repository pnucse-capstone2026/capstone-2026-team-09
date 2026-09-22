using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerbalProcess
{
    /// <summary>
    /// 저신뢰 STT 교정 패널.
    ///
    /// 전사 신뢰도가 낮을 때 단어를 칩으로 펼쳐 보여주고, 사용자가 틀린 부분을
    /// 골라 다시 말하거나 그대로 넘기거나 폐기할 수 있게 한다.
    ///
    /// [선택 규칙]
    ///   아무것도 선택 안 됨 -> 클릭한 단어 하나 선택
    ///   단일 선택 상태      -> 같은 단어 클릭 시 해제 / 다른 단어 클릭 시 범위로 확장
    ///   범위 선택 상태      -> 아무 단어나 클릭하면 그 단어 하나로 새로 시작
    ///
    /// [세 가지 출구]
    ///   그대로 전송 : 원본 전사를 채점에 넘긴다
    ///   폐기        : 이번 발화를 버리고 처음부터 다시 답변
    ///   재발화      : 선택 범위만 다시 말한다 (패널은 열린 채로 대기)
    ///
    /// 결정은 여기서 하지 않고 콜백으로 PipelineController 에 넘긴다.
    /// </summary>
    public class SubtitleCorrectionPanel : MonoBehaviour
    {
        // ===================================================================
        // 1. 참조 / 설정
        // ===================================================================
        [Header("UI Containers")]
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Transform chipsContainer;
        [SerializeField] private TextMeshProUGUI guideText;

        [Header("Prefabs")]
        [SerializeField] private WordChip wordChipPrefab;

        [Header("Buttons")]
        [SerializeField] private Button btnSendAnyway;
        [SerializeField] private Button btnDiscard;
        [SerializeField] private Button btnReSpeak;

        [Header("Settings")]
        [Tooltip("이 값 미만의 신뢰도를 가진 단어를 붉게 강조한다.")]
        [SerializeField] private float lowConfidenceThreshold = 0.75f;
        [SerializeField] private WordLayout wordLayout;

        // ===================================================================
        // 2. 상태
        // ===================================================================
        private readonly List<WordChip> _activeChips = new List<WordChip>();
        private STTManager.CorrectionRequestMessage _currentMessage;

        // 선택 범위. 둘 다 -1 이면 선택 없음, 같으면 단일 선택.
        private int _selectedStartIdx = -1;
        private int _selectedEndIdx = -1;

        // 콜백 (PipelineController 가 주입)
        private Action _onSendAnyway;
        private Action _onDiscard;
        private Action<int, int, string[]> _onReSpeak;

        // ===================================================================
        // 3. 수명
        // ===================================================================
        private void Awake()
        {
            if (panelRoot != null) panelRoot.SetActive(false);

            if (btnSendAnyway != null) btnSendAnyway.onClick.AddListener(HandleSendAnyway);
            if (btnDiscard != null) btnDiscard.onClick.AddListener(HandleDiscard);
            if (btnReSpeak != null) btnReSpeak.onClick.AddListener(HandleReSpeak);
        }

        // ===================================================================
        // 4. 열기 / 닫기
        // ===================================================================
        public void Open(
            STTManager.CorrectionRequestMessage msg,
            Action onSendAnyway,
            Action onDiscard,
            Action<int, int, string[]> onReSpeak)
        {
            _currentMessage = msg;
            _onSendAnyway = onSendAnyway;
            _onDiscard = onDiscard;
            _onReSpeak = onReSpeak;

            _selectedStartIdx = -1;
            _selectedEndIdx = -1;

            if (guideText != null)
                guideText.text = "교정할 단어를 클릭하여 선택하거나, 범위(시작/끝 단어 2회 클릭)를 " +
                                 "지정한 뒤 아래 [재발화]를 눌러 다시 말해 주세요.";

            // ★ 칩 너비 측정이 되려면 부모가 먼저 활성화되어 있어야 한다.
            //   비활성 상태에서 만들면 RectTransform 크기가 0으로 잡혀 레이아웃이 무너진다.
            if (panelRoot != null) panelRoot.SetActive(true);

            ClearChips();
            wordLayout?.Initialize();

            BuildChips(msg);
            UpdateButtonStates();
        }

        private void BuildChips(STTManager.CorrectionRequestMessage msg)
        {
            if (msg.words == null || wordChipPrefab == null || chipsContainer == null) return;

            for (int i = 0; i < msg.words.Length; i++)
            {
                float conf = (msg.word_confidences != null && i < msg.word_confidences.Length)
                    ? msg.word_confidences[i]
                    : 1.0f;

                WordChip chip = Instantiate(wordChipPrefab, chipsContainer);
                chip.Setup(this, i, msg.words[i], conf, lowConfidenceThreshold);
                _activeChips.Add(chip);
            }

            // ★ 텍스트를 채운 직후에는 RectTransform 크기가 아직 갱신되지 않았다.
            //   강제로 캔버스를 갱신해야 다음 줄의 AddChips 가 올바른 너비로 배치한다.
            Canvas.ForceUpdateCanvases();

            if (wordLayout == null) return;

            foreach (var chip in _activeChips)
                wordLayout.AddChips(chip);

            // 모든 칩 배치가 끝난 뒤 Content 높이를 갱신해야 스크롤이 동작한다.
            wordLayout.UpdateHeight();
        }

        public void Close()
        {
            if (panelRoot != null) panelRoot.SetActive(false);

            ClearChips();
            wordLayout?.ClearChips();
        }

        private void ClearChips()
        {
            foreach (var chip in _activeChips)
            {
                if (chip != null) Destroy(chip.gameObject);
            }
            _activeChips.Clear();
        }

        // ===================================================================
        // 5. 단어 선택
        // ===================================================================
        /// <summary>WordChip 이 클릭되면 호출된다. 선택 상태에 따라 동작이 갈린다.</summary>
        public void OnChipPointerDown(WordChip clickedChip)
        {
            int index = clickedChip.Index;

            // 선택 없음 -> 단일 선택
            if (_selectedStartIdx == -1 || _selectedEndIdx == -1)
            {
                UpdateSelectionRange(index, index);
                return;
            }

            // 단일 선택 상태
            if (_selectedStartIdx == _selectedEndIdx)
            {
                if (index == _selectedStartIdx)
                    UpdateSelectionRange(-1, -1);              // 같은 단어 -> 해제
                else
                    UpdateSelectionRange(_selectedStartIdx, index);   // 다른 단어 -> 범위 확장
                return;
            }

            // 범위 선택 상태 -> 기존 범위를 버리고 새로 시작
            UpdateSelectionRange(index, index);
        }

        private void UpdateSelectionRange(int start, int end)
        {
            _selectedStartIdx = Mathf.Min(start, end);
            _selectedEndIdx = Mathf.Max(start, end);

            for (int i = 0; i < _activeChips.Count; i++)
            {
                bool inRange = (i >= _selectedStartIdx && i <= _selectedEndIdx);
                _activeChips[i].SetSelected(inRange);
            }

            UpdateButtonStates();
        }

        /// <summary>선택이 있어야만 재발화가 가능하다.</summary>
        private void UpdateButtonStates()
        {
            if (btnReSpeak != null)
                btnReSpeak.interactable = (_selectedStartIdx != -1 && _selectedEndIdx != -1);
        }

        // ===================================================================
        // 6. 버튼 핸들러
        // ===================================================================
        private void HandleSendAnyway()
        {
            _onSendAnyway?.Invoke();
            Close();
        }

        private void HandleDiscard()
        {
            _onDiscard?.Invoke();
            Close();
        }

        /// <summary>
        /// 재발화 시작. 패널은 닫지 않고 '대기 중' 상태로 남는다.
        /// 재발화가 끝나면 PipelineController 가 Close() 를 호출한다.
        /// </summary>
        private void HandleReSpeak()
        {
            if (_selectedStartIdx == -1 || _selectedEndIdx == -1 || _currentMessage == null) return;

            _onReSpeak?.Invoke(_selectedStartIdx, _selectedEndIdx, _currentMessage.words);

            if (guideText != null)
            {
                guideText.text = "<color=red>● 재발화 대기 중...</color> " +
                                 "선택한 부분을 마이크에 다시 말씀해 주세요.";
            }

            // 재발화 중 중복 클릭을 막는다. ResetButtonInteractions() 로 복구한다.
            if (btnReSpeak != null) btnReSpeak.interactable = false;
            if (btnSendAnyway != null) btnSendAnyway.interactable = false;
            if (btnDiscard != null) btnDiscard.interactable = false;
        }

        /// <summary>재발화가 접수되어 교정 흐름이 끝났을 때 버튼을 되살린다.</summary>
        public void ResetButtonInteractions()
        {
            if (btnSendAnyway != null) btnSendAnyway.interactable = true;
            if (btnDiscard != null) btnDiscard.interactable = true;
            UpdateButtonStates();
        }
    }
}