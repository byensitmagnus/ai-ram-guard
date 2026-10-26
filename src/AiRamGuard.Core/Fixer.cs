using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiRamGuard.Core;

/// <summary>Where one MCP server is switched on: a config file, its section ("mcpServers", "servers", "mcp_servers"), its name, and for Claude Code the project (null = every project).</summary>
public sealed record ServerEntry(string App, string Path, string Section, string Name, string? Project = null);

/// <param name="Definition">The server as it was configured: name, command and args. Running helpers are matched against it.</param>
/// <param name="Saved">JSON: the server's definition to put back. TOML: the "enabled" line it had, or null when it had none.</param>
public sealed record FixEntry(ServerEntry Server, string Definition, string? Saved);

/// <summary>One "turn off", with what is needed to turn it back on.</summary>
public sealed record FixRecord(string Id, string Title, List<FixEntry> Entries, DateTime At);

public sealed record FixResult(bool Ok, string Message);

/// <summary>The MCP servers a config file switches on, read structurally so they can also be switched off.</summary>
public static class McpServers
{
    /// <summary>Every server that is on, with its definition to match running helpers against.</summary>
    /// <param name="on">False: the servers that are switched off instead.</param>
    public static IReadOnlyList<(ServerEntry Entry, string Definition)> List(ConfigFile c, bool on = true) =>
        c.Path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ? Toml.List(c, on) : JsonList(c, on);

    /// <summary>The servers in these configs that start this helper. Empty when none can be switched off here: a plugin, or a file with comments.</summary>
    public static IReadOnlyList<ServerEntry> Find(string helperKey, IReadOnlyList<ConfigFile> configs) =>
        Advisor.Term(helperKey) is not { } term ? []
        : configs.SelectMany(c => List(c)).Where(s => Advisor.Mentions(s.Definition, term)).Select(s => s.Entry).ToList();

    /// <summary>Switched off in one of these configs already: its running copies are leftovers of sessions started before.</summary>
    public static bool IsOff(string helperKey, IReadOnlyList<ConfigFile> configs) =>
        Advisor.Term(helperKey) is { } term && configs.SelectMany(c => List(c, on: false)).Any(s => Advisor.Mentions(s.Definition, term));

    static IReadOnlyList<(ServerEntry, string)> JsonList(ConfigFile c, bool on)
    {
        var list = new List<(ServerEntry, string)>();
        try
        {
            if (JsonText.Parse(c.Content) is not JsonObject root) return list;
            foreach (var section in new[] { "mcpServers", "servers" })
                if (root[section] is JsonObject s)
                    foreach (var (name, def) in Servers(s, on)) list.Add((new(c.App, c.Path, section, name), name + " " + def));
            if (root["projects"] is JsonObject projects)
                foreach (var (project, p) in projects)
                    if (p?["mcpServers"] is JsonObject s)
                        foreach (var (name, def) in Servers(s, on)) list.Add((new(c.App, c.Path, "mcpServers", name, project), name + " " + def));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { list.Clear(); } // duplicate keys or odd shapes: not ours to rewrite
        return list;
    }

    /// <summary>"disabled": true is how Cline-style configs switch one off.</summary>
    static IEnumerable<(string, string)> Servers(JsonObject s, bool on) =>
        s.Where(e => e.Value is JsonObject o && (o["disabled"]?.GetValueKind() == JsonValueKind.True) != on).Select(e => (e.Key, e.Value!.ToJsonString()));
}

/// <summary>JSON configs are written back as the app wrote them: same indent, line ends and BOM, and no \u escapes JSON.stringify doesn't make.</summary>
static class JsonText
{
    /// <summary>Strict: a file with comments or trailing commas gives null, because writing it back would drop them.</summary>
    public static JsonNode? Parse(string s)
    {
        try { return JsonNode.Parse(s); }
        catch (JsonException) { return null; }
    }

    /// <returns>Null when the text wouldn't read back as exactly <paramref name="root"/>: then nothing is written.</returns>
    public static string? Write(JsonNode root, string original)
    {
        var nl = original.Contains("\r\n") ? "\r\n" : "\n";
        var indent = Regex.Match(original, @"\n([ \t]+)\S").Groups[1].Value;
        var o = new JsonSerializerOptions
        {
            WriteIndented = original.TrimEnd().Contains('\n'),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = nl,
            IndentCharacter = indent.StartsWith('\t') ? '\t' : ' ',
            IndentSize = indent.Length is > 0 and <= 8 ? indent.Length : 2,
        };
        var text = root.ToJsonString(o);
        if (!JsonNode.DeepEquals(Parse(text), root)) return null;
        return original.EndsWith('\n') ? text + nl : text;
    }

