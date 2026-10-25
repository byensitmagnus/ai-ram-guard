# `airg`: command line

| Command | What it does |
|---|---|
| `airg scan` | RAM per AI app, its helpers/MCP servers (duplicates flagged) and orphans |
| `airg scan --json` | Same data as JSON (MB values) for scripts and dashboards |
| `airg advise` | Permanent fixes: duplicated MCP servers, npx wrappers, the config file that loads them, and what each saves |
| `airg advise --json` | Same as JSON: `kind`, `title`, `detail`, `savingsMB`, `configPath` |
| `airg clean` | Close orphans and trim AI apps now, regardless of the limit |
| `airg clean --dry-run` | Only show what `clean` would do |
| `airg watch` | Guard every 2 minutes (headless version of the app) |
| `airg watch --min-free 8 --interval 120 --no-game-mode` | Custom limit (GB), interval (s), game mode off |
| `airg disk` | Where AI tools and their caches write, size and drive, biggest first |
| `airg disk --json` | Same as JSON: `id`, `name`, `path`, `realPath`, `mb`, `onSystemDrive`, `movable`, `links`, `variable` |
| `airg move <id> D:` | Move a cache to `D:\AiCache\<id>` and point its tool there (user environment variable) |
| `airg move <id> <folder>` | Same, to an empty or new folder of your choice |
| `airg undo <id>` | Move it back and restore the variable exactly as it was (or remove it) |
| `airg --version` | Print the version |

Ids for `move`: `npm`, `uv`, `pip`, `nuget`, `playwright`, `huggingface`, `ollama`, `temp`. `claude` and `codex` are listed by `disk` but not moved: they hold sessions and settings. Neither is a cache with links in it (Hugging Face makes them in Developer Mode): only an admin can recreate links, so it would arrive half-moved. Programs you start after a move use the new folder; restart the ones that were open.

Exit code is `0` on success and `1` for an unknown command or a move that was refused.

## JSON shape

```json
{
  "totalRamMB": 32562,
  "freeRamMB": 13824,
  "aiMB": 10137,
  "agents": [
    {
      "id": "claude",
      "name": "Claude (Desktop / Code)",
      "processes": 166,
      "mb": 8849,
      "helpers": [{ "key": "@wonderwhy-er/desktop-commander", "count": 36, "mb": 782 }]
    }
  ],
  "orphans": [{ "pid": 4242, "name": "node.exe", "key": "rea-agents", "mb": 238 }]
}
```

## Run it as a scheduled task instead of the tray app

```powershell
$a = New-ScheduledTaskAction -Execute "$env:LOCALAPPDATA\Programs\AiRamGuard\airg.exe" -Argument "clean"
$t = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName "AI RAM Guard" -Action $a -Trigger $t
```

`clean` ignores the limit. Use `watch` for limit-based behaviour.

Every command remembers which helpers it saw running under an AI app (`%LOCALAPPDATA%\AiRamGuard\lineage.json`). Only those are closed later as leftovers, so a scheduled `clean` works best alongside the app or a `watch`, which scan regularly.
