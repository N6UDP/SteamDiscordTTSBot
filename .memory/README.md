# Discord Bot TTS - Project Memory Bank

This directory contains a shared memory bank system for AI assistants (GitHub Copilot, Roo, Cline) working on the DiscordBotTTS project.

## Usage Instructions

### For AI Assistants
1. **Memory Refresh**: Read this `README.md` file and `.github/copilot-instructions.md` before starting any work
2. **During Work**: Update memory bank files with new findings or changes
3. **Shared Instructions**: Architecture details and conventions are in `.github/copilot-instructions.md`

## Project Overview
**Name**: DiscordBotTTS (repo: SteamDiscordTTSBot)  
**Type**: Discord Bot — bridges Steam chat messages to Discord voice channels via TTS  
**Status**: Fully functional, actively maintained  
**Language**: C# (.NET 10)  
**Platform**: Windows (requires System.Speech / SAPI)

## Key Technologies
- **Discord Framework**: NetCord (v1.0.0-alpha.460) — gateway, REST, voice
- **Steam Integration**: SteamKit2 (v3.4.0) — login, friends, message receiving
- **TTS Engines**: System.Speech (Windows SAPI) + Coqui TTS (exe or server mode) + PocketTTS (Kyutai Labs, server mode)
- **Config**: System.Configuration.ConfigurationManager with `App.config` XML
- **Serialization**: System.Text.Json (built-in) for user preferences and voice mappings
- **Target Framework**: net10.0-windows

## Project Structure
```
DiscordBotTTS/
├── Program.cs              # Entry point, gateway client, slash command registration & routing
├── CommandHandler.cs       # ! prefix message command parsing, routes to TTSModule
├── TTSModule.cs            # Core TTS: voice management, speech synthesis, Coqui TTS, PocketTTS, user prefs, Steam dequeue
├── InfoModule.cs           # Simple info/echo commands (!say)
├── Steam.cs               # SteamKit2: login, friends list, message receiving → Queue
├── App.config             # Configuration (bot token, steam creds, TTS settings) — SECRETS, never commit
├── App.config.example     # Template config without secrets
├── userprefs.json         # Persisted user preferences (voice, rate, Steam ID, guild)
├── pocketvoices.json      # Persisted custom PocketTTS voice mappings (name → file path)
├── steamid.json           # Persisted Steam ID → Discord ID mapping
├── DiscordBotTTS.csproj   # Project file (.NET 10, x64, self-contained publish)
├── .github/
│   └── copilot-instructions.md  # GitHub Copilot instructions (architecture, conventions)
├── .roo/
│   └── rules/rules.md          # Roo/Cline rules (references shared instructions)
├── .memory/
│   └── README.md                # This file — shared memory bank for all AI agents
└── .prompts/
    └── add-pocket-tts.md        # Implementation prompt: add PocketTTS engine support
```

## Architecture Details

### Command Flow
```
Discord Message ("!tts join")
  → CommandHandler.HandleCommandAsync()
    → HandleTTSCommand() → HandleTTSCommandAsync()
      → TTSModule.JoinChannel() / LeaveChannel() / etc.

Discord Slash Command ("/join")
  → Program.SlashCommandHandler()
    → CommandHandler.HandleTTSCommandAsync()
      → TTSModule.JoinChannel() / LeaveChannel() / etc.
```

### Steam Bridge Flow
```
Steam Friend sends message
  → Steam.OnFriendMsg() → enqueues to Steam.Queue
  → TTSModule.Dequeuer() (background loop, polls every 100ms)
    → Dequeue() → maps SteamID → DiscordID → UserPrefs → GuildID
      → SendAsync() → generates audio → streams to voice channel
```

### Voice Connection Lifecycle
```
JoinChannel():
  1. gatewayClient.JoinVoiceChannelAsync(guildId, channelId)
  2. voiceClient.StartAsync()
  3. voiceClient.EnterSpeakingStateAsync()
  4. voiceClient.CreateOutputStream()
  5. Store in map[guildId] = (VoiceClient, VoiceGuildChannel, Stream, SemaphoreSlim)

SendAsync():
  1. Clean message (strip URLs)
  2. Log to text channel
  3. Generate audio (System.Speech or Coqui TTS)
  4. OpusEncodeStream wraps output stream
  5. Copy PCM → Opus → voice channel
  6. Add ✅ reaction on success

LeaveChannel():
  1. UpdateVoiceStateAsync(guildId, null) — disconnect
  2. Dispose output stream, voice client, semaphore
  3. Remove from map
```