    /// <summary>The object that holds this server, created when <paramref name="create"/> (undo), except a whole project: that is Claude Code's to make.</summary>
    public static JsonObject? Container(JsonObject root, ServerEntry e, bool create)
    {
        JsonObject parent = root;
        if (e.Project is not null)
        {
            if (root["projects"]?[e.Project] is not JsonObject p) return null;
            parent = p;
        }
        if (parent[e.Section] is JsonObject s) return s;
        if (!create) return null;
        var made = new JsonObject();
        parent[e.Section] = made;
        return made;
    }
}

/// <summary>Codex' config.toml, edited as text: one "enabled" line per server, everything else untouched.</summary>
static class Toml
{
    static readonly Regex Enabled = new(@"^\s*(?:enabled|""enabled""|'enabled')\s*=\s*(true|false)\s*(#.*)?\r?$");
    static readonly Regex AnyEnabled = new(@"^\s*(?:enabled|""enabled""|'enabled')\s*=");

    // [table] or [[array]]: dotted bare or quoted keys only, so a line of a multi-line array like ["a", "b"], is not one.
    const string Key = @"(?:[A-Za-z0-9_-]+|""[^""]*""|'[^']*')";
    static readonly Regex TableHeader = new($@"^\s*\[\[?\s*{Key}(?:\s*\.\s*{Key})*\s*\]\]?\s*(?:#.*)?\r?$");

    /// <summary>Which lines are table headers. Lines inside a multi-line string are not, even when they look like one.</summary>
    static bool[] Headers(List<string> lines)
    {
        var heads = new bool[lines.Count];
        string? open = null;
        for (var i = 0; i < lines.Count; i++)
        {
            if (open is null) heads[i] = TableHeader.IsMatch(lines[i]);
            foreach (var quote in new[] { "\"\"\"", "'''" })
                if (open is null || open == quote)
                    for (int at = lines[i].IndexOf(quote, StringComparison.Ordinal); at >= 0; at = lines[i].IndexOf(quote, at + 3, StringComparison.Ordinal))
                        open = open is null ? quote : null;
        }
        return heads;
    }

