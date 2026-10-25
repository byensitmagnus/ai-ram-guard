using System.Diagnostics;
using AiRamGuard.Core;
using Microsoft.Win32;
using Xunit;

/// <summary>Real moves in a temp folder, with a test-only user variable that is removed again afterwards.</summary>
public sealed class StorageTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "airg-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _var = "AIRG_TEST_" + Guid.NewGuid().ToString("N")[..8];
    string Src => Path.Combine(_root, "cache");
    string Dst => Path.Combine(_root, "moved");
    string Records => Path.Combine(_root, "moves.json");
    StorageSpot Spot(bool copy = true) => new("test", "Test cache", [_var], [Src], CopyContents: copy);

    public StorageTests()
    {
        Directory.CreateDirectory(Path.Combine(Src, "sub"));
        File.WriteAllBytes(Path.Combine(Src, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(Src, "sub", "b.bin"), new byte[500]);
        File.SetAttributes(Path.Combine(Src, "sub", "b.bin"), FileAttributes.Hidden);
    }

    static object? Raw(string name, out RegistryValueKind kind)
    {
        using var k = Registry.CurrentUser.OpenSubKey("Environment")!;
        var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        kind = v is null ? RegistryValueKind.Unknown : k.GetValueKind(name);
        return v;
    }

    static void Junction(string link, string target) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!.WaitForExit();

    [Fact]
    public void Size_counts_hidden_files_but_not_what_a_junction_points_to()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[5000]);
        Junction(Path.Combine(Src, "link"), outside);
        Assert.Equal(1500, Storage.FolderBytes(Src));
    }

    [Fact]
    public void Move_and_undo_round_trip()
    {
        var moved = Mover.Move(Spot(), Dst, Records);
        Assert.True(moved.Ok, moved.Message);
        Assert.Equal(1500, moved.MovedBytes);
        Assert.Equal(1500, Storage.FolderBytes(Dst));
        Assert.Equal(0, Storage.FolderBytes(Src));
        Assert.Equal(Dst, Raw(_var, out _));
        Assert.Equal(Dst, Storage.CurrentPath(Spot()));
        Assert.Single(Mover.History(Records));

        var undone = Mover.Undo(Spot(), Records);
        Assert.True(undone.Ok, undone.Message);
        Assert.Equal(1500, Storage.FolderBytes(Src));
        Assert.Null(Raw(_var, out _)); // there was no variable before, so there is none after
        Assert.False(Directory.Exists(Dst));
        Assert.Empty(Mover.History(Records));
    }

    [Fact]
    public void A_folder_with_links_is_refused_so_nothing_outside_moves_and_nothing_splits()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "keep.bin"), new byte[10]);
        Junction(Path.Combine(Src, "sub", "link"), outside);
        Assert.Equal(1, Storage.LinkCount(Src));

        Assert.False(Mover.Move(Spot(), Dst, Records).Ok);
        Assert.True(File.Exists(Path.Combine(outside, "keep.bin")));
        Assert.Equal(1500, Storage.FolderBytes(Src));
        Assert.False(Directory.Exists(Dst));
        Assert.Null(Raw(_var, out _));
        Assert.Empty(Mover.History(Records));
    }

    [Fact]
    public void Refuses_when_the_variable_points_at_a_drive_or_a_relative_path()
    {
        foreach (var bad in new[] { Path.GetPathRoot(_root)!, "cache" })
        {
            using (var k = Registry.CurrentUser.CreateSubKey("Environment")) k.SetValue(_var, bad);
            Assert.False(Mover.Move(Spot(), Dst, Records).Ok);
        }
        Assert.False(Directory.Exists(Dst));
        Assert.Empty(Mover.History(Records));
    }

    [Fact]
    public void Undo_keeps_a_folder_you_chose_since()
    {
        Assert.True(Mover.Move(Spot(), Dst, Records).Ok);
        var mine = Path.Combine(_root, "mine");
        using (var k = Registry.CurrentUser.CreateSubKey("Environment")) k.SetValue(_var, mine);
        Assert.Empty(Mover.Undoable(Records)); // the page stops offering the undo
        Assert.False(Mover.Undo(Spot(), Records).Ok);
        Assert.Equal(mine, Raw(_var, out _));
        Assert.Equal(1500, Storage.FolderBytes(Dst));
    }

    [Fact]
    public void An_unreadable_undo_file_blocks_moves_instead_of_being_overwritten()
    {
        File.WriteAllText(Records, "{ half a file");
        Assert.False(Mover.Move(Spot(), Dst, Records).Ok);
        Assert.Equal("{ half a file", File.ReadAllText(Records));
        Assert.Equal(1500, Storage.FolderBytes(Src));
        Assert.Null(Raw(_var, out _));
    }

    [Fact]
    public void Refuses_a_folder_with_files_a_drive_root_and_the_cache_itself()
    {
        Directory.CreateDirectory(Dst);
        File.WriteAllText(Path.Combine(Dst, "mine.txt"), "your own file");
        Assert.False(Mover.Move(Spot(), Dst, Records).Ok);
        Assert.False(Mover.Move(Spot(), Path.GetPathRoot(_root)!, Records).Ok);
        Assert.False(Mover.Move(Spot(), Path.Combine(Src, "sub"), Records).Ok);
        Assert.False(Mover.Move(new StorageSpot("x", "X", [_var], [Src], Movable: false), Path.Combine(_root, "new"), Records).Ok);
        Assert.Null(Raw(_var, out _));
        Assert.Empty(Mover.History(Records));
    }

    [Fact]
    public void Temp_only_switches_the_variable_and_undo_restores_the_raw_value()
    {
        using (var k = Registry.CurrentUser.CreateSubKey("Environment"))
            k.SetValue(_var, Src.Replace(Path.GetTempPath().TrimEnd('\\'), "%TEMP%"), RegistryValueKind.ExpandString);
        var before = Raw(_var, out _);

        Assert.True(Mover.Move(Spot(copy: false), Dst, Records).Ok);
        Assert.Equal(1500, Storage.FolderBytes(Src)); // nothing copied
        Assert.Equal(Dst, Raw(_var, out _));

        Assert.True(Mover.Undo(Spot(copy: false), Records).Ok);
        Assert.Equal(before, Raw(_var, out var kind));
        Assert.Equal(RegistryValueKind.ExpandString, kind);
    }

    [Fact]
    public void PATH_gets_the_folder_once_keeps_its_type_and_loses_only_that_entry()
    {
        using (var k = Registry.CurrentUser.CreateSubKey("Environment"))
            k.SetValue(_var, @"%USERPROFILE%\bin;C:\Tools\", RegistryValueKind.ExpandString);
        Assert.True(Env.AddToPath(@"C:\Apps\AiRamGuard", _var));
        Assert.False(Env.AddToPath(@"c:\apps\airamguard\", _var)); // already there
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools\;C:\Apps\AiRamGuard", Raw(_var, out var kind));
        Assert.Equal(RegistryValueKind.ExpandString, kind);

        Assert.True(Env.RemoveFromPath(@"C:\Apps\AiRamGuard", _var));
        Assert.False(Env.RemoveFromPath(@"C:\Apps\AiRamGuard", _var));
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools\", Raw(_var, out _));
    }

    public void Dispose()
    {
        using (var k = Registry.CurrentUser.CreateSubKey("Environment")) k.DeleteValue(_var, throwOnMissingValue: false);
        foreach (var link in Directory.Exists(_root) ? Directory.EnumerateDirectories(_root, "link", SearchOption.AllDirectories).ToList() : [])
            Directory.Delete(link); // the junction only, before the recursive delete could reach through it
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
