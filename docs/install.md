# Install AI RAM Guard

## Option 1: download (recommended)

1. Download `AiRamGuard-win-x64.zip` from the [latest release](https://github.com/byensitmagnus/ai-ram-guard/releases/latest) and unzip it.
2. Run `AiRamGuard.exe` and click **Install**.

Install copies the app to `%LOCALAPPDATA%\Programs\AiRamGuard`, adds a Start menu shortcut, starts it with Windows, adds that folder to your user `PATH` (so `airg` works in any new terminal) and adds an entry under *Settings → Apps*. Everything is per-user: no admin prompt.

Prefer not to install? Click **Just run it**. The app then runs from where you unzipped it and only starts with Windows if you switch that on in *Settings*.

Installing a newer version over an older one replaces the running copy. Settings and history are kept.

The zip is self-contained, so you don't need to install .NET. The small `-framework` zip needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

> **SmartScreen warning?** New open-source apps aren't code-signed yet, so Windows may show "Windows protected your PC". Click **More info → Run anyway**, or build from source (below).

### Silent install

```powershell
.\AiRamGuard.exe --install
```

Same as clicking Install, without the dialog. Useful for setting up several PCs.

> **Letting an AI agent install it?** A terminal inside the Claude desktop app (the Microsoft Store build) sees its own private copy of your registry and `%LOCALAPPDATA%`. An install run from there looks fine to the agent, but Windows never sees the autostart or the *Settings → Apps* entry, so the app doesn't start after a reboot. Run the installer yourself, or have the agent start it outside the app: `Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = '"<path>\AiRamGuard.exe" --install' }`.

## Option 2: build from source

```powershell
winget install Microsoft.DotNet.SDK.10
git clone https://github.com/byensitmagnus/ai-ram-guard
cd ai-ram-guard
dotnet test
dotnet publish src/AiRamGuard.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out
dotnet publish src/AiRamGuard.Cli  -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o out
.\out\AiRamGuard.exe
```

## Settings

Everything is on the *Settings* page in the app. The file behind it is `%APPDATA%\AiRamGuard\settings.json`:

```json
{
  "Guard": {
    "MinFreeMB": 6144,
    "GameModeMinFreeMB": 10240,
    "GameMode": true,
    "KillOrphans": true,
    "Trim": true,
    "LowerPriority": true,
    "IntervalSeconds": 120,
    "AgentCapsMB": { "codex": 3072 },
    "PausedUntil": null
  },
  "AutoGuard": true,
  "Notify": true
}
```

| Folder | Holds |
|---|---|
| `%APPDATA%\AiRamGuard` | `settings.json`, your own `profiles.json`, `moves.json` (how to undo cache moves) |
| `%LOCALAPPDATA%\AiRamGuard` | history, statistics, `lineage.json`, `guard.log` |

## Uninstall

*Settings → Apps → AI RAM Guard → Uninstall*, or *Uninstall…* at the bottom of the app's *Settings* page. For scripts: `"%LOCALAPPDATA%\Programs\AiRamGuard\AiRamGuard.exe" --uninstall --quiet`. That removes the app, its shortcut, autostart and its `PATH` entry. Caches you moved stay where they are: undo the moves first on the *Disk* page if you want them back on the old drive. Delete the two folders above too if you want your settings and history gone.

Portable copy: switch off *Start with Windows* in the app, exit it from the tray menu and delete the folder.