using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiRamGuard.Core;

public sealed class AgentProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] ProcessNames { get; init; } = [];
    public string[] HelperPatterns { get; init; } = [];
    /// <summary>Command lines of the agent itself when it has no app of its own (Gemini CLI runs as node.exe).</summary>
    public string[] CliPatterns { get; init; } = [];

    public bool OwnsProcessName(string name) =>
        ProcessNames.Any(n => string.Equals(n, StripExe(name), StringComparison.OrdinalIgnoreCase));

    /// <summary>The agent's own process: its app, or its CLI running in a script runtime.</summary>
    public bool Owns(ProcessInfo p) =>
        OwnsProcessName(p.Name) || (IsRuntime(p.Name) && CliPatterns.Any(c => Regex.IsMatch(p.CommandLine, c, RegexOptions.IgnoreCase)));

    static readonly string[] Runtimes = ["node", "bun", "deno", "python", "python3", "pythonw", "uv", "uvx"];
    public static bool IsRuntime(string name) => Runtimes.Contains(StripExe(name), StringComparer.OrdinalIgnoreCase);

    internal static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}

public sealed class ProfileSet
{
    public string[] HelperPatterns { get; init; } = [];
    public AgentProfile[] Agents { get; init; } = [];

    Regex? _helper;
    Regex HelperRegex => _helper ??= new Regex(
        string.Join("|", HelperPatterns.Concat(Agents.SelectMany(a => a.HelperPatterns)).Select(p => $"(?:{p})")),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True for background helpers AI apps spawn (MCP servers, npx wrappers, REPLs).</summary>
    public bool IsHelper(ProcessInfo p) =>
        p.CommandLine.Length > 0 && HelperRegex.IsMatch(p.CommandLine) && !Agents.Any(a => a.Owns(p));

    /// <summary>Agent whose own helper pattern matches, for helpers that outlived their parent.</summary>
    public AgentProfile? AgentByHelperPattern(string commandLine) =>
        Agents.FirstOrDefault(a => a.HelperPatterns.Any(h => Regex.IsMatch(commandLine, h, RegexOptions.IgnoreCase)));

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>Parses and compiles every pattern, so a bad regex fails here and not in the middle of a guard round.</summary>
    public static ProfileSet Parse(string json)
    {
        var set = JsonSerializer.Deserialize<ProfileSet>(json, Json) ?? throw new InvalidDataException("Empty profile file");
        foreach (var p in set.HelperPatterns.Concat(set.Agents.SelectMany(a => a.HelperPatterns.Concat(a.CliPatterns))))
            _ = new Regex(p);
        return set;
    }

    /// <summary>Built-in profiles, plus %APPDATA%\AiRamGuard\profiles.json agents if the user added any.</summary>
    public static ProfileSet Load()
    {
        using var s = typeof(ProfileSet).Assembly.GetManifestResourceStream("profiles.json")!;
        var builtIn = Parse(new StreamReader(s).ReadToEnd());
        var userFile = Path.Combine(Paths.ConfigDir, "profiles.json");
        if (!File.Exists(userFile)) return builtIn;
        ProfileSet user;
        try { user = Parse(File.ReadAllText(userFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            // A typo in the user's file must not stop the guard (it autostarts at every login).
            Log.Write($"ignored {userFile}: {e.Message}");
            return builtIn;
        }
        return new ProfileSet
        {
            HelperPatterns = [.. builtIn.HelperPatterns, .. user.HelperPatterns],
            Agents = [.. user.Agents, .. builtIn.Agents.Where(b => user.Agents.All(u => u.Id != b.Id))],
        };
    }
}
