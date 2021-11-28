# Hacky Steam Chat -> Discord TTS Bot
This works if you are me or have a fair bit of know-how.

## Dependencies
For the software itself just download opus.dll and libsodium.dll as per the docs [here](https://github.com/discord-net/Discord.Net/blob/1b64d19c845cb7c612a1c52288c8b44cff605105/docs/guides/voice/sending-voice.md)

For running it you need to rename `App.config.example` to `App.config` and edit the values within which will require: 
* Having a steam account you'd like to use as a message reciever (bot-ish, it never sends messages) (I have not read their terms of service, but it may be against them, use at your own risk etc etc).
* Having a discord bot account you'd like to use

## Todos
* Add unlink (not strictly needed as we let you adjust your link even today)
* Deal properly with multiple guilds (discord servers) and remove / rework UserPrefs text/voice channel id storage (perhaps avoid !tts link in cases where you haven't switched servers just channels)