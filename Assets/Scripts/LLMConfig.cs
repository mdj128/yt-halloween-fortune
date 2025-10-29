using UnityEngine;

/// <summary>
/// Serializable configuration for the LLM + ElevenLabs intro pipeline.
/// </summary>
[CreateAssetMenu(menuName = "AI/LLM Config", fileName = "LLMConfig")]
public class LLMConfig : ScriptableObject
{
    [Header("LLM (LM Studio)")]
    public string llmEndpoint = "http://127.0.0.1:1234/v1/chat/completions";
    public string llmModel = "default";

    [TextArea]
    public string systemPrompt =
        "You are Bartholomew the Bone-Seer, a spooky skeletal fortune teller who speaks in short, dramatic sentences. " +
        "Always stay in character. Every reply MUST consist solely of valid JSON matching this schema: " +
        "{\"spoken\":\"STRING\",\"choices\":[\"STRING\",\"STRING\",\"STRING\"]}. " +
        "The \"spoken\" field contains ONLY the words Bartholomew speaks aloud (no names, no stage directions, no extra quotes). " +
        "The \"choices\" array MUST contain exactly three short, distinct player options. " +
        "Do NOT add any text before or after the JSON. If you cannot comply, reply with {\"spoken\":\"I cannot see the future\",\"choices\":[\"Try again\",\"Consult another seer\",\"Leave the crypt\"]}.";

    [TextArea]
    public string userPrompt =
        "It is Halloween night and a curious visitor arrives to have their fortune read. " +
        "Greet them with an unsettling introduction and offer exactly three mysterious choices for how the reading should proceed. " +
        "Remember: reply ONLY with the JSON described in the system prompt.";

    [Header("Intro Sequence")]
    public AudioClip introClip;
    [TextArea]
    public string introSpoken;
    public AudioClip introQuestionClip;
    [TextArea]
    public string introQuestion;
    [Tooltip("Exactly three options presented before the LLM conversation begins.")]
    public string[] introChoices = new string[3];

    [Header("ElevenLabs")]
    public string elevenLabsVoiceId = "";
    public string elevenLabsModelId = "";
    public string elevenLabsApiKey = "";
    [Range(0f, 1f)] public float elevenLabsStability = 0.5f;
    [Range(0f, 1f)] public float elevenLabsSimilarityBoost = 0.75f;

    [Header("Skeleton Animation")]
    public string skeletonAnimatorName = "";
    public string talkingParameter = "IsTalking";

    [Header("Playback")]
    public float startDelay = 1f;
    public bool logTranscript = true;
    [Header("Background Music")]
    public AudioClip backgroundMusic;
    [Range(0f, 1f)] public float backgroundMusicVolume = 0.35f;
    public bool backgroundMusicLoop = true;
    public bool playBackgroundMusicOnStart = true;
    [Header("UI")]
    public bool hideDialogueUI = false;

    public bool HasVoiceId => !string.IsNullOrWhiteSpace(elevenLabsVoiceId);
    public bool HasApiKey => !string.IsNullOrWhiteSpace(elevenLabsApiKey);
    public bool HasBackgroundMusic => backgroundMusic != null;
}
