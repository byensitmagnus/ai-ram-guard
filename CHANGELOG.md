# Changelog

## Unreleased

- New fix: **Turn off** for a server that comes from a Claude Desktop plugin (for example `desktop-commander`), which has no config entry. It sets `"enabled": false` for the plugin in Claude Desktop's `plugin-list\…\plugins.json`, with a backup and **Turn on again**. The plugin's skills go too, and it takes effect when Claude Desktop restarts.
- A server started by a launcher script (`email-mcp-launch.py` starting `@codefuturist/email-mcp`) is now one fix, not two. The server's own card said "turn it off in the app" although switching off the launcher's config entry removes both. Its memory counts in the launcher's fix.
- The internet-connection warning names the helpers that hold most of the connections ("most in @codefuturist/email-mcp (96)"), so you know which server to turn off.

## 1.6.1 — 2026-11-21

- The memory-limit warning no longer waits on the *Fixes* page. The Overview's status line, the notification-area tooltip and the ring (orange) show it, and a notification tells you once, at most every 4 hours (with *Notifications* on).
- Fix: a silent upgrade or uninstall (`/VERYSILENT`, package managers) gave up after 30 seconds while the app was running. Windows' Restart Manager can't close an app that lives in the notification area. Setup now asks the app to exit first. The release check upgrades and uninstalls over a running app.

## 1.6.0 — 2026-11-21

The numbers were too kind. The app counted RAM in use right now, and its own trimming moves idle memory to the pagefile: 90 copies of an MCP server showed as 760 MB while they held 6.6 GB.

- Every app and helper is now also measured by what it holds, RAM and pagefile together. That is what closing it gives back. *Fixes* and the Overview's "fixes could save" use it, and each process counts once although several fixes would close it. The app list shows "39 GB with pagefile" next to an app that holds far more than its RAM. `airg scan` has a `held` column (`heldMB`, `commitMB`, `commitLimitMB` in `--json`).
- *Fixes* warns when Windows has handed out 85 % of all the memory it can (RAM plus pagefile). At the limit, apps crash or won't start, however much RAM is free. Freeing RAM doesn't lower it, closing does.
- New fix: **Close them** for Claude sessions nobody wrote in for 3 hours (or your own *Close idle Claude sessions after*). It scans again first, so a session you wrote in since stays. The conversation is kept.
- Idle sessions now include `claude --bg` background sessions, not only the desktop app's. A closed one is listed as stopped; `claude attach` resumes it.
- A session that works without writing is not idle. One Claude itself marks as busy is never closed, and writes by subagents, tool results and background jobs count as activity. A session you cleared with `/clear` is judged by its new conversation. Looking a session up no longer walks every project folder.

## 1.5.0 — 2026-11-04

- Close idle Claude sessions: *Settings → Limits → Close idle Claude sessions after* (off by default). Claude (Desktop / Code) keeps every session's process and MCP servers running until the app closes; a session nobody wrote in for that many hours is closed with everything it started. When you write in it again, Claude starts it with the same conversation (tested). Its Remote Control switches off. Idle time comes from the session's transcript, so a session without one is never closed. `airg clean` and `airg watch` take `--close-idle HOURS`.

## 1.4.1 — 2026-11-04

- *Fixes* warns when an AI app holds 100+ internet connections, or the whole PC 300+, and names the three programs holding the most. On mobile broadband the provider shares a limited pool of connections (CGNAT): when it runs out, pages hang although speed tests look fine. Only IPv4 connections to public addresses count; LAN, loopback and Tailscale don't use the pool.
- `airg scan` shows each AI app's internet connections (`connections` in `--json`).

## 1.4.0 — 2026-11-03

