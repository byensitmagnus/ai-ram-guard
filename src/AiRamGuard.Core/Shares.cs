using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiRamGuard.Core;

/// <summary>
/// An MCP server that can run once for every session, over HTTP (profiles.json "shareable"). Only servers that keep no state
/// between requests belong here: then sessions sharing one copy stay apart. A server with one shared session state does not.
/// </summary>
public sealed class ShareSpec
{
    /// <summary>A name the server's config entry mentions: its package or launcher, e.g. "email-mcp".</summary>
    public string Match { get; init; } = "";
    /// <summary>The argument that picks stdio, replaced by <see cref="HttpArgs"/>; they are added at the end when it isn't there.</summary>
    public string StdioArg { get; init; } = "stdio";
    /// <summary>"{port}" is filled in. The server must listen on 127.0.0.1 by default.</summary>
    public string[] HttpArgs { get; init; } = [];
    /// <summary>The variable the server reads its bearer token from: every request must carry it.</summary>
    public string TokenEnv { get; init; } = "";
    public string Path { get; init; } = "/mcp";
}

/// <summary>The one copy AI RAM Guard runs: how to start it, and the address and token the config entries now use.</summary>
/// <param name="Match">The spec's name for it: with the port, how its processes are found again to stop them.</param>
public sealed record SharedServer(string Command, List<string> Args, Dictionary<string, string> Env, string TokenEnv, string Token, int Port, string Url, string Match);

