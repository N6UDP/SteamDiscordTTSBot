# Prompt: Add PocketTTS Engine Support to DiscordBotTTS

## Context

Read `.memory/README.md` and `.github/copilot-instructions.md` for full project context before starting.

**DiscordBotTTS** is a C# (.NET 10) Discord bot that bridges Steam chat messages to Discord voice channels via Text-to-Speech. It currently supports two TTS engines:

1. **System.Speech** (Windows SAPI) — default, voices like "Microsoft David Desktop"
2. **Coqui TTS** — voices prefixed with `CoQui`, supports `exe` mode (CLI per-request) and `server` mode (persistent HTTP API)

We want to add a third engine: **PocketTTS** by Kyutai Labs ([github.com/kyutai-labs/pocket-tts](https://github.com/kyutai-labs/pocket-tts)).

## PocketTTS Overview

PocketTTS is a lightweight, CPU-friendly TTS system. Key characteristics:

- **Python-based** — install via `pip install pocket-tts` or `uv add pocket-tts`
- **Server mode** — `pocket-tts serve --host localhost --port 8000 --voice alba`
- **HTTP API** — `POST /tts` with form data: `text` (required), `voice_url` (optional URL/predefined name), `voice_wav` (optional uploaded file)
- **Health check** — `GET /health` returns `{"status": "healthy"}`
- **Predefined voices**: `alba`, `marius`, `javert`, `jean`, `fantine`, `cosette`, `eponine`, `azelma`
- **Custom voices**: Any `.wav` or `.safetensors` file, or HuggingFace URL (`hf://kyutai/tts-voices/...`)
- **Voice cloning**: Requires accepting terms at `https://huggingface.co/kyutai/pocket-tts` and logging in with `uvx hf auth login`
- **Export voices**: `pocket-tts export-voice input.wav output.safetensors` (converts audio to fast-loading embeddings)
- **Sample rate**: 24000 Hz (needs conversion to 48000 Hz stereo PCM for Discord)
- **Output format**: WAV (streaming response from server)
- **Model weights**: Downloaded from HuggingFace Hub on first use, cached locally
- **HuggingFace token**: Required for voice cloning model access; set via `HF_TOKEN` env var or `hf auth login`

### Important: Installation Source

The **PyPI release** (`pip install pocket-tts` / `uvx pocket-tts`) is older and does **NOT** include `export-voice` or the latest voice cloning features. To get full functionality (especially `export-voice` for converting uploaded audio to `.safetensors`), you **must** install from the GitHub repository:

```bash
# For uvx (recommended — runs in isolated env, no install needed):
uvx --from "git+https://github.com/kyutai-labs/pocket-tts.git" pocket-tts serve
uvx --from "git+https://github.com/kyutai-labs/pocket-tts.git" pocket-tts export-voice input.wav output.safetensors

# For pip (if managing your own venv):
pip install "pocket-tts @ git+https://github.com/kyutai-labs/pocket-tts.git"
```

All `pocket-tts` invocations in this project (server startup, export-voice) must use the `--from` GitHub URL form when using `uvx`, or the equivalent `git+` install when using pip/requirements.txt. The config key `PocketTTS_Executable` controls how the bot invokes PocketTTS (see Configuration Keys below).

## Requirements

### 1. TTS Engine Enable/Disable Switches

Add configuration keys to `App.config` to enable/disable each TTS engine independently:

```xml
<!-- TTS Engine Switches -->
<add key="EnableSystemSpeech" value="true" />
<add key="EnableCoquiTTS" value="true" />
<add key="EnablePocketTTS" value="true" />
```

- When an engine is disabled, its voices should not appear in `!tts voices` / `/voices`
- When a user tries to use a voice from a disabled engine, return a helpful error message
- `CheckVoice()` should respect these switches
- Log which engines are enabled at startup

### 2. PocketTTS Engine Integration

#### Voice Prefix Convention

Following the existing pattern (System.Speech = no prefix, Coqui = `CoQui` prefix):

- **PocketTTS voices**: Use `Pocket` prefix (e.g., `Pocket_alba`, `Pocket_marius`, `Pocket_custom_myvoice`)
- Predefined voices map like: `Pocket_alba` → sends `voice_url=alba` to the API
- Custom voices map like: `Pocket_custom_myvoice` → looks up path in config

#### Configuration Keys

Add to `App.config` / `App.config.example`:

```xml
<!-- PocketTTS Configuration -->
<add key="EnablePocketTTS" value="true" />
<add key="PocketTTS_Executable" value="uvx" />
<!-- How the bot invokes pocket-tts. Options:
     "uvx" (default) — runs: uvx --from "git+https://github.com/kyutai-labs/pocket-tts.git" pocket-tts serve ...
     "pocket-tts" — runs: pocket-tts serve ... (assumes pip-installed from git in a venv on PATH)
     "C:\path\to\python.exe -m pocket_tts" — custom Python path -->
<add key="PocketTTS_ServerHost" value="localhost" />
<add key="PocketTTS_ServerPort" value="8000" />
<add key="PocketTTS_DefaultVoice" value="alba" />
<add key="PocketTTS_StartupTimeout" value="120" />
<add key="PocketTTS_RetryInterval" value="3" />
<add key="PocketTTS_RequestTimeout" value="60" />

<!-- Directory where custom voice .safetensors files are stored -->
<!-- The bot saves exported voices here and looks them up by name -->
<add key="PocketTTS_VoiceDirectory" value="C:\PocketTTS\voices" />

<!-- PocketTTS Custom Voice Mappings (optional, for non-standard paths/URLs) -->
<!-- Format: voice_name:path_or_url (semicolon-separated) -->
<!-- Voices in PocketTTS_VoiceDirectory are auto-discovered and don't need entries here -->
<add key="PocketTTS_CustomVoices" value="custom_mary:hf://kyutai/tts-voices/alba-mackenna/casual.wav" />

<!-- Discord user IDs allowed to upload custom voices (comma-separated) -->
<!-- Leave empty to allow no one; use "*" to allow everyone -->
<add key="PocketTTS_VoiceUploadAllowlist" value="" />

<!-- Maximum file size for voice uploads in MB -->
<add key="PocketTTS_MaxUploadSizeMB" value="10" />

<!-- HuggingFace Token for downloading models and voice cloning access -->
<add key="HuggingFace_Token" value="" />
```

#### Server Lifecycle

Follow the existing Coqui server pattern in `TTSModule.cs`:

1. **Startup**: `InitializePocketTTSServerAsync()` — called from `Program.cs` alongside `InitializeCoquiServerAsync()`
   - Build the command from `PocketTTS_Executable` config:
     - If `"uvx"` (default): run `uvx --from "git+https://github.com/kyutai-labs/pocket-tts.git" pocket-tts serve --host {host} --port {port} --voice {default_voice}`
     - If `"pocket-tts"`: run `pocket-tts serve --host {host} --port {port} --voice {default_voice}`
     - Otherwise: treat value as custom command prefix
   - If `HuggingFace_Token` is set, pass it as `HF_TOKEN` environment variable to the process
   - Redirect stdout/stderr for logging
   - Wait for server readiness via `GET /health` with retry loop
   
2. **Health monitoring**: Check `/health` endpoint before requests (optional, for robustness)

3. **Cleanup**: `CleanupPocketTTSServer()` — called alongside `CleanupCoquiServer()` in shutdown handlers

#### Audio Generation Flow

In `SendAsync()`, add a third branch for PocketTTS voices:

```
if (voice.StartsWith("Pocket"))     → PocketTTS engine
else if (voice.StartsWith("CoQui")) → Coqui TTS engine  
else                                → System.Speech engine
```

The PocketTTS flow:
1. Resolve the voice: strip `Pocket_` prefix, look up in predefined list or custom voice config
2. `POST http://localhost:{port}/tts` with form data:
   - `text` = cleaned message
   - `voice_url` = resolved voice name/URL (for predefined or URL-based voices)
   - OR upload `voice_wav` file (for local .wav custom voices)
3. Receive WAV response (streaming)
4. Save to temp file
5. Convert via ffmpeg to 48kHz stereo PCM (same as Coqui flow): `ffmpeg -i input.wav -ac 2 -f s16le -ar 48000 pipe:1`
6. Stream through OpusEncodeStream to Discord voice channel

#### Voice Discovery

At startup (after server is ready):
- Register predefined voices: `Pocket_alba`, `Pocket_marius`, `Pocket_javert`, `Pocket_jean`, `Pocket_fantine`, `Pocket_cosette`, `Pocket_eponine`, `Pocket_azelma`
- Auto-discover custom voices: scan `PocketTTS_VoiceDirectory` for `.safetensors` files, register as `Pocket_{filename_without_extension}`
- Register additional custom voices from `PocketTTS_CustomVoices` config (for HF URLs and non-standard paths)
- Store in a dictionary similar to `coquiVoiceModels`

#### CheckVoice() Updates

Extend `CheckVoice()` to handle `Pocket` prefix:

```csharp
if (voice.StartsWith("Pocket", StringComparison.OrdinalIgnoreCase))
{
    // Check if PocketTTS is enabled
    // Look up in pocketVoiceModels dictionary
    // Allow "Pocket" alone for default voice
    // Return the voice name if valid
}
```

#### ListVoices() Updates

Add PocketTTS section to voice listing, showing:
- Predefined voices (always available)
- Custom voices (from voice directory + config)
- Note about voice upload/cloning capability (`!tts uploadvoice`)

### 3. Help Text Updates

Update `Help()` to mention PocketTTS voices, the `Pocket_` prefix convention, and voice upload commands.

### 4. Error Handling

- If PocketTTS server is not running, log warning and skip (don't crash)
- If a PocketTTS request fails, log the error and send a message to the text channel
- If HuggingFace token is missing and voice cloning is attempted, provide a helpful error message
- Timeout handling for slow generations (PocketTTS can be slower on CPU for long texts)

### 5. Discord Voice Upload & Management

Users on the allowlist can upload voice samples directly from Discord, and the bot will convert them to `.safetensors` for fast loading. They can also upload pre-made `.safetensors` files directly.

#### Commands

| Command | Slash | Description |
|---------|-------|-------------|
| `!tts uploadvoice <name> [reply to message with attachment]` | `/uploadvoice` | Upload a `.wav`/`.mp3` voice sample or `.safetensors` file from a Discord message attachment. The user replies to a message containing the audio/safetensors file, or attaches it directly to the command message. |
| `!tts renamevoice <old_name> <new_name>` | `/renamevoice` | Rename a custom voice (renames the `.safetensors` file in the voice directory) |
| `!tts deletevoice <name>` | `/deletevoice` | Delete a custom voice (removes the `.safetensors` file) |
| `!tts customvoices` | `/customvoices` | List all custom voices in the voice directory |

#### Upload Flow

1. **Authorization check**: Verify user's Discord ID is in `PocketTTS_VoiceUploadAllowlist` (comma-separated list of user IDs, or `*` for everyone)
2. **Attachment resolution**:
   - If the command message has an attachment, use that
   - If the command message is a reply, check the referenced message for attachments
   - Validate file extension is `.wav`, `.mp3`, or `.safetensors`
   - Validate file size (configurable max, suggest 10MB default via `PocketTTS_MaxUploadSizeMB`)
3. **Download**: Download the attachment to a temp file via Discord CDN URL
4. **Processing**:
   - If `.safetensors`: copy directly to `PocketTTS_VoiceDirectory/{name}.safetensors`
   - If `.wav` or `.mp3`: run `export-voice` to convert to safetensors:

     ```bash
     uvx --from "git+https://github.com/kyutai-labs/pocket-tts.git" pocket-tts export-voice {temp_input_path} {voice_dir}/{name}.safetensors
     ```

     (Use the same `PocketTTS_Executable` config to build the command, same as server startup)
   - Pass `HF_TOKEN` env var if configured (needed for voice cloning model weights)
5. **Registration**: Add `Pocket_{name}` to the voice dictionary so it's immediately available
6. **Feedback**: Send confirmation message with the voice name to use: `Voice uploaded! Use: !tts changevoice Pocket_{name}`
7. **Cleanup**: Delete temp files

#### Rename Flow

1. **Authorization check**: Same allowlist check
2. **Validation**: Verify old voice exists in voice directory, new name doesn't conflict with predefined voices
3. **Rename**: Rename `{old_name}.safetensors` → `{new_name}.safetensors` in voice directory
4. **Update dictionary**: Remove old entry, add new entry
5. **Note**: Does NOT update any users currently using the old voice name — they'll get an error on next TTS and need to `!tts changevoice` to the new name. Log a warning about this.

#### Delete Flow

1. **Authorization check**: Same allowlist check
2. **Validation**: Verify voice exists, is not a predefined voice
3. **Delete**: Remove `.safetensors` file from voice directory
4. **Update dictionary**: Remove entry
5. **Warning**: Same caveat about users currently using the voice

#### Error Handling for Uploads

- User not on allowlist → "You don't have permission to upload custom voices."
- No attachment found → "Please attach a .wav, .mp3, or .safetensors file, or reply to a message with one."
- Invalid file type → "Unsupported file type. Please upload a .wav, .mp3, or .safetensors file."
- File too large → "File exceeds maximum size of {max}MB."
- `export-voice` fails → "Failed to process voice sample. The audio file may be corrupted or too short."
- Name conflicts with predefined voice → "Cannot use that name — it conflicts with a built-in voice."
- Voice directory not configured or doesn't exist → "Custom voice storage is not configured. Set PocketTTS_VoiceDirectory in App.config."
- HF token missing when `export-voice` needs it → "HuggingFace token is required for voice processing. Set HuggingFace_Token in App.config and accept terms at <https://huggingface.co/kyutai/pocket-tts>"

### 6. Files to Modify

| File | Changes |
|------|---------|
| `TTSModule.cs` | Add PocketTTS server management, voice routing, HTTP client calls, voice discovery, voice upload/rename/delete commands |
| `CommandHandler.cs` | Add `uploadvoice`, `renamevoice`, `deletevoice`, `customvoices` command routing |
| `Program.cs` | Call `InitializePocketTTSServerAsync()` at startup, `CleanupPocketTTSServer()` at shutdown; register new slash commands |
| `App.config` | Add PocketTTS config keys, engine switches, HuggingFace token, voice directory, allowlist |
| `App.config.example` | Mirror all new config keys with placeholder values |
| `.memory/README.md` | Update with PocketTTS architecture details |
| `.github/copilot-instructions.md` | Update TTS engine list and conventions |

### 7. Testing Checklist

- [ ] Bot starts with PocketTTS enabled, server launches and becomes healthy
- [ ] Bot starts with PocketTTS disabled, no server process spawned
- [ ] `!tts voices` shows PocketTTS voices when enabled, hides when disabled
- [ ] `!tts changevoice Pocket_alba` sets voice correctly
- [ ] Steam message with Pocket voice generates audio and plays in Discord
- [ ] Custom voice from config works (both local file and HF URL)
- [ ] Server cleanup on bot shutdown (no orphan processes)
- [ ] Graceful handling when PocketTTS server is unavailable
- [ ] ffmpeg conversion produces correct 48kHz stereo PCM output
- [ ] HuggingFace token is passed to server process when configured
- [ ] `!tts uploadvoice myvoice` with attached .wav downloads, converts to safetensors, registers voice
- [ ] `!tts uploadvoice myvoice` with reply to a message containing .mp3 works
- [ ] `!tts uploadvoice myvoice` with attached .safetensors copies directly without conversion
- [ ] Voice upload rejected for users not on allowlist
- [ ] `!tts renamevoice old new` renames the safetensors file and updates the dictionary
- [ ] `!tts deletevoice myvoice` deletes the file and removes from dictionary
- [ ] `!tts customvoices` lists all .safetensors files in voice directory
- [ ] Predefined voice names cannot be used for uploads/renames
- [ ] Auto-discovery at startup finds existing .safetensors files in voice directory

## Implementation Notes

- **Installation**: The PyPI release (v1.0.3) does NOT include `export-voice`. Always use `uvx --from "git+https://github.com/kyutai-labs/pocket-tts.git" pocket-tts` or `pip install "pocket-tts @ git+https://github.com/kyutai-labs/pocket-tts.git"` to get the full feature set including voice cloning export
- The PocketTTS server outputs WAV at 24000 Hz sample rate — this MUST be converted to 48000 Hz stereo PCM via ffmpeg before streaming to Discord (same pipeline as Coqui TTS)
- The `POST /tts` endpoint returns a streaming WAV response, so save to temp file first, then process with ffmpeg (matching the Coqui flow)
- Predefined voice names (alba, marius, etc.) can be passed directly as `voice_url` to the API — no URL prefix needed
- For custom `.safetensors` voices, pass the file path as `voice_url`
- For custom `.wav` voices, upload as `voice_wav` multipart form file
- The PocketTTS server pre-loads the default voice at startup; switching voices mid-request is handled by the API
- Model weights are ~400MB and downloaded on first use from HuggingFace Hub — first startup will be slow
- The `PocketTTS_Executable` config controls invocation; default `"uvx"` automatically adds `--from "git+https://github.com/kyutai-labs/pocket-tts.git"` before `pocket-tts`
- Voice upload workflow runs `export-voice` as a one-shot process (not through the server) — it's a separate CLI invocation that may take 10-30 seconds depending on audio length
- The voice directory (`PocketTTS_VoiceDirectory`) should be created by the bot at startup if it doesn't exist
- `.safetensors` files are small (~few KB) and load near-instantly vs. raw `.wav` files which require slow audio processing on every server restart
