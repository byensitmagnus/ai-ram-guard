# Install AI RAM Guard

## Windows: one installer

1. [Download AiRamGuard-Setup.exe](https://github.com/byensitmagnus/ai-ram-guard/releases/latest/download/AiRamGuard-Setup.exe).
2. Open it and click **Install**. Leave **Open AI RAM Guard** checked on the final screen.

The app opens after installation. It adds a Start menu shortcut, starts quietly with Windows, puts `airg` on your user PATH and appears in *Settings → Apps*. No admin rights, .NET download or manual unpacking is needed.

For an update, run the new installer over the existing version. If the old app is using the files, Setup asks to close it. Settings and history are retained. Remove it using *Settings → Apps → AI RAM Guard*; your history and settings are kept.

Silent deployment: `AiRamGuard-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. Silent installation does not open the window.

## Mac beta: one universal download

1. [Download the DMG](https://github.com/byensitmagnus/ai-ram-guard/releases/latest/download/AiRamGuard-mac-universal.dmg).
2. Open it and drag **AI RAM Guard** to **Applications**.
3. Open **AI RAM Guard** from Applications. Its window opens, and the menu bar icon stays available when you close the window.

Requires macOS 13 or newer. The same app supports Apple Silicon and Intel; no .NET runtime or Rosetta is required. Enable **Start at login** in the app if wanted. To update, quit the old app and replace it in Applications. To uninstall, quit it and move it to Trash. Its local data stays unless you remove it separately.

The Mac beta includes RAM monitoring, duplicate-helper counts, cleanup of verified leftover helpers and disk usage. It does not include Windows working-set trimming, config mutation or cache relocation. [Mac implementation and build instructions](../macos/README.md).

## First-open approval

These downloads do not yet use a commercial code-signing identity. Do not expect every operating system to open a downloaded build without a security prompt.

- **Windows SmartScreen:** verify that the file came from this project's release. If you choose to run it, click **More info → Run anyway**. You do not need to disable SmartScreen.
- **Mac Gatekeeper:** the beta has an ad-hoc integrity signature, but **is not notarized by Apple**. After an opening attempt is blocked, use *System Settings → Privacy & Security → Open Anyway*. [Apple's instructions](https://support.apple.com/102445). Do not disable Gatekeeper or remove quarantine attributes.

Removing the Mac approval prompt for normal distribution requires a Developer ID certificate and Apple notarization. The packaging script supports a configured signing identity and notary profile; no signing credentials are included in this repository.

## Portable Windows copy and CLI

The [portable ZIP](https://github.com/byensitmagnus/ai-ram-guard/releases/latest/download/AiRamGuard-win-x64.zip) remains available for scripts and advanced users. Extract both files; run `AiRamGuard.exe` and choose **Just run it**, or use `airg.exe` in a terminal.

Legacy silent install from the ZIP: `AiRamGuard.exe --install`. This starts in the notification area; open the Start menu shortcut to show the window. Clicking **Install** in the portable welcome screen now opens the installed app immediately.

A terminal hosted by a Store/MSIX app can see private copies of the registry and AppData. Use the normal installer from Windows for a user installation; an agent can start it outside its package with `Invoke-CimMethod Win32_Process -MethodName Create`.

## Build from source

Windows: .NET 10 SDK, `dotnet test -c Release`, then publish `src/AiRamGuard.Tray` and `src/AiRamGuard.Cli` as self-contained `win-x64` binaries. [CI](../.github/workflows/ci.yml) and [release workflow](../.github/workflows/release.yml) contain the exact commands.

Mac: Xcode Command Line Tools, `swift test --package-path macos`, then `bash packaging/macos/package.sh`. This builds both architectures and packages the app. [Mac build details](../macos/README.md).

## Windows data folders

| Folder | Contents |
| --- | --- |
| `%APPDATA%\AiRamGuard` | Settings and custom profiles |
| `%LOCALAPPDATA%\AiRamGuard` | History, statistics, lineage, config backups and log |

Windows settings remain available in the app. Mac data is local under `~/Library/Application Support/AiRamGuard`.
