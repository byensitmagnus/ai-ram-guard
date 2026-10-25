using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiRamGuard.Core;

public sealed class AgentProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] ProcessNames { get; init; } = [];
    public string[] HelperPatterns { get; init; } = [];

    public bool OwnsProcessName(string name) =>
        ProcessNames.Any(n => string.Equals(n, StripExe(name), StringComparison.OrdinalIgnoreCase));

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
        p.CommandLine.Length > 0 && HelperRegex.IsMatch(p.CommandLine) && !Agents.Any(a => a.OwnsProcessName(p.Name));

    /// <summary>Agent whose own helper pattern matches, for helpers that outlived their parent.</summary>
    public AgentProfile? AgentByHelperPattern(string commandLine) =>
        Agents.FirstOrDefault(a => a.HelperPatterns.Any(h => Regex.IsMatch(commandLine, h, RegexOptions.IgnoreCase)));

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static ProfileSet Parse(string json) =>
        JsonSerializer.Deserialize<ProfileSet>(json, Json) ?? throw new InvalidDataException("Empty profile file");

    /// <summary>Built-in profiles, plus %APPDATA%\AiRamGuard\profiles.json agents if the user added any.</summary>
    public static ProfileSet Load()
    {
        using var s = typeof(ProfileSet).Assembly.GetManifestResourceStream("profiles.json")!;
        var builtIn = Parse(new StreamReader(s).ReadToEnd());
        var userFile = Path.Combine(Paths.ConfigDir, "profiles.json");
        if (!File.Exists(userFile)) return builtIn;
        var user = Parse(File.ReadAllText(userFile));
        return new ProfileSet
        {
            HelperPatterns = [.. builtIn.HelperPatterns, .. user.HelperPatterns],
            Agents = [.. user.Agents, .. builtIn.Agents.Where(b => user.Agents.All(u => u.Id != b.Id))],
        };
    }
}