### TTS Engine Routing
- Engine switches: `EnableSystemSpeech`, `EnableCoquiTTS`, `EnablePocketTTS` (each can be toggled independently)
- Voice name starts with `Pocket` → PocketTTS engine (server mode HTTP API)
  - Predefined voices: alba, marius, javert, jean, fantine, cosette, eponine, azelma (sent as `voice_url`)
  - Custom `.wav` voices uploaded by users (sent as `voice_wav` file upload)
  - Custom `.safetensors` voices from `PocketTTS_VoiceDirectory` (sent as `voice_url` path)
- Voice name starts with `CoQui` → Coqui TTS engine
  - `CoquiMode=exe` → spawn tts.exe per request with model args
  - `CoquiMode=server` → HTTP POST to persistent tts-server API
- All other voice names → System.Speech.Synthesis.SpeechSynthesizer
- Voice matching: exact → prepend "Microsoft " → append " Desktop" (fuzzy)

### Data Persistence
- `userprefs.json`: ConcurrentDictionary<ulong, UserPrefs> (Discord ID → prefs)
- `steamid.json`: ConcurrentDictionary<ulong, ulong> (Steam ID → Discord ID)
- `pocketvoices.json`: Dictionary<string, string> (custom voice name → file path) — custom PocketTTS voices uploaded by users
- Serialization: System.Text.Json (`JsonSerializer.Serialize/Deserialize`)
- Atomic write: write .tmp → move current to timestamped `.bak` → move .tmp to current
- Timestamped backups: `{path}.{yyyy-MM-dd_HH-mm-ss}.bak` (never overwrites previous backups)
- Empty write guards: refuses to write empty, `{}`, or `[]` JSON data
- Snapshot serialization: `Saver()` copies ConcurrentDictionary to a plain `Dictionary<>` before serializing to avoid race conditions
- Auto-save loop every 10 seconds in `Saver()` task (wrapped in try/catch to prevent silent death)
- Load validation: checks for empty files on startup, logs item counts

## Current Status
✅ **Fully Functional**: Bot connects to Discord and responds to commands  
✅ **Dual Command System**: Both `!` prefix and `/` slash commands working  
✅ **Steam Integration**: Connected and operational, auto-accepts friend requests  
✅ **Voice Channel**: Auto-detect user's channel or specify by ID  
✅ **Coqui TTS**: Server mode with speaker discovery and exe fallback  
✅ **PocketTTS**: Server mode with predefined + custom voices, upload/rename/delete management  
✅ **Engine Switches**: Each TTS engine can be independently enabled/disabled  
✅ **Error Handling**: Comprehensive logging, graceful shutdown, voice reconnection  
✅ **GHCP/Roo/Cline Support**: Shared instructions and memory bank for AI agents

