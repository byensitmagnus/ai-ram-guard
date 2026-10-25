# Changelog

## 1.0.0 (2026-10-25)

First public release.

**App**
- Dashboard: RAM ring, 24-hour history graph, a row per AI app with its icon, MCP servers and usage
- Recommendations page: duplicated MCP servers and npx wrappers, with the config file that loads them and the RAM each fix saves
- Activity page with everything the guard did
- Settings: automatic guard, leftover cleanup, memory freeing, game mode, notifications, autostart, limits, check interval, per-app RAM caps
- Pause for an hour from the window or the tray menu
- Follows the Windows light/dark theme
- Per-user installer (no admin): Start menu shortcut, autostart, uninstall from Settings → Apps; or run portable
- Starting it again brings the window up; installing a newer version replaces the running one

**Engine**
- RAM per AI app (Claude, Codex/ChatGPT, Cursor, Windsurf, VS Code, Gemini CLI, Grok, LM Studio, Ollama)
- MCP servers named by package, with `cmd /c` and npx bin shims folded into the server they start
- Safe leftover cleanup: only helpers seen running under a live AI app, once their app is gone; PID-reuse check before closing and trimming
- Memory freeing below a free-RAM limit; the app in front is never touched, including its helpers and the terminal a CLI agent runs in
- A broken user `profiles.json` is ignored and logged instead of stopping the app
- Per-app caps, game mode, pause
- Advisor that reads only the MCP server sections of known config files

**CLI**
- `airg scan`, `airg advise`, `airg clean`, `airg watch`, all with JSON output where it makes sense
