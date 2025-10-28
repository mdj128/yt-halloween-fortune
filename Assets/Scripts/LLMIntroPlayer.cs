using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Retrieves a short line from a local OpenAI-compatible LLM and speaks it via ElevenLabs TTS.
/// Attach to a GameObject with an AudioSource for a quick POC.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class LLMIntroPlayer : MonoBehaviour
{
    [Header("LLM (LM Studio)")]
    [Tooltip("Base URL for the OpenAI-compatible endpoint (LM Studio default shown).")]
    public string llmEndpoint = "http://127.0.0.1:1234/v1/chat/completions";

    [Tooltip("Model name exposed by LM Studio, e.g. 'qwen2.5-7b-instruct'.")]
    public string llmModel = "default";

    [TextArea]
    [Tooltip("System prompt to steer the skeleton's persona.")]
    public string systemPrompt = "You are a friendly skeleton caretaker welcoming visitors to a spooky graveyard.";

    [TextArea]
    [Tooltip("User prompt used to kick off the conversation when the scene loads.")]
    public string userPrompt = "Greet the player as they arrive on this haunted Halloween night.";

    [Header("ElevenLabs TTS")]
    [Tooltip("https://api.elevenlabs.io/ voice ID to render the line with.")]
    public string elevenLabsVoiceId = "";

    [Tooltip("ElevenLabs API key (keep this in a secure place; assign at runtime).")]
    public string elevenLabsApiKey = "";

    [Tooltip("Optional: override the ElevenLabs model id.")]
    public string elevenLabsModelId = "";

    [Tooltip("If true, attempt to read ELEVENLABS_API_KEY from environment when the component starts.")]
    public bool readApiKeyFromEnvironment = true;

    [Tooltip("If true, attempt to read ELEVENLABS_VOICE_ID from environment when the component starts.")]
    public bool readVoiceIdFromEnvironment = true;

    [Header("Playback")]
    [Tooltip("Delay (seconds) before requesting the LLM on start, giving XR/Audio a moment to initialise.")]
    public float startDelay = 1.0f;

    [Tooltip("Set true to log the generated transcript to the console.")]
    public bool logTranscript = true;

    private AudioSource _audioSource;

    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
    }

    private void Start()
    {
        if (_audioSource == null)
        {
            Debug.LogError("LLMIntroPlayer requires an AudioSource.");
            return;
        }

        if (readApiKeyFromEnvironment && string.IsNullOrWhiteSpace(elevenLabsApiKey))
        {
            elevenLabsApiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        }

        if (readVoiceIdFromEnvironment && string.IsNullOrWhiteSpace(elevenLabsVoiceId))
        {
            var envVoice = Environment.GetEnvironmentVariable("ELEVENLABS_VOICE_ID");
            if (!string.IsNullOrWhiteSpace(envVoice))
            {
                elevenLabsVoiceId = envVoice;
            }
        }

        if (string.IsNullOrWhiteSpace(elevenLabsApiKey))
        {
            Debug.LogWarning("ElevenLabs API key is empty. Populate it in the inspector before running.");
            return;
        }

        if (string.IsNullOrWhiteSpace(elevenLabsVoiceId))
        {
            Debug.LogWarning("ElevenLabs voice ID is empty. Populate ELEVENLABS_VOICE_ID env var or set it on the component.");
            return;
        }

        StartCoroutine(RunSequence());
    }

    private IEnumerator RunSequence()
    {
        if (startDelay > 0f)
        {
            yield return new WaitForSeconds(startDelay);
        }

        string text;

        using (var llmRequest = BuildLlmRequest())
        {
            yield return llmRequest.SendWebRequest();

            if (llmRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"LLM request failed: {llmRequest.error}");
                yield break;
            }

            ChatResponse response;
            try
            {
                response = JsonUtility.FromJson<ChatResponse>(llmRequest.downloadHandler.text);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to parse LLM response: {ex.Message}");
                yield break;
            }

            if (response?.choices == null || response.choices.Length == 0 || response.choices[0].message == null)
            {
                Debug.LogError("LLM response missing choices or message.");
                yield break;
            }

            text = SanitizeTranscript(response.choices[0].message.content);
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning("LLM response was empty after sanitization.");
                yield break;
            }
        }

        if (logTranscript)
        {
            Debug.Log($"Skeleton says: {text}");
        }

        using (var ttsRequest = BuildTtsRequest(text))
        {
            yield return ttsRequest.SendWebRequest();

            if (ttsRequest.result != UnityWebRequest.Result.Success)
            {
                string responseBody = TryGetResponseText(ttsRequest);
                Debug.LogError($"ElevenLabs TTS request failed: {ttsRequest.error}\n{responseBody}");
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(ttsRequest);

            if (logTranscript)
            {
                var contentType = ttsRequest.GetResponseHeader("Content-Type");
                if (!string.IsNullOrWhiteSpace(contentType))
                {
                    Debug.Log($"ElevenLabs audio content type: {contentType}");
                }
            }

            if (clip == null || clip.length <= 0f)
            {
                string responseBody = TryGetResponseText(ttsRequest);
                float clipLength = clip == null ? 0f : clip.length;
                Debug.LogError($"Received empty audio clip from ElevenLabs. Content-Type={ttsRequest.GetResponseHeader("Content-Type")} Length={clipLength}\n{responseBody}");
                yield break;
            }

            _audioSource.clip = clip;
            _audioSource.Play();
        }
    }

    private static readonly Regex ActionMarkupRegex = new(@"\*[^*]+\*|\[[^\]]+\]|\([^\)]+\)", RegexOptions.Compiled);

    private static string SanitizeTranscript(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        var withoutActions = ActionMarkupRegex.Replace(trimmed, string.Empty);
        return withoutActions.Replace("  ", " ").Trim();
    }

    private UnityWebRequest BuildLlmRequest()
    {
        var prompt = new ChatRequest
        {
            model = llmModel,
            messages = new[]
            {
                new ChatMessage { role = "system", content = systemPrompt },
                new ChatMessage { role = "user", content = userPrompt }
            }
        };

        string json = JsonUtility.ToJson(prompt);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

        var request = new UnityWebRequest(llmEndpoint, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        return request;
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

    private UnityWebRequest BuildTtsRequest(string text)
    {
        var payload = new ElevenLabsRequest
        {
            text = text,
            model_id = elevenLabsModelId,
            voice_settings = new ElevenLabsVoiceSettings
            {
                stability = 0.4f,
                similarity_boost = 0.7f
            }
        };

        string json = JsonUtility.ToJson(payload);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        string uri = $"https://api.elevenlabs.io/v1/text-to-speech/{elevenLabsVoiceId}";

        var request = new UnityWebRequest(uri, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerAudioClip(uri, AudioType.MPEG);
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", "audio/mpeg");
        request.SetRequestHeader("xi-api-key", elevenLabsApiKey);

        return request;
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
    private class ChatResponse
    {
        public ChatChoice[] choices;
    }

    [Serializable]
    private class ChatChoice
    {
        public ChatMessage message;
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
