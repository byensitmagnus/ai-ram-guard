using System.Text.Json.Nodes;
using AiRamGuard.Core;
using Xunit;

/// <summary>Sharing rewrites real config files in a temp folder. No server is started: <see cref="Shares.Check"/> is swapped.</summary>
public sealed class ShareTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "airg-share-" + Guid.NewGuid().ToString("N")[..8]);
    string Records => Path.Combine(_root, "fixes.json");
    string Claude => Path.Combine(_root, ".claude.json");

    static readonly ShareSpec Mail = new() { Match = "email-mcp", HttpArgs = ["http", "--port", "{port}"], TokenEnv = "EMAIL_MCP_HTTP_TOKEN" };

    // Two mail servers through one launcher (read-only and full), the same read-only one in a project, and one that isn't shareable.
    const string ClaudeJson = """
        {
          "numStartups": 12,
          "mcpServers": {
            "mail": { "type": "stdio", "command": "python.exe", "args": ["C:\\x\\email-mcp-launch.py", "ro", "stdio"], "env": {} },
            "mail-send": { "type": "stdio", "command": "python.exe", "args": ["C:\\x\\email-mcp-launch.py", "rw", "stdio"], "env": {} },
            "rea": { "command": "node", "args": ["rea-agents/rea.mjs", "mcp"] }
          },
          "projects": {
            "C:/work/shop": { "mcpServers": { "mail": { "type": "stdio", "command": "python.exe", "args": ["C:\\x\\email-mcp-launch.py", "ro", "stdio"], "env": {} } } }
          }
        }
        """;

    readonly string _original = ClaudeJson.Replace("\r\n", "\n") + "\n";
    readonly Func<SharedServer, TimeSpan, bool> _check = Shares.Check;

    public ShareTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Claude, _original);
        Shares.Check = (_, _) => true;
    }

    public void Dispose()
    {
        Shares.Check = _check;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    ConfigFile[] Configs() => [new("Claude Code", Claude, File.ReadAllText(Claude))];
    IReadOnlyList<ServerEntry> Mails() => McpServers.Find("email-mcp-launch.py", Configs());

    [Fact]
    public void Knows_a_launcher_by_its_name_but_not_a_similar_one_or_an_http_server()
    {
        Assert.NotNull(Shares.Spec(JsonNode.Parse("""{"command":"python","args":["email-mcp-launch.py","ro"]}""")!.AsObject(), [Mail]));
        Assert.NotNull(Shares.Spec(JsonNode.Parse("""{"command":"npx","args":["@codefuturist/email-mcp","stdio"]}""")!.AsObject(), [Mail]));
        Assert.Null(Shares.Spec(JsonNode.Parse("""{"command":"npx","args":["my-email-mcp"]}""")!.AsObject(), [Mail]));
        Assert.Null(Shares.Spec(JsonNode.Parse("""{"type":"http","url":"http://127.0.0.1:1/mcp"}""")!.AsObject(), [Mail]));
    }

    [Fact]
    public void The_shared_copy_runs_the_same_command_over_http_on_loopback()
    {
        var s = Shares.Plan(JsonNode.Parse("""{"command":"python","args":["l.py","ro","stdio"],"env":{"A":"1"}}""")!.AsObject(), Mail);
        Assert.Equal(["l.py", "ro", "http", "--port", s.Port.ToString()], s.Args);
        Assert.Equal("1", s.Env["A"]);
        Assert.Equal($"http://127.0.0.1:{s.Port}/mcp", s.Url);
        Assert.Equal(64, s.Token.Length);
        Assert.True(Shares.PointsAt(Shares.Entry(s), s));
        Assert.False(Shares.PointsAt(Shares.Entry(s with { Token = "other" }), s)); // someone changed the entry since: undo leaves it

        Assert.True(Shares.Runs($"python.exe C:\\x\\email-mcp-launch.py l.py ro http --port {s.Port}", s with { Match = "email-mcp" }));
        Assert.False(Shares.Runs($"claude -p \"why does email-mcp on port {s.Port} fail\"", s with { Match = "email-mcp" })); // a prompt is not the server
    }

    [Fact]
    public void Shares_each_distinct_server_once_and_undo_puts_every_entry_back()
    {
        var r = Fixer.Share("mail ×20", Mails(), [Mail], Records);
        Assert.True(r.Ok, r.Message);

        var root = JsonNode.Parse(File.ReadAllText(Claude))!;
        var ro = root["mcpServers"]!["mail"]!;
        Assert.Equal("http", ro["type"]!.GetValue<string>());
        Assert.StartsWith("Bearer ", ro["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal(ro["url"]!.GetValue<string>(), root["projects"]!["C:/work/shop"]!["mcpServers"]!["mail"]!["url"]!.GetValue<string>()); // same definition, same copy
        Assert.NotEqual(ro["url"]!.GetValue<string>(), root["mcpServers"]!["mail-send"]!["url"]!.GetValue<string>()); // read-only and full stay apart
        Assert.Equal("node", root["mcpServers"]!["rea"]!["command"]!.GetValue<string>());
        Assert.Equal(12, root["numStartups"]!.GetValue<int>());

        var applied = Fixer.Applied(Configs(), Records);
        Assert.Equal(2, applied.Count);
        Assert.All(applied, f => Assert.NotNull(f.Shared));

        foreach (var f in applied) Assert.True(Fixer.Undo(f.Id, Records).Ok);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(_original), JsonNode.Parse(File.ReadAllText(Claude)))); // as Claude Code writes it: the layout may change
        Assert.Empty(Fixer.Applied(Configs(), Records));
    }

    [Fact]
    public void Changes_nothing_when_the_shared_copy_does_not_answer()
    {
        Shares.Check = (_, _) => false;
        var r = Fixer.Share("mail ×20", Mails(), [Mail], Records);
        Assert.False(r.Ok);
        Assert.Contains("nothing was changed", r.Message);
        Assert.Equal(_original, File.ReadAllText(Claude));
        Assert.Empty(Fixer.History(Records));
    }

    [Fact]
    public void Undo_leaves_an_entry_alone_when_it_was_changed_since()
    {
        Assert.True(Fixer.Share("mail", Mails(), [Mail], Records).Ok);
        var root = JsonNode.Parse(File.ReadAllText(Claude))!;
        root["mcpServers"]!["mail"] = JsonNode.Parse("""{"command":"mine"}""");
        File.WriteAllText(Claude, root.ToJsonString());

        foreach (var f in Fixer.History(Records)) Fixer.Undo(f.Id, Records);
        root = JsonNode.Parse(File.ReadAllText(Claude))!;
        Assert.Equal("mine", root["mcpServers"]!["mail"]!["command"]!.GetValue<string>());
        Assert.Equal("python.exe", root["mcpServers"]!["mail-send"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Advice_offers_sharing_only_with_a_spec()
    {
        var advice = new Advice(AdviceKind.Duplicates, "t", "d", 1, Claude, Mails());
        Assert.False(advice.Share);
        Assert.NotNull(Shares.Spec(Mails()[0], Configs(), [Mail]));
        Assert.Null(Shares.Spec(Mails()[0], Configs(), []));
    }
}