/// <summary>Starts, checks and stops shared servers. They are AI RAM Guard's children and outlive it; each round starts any that stopped.</summary>
public static class Shares
{
    /// <summary>Claude Code takes an HTTP server with a header in ~/.claude.json. Other apps' formats differ, so only its entries are shared.</summary>
    public static bool CanShare(ServerEntry s) =>
        s.App == "Claude Code" && s.Section == "mcpServers" && Path.GetFileName(s.Path).Equals(".claude.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>The spec for this entry, when it is a stdio server one of the specs knows.</summary>
    public static ShareSpec? Spec(ServerEntry s, IReadOnlyList<ConfigFile> configs, IReadOnlyList<ShareSpec> specs)
    {
        if (!CanShare(s) || configs.FirstOrDefault(c => c.Path == s.Path) is not { } c) return null;
        if (JsonText.Parse(c.Content) is not JsonObject root || JsonText.Container(root, s, create: false)?[s.Name] is not JsonObject def) return null;
        return Spec(def, specs);
    }

    internal static ShareSpec? Spec(JsonObject def, IReadOnlyList<ShareSpec> specs)
    {
        if (def["command"] is null || def["url"] is not null || (def["type"]?.GetValue<string>() ?? "stdio") != "stdio") return null;
        var text = def["command"]!.ToJsonString() + " " + (def["args"]?.ToJsonString() ?? "");
        return specs.FirstOrDefault(sp => sp.Match.Length >= 3 && sp.HttpArgs.Length > 0 && sp.TokenEnv.Length > 0 && Names(text, sp.Match));
    }

    /// <summary>How to run this entry as one shared copy: its own command and env, HTTP instead of stdio, a free port and a new token.</summary>
    internal static SharedServer Plan(JsonObject def, ShareSpec spec)
    {
        var port = FreePort();
        var args = (def["args"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? "").ToList() ?? [];
        var http = spec.HttpArgs.Select(a => a.Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var at = args.LastIndexOf(spec.StdioArg);
        if (at >= 0) { args.RemoveAt(at); args.InsertRange(at, http); } else args.AddRange(http);
        var env = (def["env"] as JsonObject)?.ToDictionary(e => e.Key, e => e.Value?.GetValue<string>() ?? "") ?? [];
        return new SharedServer(def["command"]!.GetValue<string>(), args, env, spec.TokenEnv, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            port, $"http://127.0.0.1:{port}{spec.Path}", spec.Match);
    }

    /// <summary>What the config entry becomes: Claude Code's HTTP server, with the token as a header.</summary>
    internal static JsonObject Entry(SharedServer s) => new()
    {
        ["type"] = "http",
        ["url"] = s.Url,
        ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + s.Token },
    };

    /// <summary>The entry is still exactly what sharing wrote (address and token): only then does undo replace it.</summary>
    internal static bool PointsAt(JsonNode? entry, SharedServer s) =>
        entry is JsonObject o && o["url"] is JsonValue v && v.TryGetValue<string>(out var url) && url == s.Url
        && o["headers"]?["Authorization"] is JsonValue h && h.TryGetValue<string>(out var auth) && auth == "Bearer " + s.Token;

    /// <summary>"email-mcp" in "@codefuturist/email-mcp" and in a launcher "email-mcp-launch.py", not in "my-email-mcp". The check after starting is what proves a launcher passes the arguments on.</summary>
    static bool Names(string text, string term) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![A-Za-z0-9_-]){System.Text.RegularExpressions.Regex.Escape(term)}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static bool Runs(string commandLine, SharedServer s)
    {
        if (!Names(commandLine, s.Match)) return false;
        var at = 0;
        foreach (var a in s.Args)
        {
            var i = commandLine.IndexOf(a, at, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            at = i + a.Length;
        }
        return true;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public static void Start(SharedServer s)
    {
        var psi = new ProcessStartInfo(s.Command) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        foreach (var a in s.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in s.Env) psi.Environment[k] = v;
        psi.Environment[s.TokenEnv] = s.Token;
        using var p = Process.Start(psi);
        Log.Write($"started shared {Advisor.Display(s.Command)} on port {s.Port} (pid {p?.Id})");
    }

    /// <summary>
    /// Closes the copy and what it started: processes whose command line has every argument it was started with, in order
    /// (the port among them), and their children. A prompt that merely mentions the server and the port doesn't match.
    /// </summary>
    public static void Stop(SharedServer s)
    {
        var snap = SnapshotReader.Read();
        var roots = snap.Processes.Where(p => Runs(p.CommandLine, s)).ToList();
        var byPid = snap.Processes.ToDictionary(p => p.Pid);
        var all = snap.Processes.Where(p => roots.Contains(p) || roots.Any(r => Analyzer.HasAncestor(p, r.Pid, byPid))).ToList();
        var closed = all.Count(Guard.Close);
        Log.Write($"stopped shared server on port {s.Port}: closed {closed} processes");
        Started.TryRemove(s.Port, out _);
    }

    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Initialize, then list tools <paramref name="times"/> times: a server that answers only its first request over HTTP
    /// (email-mcp 0.6.0 does) fails here, before any config points at it.
    /// </summary>
    public static bool Answers(SharedServer s, int times = 2)
    {
        var calls = new List<string> { """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"ai-ram-guard","version":"1"}}}""" };
        for (var i = 0; i < times; i++) calls.Add($$$"""{"jsonrpc":"2.0","id":{{{i + 2}}},"method":"tools/list","params":{}}""");
        try
        {
            foreach (var body in calls)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, s.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Authorization = new("Bearer", s.Token);
                req.Headers.Accept.ParseAdd("application/json");
                req.Headers.Accept.ParseAdd("text/event-stream");
                using var res = Http.Send(req);
                if (res.StatusCode != HttpStatusCode.OK) return false;
                using var reader = new StreamReader(res.Content.ReadAsStream());
                if (!reader.ReadToEnd().Contains("\"result\"", StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { return false; }
    }

    /// <summary>Starts it and waits until it answers. False (and stopped again) when it doesn't within <paramref name="wait"/>.</summary>
    public static bool StartAndCheck(SharedServer s, TimeSpan wait)
    {
        try { Start(s); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Write($"shared server didn't start: {e.Message}"); return false; }
        Started[s.Port] = DateTime.Now;
        var until = DateTime.Now + wait;
        while (DateTime.Now < until)
        {
            Thread.Sleep(500);
            if (!Answers(s)) continue;
            if (Answers(s with { Token = "wrong" }, times: 0) || !LoopbackOnly(s.Port))
            {
                Log.Write($"shared server on port {s.Port} answers without its token or listens beyond this PC: stopped");
                break;
            }
            return true;
        }
        Stop(s);
        return false;
    }

    static bool LoopbackOnly(int port)
    {
        var on = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == port).ToList();
        return on.Count > 0 && on.All(e => IPAddress.IsLoopback(e.Address));
    }

    /// <summary>What sharing runs before it changes a config. Tests swap it: they start no server.</summary>
    internal static Func<SharedServer, TimeSpan, bool> Check = StartAndCheck;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> Started = new();

    /// <summary>Each round: starts a shared copy that isn't answering (after a reboot, a crash). At most once every 2 minutes per copy.</summary>
    public static void KeepRunning(IReadOnlyList<FixRecord> applied)
    {
        foreach (var s in applied.Select(r => r.Shared).OfType<SharedServer>())
        {
            if (Answers(s, times: 0)) continue;
            if (Started.TryGetValue(s.Port, out var at) && DateTime.Now - at < TimeSpan.FromMinutes(2)) continue;
            if (!Fixer.History().Any(r => r.Shared is { } k && k.Port == s.Port && k.Token == s.Token)) continue; // undone while this round ran
            Started[s.Port] = DateTime.Now;
            try { Start(s); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Write($"shared server on port {s.Port} didn't start: {e.Message}"); }
        }
    }
}

public static partial class Fixer
{
    static bool IsShared(ServerEntry e, SharedServer s, IReadOnlyList<ConfigFile> configs) =>
        configs.FirstOrDefault(c => c.Path == e.Path) is { } c && JsonText.Parse(c.Content) is JsonObject root
        && Shares.PointsAt(JsonText.Container(root, e, create: false)?[e.Name], s);

    static bool IsSharedOnDisk(ServerEntry e, SharedServer s)
    {
        try
        {
            var text = Read(e.Path).Text;
            return JsonText.Parse(text) is not JsonObject || IsShared(e, s, [new ConfigFile(e.App, e.Path, text)]); // half-written by Claude Code: not proof it's gone
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { return true; } // the safe side: keep the record
    }

    /// <summary>
    /// Runs one copy of each server for all Claude Code sessions and points its config entries at it. The copy is started and checked
    /// first: when it doesn't answer, nothing is changed. Entries with the same command share one copy; different ones (a read-only
    /// and a full mail server) each get their own.
    /// </summary>
    public static FixResult Share(string title, IReadOnlyList<ServerEntry> servers, IReadOnlyList<ShareSpec> specs, string? file = null, TimeSpan? wait = null)
    {
        file ??= Paths.FixesFile;
        var mine = servers.Where(Shares.CanShare).ToList();
        if (mine.Count == 0) return new(false, "Only Claude Code's servers can be shared.");
        if (Load(file) is null) return new(false, Unreadable(file));

        // Group by definition from the file as it is now.
        var groups = new List<(JsonObject Def, ShareSpec Spec, List<ServerEntry> Entries)>();
        foreach (var path in mine.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = Read(path).Text; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { return new(false, $"{Advisor.Display(path)} can't be read: {e.Message}"); }
            if (JsonText.Parse(text) is not JsonObject root) return new(false, $"{Advisor.Display(path)} has comments or isn't plain JSON, so AI RAM Guard leaves it alone.");
            foreach (var s in mine.Where(s => s.Path == path))
            {
                if (JsonText.Container(root, s, create: false)?[s.Name] is not JsonObject def || Shares.Spec(def, specs) is not { } spec) continue;
                var same = groups.FindIndex(g => JsonNode.DeepEquals(g.Def, def));
                if (same >= 0) groups[same].Entries.Add(s); else groups.Add(((JsonObject)def.DeepClone(), spec, [s]));
            }
        }
        if (groups.Count == 0) return new(false, "None of these servers can run as one shared copy.");

        var done = new List<string>();
        var problems = new List<string>();
        foreach (var (def, spec, entries) in groups)
        {
            var shared = Shares.Plan(def, spec);
            var names = string.Join(", ", entries.Select(e => e.Name).Distinct());
            if (!Shares.Check(shared, wait ?? TimeSpan.FromSeconds(45)))
            {
                problems.Add($"{names}: it didn't answer over HTTP, so nothing was changed. Its version may not support several sessions at once yet.");
                continue;
            }
            var records = Load(file)!;
            var record = new FixRecord(Guid.NewGuid().ToString("N")[..8], title, [], DateTime.Now, shared);
            records.Add(record);
            foreach (var group in entries.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                var error = Rewrite(group.Key, file, text =>
                {
                    var changed = new List<FixEntry>();
                    if (JsonText.Parse(text) is not JsonObject root) return (text, changed, "it isn't plain JSON any more");
                    foreach (var e in group)
                    {
                        if (JsonText.Container(root, e, create: false) is not { } box || box[e.Name] is not JsonObject now || !JsonNode.DeepEquals(now, def)) continue; // changed since: leave it
                        var saved = now.ToJsonString();
                        box[e.Name] = Shares.Entry(shared);
                        changed.Add(new FixEntry(e, e.Name + " " + saved, saved));
                    }
                    return JsonText.Write(root, text) is { } written ? (written, changed, null) : (text, changed, Lossy);
                },
                changed => { record.Entries.AddRange(changed); Save(records, file); },
                changed => { record.Entries.RemoveAll(changed.Contains); TrySave(records, file); });
                if (error is not null) problems.Add($"{Advisor.Display(group.Key)}: {error}");
            }
            record.Entries.RemoveAll(e => !IsSharedOnDisk(e.Server, shared)); // Claude Code may have written its own copy over ours
            if (record.Entries.Count == 0)
            {
                records.Remove(record);
                TrySave(records, file);
                Shares.Stop(shared);
                problems.Add($"{names}: the config changed while AI RAM Guard wrote it. Try again in a moment.");
                continue;
            }
            TrySave(records, file);
            foreach (var e in record.Entries) Log.Write($"shared {e.Server.Name} in {Where(e.Server)}: one copy on port {shared.Port}");
            done.Add(names);
        }
        if (done.Count == 0) return new(false, string.Join("\n", problems));
        var msg = $"{string.Join(" and ", done)} now run{(done.Count == 1 && !done[0].Contains(',') ? "s" : "")} once for every Claude Code session. New sessions use the shared copy; open sessions keep their own until you close them.";
        return new(true, problems.Count > 0 ? msg + "\nNot changed: " + string.Join("\n", problems) : msg);
    }
}
