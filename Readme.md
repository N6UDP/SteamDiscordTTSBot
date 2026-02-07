# Hacky Steam Chat -> Discord Voice channel TTS Bot

This works if you are me or have a fair bit of know-how.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows, x64)
- A Discord bot account with a valid token
- A Steam account for message bridging
- ffmpeg (for Coqui TTS audio conversion, optional)

## Setup

1. Rename `App.config.example` to `App.config`
2. Edit the values within:
   - `BotToken` — your Discord bot token
   - `SteamUser` / `SteamPass` — Steam credentials (required for Steam→Discord TTS bridge)
   - TTS engine settings (optional, for Coqui TTS)
3. Build and run:

```powershell
dotnet build
dotnet run
```

## Usage

### Text Commands

* `!tts link <steamid> [voice] [rate]` - Link Steam account to Discord
* `!tts unlink` - Unlink Steam account
* `!tts verify` - Check current link status
* `!tts join [channel_id]` - Join voice channel (auto-detects if no ID provided)
* `!tts leave` - Leave voice channel
* `!tts changevoice <voice>` - Change TTS voice
* `!tts changerate <-10 to 10>` - Change speech rate (10 is fastest)
* `!tts changeserver` - Change server
* `!tts voices` - List all available voices
* `!tts say <message>` - Speak a message via TTS (Discord-native, no Steam needed)
* `!tts help` - Show help

### Slash Commands

All commands are also available as slash commands (e.g., `/link`, `/join`, `/changevoice`, etc.)

### Voice Support

* **System Voices**: Microsoft David Desktop, Microsoft Zira Desktop, etc.
* **Coqui TTS Voices**: Any voice starting with "CoQui" prefix (e.g., CoQui_female_1, CoQui_male_1, etc.)