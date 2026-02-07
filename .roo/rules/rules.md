# DiscordBotTTS Rules

## Memory Bank & Shared Instructions

Please reference and use the shared memory bank and instructions:

- **Memory Bank**: `.memory/README.md` — Project overview, status, and context for AI agents
- **Copilot Instructions**: `.github/copilot-instructions.md` — Detailed architecture, patterns, and conventions

Always read these files before starting work. Update them after significant changes.

## Key Rules

1. Always preserve dual command system (text `!` commands + `/` slash commands)
2. Always preserve the Steam bridge functionality
3. Config changes must be reflected in both `App.config` and `App.config.example`
4. Use `ConcurrentDictionary` and `SemaphoreSlim` for thread-safe shared state
5. Follow the existing log format: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}` or `{DateTime.Now:s}:Module:Level: message`
6. New TTS engines should use voice name prefix routing (e.g., `CoQui` prefix → Coqui TTS)
7. Update the memory bank after completing work