    /// <summary>"[mcp_servers.rea]" or [mcp_servers."my.server"] → its name, and whether it is the server's own table (not .env or .tools).</summary>
    internal static (string Name, bool Main)? Header(string line)
    {
        const string prefix = "[mcp_servers.";
        var t = line.Trim();
        if (!t.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = t[prefix.Length..];
        string name;
        if (rest.Length > 0 && rest[0] is '"' or '\'')
        {
            var end = rest.IndexOf(rest[0], 1);
            if (end < 0) return null;
            name = rest[1..end];
            rest = rest[(end + 1)..];
        }
        else
        {
            var end = rest.IndexOfAny(['.', ']', ' ']);
            if (end <= 0) return null;
            name = rest[..end];
            rest = rest[end..];
        }
        return (name, rest.TrimStart().StartsWith(']'));
    }

    /// <summary>The server's own table; -1 when it isn't there or is there twice (Codex would refuse that file anyway).</summary>
    static int Find(List<string> lines, bool[] heads, string name)
    {
        var at = Enumerable.Range(0, lines.Count).Where(i => heads[i] && Header(lines[i]) is { Main: true } h && h.Name == name).ToList();
        return at.Count == 1 ? at[0] : -1;
    }

    /// <summary>The server's own lines: up to the next table header.</summary>
    static IEnumerable<int> Body(List<string> lines, bool[] heads, int header)
    {
        for (var i = header + 1; i < lines.Count && !heads[i]; i++) yield return i;
    }

    static bool IsOff(List<string> lines, bool[] heads, int header) => Body(lines, heads, header).Any(i => Enabled.Match(lines[i]) is { Success: true } m && m.Groups[1].Value == "false");

    public static IReadOnlyList<(ServerEntry, string)> List(ConfigFile c, bool on = true)
    {
        var lines = c.Content.Split('\n').ToList();
        var heads = Headers(lines);
        var list = new List<(ServerEntry, string)>();
        for (var i = 0; i < lines.Count; i++)
            if (heads[i] && Header(lines[i]) is { Main: true } h && IsOff(lines, heads, i) != on)
                list.Add((new(c.App, c.Path, "mcp_servers", h.Name), h.Name + "\n" + string.Join("\n", Body(lines, heads, i).Select(j => lines[j]))));
        return list;
    }

    /// <summary>
    /// Sets enabled = false: replaces the server's enabled line, or adds one under its header.
    /// Null when the server isn't there, or its "enabled" is written in a way this doesn't change (an inline comment block, an odd layout):
    /// a second "enabled" key would make Codex refuse the whole file.
    /// </summary>
    public static (string Text, string? Saved)? Off(string text, string name)
    {
        var lines = text.Split('\n').ToList();
        var heads = Headers(lines);
        var h = Find(lines, heads, name);
        if (h < 0) return null;
        var body = Body(lines, heads, h).ToList();
        var enabledLines = body.Count(i => AnyEnabled.IsMatch(lines[i]));
        if (enabledLines > 1 || enabledLines == 1 && !body.Any(i => Enabled.IsMatch(lines[i]))) return null;
        foreach (var i in body)
            if (Enabled.IsMatch(lines[i]))
            {
                var saved = lines[i];
                lines[i] = "enabled = false" + (saved.EndsWith('\r') ? "\r" : "");
                return (string.Join('\n', lines), saved);
            }
        lines.Insert(h + 1, "enabled = false" + (lines[h].EndsWith('\r') ? "\r" : ""));
        return (string.Join('\n', lines), null);
    }

    /// <summary>Puts back the line it had (or removes the one Off added). Unchanged text when it's on already.</summary>
    public static string? On(string text, string name, string? saved)
    {
        var lines = text.Split('\n').ToList();
        var heads = Headers(lines);
        var h = Find(lines, heads, name);
        if (h < 0) return null;
        foreach (var i in Body(lines, heads, h))
            if (Enabled.Match(lines[i]) is { Success: true } m && m.Groups[1].Value == "false")
            {
                if (saved is null) lines.RemoveAt(i); else lines[i] = saved;
                return string.Join('\n', lines);
            }
        return text;
    }
}

/// <summary>Switches MCP servers off in the apps' own config files, with a backup and a record to switch them back on.</summary>
public static class Fixer
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    const int KeepBackups = 20;

    public static List<FixRecord> History(string? file = null) => Load(file ?? Paths.FixesFile) ?? [];

    /// <summary>Fixes that still hold: at least one of their servers is still off. Read from the configs already in memory.</summary>
    public static IReadOnlyList<FixRecord> Applied(IReadOnlyList<ConfigFile> configs, string? file = null) =>
        History(file).Where(r => r.Entries.Any(e => IsOff(e.Server, configs))).ToList();

    static bool IsOff(ServerEntry s, IReadOnlyList<ConfigFile> configs) =>
        configs.FirstOrDefault(c => c.Path == s.Path) is { } c && !McpServers.List(c).Any(x => x.Entry == s);

