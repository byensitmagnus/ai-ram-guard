# Why Claude Code, Codex and Cursor use so much RAM on Windows

If Task Manager shows dozens of **"Node.js JavaScript Runtime"** processes and your RAM is at 70–90 %, AI coding tools are the usual cause. This page explains why, how to measure it, and how to fix it, with or without AI RAM Guard.

## The short version

1. **MCP servers run once per session, not once per machine.** Claude Code, Codex, Cursor and others start every MCP server in your config for each chat, thread or window.
2. **`npx` doubles it.** A server started as `npx -y some-mcp@latest` runs an extra `npx` Node process (~90 MB) in front of the real server, plus a `cmd.exe` wrapper on Windows.
3. **Sessions don't really close.** Background sessions, sub-agents and "archived but still loaded" chats keep their servers alive.
4. **Crashes leave orphans.** If the AI app dies, its MCP servers can keep running with no parent.

## Measure it

```powershell
airg scan
```

Or without installing anything (PowerShell):

```powershell
Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
  Group-Object CommandLine | Sort-Object Count -Descending |
  Select-Object Count, @{n='MB';e={[int](($_.Group | Measure-Object WorkingSetSize -Sum).Sum/1MB)}}, Name
```

Identical command lines with a high count are duplicated MCP servers.

## Fix it

| Fix | Effect | Effort |
|---|---|---|
| Close or archive AI sessions you don't use | Removes their MCP servers | Manual |
| Only load heavy MCP servers in the projects that need them (project config instead of global config) | Fewer servers per session | One-time |
| Disable MCP servers you never use | Biggest single win | One-time |
| Install the server globally (`npm i -g`) and point the config at `node <path>` instead of `npx -y …@latest` | Removes the `npx` wrapper (~90 MB per copy) | One-time |
| Run **AI RAM Guard** | Closes orphans, frees idle AI memory automatically, game mode | Install once |

### Where the configs live

| App | MCP config |
|---|---|
| Claude Code | `~/.claude.json` (global `mcpServers`), `.mcp.json` in a project, plugins |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json`, plus connectors/extensions in the app |
| OpenAI Codex | `~/.codex/config.toml` → `[mcp_servers.<name>]` (add `enabled = false` to switch one off) |
| Cursor | `~/.cursor/mcp.json` and `.cursor/mcp.json` in a project |

## A real example

On the machine this tool was built on (32 GB, Windows 11):

- `@wonderwhy-er/desktop-commander`: **36 processes, ~0.8–2.3 GB** (11 Claude sessions × server + `npx` + `cmd`)
- `rea-agents` MCP: **25 processes, ~4 GB** (loaded globally in both Claude and Codex, used rarely)
- After switching REA off globally in Codex and cleaning up: **8.2 → 13.1 GB free**, with every app still open.
