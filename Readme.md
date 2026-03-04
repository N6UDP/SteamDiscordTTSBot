# Hacky Steam Chat -> Discord Voice channel TTS Bot

This works if you are me or have a fair bit of know-how.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows, x64)
- A Discord bot account with a valid token
- A Steam account for message bridging
- ffmpeg (for Coqui TTS audio conversion, optional)
- Native dependencies for NetCord voice support (DAVE protocol): `libdave.dll`, `opus.dll`, `libopus.dll`, `libsodium.dll` — see [NetCord: Installing Native Dependencies](https://github.com/NetCordDev/NetCord/blob/alpha/Documentation/guides/basic-concepts/installing-native-dependencies.md)

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

* `!tts link <steamid> [voice] [rate]` — Link Steam account to Discord
* `!tts unlink` — Unlink Steam account
* `!tts verify` — Check current link status
* `!tts join [channel_id]` — Join voice channel (auto-detects if no ID provided)
* `!tts leave` — Leave voice channel
* `!tts say <message>` — Send a TTS message directly from Discord
* `!tts changevoice <voice>` — Change TTS voice
* `!tts changerate <-10 to 10>` — Change speech rate (10 is fastest)
* `!tts changeserver` — Change server
* `!tts voices` — List all available voices
* `!tts uploadvoice <name> [options]` — Upload a custom PocketTTS voice (attach .wav/.mp3/.safetensors)
  * Options: `--truncate` `--lsd-decode-steps <n>` `--temperature <f>` `--noise-clamp <f>` `--eos-threshold <f>` `--frames-after-eos <n>`
* `!tts renamevoice <old> <new>` — Rename a custom PocketTTS voice
* `!tts deletevoice <name>` — Delete a custom PocketTTS voice
* `!tts customvoices` — List custom PocketTTS voices
* `!tts destination <discord|mumble|both>` — Change where TTS audio is sent
* `!tts mumblestatus` — Show Mumble connection status
* `!tts help` — Show help

### Slash Commands

All commands are also available as slash commands (e.g., `/link`, `/join`, `/changevoice`, `/say`, etc.)

### Available Voice Types

* **System Voices** — Windows SAPI voices (e.g., Microsoft David Desktop, Microsoft Zira Desktop)
* **Coqui TTS Voices** — Prefix `CoQui` (e.g., CoQui_female_1, CoQui_male_1)
* **PocketTTS Voices** — Prefix `Pocket` (e.g., Pocket_alba, Pocket_marius)

### TTS Destinations

Audio can be routed to `discord` (default), `mumble`, or `both` using `!tts destination`.