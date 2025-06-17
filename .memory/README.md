# Discord Bot TTS - Project Memory Bank

This directory contains a shared memory bank system for AI assistants (roo and cline) working on the DiscordBotTTS project.

## Usage Instructions

### For AI Assistants
1. **Memory Refresh**: Read the main `README.md` file and other relevant referenced files
1. **During Work**: Update memory bank files with new findings or changes

## Project Overview
**Name**: DiscordBotTTS  
**Type**: Discord Bot with Text-to-Speech functionality  
**Status**: Successfully converted from Discord.Net to NetCord  
**Language**: C# (.NET 9)  

## Key Technologies
- **Discord Framework**: NetCord (v1.0.0-alpha.386)
- **Steam Integration**: SteamKit2
- **TTS**: System.Speech + Optional Coqui TTS
- **Target Framework**: net9.0-windows

## Project Structure
```
DiscordBotTTS/
├── Program.cs              # Main entry point, gateway client setup
├── CommandHandler.cs       # Message processing and command routing
├── TTSModule.cs            # TTS functionality and voice management
├── InfoModule.cs           # Basic info commands
├── Steam.cs               # Steam integration
├── App.config             # Configuration (bot token, steam creds)
├── DiscordBotTTS.csproj   # Project file
└── .memory/               # Memory bank for AI assistants
```

## Current Status
✅ **Fully Functional**: Bot connects to Discord and responds to commands  
✅ **Command System**: All TTS commands working (`!tts join`, `!tts help`, etc.)  
✅ **Steam Integration**: Connected and operational  
✅ **Voice Channel Detection**: Automatic user voice state detection  
✅ **Error Handling**: Comprehensive logging and error management

## Key Commands
- `!tts join [channel_id]` - Join voice channel (auto-detects user's channel)
- `!tts leave` - Leave voice channel
- `!tts link <steamid> [voice] [rate]` - Link Steam account
- `!tts changevoice <voice>` - Change TTS voice
- `!tts changerate <rate>` - Change speech rate (-10 to 10)
- `!tts help` - Show help
- `!say <message>` - Echo message

## Configuration
**App.config** contains:
- `BotToken`: Discord bot token
- `SteamUser` / `SteamPass`: Steam credentials  
- `EnableMessageContent`: true (for message content intent)
- `ffmpeg` / `coquitts`: Optional Coqui TTS paths

## Recent Major Changes
1. **Discord.Net → NetCord Migration**: Complete conversion to NetCord API
1. **Comprehensive Logging**: Added detailed debug logging throughout
1. **Error Handling**: Robust error handling with user-friendly messages

## Technical Details
- **Gateway Intents**: `AllNonPrivileged | MessageContent`
- **Message Processing**: Custom command parser with `!` prefix
- **Voice Features**: NetCord voice implementation
- **Steam Integration**: Fully operational with friend state tracking

## Build & Run
```powershell
cd C:\Users\lburton\source\repos\DiscordBotTTS
dotnet build
dotnet run
```

## Recent Manual Test Results
- Bot connects successfully
- Message handling working perfectly
- Voice channel detection operational
- All command parsing functional

## Future Enhancements
- Add more TTS voice options
