# Hacky Steam Chat -> Discord Voice channel TTS Bot

This works if you are me or have a fair bit of know-how.

## Dependencies

For the software itself just download opus.dll and libsodium.dll as per the docs [here](https://github.com/discord-net/Discord.Net/blob/1b64d19c845cb7c612a1c52288c8b44cff605105/docs/guides/voice/sending-voice.md)

For running it you need to rename `App.config.example` to `App.config` and edit the values within which will require:

* Having a steam account you'd like to use as a message reciever (bot-ish, it never sends messages) (I have not read their terms of service, but it may be against them, use at your own risk etc etc).
* Having a discord bot account you'd like to use

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
* `!tts help` - Show help

### Slash Commands

All commands are also available as slash commands (e.g., `/link`, `/join`, `/changevoice`, etc.)

### Voice Support

* **System Voices**: Microsoft David Desktop, Microsoft Zira Desktop, etc.
* **Coqui TTS Voices**: Any voice starting with "CoQui" prefix (e.g., CoQui_female_1, CoQui_male_1, etc.)