    /// <param name="file">The undo record (tests use their own). Backups go in a "backups" folder next to it.</param>
    public static FixResult TurnOff(string title, IReadOnlyList<ServerEntry> servers, string? file = null)
    {
        file ??= Paths.FixesFile;
        if (servers.Count == 0) return new(false, "Nothing to turn off.");
        if (Load(file) is not { } records) return new(false, Unreadable(file));
        var record = new FixRecord(Guid.NewGuid().ToString("N")[..8], title, [], DateTime.Now);
        records.Add(record);
        var problems = new List<string>();

        foreach (var group in servers.GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
        {
            var path = group.Key;
            var error = Rewrite(path, file, text =>
            {
                var done = new List<FixEntry>();
                if (IsToml(path))
                {
                    var c = new ConfigFile(group.First().App, path, text);
                    foreach (var s in group)
                    {
                        var def = Toml.List(c).FirstOrDefault(x => x.Item1 == s).Item2;
                        if (def is null) continue; // off already, or gone
                        if (Toml.Off(text, s.Name) is not { } off) return (text, done, $"its \"enabled\" setting is written in a way AI RAM Guard doesn't change: turn {s.Name} off there");
                        text = off.Text;
                        done.Add(new FixEntry(s, def, off.Saved));
                    }
                    return (text, done, null);
                }
                if (JsonText.Parse(text) is not JsonObject root) return (text, done, "it has comments or isn't plain JSON, so AI RAM Guard leaves it alone");
                foreach (var s in group)
                {
                    if (JsonText.Container(root, s, create: false) is not { } box || box[s.Name] is not JsonObject server) continue;
                    var saved = server.ToJsonString();
                    box.Remove(s.Name);
                    done.Add(new FixEntry(s, s.Name + " " + saved, saved));
                }
                return JsonText.Write(root, text) is { } written ? (written, done, null) : (text, done, Lossy);
            },
            done => { record.Entries.AddRange(done); Save(records, file); }, // the undo record is written before the config changes
            done => { record.Entries.RemoveAll(done.Contains); TrySave(records, file); });
            if (error is not null) problems.Add($"{Advisor.Display(path)}: {error}");
        }

        // Read back from disk: the app may have written its own copy over ours a moment later.
        var back = record.Entries.Where(e => !IsOffOnDisk(e.Server)).ToList();
        if (back.Count > 0)
        {
            problems.Add($"{string.Join(", ", back.Select(e => e.Server.Name).Distinct())} is on again: {back[0].Server.App} rewrote the file. Close {back[0].Server.App} and try again");
            record.Entries.RemoveAll(back.Contains);
        }
        if (record.Entries.Count == 0)
        {
            records.Remove(record);
            TrySave(records, file);
            return new(false, problems.Count > 0 ? string.Join("\n", problems) : "It's off already.");
        }
        TrySave(records, file);
        foreach (var e in record.Entries) Log.Write($"turned off {e.Server.Name} in {Where(e.Server)}");
        var msg = $"Turned off {string.Join(", ", record.Entries.Select(e => e.Server.Name).Distinct())}. New sessions start without it; open sessions keep their copy until you close them.";
        return new(true, problems.Count > 0 ? msg + "\nNot changed: " + string.Join("\n", problems) : msg);
    }

    /// <summary>Turns the servers of one fix back on, exactly as they were configured.</summary>
    public static FixResult Undo(string id, string? file = null)
    {
        file ??= Paths.FixesFile;
        if (Load(file) is not { } records) return new(false, Unreadable(file));
        if (records.FirstOrDefault(r => r.Id == id) is not { } record) return new(false, "That was undone already.");
        var problems = new List<string>();
        var noProject = new HashSet<string>();

        foreach (var group in record.Entries.GroupBy(e => e.Server.Path, StringComparer.OrdinalIgnoreCase))
        {
            var path = group.Key;
            if (!File.Exists(path)) { problems.Add($"{Advisor.Display(path)} is gone"); continue; }
            var error = Rewrite(path, file, text =>
            {
                var done = new List<FixEntry>();
                if (IsToml(path))
                {
                    foreach (var e in group)
                        if (Toml.On(text, e.Server.Name, e.Saved) is { } on) { text = on; done.Add(e); }
                    return (text, done, null);
                }
                if (JsonText.Parse(text) is not JsonObject root) return (text, done, "it isn't plain JSON any more");
                foreach (var e in group)
                {
                    if (JsonText.Container(root, e.Server, create: true) is not { } box) { noProject.Add(e.Server.Project!); continue; }
                    if (box.ContainsKey(e.Server.Name) || e.Saved is null) continue; // on again already, by you or the app
                    box[e.Server.Name] = JsonNode.Parse(e.Saved);
                    done.Add(e);
                }
                return JsonText.Write(root, text) is { } written ? (written, done, null) : (text, done, Lossy);
            }, _ => { }, _ => { });
            if (error is not null) problems.Add($"{Advisor.Display(path)}: {error}");
        }

        var stillOff = record.Entries.Where(e => File.Exists(e.Server.Path) && IsOffOnDisk(e.Server)).ToList();
        problems.AddRange(noProject.Select(p => $"project {p} is no longer in the config"));
        if (stillOff.Count > 0 || problems.Count > 0)
            return new(false, "Couldn't turn all of it back on:\n" + string.Join("\n", problems.Distinct()) + (stillOff.Count > 0 ? $"\nStill off: {string.Join(", ", stillOff.Select(e => e.Server.Name).Distinct())}" : ""));
        records.Remove(record);
        TrySave(records, file);
        foreach (var e in record.Entries) Log.Write($"turned on {e.Server.Name} again in {Where(e.Server)}");
        return new(true, $"Turned {string.Join(", ", record.Entries.Select(e => e.Server.Name).Distinct())} back on. New sessions start it again.");
    }

    const string Lossy = "writing it back would change more than the servers, so it was left alone";

    static bool IsToml(string path) => path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);

