# Contributing to AI RAM Guard

Thanks for helping! The most useful contributions, in order:

1. **A new AI app or MCP pattern.** Usually only JSON: see [docs/adding-an-agent.md](docs/adding-an-agent.md).
2. **A real-world report.** Open an issue with your `airg scan --json` output (remove anything private). It shows us which tools waste the most RAM.
3. **Bug fixes and features** from the [roadmap](README.md#roadmap).

## Develop

```powershell
dotnet build
dotnet test
dotnet run --project src/AiRamGuard.Cli -- scan
dotnet run --project src/AiRamGuard.Tray
```

## Rules of the codebase

- **Deciding is pure, acting is separate.** New behaviour goes into `Analyzer`/`Policy` with a unit test; `Windows.cs` stays a thin layer.
- **Never close what the user is using.** Any new "close" path needs a test proving that a live session's processes are untouched.
- **Stay light.** No new dependencies without a strong reason; this is a RAM tool.
- Keep pull requests small, and describe *why* in the PR text.

By contributing you agree that your contribution is licensed under the MIT license.
