# Add support for an AI app

AI RAM Guard knows AI apps from data, not code. Every app is an entry in [`profiles.json`](../src/AiRamGuard.Core/profiles.json):

```json
{
  "id": "zed",
  "name": "Zed (AI agent)",
  "processNames": ["zed"],
  "helperPatterns": ["zed-mcp"]
}
```

| Field | Meaning |
|---|---|
| `id` | Short unique id, lowercase |
| `name` | Shown in the report |
| `processNames` | The app's own executables, without `.exe`, case-insensitive. Everything started under them counts toward the app. |
| `helperPatterns` | Regular expressions (.NET) matched against a process's command line, for helpers specific to this app. |
| `cliPatterns` | For CLI agents with no app process of their own (Gemini CLI runs as `node.exe`): regular expressions for the agent's own command line. Only checked for script runtimes (`node`, `bun`, `deno`, `python`, `uv`). |

Top-level `helperPatterns` apply to all apps (generic MCP patterns).

## Try it locally, no build needed

Open AI RAM Guard → **Settings → Advanced → Add your own AI app → Edit profiles**. That opens `%APPDATA%\AiRamGuard\profiles.json`. Your entries are merged with the built-in ones, and yours win on the same `id`. A file with a typo is ignored (and logged), never fatal. Restart AI RAM Guard, then check:

```powershell
airg scan
```

## Send it upstream

1. Add the entry to `src/AiRamGuard.Core/profiles.json`.
2. If you added a helper pattern, add a test case in `tests/AiRamGuard.Core.Tests/AnalyzerTests.cs` with a real (anonymised) command line.
3. `dotnet test`, then open a pull request. The issue template "New AI app" lists what we need.

## Finding the right names

```powershell
Get-CimInstance Win32_Process | Where-Object CommandLine -match 'mcp|npx|uvx' |
  Select-Object ProcessId, ParentProcessId, Name, CommandLine | Format-List
```
