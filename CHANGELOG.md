# Changelog

## 0.1.0 (2026-10-25)

First public release.

- RAM per AI app (Claude, Codex/ChatGPT, Cursor, Windsurf, VS Code, Gemini CLI, Grok, LM Studio, Ollama)
- Named MCP servers with duplicate detection
- Safe orphan cleanup (parent-chain rule + PID-reuse check)
- Working-set trimming below a free-RAM limit, never the foreground window
- Game mode with a stricter limit while a fullscreen app is in front
- Tray app with live ring icon, report window, autostart
- `airg` CLI with `scan`, `scan --json`, `clean`, `clean --dry-run`, `watch`
- User-extendable `profiles.json`