## Key Commands
| Command | Slash | Description |
|---------|-------|-------------|
| `!tts join [channel_id]` | `/join` | Join voice channel (auto-detects user's channel) |
| `!tts leave` | `/leave` | Leave voice channel |
| `!tts link <steamid> [voice] [rate]` | `/link` | Link Steam account |
| `!tts unlink` | `/unlink` | Unlink Steam account |
| `!tts verify` | `/verify` | Check current link status |
| `!tts changevoice <voice>` | `/changevoice` | Change TTS voice |
| `!tts changerate <rate>` | `/changerate` | Change speech rate (-10 to 10) |
| `!tts changeserver` | `/changeserver` | Change server/guild binding |
| `!tts voices` | `/voices` | List available voices |
| `!tts say <message>` | `/say` | Speak a message via TTS (Discord-native input) |
| `!tts uploadvoice <name>` | `/uploadvoice` | Upload a custom PocketTTS voice (.wav attachment) |
| `!tts renamevoice <old> <new>` | `/renamevoice` | Rename a custom PocketTTS voice |
| `!tts deletevoice <name>` | `/deletevoice` | Delete a custom PocketTTS voice |
| `!tts customvoices` | `/customvoices` | List all custom PocketTTS voices |
| `!tts help` | `/help` | Show help |
| `!say <message>` | — | Echo message |

## Configuration (App.config)

### Required Settings
| Key | Description |
|-----|-------------|
| `BotToken` | Discord bot token |
| `SteamUser` | Steam account username |
| `SteamPass` | Steam account password |
| `EnableMessageContent` | Enable message content intent (true) |

### Optional TTS Settings
| Key | Description |
|-----|-------------|
| `ffmpeg` | Path to ffmpeg.exe (for Coqui audio conversion) |
| `coquitts` | Path to tts.exe (Coqui CLI) |
| `CoquiMode` | `exe` (CLI per-request) or `server` (persistent HTTP API) |
| `CoquiVoiceModels` | Voice:model mappings for exe mode (semicolon-separated) |
| `coqui_server` | Path to tts-server.exe |
| `coqui_server_port` | Server port (default: 5002) |
| `coqui_server_use_cuda` | Enable CUDA acceleration |
| `coqui_server_model` | TTS model name for server mode |
| `coqui_server_timeout` | Request timeout in seconds |
| `coqui_server_startup_timeout` | Server startup timeout |
| `coqui_server_retry_interval` | Retry interval for server readiness |
| `coqui_server_default_language` | Default language code (en) |
| `CoquiSpeakers` | Fallback speaker list (comma-separated) |

### Engine Switches
| Key | Description |
|-----|-------------|
| `EnableSystemSpeech` | Enable/disable System.Speech engine (default: true) |
| `EnableCoquiTTS` | Enable/disable Coqui TTS engine (default: true) |
| `EnablePocketTTS` | Enable/disable PocketTTS engine (default: true) |

### PocketTTS Settings
| Key | Description |
|-----|-------------|
| `PocketTTS_Executable` | Path to PocketTTS server executable (python script) |
| `PocketTTS_ServerHost` | PocketTTS server host (default: 127.0.0.1) |
| `PocketTTS_ServerPort` | PocketTTS server port (default: 5005) |
| `PocketTTS_DefaultVoice` | Default PocketTTS voice name (default: alba) |
| `PocketTTS_StartupTimeout` | Server startup timeout in seconds (default: 120) |
| `PocketTTS_RetryInterval` | Retry interval in seconds for server readiness (default: 2) |
| `PocketTTS_RequestTimeout` | HTTP request timeout in seconds (default: 60) |
| `PocketTTS_VoiceDirectory` | Directory for .safetensors voice models |
| `PocketTTS_VoiceUploadAllowlist` | Comma-separated Discord user IDs allowed to upload voices (empty = all) |
| `PocketTTS_MaxUploadSizeMB` | Max upload size for voice files in MB (default: 25) |
| `HuggingFace_Token` | HuggingFace API token (for gated models) |

## Build & Run
```powershell
cd C:\Users\lburton\source\repos\DiscordBotTTS
dotnet build
dotnet run
```

## Important Conventions
- Log format: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}` or `{DateTime.Now:s}:Module:Level: message`
- Voice prefix routing: `Pocket*` → PocketTTS, `CoQui*` → Coqui TTS, no prefix → System.Speech
- Speech rate: -10 to 10
- Guild ID = primary key for voice connections
- Bot auto-accepts all Steam friend requests
- Thread safety: ConcurrentDictionary + SemaphoreSlim for shared state
- Config changes: update both `App.config` and `App.config.example`

## Recent Changes
- **2025-07-17**: Added PocketTTS (Kyutai Labs) engine — server mode, predefined voices (alba, marius, etc.), custom .wav voice upload/rename/delete, `pocketvoices.json` persistence, engine enable/disable switches, slash command integration
- **2025-07-17**: Added `.copilotignore` to hide `App.config` from AI agents (contains secrets)
- **2025-07-16**: Migrated from Newtonsoft.Json to System.Text.Json (removed NuGet package, using built-in)
- **2025-07-16**: Added `!tts say <message>` / `/say` command for Discord-native TTS input (no Steam needed)
- **2025-07-16**: Timestamped JSON backups (`{file}.{timestamp}.bak`), empty write guards, snapshot serialization in Saver()
- **2025-07-16**: Updated from .NET 9 to .NET 10, updated all dependencies
- **2025-07-16**: Added GitHub Copilot instructions (`.github/copilot-instructions.md`)
- **2025-07-16**: Updated Roo rules to reference shared instructions
- **2025-07-16**: Enhanced memory bank with comprehensive architecture documentation
- **2025-07-16**: Code cleanup — removed unused usings, replaced FQ type references
- **Previous**: Discord.Net → NetCord migration, Coqui TTS server mode, comprehensive logging

## Future Enhancements
- More TTS voice options
- Old timestamped backup cleanup (prune backups older than N days)

## Known Issues
- ~~JSON persistence files becoming empty~~ — **FIXED**: Added empty write guards, snapshot serialization, timestamped backups, try/catch in Saver(), and load-time validation. Previous backup files are preserved with timestamps for recovery.
