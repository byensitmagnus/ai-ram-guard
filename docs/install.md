# Install AI RAM Guard

## Option 1: download (recommended)

1. Download `AiRamGuard-win-x64.zip` from the [latest release](https://github.com/byensitmagnus/ai-ram-guard/releases/latest).
2. Unzip it anywhere, e.g. `%LOCALAPPDATA%\Programs\AiRamGuard`.
3. Run `AiRamGuard.exe`. A ring icon appears next to the clock (look under `^` if Windows hides it).
4. Right-click the icon → **Start with Windows**.

The zip is self-contained, so you don't need to install .NET. The small `-framework` zip needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

> **SmartScreen warning?** New open-source apps aren't code-signed yet, so Windows may show "Windows protected your PC". Click **More info → Run anyway**, or build from source (below) if you prefer.

## Option 2: build from source

```powershell
winget install Microsoft.DotNet.SDK.8
git clone https://github.com/byensitmagnus/ai-ram-guard
cd ai-ram-guard
dotnet test
dotnet publish src/AiRamGuard.Tray -c Release -r win-x64 --self-contained -o out
dotnet publish src/AiRamGuard.Cli  -c Release -r win-x64 --self-contained -o out
.\out\AiRamGuard.exe
```

## Settings

Everything is in the tray menu. The file behind it is `%APPDATA%\AiRamGuard\settings.json`:

```json
{
  "Guard": {
    "MinFreeMB": 6144,
    "GameModeMinFreeMB": 10240,
    "GameMode": true,
    "KillOrphans": true,
    "Trim": true,
    "IntervalSeconds": 300
  },
  "AutoGuard": true,
  "Notify": true
}
```

## Uninstall

Right-click → untick **Start with Windows** → **Exit**, then delete the folder, `%APPDATA%\AiRamGuard` and `%LOCALAPPDATA%\AiRamGuard`.
