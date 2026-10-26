using System.Text.Json.Nodes;
using AiRamGuard.Core;
using Xunit;

/// <summary>Real config files in a temp folder: switched off, checked, switched back on.</summary>
public sealed class FixerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "airg-fix-" + Guid.NewGuid().ToString("N")[..8]);
    string Records => Path.Combine(_root, "fixes.json");
    string Claude => Path.Combine(_root, ".claude.json");
    string Codex => Path.Combine(_root, "config.toml");

    // As Claude Code writes it: JSON.stringify(…, null, 2), "\n" line ends.
    const string ClaudeJson = """
        {
          "numStartups": 12,
          "tips": "<b>&'",
          "mcpServers": {
            "rea": {
              "command": "C:\\Program Files\\nodejs\\node.exe",
              "args": ["C:\\npm\\node_modules\\rea-agents\\scripts\\rea.mjs", "mcp"],
              "env": { "TOKEN": "x" }
            },
            "buffer": { "type": "http", "url": "https://mcp.buffer.com/mcp" }
          },
          "projects": {
            "C:/work/shop": { "allowedTools": [], "mcpServers": { "rea": { "command": "node", "args": ["rea-agents/rea.mjs"] } } },
            "C:/work/other": { "allowedTools": [] }
          }
        }
        """;

    const string CodexToml = "model = \"gpt\"\r\n\r\n[mcp_servers.rea]\r\ncommand = \"node\"\r\nargs = [\"rea-agents\\\\rea.mjs\"]\r\n\r\n[mcp_servers.rea.env]\r\nenabled = \"not this one\"\r\n\r\n[mcp_servers.node_repl]\r\ncommand = \"node\"\r\nenabled = true\r\n";

    public FixerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Claude, ClaudeJson.Replace("\r\n", "\n") + "\n");
        File.WriteAllText(Codex, CodexToml);
    }

    public void Dispose()
    {
        foreach (var link in Directory.Exists(_root) ? Directory.EnumerateDirectories(_root, "link", SearchOption.AllDirectories).ToList() : [])
            Directory.Delete(link); // the junction only, before the recursive delete could reach through it
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    ConfigFile Read(string app, string path) => new(app, path, File.ReadAllText(path));

    [Fact]
    public void Finds_the_server_everywhere_it_is_switched_on_but_not_other_servers()
    {
        var found = McpServers.Find("rea-agents", [Read("Claude Code", Claude)]);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, s => s.Project is null && s.Name == "rea");
        Assert.Contains(found, s => s.Project == "C:/work/shop");
    }

    [Fact]
    public void Json_turn_off_removes_only_the_server_and_undo_puts_back_exactly_what_was_there()
    {
        var before = JsonNode.Parse(File.ReadAllText(Claude));
        var r = Fixer.TurnOff("rea", McpServers.Find("rea-agents", [Read("Claude Code", Claude)]), Records);
        Assert.True(r.Ok, r.Message);

        var text = File.ReadAllText(Claude);
        var after = JsonNode.Parse(text)!;
        Assert.Null(after["mcpServers"]!["rea"]);
        Assert.Null(after["projects"]!["C:/work/shop"]!["mcpServers"]!["rea"]);
        Assert.NotNull(after["mcpServers"]!["buffer"]);
        Assert.Contains("\n  \"numStartups\": 12,", text); // same indent and line ends
        Assert.DoesNotContain("\r", text);
        Assert.Contains("\"<b>&'\"", text); // no \u003C escapes JSON.stringify wouldn't make
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups")));

        var applied = Assert.Single(Fixer.Applied([Read("Claude Code", Claude)], Records));
        var u = Fixer.Undo(applied.Id, Records);
        Assert.True(u.Ok, u.Message);
        Assert.True(JsonNode.DeepEquals(before, JsonNode.Parse(File.ReadAllText(Claude))));
        Assert.Empty(Fixer.History(Records));
    }

    [Fact]
    public void A_json_file_with_comments_is_never_rewritten()
    {
        var vscode = Path.Combine(_root, "mcp.json");
        const string jsonc = "{\n  // mine\n  \"servers\": { \"rea\": { \"args\": [\"rea-agents\"] } }\n}\n";
        File.WriteAllText(vscode, jsonc);
        Assert.Empty(McpServers.Find("rea-agents", [Read("VS Code", vscode)]));
        Assert.False(Fixer.TurnOff("rea", [new ServerEntry("VS Code", vscode, "servers", "rea")], Records).Ok);
        Assert.Equal(jsonc, File.ReadAllText(vscode));
    }

    [Fact]
    public void Toml_turn_off_adds_or_flips_one_line_and_undo_restores_the_exact_text()
    {
        var servers = McpServers.List(Read("Codex", Codex)).Select(s => s.Entry).ToList();
        Assert.Equal(["rea", "node_repl"], servers.Select(s => s.Name));

        var r = Fixer.TurnOff("both", servers, Records);
        Assert.True(r.Ok, r.Message);
        var text = File.ReadAllText(Codex);
        Assert.Contains("[mcp_servers.rea]\r\nenabled = false\r\ncommand", text);
        Assert.Contains("command = \"node\"\r\nenabled = false\r\n", text);
        Assert.Contains("enabled = \"not this one\"", text); // .env is another table
        Assert.Empty(McpServers.List(Read("Codex", Codex)));
        Assert.True(McpServers.IsOff("rea-agents", [Read("Codex", Codex)]));

        Assert.True(Fixer.Undo(Fixer.History(Records)[0].Id, Records).Ok);
        Assert.Equal(CodexToml, File.ReadAllText(Codex));
    }

    [Fact]
    public void A_server_you_switched_back_on_yourself_is_no_longer_listed_as_fixed()
    {
        Fixer.TurnOff("rea", McpServers.Find("rea-agents", [Read("Codex", Codex)]), Records);
        Assert.Single(Fixer.Applied([Read("Codex", Codex)], Records));
        File.WriteAllText(Codex, CodexToml);
        Assert.Empty(Fixer.Applied([Read("Codex", Codex)], Records));
    }

    [Fact]
    public void An_unreadable_undo_file_blocks_changes()
    {
        File.WriteAllText(Records, "{ broken");
        Assert.False(Fixer.TurnOff("rea", McpServers.Find("rea-agents", [Read("Codex", Codex)]), Records).Ok);
        Assert.Equal(CodexToml, File.ReadAllText(Codex));
    }

    static readonly AgentProfile ClaudeProfile = new() { Id = "claude", Name = "Claude", ProcessNames = ["claude"] };

    static Report Running(int copies) =>
        new(new Snapshot([], 32_000L << 20, 8_000L << 20), [new AgentUsage(ClaudeProfile, [],
            Analyzer.GroupHelpers(Enumerable.Range(1, copies).Select(i => new ProcessInfo(i, 1, "node.exe", "node C:\\npm\\node_modules\\rea-agents\\scripts\\rea.mjs mcp", 100L << 20, DateTime.Now))))], []);

    [Fact]
    public void Advice_offers_the_fix_and_drops_it_once_switched_off_while_old_copies_still_run()
    {
        var configs = new[] { Read("Claude Code", Claude) };
        var advice = Assert.Single(Advisor.Advise(Running(5), configs), a => a.Kind == AdviceKind.Duplicates);
        Assert.True(advice.CanFix);
        Assert.Equal(2, advice.Servers!.Count);

        Fixer.TurnOff(advice.Title, advice.Servers, Records);
        configs = [Read("Claude Code", Claude)];
        Assert.DoesNotContain(Advisor.Advise(Running(5), configs, Fixer.Applied(configs, Records)), a => a.Kind == AdviceKind.Duplicates);
    }

    [Fact]
    public void Temp_clean_up_deletes_only_old_files_and_never_follows_a_link()
    {
        var temp = Path.Combine(_root, "temp");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(temp, "old-dir"));
        Directory.CreateDirectory(outside);
        var old = DateTime.UtcNow.AddDays(-30);
        void File_(string path, DateTime write, DateTime created)
        {
            File.WriteAllBytes(path, new byte[100]);
            File.SetLastWriteTimeUtc(path, write);
            File.SetCreationTimeUtc(path, created);
        }
        File_(Path.Combine(temp, "old-dir", "old.tmp"), old, old);
        File_(Path.Combine(temp, "new.tmp"), DateTime.UtcNow, DateTime.UtcNow);
        File_(Path.Combine(temp, "unpacked.dll"), old, DateTime.UtcNow); // old write time, created just now: in use by an installer
        File_(Path.Combine(outside, "keep.tmp"), old, old);
        File_(Path.Combine(temp, "open.log"), old, old);
        Directory.CreateDirectory(Path.Combine(temp, "lock-dir")); // empty, created long ago, used just now
        Directory.SetCreationTimeUtc(Path.Combine(temp, "lock-dir"), old);
        Directory.SetCreationTimeUtc(Path.Combine(temp, "old-dir"), old);
        Directory.SetLastWriteTimeUtc(Path.Combine(temp, "old-dir"), old);
        StorageTests.Junction(Path.Combine(temp, "link"), outside);

        Assert.Equal(new TempFiles(2, 200), TempCleaner.Find(temp));
        // Open the way Node and .NET open files: deleting is allowed, but the file is in use.
        using (new FileStream(Path.Combine(temp, "open.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Assert.Equal(new TempFiles(1, 100, 1), TempCleaner.Clean(temp));
        Assert.True(File.Exists(Path.Combine(temp, "open.log")));
        Assert.False(Directory.Exists(Path.Combine(temp, "old-dir")));
        Assert.True(Directory.Exists(Path.Combine(temp, "lock-dir")));
        Assert.True(File.Exists(Path.Combine(temp, "new.tmp")));
        Assert.True(File.Exists(Path.Combine(temp, "unpacked.dll")));
        Assert.True(File.Exists(Path.Combine(outside, "keep.tmp")));
    }

    [Fact]
    public void Temp_clean_up_refuses_anything_but_a_temp_folder()
    {
        var notTemp = Path.Combine(_root, "projects");
        Directory.CreateDirectory(notTemp);
        File.WriteAllBytes(Path.Combine(notTemp, "old.txt"), new byte[10]);
        File.SetLastWriteTimeUtc(Path.Combine(notTemp, "old.txt"), DateTime.UtcNow.AddDays(-30));
        File.SetCreationTimeUtc(Path.Combine(notTemp, "old.txt"), DateTime.UtcNow.AddDays(-30));
        Assert.Equal(new TempFiles(0, 0), TempCleaner.Clean(notTemp));
        Assert.True(File.Exists(Path.Combine(notTemp, "old.txt")));
        Assert.Equal(new TempFiles(0, 0), TempCleaner.Find("C:\\"));
        Assert.Equal(new TempFiles(0, 0), TempCleaner.Find(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(TempCleaner.IsTempFolder(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!)); // C:\Users
        Assert.True(TempCleaner.IsTempFolder(Path.GetTempPath()));
    }

    [Fact]
    public void Toml_ignores_headers_inside_strings_and_leaves_a_doubled_enabled_alone()
    {
        const string toml = "[mcp_servers.a]\ncommand = \"node\"\ndescription = \"\"\"\n[mcp_servers.fake]\n\"\"\"\n\"enabled\" = true\n";
        File.WriteAllText(Codex, toml);
        var servers = McpServers.List(Read("Codex", Codex)).Select(s => s.Entry).ToList();
        Assert.Equal(["a"], servers.Select(s => s.Name));
        Assert.True(Fixer.TurnOff("a", servers, Records).Ok);
        var text = File.ReadAllText(Codex);
        Assert.Single(text.Split('\n'), l => l.Contains("enabled"));
        Assert.Contains("\nenabled = false\n", text);

        const string doubled = "[mcp_servers.b]\nenabled = true\nenabled = true\n";
        File.WriteAllText(Codex, doubled);
        Assert.False(Fixer.TurnOff("b", [new ServerEntry("Codex", Codex, "mcp_servers", "b")], Records).Ok);
        Assert.Equal(doubled, File.ReadAllText(Codex));
    }
}
