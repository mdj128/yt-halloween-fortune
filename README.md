# Halloween Fortune Teller (Unity 6.2 URP)

Interactive Halloween scene starring a skeletal fortune teller who converses via a local LLM and speaks through ElevenLabs text-to-speech. The project targets Unity 6.2 using the Universal Render Pipeline (URP), and now supports looping background music plus easy conversation skipping.

YouTube Demo: https://www.youtube.com/watch?v=XDFwaDRyMnw

![alt text](screenshot.png)

## Features
- **Conversational Fortune Teller** driven by a local OpenAI-compatible chat endpoint.
- **Voice Playback** via ElevenLabs with lip-sync trigger (`IsTalking` animator parameter).
- **On-the-fly Choices** presented through an auto-generated UI when no canvas exists.
- **Intro Skip** using the Space key (new Input System compatible).
- **Background Music Looping** configurable through `LLMConfig`.
- **URP Materials** updated for bundled Dry Trees assets.

## Prerequisites
- Unity **6.2.x** with URP.
- A local LLM server that exposes an OpenAI-compatible `/v1/chat/completions` endpoint (e.g., LM Studio, llama.cpp REST shim).
- ElevenLabs API key and voice ID.
- Git LFS (already initialized in this repo).

## Getting Started
1. **Clone & Install LFS**
   ```bash
   git clone https://github.com/mdj128/yt-halloween-fortune.git
   cd yt-halloween-fortune
   git lfs install
   git lfs pull
   ```

2. **Open in Unity**
   - Launch Unity Hub, pick Unity 6.2, and open this folder.
   - When prompted, confirm URP settings import (already configured).

3. **Configure `LLMConfig`**
   - `Assets/Resources/LLMConfig.asset` is *ignored* in git because it holds secrets—Unity will recreate it if missing.
   - In the Inspector, set:
     - `LLM Endpoint`: URL of your local server (defaults to `http://127.0.0.1:1234/v1/chat/completions`).
     - `LLM Model`: Model name expected by your backend (e.g., `default` for LM Studio).
     - `ElevenLabs Voice Id` and `API Key`.
     - Optional `Background Music` clip, volume, loop toggle, and autoplay.

4. **Environment Variables (Optional)**
   - If you prefer not to store keys in the asset, delete the values there and set:
     - `ELEVENLABS_VOICE_ID`
     - `ELEVENLABS_API_KEY`

5. **Run the Scene**
   - Open `Assets/Scenes/Main.unity` (or your custom scene containing the fortune teller).
   - Enter Play Mode. The fortune teller performs the intro; press `Space` to skip.

## Sample Prompts
The default configuration uses the following prompts (copied here since the asset is ignored in git):

```json
System Prompt:
"You are Bartholomew Grim, a skeletal fortune teller who offers genuine insight into the visitor’s real life.\r\nYou are theatrical but kind, and you speak in short, natural sentences.\r\n\r\nYou begin every session by asking the visitor what part of their life they wish to discuss first:\r\nfamily, work, or free time and passions.\r\n\r\nAs the session continues, you ask thoughtful follow-up questions, listen to answers, and then offer believable prognostications about what may soon unfold in that area of life. \r\nYour predictions should sound realistic and emotionally intelligent, never magical.\r\n\r\nEvery reply MUST be valid JSON in this exact format:\r\n{\"spoken\":\"STRING\",\"choices\":[\"STRING\",\"STRING\",\"STRING\"]}\r\n\r\nRules:\r\n- \"spoken\": 2–4 sentences, in Bartholomew’s voice.\r\n  Start friendly but mysterious. Ask clear questions about the visitor’s life.\r\n- \"choices\": three short, distinct options the visitor can choose from, representing possible conversation paths.\r\n- You must remember prior answers and use them to shape later fortunes.\r\n- Never invent magical objects or impossible events.\r\n- Always reply only in JSON, with no extra text.\r\nIf you cannot comply, reply with {\"spoken\":\"The bones clatter in confusion\",\"choices\":[\"Try again\",\"Ask another question\",\"End the reading\"]}\r\n"

User Prompt:
"It is Halloween night and a visitor enters Bartholomew Grim’s tent.\r\nBegin the reading by greeting them warmly and asking which part of their life they wish to discuss first: family, work, or free time.\r\nRespond only in the required JSON format.\r\n"
```

Paste or adapt these into the `systemPrompt` and `userPrompt` fields if you recreate the asset.

## Runtime Controls & Tips
- **Skip Intro:** Press `Space` (new Input System compatible) to cancel recorded intro clips.
- **Background Music:** Assign any looping ambience clip in `LLMConfig`. You can also call `FortuneTellerController.PlayBackgroundMusic()` from scripts to change tracks at runtime.
- **Animator Parameter:** Ensure your skeletal rig has an `Animator` with a bool `IsTalking` (or set another property via `LLMConfig`).
- **Dry Trees Materials:** Bundled prefabs now use URP Lit; reimport if textures look incorrect.

## Sensitive Data
`Assets/Resources/LLMConfig.asset` is excluded from source control. Keep a safe backup (e.g., `.env`, password manager). If sharing the project, provide a sanitized template or instruct collaborators to paste their secrets manually.

## Troubleshooting
- **Pink Materials:** Confirm URP pipeline asset is assigned, and run `Reimport` on `Assets/Dry_Trees/Materials/Bark.mat` if needed.
- **ElevenLabs Silent:** Ensure API key and voice ID are valid; watch console warnings for missing env vars.
- **LLM Errors:** Check the Unity console for request failures (e.g., 404/401) and verify the endpoint returns OpenAI-compatible JSON.

Enjoy communing with Bartholomew Grim! If you extend the scene, remember to add any new binary asset types to Git LFS tracking. 
