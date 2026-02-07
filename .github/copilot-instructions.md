# GitHub Copilot Instructions for DiscordBotTTS

## Memory Bank

Always read and reference the shared memory bank at `.memory/README.md` before starting any work. Update it with new findings or changes when work is completed.

## Project Overview

**DiscordBotTTS** is a C# (.NET 10) Discord bot that bridges Steam chat messages to Discord voice channels via Text-to-Speech. It uses the **NetCord** library for Discord integration and **SteamKit2** for Steam connectivity.

## Architecture

### Core Files

| File | Purpose |
|------|---------|
| `Program.cs` | Entry point, gateway client setup, slash command registration & routing |
| `CommandHandler.cs` | `!` prefix message command parsing and routing to TTSModule |
| `TTSModule.cs` | Core TTS logic: voice channel management, speech synthesis, Coqui TTS integration, user preferences, Steam message dequeuing |
| `InfoModule.cs` | Simple info/echo commands |
| `Steam.cs` | SteamKit2 integration: login, friends list, message receiving |
| `App.config` | All configuration (bot token, Steam creds, TTS engine settings) — **contains secrets, never commit** |
| `App.config.example` | Template config without secrets |

### Key Patterns

- **Command System**: Dual command system — both `!` prefix text commands and `/` slash commands route through `CommandHandler.HandleTTSCommandAsync()`
- **Discord-native TTS**: `!tts say <message>` / `/say` lets users speak TTS directly from Discord without needing Steam
- **Voice Connection**: Uses NetCord's `VoiceClient` with Opus encoding. Connections tracked in a `ConcurrentDictionary<ulong, (VoiceClient, VoiceGuildChannel, Stream, SemaphoreSlim)>` keyed by guild ID
- **TTS Engines**: Two engines supported:
  - `System.Speech.Synthesis.SpeechSynthesizer` for Windows SAPI voices
  - Coqui TTS in two modes: `exe` (CLI per-request) or `server` (persistent HTTP API)
- **Steam Bridge**: Steam messages are enqueued in `Steam.Queue` and dequeued by `TTSModule.Dequeuer()`, which maps Steam IDs → Discord users → guild voice channels
- **User Preferences**: Stored in `userprefs.json` (voice, rate, Steam ID, guild) with atomic write + timestamped backup. Empty write guards prevent data loss. Snapshot serialization avoids race conditions.
- **Configuration**: Uses `System.Configuration.ConfigurationManager` with `App.config` XML

### Important Conventions

- Log format: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}` or `{DateTime.Now:s}:Module:Level: message`
- Voice names starting with `CoQui` prefix are routed to Coqui TTS engine
- All other voices use System.Speech
- Speech rate range: -10 to 10
- Guild ID is the primary key for voice connections
- The bot auto-accepts Steam friend requests

### Dependencies

- **NetCord** (1.0.0-alpha.460) — Discord gateway, REST, voice
- **NetCord.Services** (1.0.0-alpha.460) — Service framework
- **SteamKit2** (3.4.0) — Steam network client
- **System.Speech** (10.0.x) — Windows SAPI TTS
- **System.Configuration.ConfigurationManager** (10.0.x) — App.config access
- **System.Text.Json** (built-in) — JSON serialization for user prefs & Steam ID mapping

### Build & Run

```powershell
cd C:\Users\lburton\source\repos\DiscordBotTTS
dotnet build
dotnet run
```

### When Making Changes

1. **Always preserve** the dual command system (text + slash commands)
2. **Always preserve** the Steam bridge functionality
3. **Test voice names** — the `CheckVoice()` method handles fuzzy matching
4. **Config changes** must be reflected in both `App.config` and `App.config.example`
5. **New TTS engines** should follow the existing pattern: check voice prefix → route to appropriate engine in `SendAsync()`
6. **Thread safety** — use `ConcurrentDictionary` and `SemaphoreSlim` for shared state
7. **Update the memory bank** (`.memory/README.md`) after significant changes
