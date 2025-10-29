using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.Controls;
#endif

/// <summary>
/// Drives an interactive fortune-teller conversation using a local LLM and ElevenLabs TTS.
/// Instantiates a lightweight UI if none is provided.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class FortuneTellerController : MonoBehaviour
{
    private const string ConfigResourcePath = "LLMConfig";
    private const string FormatInstructions =
        "Respond ONLY with valid JSON. Format: {\"spoken\":\"...\",\"choices\":[\"...\",\"...\",\"...\"]}. " +
        "\"spoken\" must contain only the words you will say aloud (no descriptive actions, no labels like 'Bartholomew:'). " +
        "\"choices\" must be an array of exactly three short, distinct player options. Do not include any extra text before or after the JSON.";
    private const string RetryReminder =
        "That response was not valid JSON. Reply again using ONLY the schema {\"spoken\":\"...\",\"choices\":[\"...\",\"...\",\"...\"]} with exactly three choices.";
    private const int MaxRetries = 3;

    [SerializeField] private LLMConfig config;
    [SerializeField] private Text dialogueText;
    [SerializeField] private Button[] choiceButtons;
    [SerializeField] private Animator talkingAnimator;
    [SerializeField] private string talkingParameter = "IsTalking";
    [SerializeField] private bool logResponses = false;
    [SerializeField] private KeyCode skipKey = KeyCode.Space;

    private readonly List<ChatMessage> _conversation = new();
    private readonly Regex _actionMarkupRegex = new(@"\*[^*]+\*|\[[^\]]+\]|\([^\)]+\)", RegexOptions.Compiled);

    private AudioSource _audioSource;
    private AudioSource _musicSource;
    private GameObject _uiRoot;
    private bool _uiHiddenByConfig;
    private bool _choicesCurrentlyVisible;
    private string[] _currentChoices = Array.Empty<string>();
    private string _voiceId = string.Empty;
    private string _apiKey = string.Empty;
    private bool _requestInFlight;
    private bool _awaitingIntroChoice;
    private GameObject _choicesContainer;
    private int _talkingParameterHash = -1;

    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            _audioSource = gameObject.AddComponent<AudioSource>();
        }
        _audioSource.playOnAwake = false;
        _audioSource.spatialize = false;

        EnsureMusicSource();

        EnsureUI();
        HookupButtons();
    }

    private void Start()
    {
        if (config == null)
        {
            config = Resources.Load<LLMConfig>(ConfigResourcePath);
        }

        if (config == null)
        {
            Debug.LogError($"FortuneTellerController: Could not find LLMConfig at Resources/{ConfigResourcePath}.");
            return;
        }

        dialogueText.text = "The fortune teller prepares to speak...";
        SetChoicesInteractable(false);
        SetChoicesVisible(false);

        _voiceId = config.HasVoiceId ? config.elevenLabsVoiceId : Environment.GetEnvironmentVariable("ELEVENLABS_VOICE_ID");
        _apiKey = config.HasApiKey ? config.elevenLabsApiKey : Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        logResponses = config.logTranscript;

        if (talkingAnimator == null && !string.IsNullOrEmpty(config.skeletonAnimatorName))
        {
            var target = GameObject.Find(config.skeletonAnimatorName);
            if (target != null)
            {
                talkingAnimator = target.GetComponent<Animator>() ?? target.GetComponentInChildren<Animator>();
            }
            else if (logResponses)
            {
                Debug.LogWarning($"FortuneTellerController: Could not find GameObject '{config.skeletonAnimatorName}' to drive talking animation.");
            }
        }

        if (!string.IsNullOrEmpty(config.talkingParameter))
        {
            talkingParameter = config.talkingParameter;
        }

        if (talkingAnimator != null)
        {
            _talkingParameterHash = Animator.StringToHash(talkingParameter);
            SetTalkingState(false);
        }
        else if (!string.IsNullOrEmpty(config.skeletonAnimatorName))
        {
            Debug.LogWarning("FortuneTellerController: No Animator found for skeleton talking control.");
        }

        if (string.IsNullOrWhiteSpace(_voiceId))
        {
            Debug.LogWarning("FortuneTellerController: ElevenLabs voice ID not set (config or ELEVENLABS_VOICE_ID). Choices will be shown but audio will be silent.");
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            Debug.LogWarning("FortuneTellerController: ElevenLabs API key not set (config or ELEVENLABS_API_KEY). Choices will be shown but audio will be silent.");
        }

        _conversation.Clear();
        var basePrompt = string.IsNullOrWhiteSpace(config.systemPrompt) ? string.Empty : config.systemPrompt.Trim();
        _conversation.Add(new ChatMessage
        {
            role = "system",
            content = string.IsNullOrWhiteSpace(basePrompt) ? FormatInstructions : $"{basePrompt}\n\n{FormatInstructions}"
        });

        ConfigureBackgroundMusic();
        ApplyUiVisibility(config != null && !config.hideDialogueUI);

        StartCoroutine(RunStartupSequence());
    }

    private void EnsureUI()
    {
        if (dialogueText != null && choiceButtons != null && choiceButtons.Length >= 3)
        {
            CacheUiRoot();
            if (EventSystem.current == null)
            {
                CreateEventSystem();
            }
            return;
        }

        if (EventSystem.current == null)
        {
            CreateEventSystem();
        }

        var canvasGo = new GameObject("FortuneTellerCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        DontDestroyOnLoad(canvasGo);
        _uiRoot = canvasGo;

        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        var panel = CreateRectTransform("DialoguePanel", canvas.transform);
        var panelRect = panel;
        panelRect.anchorMin = new Vector2(0.05f, 0.05f);
        panelRect.anchorMax = new Vector2(0.95f, 0.35f);
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;

        var panelImage = panel.gameObject.AddComponent<Image>();
        panelImage.color = new Color(0.05f, 0.05f, 0.05f, 0.8f);

        var panelLayout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
        panelLayout.padding = new RectOffset(20, 20, 20, 20);
        panelLayout.spacing = 16f;
        panelLayout.childAlignment = TextAnchor.UpperLeft;
        panelLayout.childControlHeight = true;
        panelLayout.childControlWidth = true;
        panelLayout.childForceExpandHeight = false;
        panelLayout.childForceExpandWidth = true;

        var textGo = CreateRectTransform("DialogueText", panel);
        dialogueText = textGo.gameObject.AddComponent<Text>();
        dialogueText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        dialogueText.fontSize = 26;
        dialogueText.color = new Color(0.92f, 0.87f, 0.76f);
        dialogueText.alignment = TextAnchor.UpperLeft;
        dialogueText.horizontalOverflow = HorizontalWrapMode.Wrap;
        dialogueText.verticalOverflow = VerticalWrapMode.Truncate;
        var textLayout = textGo.gameObject.AddComponent<LayoutElement>();
        textLayout.minHeight = 120f;
        textLayout.preferredHeight = 160f;
        textLayout.flexibleHeight = 1f;

        var choicesContainer = CreateRectTransform("Choices", panel);
        _choicesContainer = choicesContainer.gameObject;
        var choicesLayout = choicesContainer.gameObject.AddComponent<VerticalLayoutGroup>();
        choicesLayout.spacing = 8f;
        choicesLayout.childAlignment = TextAnchor.UpperCenter;
        choicesLayout.childControlWidth = true;
        choicesLayout.childControlHeight = true;
        choicesLayout.childForceExpandWidth = true;
        choicesLayout.childForceExpandHeight = false;

        choiceButtons = new Button[3];
        for (int i = 0; i < 3; i++)
        {
            var buttonGo = CreateRectTransform($"ChoiceButton{i + 1}", choicesContainer);
            var image = buttonGo.gameObject.AddComponent<Image>();
            image.color = new Color(0.23f, 0.18f, 0.33f, 0.85f);
            image.raycastTarget = true;

            var button = buttonGo.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(0.35f, 0.28f, 0.48f, 0.95f);
            colors.pressedColor = new Color(0.18f, 0.14f, 0.26f, 0.9f);
            button.colors = colors;

            var btLayout = buttonGo.gameObject.AddComponent<LayoutElement>();
            btLayout.minHeight = 48f;
            btLayout.preferredHeight = 52f;

            var labelGo = CreateRectTransform("Text", buttonGo);
            var label = labelGo.gameObject.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 20;
            label.color = new Color(0.95f, 0.94f, 0.9f);
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.text = $"Choice {i + 1}";

            choiceButtons[i] = button;
        }

        SetChoicesVisible(false);
        CacheUiRoot();
    }

    private void CreateEventSystem()
    {
        var es = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        es.AddComponent<StandaloneInputModule>();
#endif
        DontDestroyOnLoad(es);
    }

    private static RectTransform CreateRectTransform(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0, 0);
        rect.anchorMax = new Vector2(1, 1);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return rect;
    }

    private void HookupButtons()
    {
        if (choiceButtons == null)
        {
            return;
        }

        for (int i = 0; i < choiceButtons.Length; i++)
        {
            int index = i;
            if (choiceButtons[i] == null)
            {
                continue;
            }

            choiceButtons[i].onClick.RemoveAllListeners();
            choiceButtons[i].onClick.AddListener(() => OnChoiceSelected(index));
        }
    }

    private void OnChoiceSelected(int index)
    {
        if (_requestInFlight || _currentChoices == null || index < 0 || index >= _currentChoices.Length)
        {
            return;
        }

        string choice = _currentChoices[index];
        if (string.IsNullOrWhiteSpace(choice))
        {
            return;
        }

        if (_awaitingIntroChoice)
        {
            _awaitingIntroChoice = false;
            dialogueText.text = $"You choose: {choice}\n\nThe fortune teller contemplates...";
            SetChoicesInteractable(false);
            SetChoicesVisible(false);

            string initialPrompt = string.IsNullOrWhiteSpace(config.userPrompt)
                ? $"The visitor selects \"{choice}\". Continue the reading with eerie insight and present three options."
                : $"{config.userPrompt}\nThe visitor selects \"{choice}\".";

            AppendUserLine(choice);
            StartCoroutine(AdvanceConversation(initialPrompt));
            return;
        }

        dialogueText.text = $"You choose: {choice}\n\nThe fortune teller contemplates...";
        SetChoicesInteractable(false);
        SetChoicesVisible(false);
        AppendUserLine(choice);
        StartCoroutine(AdvanceConversation($"The visitor selects: \"{choice}\". React in character, reveal a new omen, and offer three new options."));
    }

    private IEnumerator RunStartupSequence()
    {
        _awaitingIntroChoice = false;
        if (config.introClip != null || !string.IsNullOrWhiteSpace(config.introSpoken))
        {
            yield return PlayIntroSequence(null);
        }

        if (HasIntroQuestion())
        {
            SetChoicesVisible(false);
            if (config.introQuestionClip != null)
            {
                bool questionRecorded = false;
                yield return PlayClipWithText(
                    config.introQuestionClip,
                    config.introQuestion,
                    null,
                    completedText =>
                    {
                        if (!string.IsNullOrWhiteSpace(completedText))
                        {
                            AppendAssistantLine(completedText);
                            questionRecorded = true;
                        }
                    });
                if (!questionRecorded && !string.IsNullOrWhiteSpace(config.introQuestion))
                {
                    AppendAssistantLine(config.introQuestion);
                }
            }
            else if (!string.IsNullOrWhiteSpace(config.introQuestion))
            {
                dialogueText.text = config.introQuestion;
                AppendAssistantLine(config.introQuestion);
            }
            else
            {
                dialogueText.text = string.Empty;
            }

            ApplyChoices(config.introChoices);
            _awaitingIntroChoice = true;
            yield break;
        }

        StartCoroutine(AdvanceConversation(config.userPrompt));
    }

    private IEnumerator AdvanceConversation(string userMessage)
    {
        if (_requestInFlight)
        {
            yield break;
        }

        _requestInFlight = true;

        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            _conversation.Add(new ChatMessage { role = "user", content = userMessage });
        }

        SetChoicesVisible(false);

        int attempt = 0;
        FortuneResponse parsedResponse = null;
        string parsedJson = null;
        string spoken = null;

        while (attempt < MaxRetries)
        {
            using (var llmRequest = BuildChatRequest())
            {
                yield return llmRequest.SendWebRequest();

                if (llmRequest.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"FortuneTellerController: LLM request failed: {llmRequest.error}");
                    _requestInFlight = false;
                    SetTalkingState(false);
                    SetChoicesVisible(true);
                    SetChoicesInteractable(true);
                    yield break;
                }

                var raw = llmRequest.downloadHandler.text;
                if (logResponses)
                {
                    Debug.Log($"LLM raw response (attempt {attempt + 1}): {raw}");
                }

                if (TryParseResponse(raw, out parsedResponse, out parsedJson))
                {
                    spoken = SanitizeTranscript(parsedResponse.spoken);
                    break;
                }

                attempt++;
                if (attempt < MaxRetries)
                {
                    _conversation.Add(new ChatMessage { role = "user", content = RetryReminder });
                    continue;
                }

                Debug.LogError("FortuneTellerController: Failed to parse LLM response after multiple attempts.");
                _requestInFlight = false;
                SetTalkingState(false);
                SetChoicesVisible(true);
                SetChoicesInteractable(true);
                yield break;
            }
        }

        _conversation.Add(new ChatMessage { role = "assistant", content = parsedJson });

        if (string.IsNullOrWhiteSpace(spoken))
        {
            Debug.LogWarning("FortuneTellerController: Received empty spoken text.");
            dialogueText.text = string.Empty;
        }
        else
        {
            dialogueText.text = string.Empty;
            yield return PlaySpeech(spoken);
        }

        ApplyChoices(parsedResponse.choices);
        _requestInFlight = false;
    }

    private void ApplyChoices(string[] choices)
    {
        _currentChoices = NormalizeChoices(choices);
        for (int i = 0; i < choiceButtons.Length; i++)
        {
            var button = choiceButtons[i];
            if (button == null)
            {
                continue;
            }

            string choice = i < _currentChoices.Length ? _currentChoices[i] : string.Empty;
            bool hasChoice = !string.IsNullOrWhiteSpace(choice);
            button.gameObject.SetActive(hasChoice);
            button.interactable = hasChoice;

            var label = button.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.text = hasChoice ? choice : "---";
            }
        }

        SetChoicesVisible(true);
        SetChoicesInteractable(true);
    }

    private string[] NormalizeChoices(string[] choices)
    {
        if (choices == null || choices.Length == 0)
        {
            return new[]
            {
                "Ask about the shadows", "Request a bone reading", "Politely take your leave"
            };
        }

        var results = new List<string>(3);
        foreach (var choice in choices)
        {
            if (results.Count >= 3)
            {
                break;
            }

            var cleaned = SanitizeTranscript(choice);
            if (!string.IsNullOrWhiteSpace(cleaned) && !results.Contains(cleaned))
            {
                results.Add(cleaned);
            }
        }

        while (results.Count < 3)
        {
            results.Add("Contemplate in silence");
        }

        return results.ToArray();
    }

    private bool HasIntroQuestion()
    {
        if (string.IsNullOrWhiteSpace(config.introQuestion) || config.introChoices == null)
        {
            return false;
        }

        int nonEmpty = 0;
        foreach (var choice in config.introChoices)
        {
            if (!string.IsNullOrWhiteSpace(choice))
            {
                nonEmpty++;
            }
        }

        return nonEmpty > 0;
    }

    private IEnumerator PlayIntroSequence(System.Action onSkipped)
    {
        if (config.introClip != null)
        {
            bool introRecorded = false;
            yield return PlayClipWithText(config.introClip, config.introSpoken, onSkipped, completedText =>
            {
                if (!string.IsNullOrWhiteSpace(completedText))
                {
                    AppendAssistantLine(completedText);
                    introRecorded = true;
                }
            });
            if (!introRecorded && !string.IsNullOrWhiteSpace(config.introSpoken))
            {
                AppendAssistantLine(config.introSpoken);
            }
        }
        else if (!string.IsNullOrWhiteSpace(config.introSpoken))
        {
            dialogueText.text = config.introSpoken;
            float duration = Mathf.Clamp(config.introSpoken.Length * 0.05f, 1f, 5f);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (WasSkipPressed())
                {
                    break;
                }
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            AppendAssistantLine(config.introSpoken);
        }
    }

    private IEnumerator PlayClipWithText(AudioClip clip, string textOverride, System.Action onSkipped = null, System.Action<string> onCompleted = null)
    {
        if (clip == null)
        {
            yield break;
        }

        if (config != null)
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(0f, config.startDelay));
        }

        if (!string.IsNullOrWhiteSpace(textOverride))
        {
            dialogueText.text = textOverride;
        }

        _audioSource.clip = clip;
        SetTalkingState(true);
        _audioSource.Play();
        bool skipped = false;
        while (_audioSource.isPlaying)
        {
            if (WasSkipPressed())
            {
                _audioSource.Stop();
                skipped = true;
                onSkipped?.Invoke();
                break;
            }
            yield return null;
        }
        SetTalkingState(false);
        _audioSource.clip = null;

        if (!skipped && onCompleted != null)
        {
            onCompleted.Invoke(string.IsNullOrWhiteSpace(textOverride) ? string.Empty : textOverride);
        }
    }

    private IEnumerator PlaySpeech(string spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken))
        {
            SetTalkingState(false);
            dialogueText.text = spoken;
            yield break;
        }

        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_voiceId))
        {
            SetTalkingState(false);
            dialogueText.text = spoken;
            yield break;
        }

        using (var ttsRequest = BuildTtsRequest(spoken))
        {
            yield return ttsRequest.SendWebRequest();

            if (ttsRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"FortuneTellerController: ElevenLabs TTS request failed: {ttsRequest.error}\n{TryGetResponseText(ttsRequest)}");
                SetTalkingState(false);
                dialogueText.text = spoken;
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(ttsRequest);
            if (clip == null || clip.length <= 0f)
            {
                Debug.LogError($"FortuneTellerController: Received empty audio clip. Content-Type={ttsRequest.GetResponseHeader("Content-Type")}\n{TryGetResponseText(ttsRequest)}");
                SetTalkingState(false);
                dialogueText.text = spoken;
                yield break;
            }

            _audioSource.clip = clip;
            dialogueText.text = spoken;
            SetTalkingState(true);
            _audioSource.Play();
            while (_audioSource.isPlaying)
            {
                yield return null;
            }
            SetTalkingState(false);
        }
    }

    private UnityWebRequest BuildChatRequest()
    {
        var payload = new ChatRequest
        {
            model = config.llmModel,
            messages = _conversation.ToArray()
        };

        string json = JsonUtility.ToJson(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);
        var request = new UnityWebRequest(config.llmEndpoint, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(body);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        return request;
    }

    private UnityWebRequest BuildTtsRequest(string text)
    {
        var payload = new ElevenLabsRequest
        {
            text = text,
            model_id = config.elevenLabsModelId,
            voice_settings = new ElevenLabsVoiceSettings
            {
                stability = GetSnappedStability(config.elevenLabsStability),
                similarity_boost = Mathf.Clamp01(config.elevenLabsSimilarityBoost)
            }
        };

        string json = JsonUtility.ToJson(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);
        string uri = $"https://api.elevenlabs.io/v1/text-to-speech/{_voiceId}";

        var request = new UnityWebRequest(uri, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(body);
        request.downloadHandler = new DownloadHandlerAudioClip(uri, AudioType.MPEG);
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", "audio/mpeg");
        request.SetRequestHeader("xi-api-key", _apiKey);
        return request;
    }

    private static float GetSnappedStability(float value)
    {
        float[] allowed = { 0f, 0.5f, 1f };
        float clamped = Mathf.Clamp01(value);
        float closest = allowed[0];
        float bestDistance = Mathf.Abs(clamped - allowed[0]);
        for (int i = 1; i < allowed.Length; i++)
        {
            float distance = Mathf.Abs(clamped - allowed[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                closest = allowed[i];
            }
        }
        return closest;
    }

    private string SanitizeTranscript(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        var withoutActions = _actionMarkupRegex.Replace(trimmed, string.Empty);
        return withoutActions.Replace("  ", " ").Trim();
    }

    private static string TryGetResponseText(UnityWebRequest request)
    {
        try
        {
            if (request.downloadHandler != null)
            {
                var data = request.downloadHandler.data;
                if (data != null && data.Length > 0)
                {
                    return Encoding.UTF8.GetString(data);
                }
            }
        }
        catch (Exception ex)
        {
            return $"(failed to read error body: {ex.Message})";
        }

        return string.Empty;
    }

    private bool TryParseResponse(string raw, out FortuneResponse response, out string parsedJson)
    {
        response = null;
        parsedJson = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string json = string.Empty;
        try
        {
            string content = null;
            try
            {
                var completion = JsonUtility.FromJson<ChatCompletionResponse>(raw);
                if (completion != null && completion.choices != null && completion.choices.Length > 0)
                {
                    content = completion.choices[0]?.message?.content;
                }
            }
            catch (Exception jsonUtilityEx)
            {
                if (logResponses)
                {
                    Debug.LogWarning($"FortuneTellerController: JsonUtility parse failed: {jsonUtilityEx.Message}");
                }
            }

            if (string.IsNullOrEmpty(content))
            {
                var top = MiniJson.Deserialize(raw) as Dictionary<string, object>;
                if (top == null || !top.TryGetValue("choices", out var choicesObjTop) || choicesObjTop is not IList choicesList || choicesList.Count == 0)
                {
                    if (logResponses)
                    {
                        Debug.LogWarning($"FortuneTellerController: top-level JSON missing choices array:\n{raw}");
                    }
                    return false;
                }

                if (choicesList[0] is not Dictionary<string, object> firstChoice ||
                    !firstChoice.TryGetValue("message", out var messageObj) ||
                    messageObj is not Dictionary<string, object> messageDict ||
                    !messageDict.TryGetValue("content", out var contentObj) ||
                    contentObj is not string extractedContent)
                {
                    if (logResponses)
                    {
                        Debug.LogWarning($"FortuneTellerController: could not find message.content in response:\n{raw}");
                    }
                    return false;
                }

                content = extractedContent;
            }

            json = ExtractJsonBlock(content);
            if (string.IsNullOrEmpty(json))
            {
                if (TryParseEnumeratedContent(content, out var fallbackResponse, out var synthesizedJson))
                {
                    response = fallbackResponse;
                    parsedJson = synthesizedJson;
                    return true;
                }

                if (logResponses)
                {
                    Debug.LogWarning($"FortuneTellerController: Could not locate JSON in assistant content:\n{content}");
                }
                return false;
            }
            parsedJson = json;

            // Primary parse via JsonUtility
            FortuneJson fortune = null;
            try
            {
                fortune = JsonUtility.FromJson<FortuneJson>(json);
            }
            catch (Exception)
            {
                fortune = null;
            }

            if (fortune != null && !string.IsNullOrWhiteSpace(fortune.spoken))
            {
                response = new FortuneResponse
                {
                    spoken = fortune.spoken,
                    choices = fortune.choices ?? Array.Empty<string>()
                };
                return true;
            }

            // Fallback to MiniJson to handle odd formatting
            var obj = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (obj == null)
            {
                if (logResponses)
                {
                    Debug.LogWarning($"FortuneTellerController: Parsed JSON is not an object:\n{json}");
                }
                return false;
            }

            if (!obj.TryGetValue("spoken", out var spokenObj))
            {
                if (logResponses)
                {
                    Debug.LogWarning($"FortuneTellerController: JSON missing 'spoken' field:\n{json}");
                }
                return false;
            }

            var spoken = SanitizeTranscript(spokenObj as string);

            string[] choices = Array.Empty<string>();
            if (obj.TryGetValue("choices", out var choicesObj) && choicesObj is IList list)
            {
                var temp = new List<string>();
                foreach (var item in list)
                {
                    if (item is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        temp.Add(s);
                    }
                }
                choices = temp.ToArray();
            }

            response = new FortuneResponse
            {
                spoken = spoken,
                choices = choices
            };
            if (string.IsNullOrWhiteSpace(spoken))
            {
                if (logResponses)
                {
                    Debug.LogWarning($"FortuneTellerController: 'spoken' field empty after sanitization:\n{json}");
                }
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"FortuneTellerController: JSON parse failed: {ex.Message}\n{json}");
            parsedJson = json;
            return false;
        }
    }

    private string ExtractJsonBlock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        int depth = 0;
        int start = -1;
        var inString = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '"' && (i == 0 || raw[i - 1] != '\\'))
            {
                inString = !inString;
            }

            if (inString)
            {
                continue;
            }

            if (c == '{')
            {
                if (depth == 0)
                {
                    start = i;
                }
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start != -1)
                {
                    int length = i - start + 1;
                    return raw.Substring(start, length);
                }
            }
        }

        return string.Empty;
    }

    private bool TryParseEnumeratedContent(string content, out FortuneResponse response, out string synthesizedJson)
    {
        response = null;
        synthesizedJson = string.Empty;

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var choices = new List<string>();
        int firstChoiceLineIndex = -1;
        var choicePattern = new Regex(@"^\s*(?:\d+[\.\)]|\-\s|\*\s)(.+)$");

        for (int i = 0; i < lines.Length; i++)
        {
            var match = choicePattern.Match(lines[i]);
            if (match.Success)
            {
                if (firstChoiceLineIndex == -1)
                {
                    firstChoiceLineIndex = i;
                }
                var choiceText = SanitizeTranscript(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(choiceText))
                {
                    choices.Add(choiceText);
                }
            }
        }

        if (choices.Count == 0)
        {
            return false;
        }

        while (choices.Count < 3)
        {
            choices.Add("Contemplate your fate in silence");
        }

        string spokenSection = content;
        if (firstChoiceLineIndex > 0)
        {
            spokenSection = string.Join("\n", lines, 0, firstChoiceLineIndex);
        }
        spokenSection = SanitizeTranscript(spokenSection);

        if (string.IsNullOrWhiteSpace(spokenSection))
        {
            spokenSection = "The fortune teller gestures for you to choose.";
        }

        response = new FortuneResponse
        {
            spoken = spokenSection,
            choices = choices.ToArray()
        };

        synthesizedJson = BuildJsonPayload(response.spoken, response.choices);
        return true;
    }

    private string BuildJsonPayload(string spoken, string[] choices)
    {
        var sb = new StringBuilder();
        sb.Append("{\"spoken\":\"");
        sb.Append(EscapeForJson(spoken));
        sb.Append("\",\"choices\":[");
        for (int i = 0; i < choices.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append('\"');
            sb.Append(EscapeForJson(choices[i]));
            sb.Append('\"');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string EscapeForJson(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    private bool WasSkipPressed()
    {
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
        if (Input.GetKeyDown(skipKey))
        {
            return true;
        }
        return false;
#elif ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return false;
        }

        switch (skipKey)
        {
            case KeyCode.Space:
                return keyboard.spaceKey.wasPressedThisFrame;
            case KeyCode.Return:
                return keyboard.enterKey.wasPressedThisFrame;
            case KeyCode.KeypadEnter:
                return keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
            case KeyCode.Escape:
                return keyboard.escapeKey.wasPressedThisFrame;
            default:
                var mappedKey = TryMapKey(skipKey, keyboard);
                return mappedKey != null && mappedKey.wasPressedThisFrame;
        }
#else
        return false;
#endif
    }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
    private static KeyControl TryMapKey(KeyCode keyCode, Keyboard keyboard)
    {
        // Handles common alphanumeric keys when using the new Input System only.
        if (keyCode >= KeyCode.A && keyCode <= KeyCode.Z)
        {
            int offset = keyCode - KeyCode.A;
            return keyboard[(Key)((int)Key.A + offset)];
        }

        if (keyCode >= KeyCode.Alpha0 && keyCode <= KeyCode.Alpha9)
        {
            int offset = keyCode - KeyCode.Alpha0;
            return keyboard[(Key)((int)Key.Digit0 + offset)];
        }

        return null;
    }
#endif

    private void OnDestroy()
    {
        if (_musicSource != null && _musicSource.isPlaying)
        {
            _musicSource.Stop();
        }
    }

    private void SetTalkingState(bool talking)
    {
        if (talkingAnimator == null || _talkingParameterHash == -1)
        {
            return;
        }

        try
        {
            talkingAnimator.SetBool(_talkingParameterHash, talking);
        }
        catch (Exception ex)
        {
            if (logResponses)
            {
                Debug.LogWarning($"FortuneTellerController: Failed to set talking parameter: {ex.Message}");
            }
        }
    }

    private void SetChoicesVisible(bool visible)
    {
        _choicesCurrentlyVisible = visible;
        if (_choicesContainer != null)
        {
            _choicesContainer.SetActive(!_uiHiddenByConfig && visible);
        }
    }

    private void SetChoicesInteractable(bool value)
    {
        if (choiceButtons == null)
        {
            return;
        }

        foreach (var button in choiceButtons)
        {
            if (button != null)
            {
                button.interactable = value && !_uiHiddenByConfig;
            }
        }
    }

    private void ApplyUiVisibility(bool visible)
    {
        _uiHiddenByConfig = !visible;

        if (_uiRoot != null && _uiRoot != gameObject)
        {
            if (_uiRoot.activeSelf != visible)
            {
                _uiRoot.SetActive(visible);
            }
        }
        else
        {
            if (dialogueText != null)
            {
                dialogueText.gameObject.SetActive(visible);
            }
            if (_choicesContainer != null)
            {
                _choicesContainer.SetActive(visible && _choicesCurrentlyVisible);
            }
        }

        if (!visible)
        {
            SetChoicesInteractable(false);
        }
        else if (_choicesContainer != null)
        {
            _choicesContainer.SetActive(_choicesCurrentlyVisible);
        }
    }

    private void CacheUiRoot()
    {
        if (_uiRoot != null)
        {
            return;
        }

        if (dialogueText != null)
        {
            var canvas = dialogueText.GetComponentInParent<Canvas>(true);
            if (canvas != null)
            {
                _uiRoot = canvas.gameObject;
                return;
            }

            _uiRoot = dialogueText.gameObject;
            return;
        }

        if (_choicesContainer != null)
        {
            _uiRoot = _choicesContainer;
        }
    }

    private void EnsureMusicSource()
    {
        if (_musicSource != null)
        {
            return;
        }

        var existingSources = GetComponents<AudioSource>();
        foreach (var source in existingSources)
        {
            if (source != null && source != _audioSource)
            {
                _musicSource = source;
                break;
            }
        }

        if (_musicSource == null)
        {
            _musicSource = gameObject.AddComponent<AudioSource>();
        }

        _musicSource.playOnAwake = false;
        _musicSource.loop = true;
        _musicSource.spatialize = false;
        _musicSource.volume = 0.35f;
        _musicSource.priority = Mathf.Clamp((_audioSource != null ? _audioSource.priority + 1 : 129), 0, 256);
    }

    private void ConfigureBackgroundMusic()
    {
        EnsureMusicSource();
        if (_musicSource == null)
        {
            return;
        }

        if (config != null && config.HasBackgroundMusic)
        {
            var desiredVolume = Mathf.Clamp01(config.backgroundMusicVolume);
            var shouldRestart = _musicSource.clip != config.backgroundMusic;

            _musicSource.clip = config.backgroundMusic;
            _musicSource.volume = desiredVolume;
            _musicSource.loop = config.backgroundMusicLoop;

            if (config.playBackgroundMusicOnStart)
            {
                if (shouldRestart && _musicSource.isPlaying)
                {
                    _musicSource.Stop();
                }

                if (!_musicSource.isPlaying && _musicSource.clip != null)
                {
                    _musicSource.Play();
                }
            }
            else if (shouldRestart && _musicSource.isPlaying)
            {
                _musicSource.Stop();
            }
        }
        else
        {
            if (_musicSource.isPlaying)
            {
                _musicSource.Stop();
            }
            _musicSource.clip = null;
        }
    }

    public void PlayBackgroundMusic(AudioClip clip = null, bool loop = true, float volume = -1f)
    {
        EnsureMusicSource();
        if (_musicSource == null)
        {
            return;
        }

        if (clip != null && _musicSource.clip != clip)
        {
            if (_musicSource.isPlaying)
            {
                _musicSource.Stop();
            }
            _musicSource.clip = clip;
        }

        if (_musicSource.clip == null)
        {
            return;
        }

        _musicSource.loop = loop;
        if (volume >= 0f)
        {
            _musicSource.volume = Mathf.Clamp01(volume);
        }

        if (!_musicSource.isPlaying)
        {
            _musicSource.Play();
        }
    }

    public void StopBackgroundMusic()
    {
        if (_musicSource != null && _musicSource.isPlaying)
        {
            _musicSource.Stop();
        }
    }

    private void AppendAssistantLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _conversation.Add(new ChatMessage
        {
            role = "assistant",
            content = text.Trim()
        });
    }

    private void AppendUserLine(string choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
        {
            return;
        }

        _conversation.Add(new ChatMessage
        {
            role = "user",
            content = choice.Trim()
        });
    }

    [Serializable]
    private class ChatRequest
    {
        public string model;
        public ChatMessage[] messages;
    }

    [Serializable]
    private class ChatMessage
    {
        public string role;
        public string content;
    }

    [Serializable]
    private class FortuneResponse
    {
        public string spoken;
        public string[] choices;
    }

    [Serializable]
    private class ChatCompletionResponse
    {
        public Choice[] choices;
    }

    [Serializable]
    private class Choice
    {
        public Message message;
    }

    [Serializable]
    private class Message
    {
        public string content;
    }

    [Serializable]
    private class FortuneJson
    {
        public string spoken;
        public string[] choices;
    }

    [Serializable]
    private class ElevenLabsRequest
    {
        public string text;
        public string model_id;
        public ElevenLabsVoiceSettings voice_settings;
    }

    [Serializable]
    private class ElevenLabsVoiceSettings
    {
        public float stability;
        public float similarity_boost;
    }
}