    /// <summary>When the file can't be read right now, it counts as off: that keeps the undo record, which is the safe side.</summary>
    static bool IsOffOnDisk(ServerEntry s)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return IsOff(s, [new ConfigFile(s.App, s.Path, Read(s.Path).Text)]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                if (attempt == 4) return true;
                Thread.Sleep(100);
            }
        }
    }

    static string Where(ServerEntry s) => Advisor.Display(s.Path) + (s.Project is null ? "" : $" (project {s.Project})");

    /// <summary>
    /// Read, change, check, back up, write, check again. The app writes the same file whenever it likes (Claude Code rewrites ~/.claude.json often):
    /// if the file changed while we worked we start over, so its change is never written over.
    /// </summary>
    /// <returns>Null when written (or nothing to change), else why not.</returns>
    static string? Rewrite(string path, string recordsFile, Func<string, (string Text, List<FixEntry> Done, string? Error)> change,
        Action<List<FixEntry>> beforeWrite, Action<List<FixEntry>> writeFailed)
    {
        // A dotfile managed as a link: write the real file, keep the link.
        var real = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            string text;
            bool bom;
            try { (text, bom) = Read(real); }
            catch (DecoderFallbackException) { return "it isn't UTF-8 text, so AI RAM Guard leaves it alone"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(100); continue; }
            string next;
            List<FixEntry> done;
            try
            {
                (next, done, var error) = change(text);
                if (error is not null) return error;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return Lossy; } // duplicate keys, a lone surrogate escape
            if (done.Count == 0 || next == text) return null;

            var tmp = real + ".airg-tmp";
            var recorded = false;
            try
            {
                // Everything slow first (the backup, the undo record, the new file), so the app's write can only land in the short gap between the last check and the move.
                Backup(real, recordsFile);
                beforeWrite(done);
                recorded = true;
                File.WriteAllText(tmp, next, new UTF8Encoding(bom));
                if (Read(real).Text != text) // the app wrote in the meantime: start over on its version
                {
                    File.Delete(tmp);
                    writeFailed(done);
                    continue;
                }
                File.Move(tmp, real, overwrite: true);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (recorded) writeFailed(done);
                if (attempt == 4) return e.Message;
                Thread.Sleep(100);
            }
        }
        return "the app kept changing it; try again in a moment";
    }

    /// <summary>Shared read, so the app having the file open isn't an error. Strict UTF-8: writing back a file in another encoding would garble it.</summary>
    static (string Text, bool Bom) Read(string path)
    {
        byte[] bytes;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[f.Length];
            f.ReadExactly(bytes);
        }
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    /// <summary>A copy of the whole file before each change, newest <see cref="KeepBackups"/> kept per file name, so fixes in one app never push out another's.</summary>
    static void Backup(string path, string recordsFile)
    {
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordsFile))!, "backups");
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(path);
        const int stamp = 20; // "yyyyMMdd-HHmmss-fff-"
        File.Copy(path, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{name}"), overwrite: true);
        foreach (var old in new DirectoryInfo(dir).GetFiles().Where(f => f.Name.Length > stamp && f.Name[stamp..] == name)
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepBackups)) old.Delete();
    }

    /// <summary>Null when the file is there but can't be read: a fix must not then overwrite the undo records in it.</summary>
    static List<FixRecord>? Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<FixRecord?>>(File.ReadAllText(file))?.OfType<FixRecord>().Where(r => r.Entries is not null).ToList() ?? [] : []; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    static void Save(List<FixRecord> records, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(records, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    static void TrySave(List<FixRecord> records, string file)
    {
        try { Save(records, file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"{file} not updated: {e.Message}"); }
    }

    static string Unreadable(string file) => $"{file} can't be read, and it holds how to undo earlier fixes. Fix or remove it first. Nothing was changed.";
}
