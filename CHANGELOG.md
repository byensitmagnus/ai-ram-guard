# Changelog

## 1.2.0 (unreleased)

**New**
- *Disk* page and `airg disk`: where AI tools and their caches write (npm, uv, pip, NuGet, Playwright, Hugging Face, Ollama, temp, Claude Code, Codex), the size and the real drive, also when the folder is a junction or npm's cache is set in `~/.npmrc`
- Move a cache to another drive in one click, or with `airg move <id> D:`, and undo it (`airg undo <id>`). Uses `robocopy`: files in use stay behind, a cache with links in it (Hugging Face in Developer Mode) is left alone rather than split, the old variable is restored exactly, and undo never overwrites a folder you chose since
- Lower priority in the background (on by default): AI apps you are not using get low memory priority, and while a game is in front also below-normal CPU priority and EcoQoS. Restored as soon as you switch to the app (or its terminal), the game ends, you pause, or the app exits
- Keyboard: Ctrl+1–5 switch pages
- Install puts `airg` on your user `PATH` (removed again on uninstall)

**Smaller and supported**
- Runs on .NET 10 (LTS, supported until November 2028); .NET 8 support ends 10 November 2026
- Download about a third smaller: the app is 52 MB instead of 72 MB, and `airg.exe` 12 MB instead of 35 MB
- `airg` prints numbers the same way everywhere ("4,600 MB", "8.4 GB") and writes UTF-8, so output piped to a file or another tool keeps its characters

## 1.1.0 (2026-10-25)

After an independent design and code review.

**Fixes**
- A maximised window (browser, editor) no longer counts as a fullscreen game, so game mode only kicks in for real games
- Memory is not trimmed again every round: a process trimmed in the last 15 minutes is skipped unless it grew by a quarter ("Free RAM now" still trims everything)
- "Freed by AI RAM Guard" now counts what the guard itself freed, not every change in free RAM
- `~/.claude.json` is only re-read when it changed

**Look and feel**
- Overview: AI usage is the big number in the ring; free RAM, with your limit, is the first tile
- Duplicated MCP servers show as orange chips (`desktop-commander ×48`) on each app
- App bars show the share of installed RAM, with a tick at the app's cap
- History graph with round GB steps and clock times
- Activity is a readable timeline grouped by day instead of a raw log
- Recommendations show savings as a badge; easier-to-read text width
- Sharper logo and a clearer tray icon that follows the taskbar theme
- One brand colour family (no blue-violet gradient end); brand text is lifted for contrast on dark
- Toggles slide, buttons and switches have screen-reader names
- Keyboard: Ctrl+1–4 switch pages, F5 checks again, Esc closes the window
- High-DPI screens: the window is scaled by Windows with sharp text instead of clipping

**Install**
- `AiRamGuard.exe --uninstall --quiet` removes the app without a dialog, so package managers like winget can uninstall it
- Releases include `SHA256SUMS.txt`


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