- Direct download page in Danish and English, with one Windows installer and one universal Mac download. GitHub README now starts with downloads and two installation steps.
- New Windows Setup EXE: no ZIP extraction, per-user installation, Start menu shortcut, autostart, CLI on PATH and a standard uninstaller. Opens the app after interactive installation.
- Fix a startup race: a released installer mutex can still exist while the installed app starts. The app now checks actual ownership instead of treating object existence as a running instance. Two regression tests cover it.
- First native Mac beta for macOS 13+, Apple Silicon and Intel. Memory/process overview, duplicate helper counts, automatic or manual cleanup of previously observed orphan helpers, disk usage, pause, menu bar and optional login startup. No Windows-style memory trimming, config rewriting or cache moves on Mac.
- Mac helper cleanup rechecks PID, UID, microsecond start identity and live app ancestry, including the original owning app. It never treats an arbitrary orphan process as safe to close.
- Universal Mac DMG/ZIP with an ad-hoc integrity signature. This beta is not Apple-notarized and requires explicit first-open approval; the page and installation guide say so before download. Developer ID/notarization support is ready for configured credentials.
- CI builds and tests Windows and Mac. Release checks include clean Windows installation/window startup/uninstallation and a real Mac application-start smoke test.
## 1.3.1 — 2026-11-02

- Fix the app window freezing or failing to open from the notification area or a second launch on some PCs. Window signals and background scan results now always return to the Windows Forms UI thread, even when startup already has a default synchronization context.
- Add regression tests for opening a window and completing a background scan with both an absent and a default startup context.

## 1.3.0 (2026-10-26)

The app now does the fix instead of telling you what to do.

**New**
- *Fixes* page (was *Recommendations*): every fix the app can make has its own button. **Turn off** a duplicated MCP server: it is removed from `~/.claude.json` (every project included) or set to `enabled = false` in Codex' `config.toml`. A backup is made first, and **Turn on again** restores it exactly. **Close them** for leftovers, **Cap at N GB** for an app that uses over a fifth of your RAM, and **Fix all** when there are several. What only you can change (a plugin, an npx wrapper) is listed apart under *Needs you*, with where to do it.
- Safe next to apps that write their config all the time: the file is read again right before writing, and if it changed, the fix starts over on the new version. Written atomically, read back afterwards, format (indent, line ends, BOM) kept. A file with comments, a non-UTF-8 file or an unusual TOML layout is never rewritten. A server is matched by its whole name or package, never by a common name like `index.js`, and the card names the entry it removes. Undo records and backups (20 per file) stay on this PC, not in the roaming profile.
- *Disk*: **Clean up** old temp files. Only in a temp folder, only files nobody wrote or created for 7 days, never a file any program has open (also one opened with delete sharing), never through a link, and with the count and size shown first.
- `airg --version` (also `-v`, `version`) prints the version. It used to print the help and exit 1.
- `airg advise --json` has `canFix`.

**Redesign**
- One layout on every page: a header with one main action, then cards with the same spacing (32 px page margin, 24 inside a card, 16 between cards).
- Overview leads with one sentence ("AI apps use 10.0 GB") and two numbers: free RAM and what the fixes would save, with **Free RAM now** and **See fixes** under them.
- Activity groups back-to-back rounds ("Freed 46.6 GB in 10 rounds") and game-mode priority changes, and shows fixes, temp clean-ups and caps.
- Paths under your user folder show as `~` on the Disk page too.

**Fix**
- A helper called `server.mjs` was matched to every config, because the section name `mcp_servers` contains "server".

## 1.2.2 (2026-10-25)

**Fix**
- A dark box covered the top right of the first card on every page (on Overview the *Everything else* tile). The header button panel kept its 200×100 default height and painted over the card.
- Page scrollbars are dark in the dark theme instead of white.
- README screenshots are now captured with `PrintWindow`, which shows controls in their real stacking order, so a bug like the one above shows up in them.

## 1.2.1 (2026-10-25)

**Fix**
- Memory is no longer trimmed again every round. The 15-minute pause after a trim compared against the size right after it, which is near zero, so any regrowth ended the pause. Now a process is trimmed again only when it grows a quarter past its size before the trim. Seen on a 32 GB gaming PC: in game mode about 215 AI processes were trimmed every 2 minutes.

## 1.2.0 (2026-10-25)

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
