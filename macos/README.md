# AI RAM Guard for Mac

The Mac preview is a native menu-bar app for **macOS 13 Ventura or later**, on **Apple Silicon and Intel**. It needs no .NET, Homebrew or other runtime.

## Install

1. Download `AiRamGuard-mac-universal.dmg` from the GitHub release.
2. Open it and drag **AI RAM Guard** into **Applications**.
3. Open **AI RAM Guard** from Applications. Its overview opens immediately; the memory-chip menu-bar icon keeps it available when you close the window.

**First preview: Apple verification is not configured yet.** The download is ad-hoc signed, not Developer ID signed or notarized. macOS may block its first launch. After trying to open it, use **System Settings → Privacy & Security → Open Anyway**, then confirm that you intended to open AI RAM Guard. Do not disable Gatekeeper. If your Mac is managed by an organisation, its policies may prevent opening unnotarized apps.

Start with your Mac is optional, under Settings. macOS may ask you to allow it under **General → Login Items**.

## What this version does

- Shows RAM and process counts for Claude, Codex/ChatGPT, Cursor, Windsurf, VS Code, Gemini CLI, Ollama and LM Studio, including their descendants.
- Shows repeated MCP helpers within each app. Several copies can be legitimate: the app does not stop active sessions.
- Safely closes leftover MCP servers it previously saw under an AI app, only after their live parent and AI owner have gone. It rechecks the current user's PID and microsecond start identity immediately before sending SIGTERM.
- Measures AI session, model and cache folders without moving or deleting anything. Linked folders are skipped. An incomplete large or unreadable folder is marked with ≥.
- Scans once per minute, with Pause and manual Refresh.

The RAM figure is resident memory, which can include shared pages in more than one process. “Reclaimable” is a VM-statistics estimate, not Activity Monitor's memory-pressure score.

macOS has no equivalent to Windows working-set trimming. The Mac preview does not pretend to free active-app memory, change MCP configuration, set RAM caps or move/delete caches. It also does not currently change CPU or memory priority.

Lineage identities are stored locally in `~/Library/Application Support/AiRamGuard/mac-lineage.json`. Command lines are used in memory for classification, and are never written to that file, logs, or scan JSON. There is no telemetry.

## Build and test

On a Mac with Apple's Command Line Tools and Swift 5.9 or later:

```sh
cd macos
swift test
swift run AiRamGuardMac --scan-json
swift run AiRamGuardMac --smoke-test
cd ..
bash packaging/macos/package.sh 1.4.0 out/mac
```

`--scan-json` reads process state and updates the local lineage file; it never closes or reprioritises a process. `--smoke-test` opens the UI, performs its normal scan, then exits. CI should use a temporary HOME so normal user data is not touched.

Packaging builds both CPU architectures, creates an app icon, signs the app ad hoc and produces the DMG and ZIP. For a notarized release, set `MAC_SIGN_IDENTITY` to an installed Developer ID Application certificate and `MAC_NOTARY_PROFILE` to an existing `notarytool` keychain profile. Signing credentials must be configured privately in CI. The script never removes quarantine attributes or bypasses macOS security checks.
