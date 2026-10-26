using AiRamGuard.Tray;
using Xunit;

public class ActivityTests
{
    // The app runs as en-US; the test host would format "3,0 GB" on a Danish PC.
    public ActivityTests() => System.Globalization.CultureInfo.CurrentCulture = new("en-US");

    static List<ActivityEntry> Read(params string[] newestFirst) => ActivityPage.Group(newestFirst.Select(ActivityPage.Parse).OfType<ActivityEntry>()).ToList();

    [Fact]
    public void Back_to_back_trims_are_one_row_but_a_round_that_closed_leftovers_stays_apart()
    {
        var rows = Read(
            "2026-10-25 14:55:00 low RAM: closed 0 orphans, trimmed 40 AI processes, freed 2048 MB, free 5.0 -> 7.0 GB",
            "2026-10-25 14:45:00 low RAM: closed 0 orphans, trimmed 40 AI processes, freed 1024 MB, free 5.0 -> 6.0 GB",
            "2026-10-25 14:40:00 low RAM: closed 2 orphans, trimmed 40 AI processes, freed 512 MB, free 5.0 -> 5.5 GB",
            "2026-10-25 14:30:00 low RAM: closed 0 orphans, trimmed 40 AI processes, freed 512 MB, free 5.0 -> 5.5 GB");
        Assert.Equal(["Freed 3.0 GB in 2 rounds", "Freed 512 MB", "Freed 512 MB"], rows.Select(r => r.Title));
        Assert.StartsWith("14:45–14:55", rows[0].Detail);
    }

    [Fact]
    public void Priority_changes_group_and_new_lines_read_as_plain_words()
    {
        var rows = Read(
            "2026-10-25 18:25:00 restored CPU priority of 6 AI processes",
            "2026-10-25 18:23:00 game in front: lowered CPU priority of 1 AI processes",
            "2026-10-25 18:20:00 turned off rea in ~\\.claude.json",
            "2026-10-25 18:19:00 turned on rea again in ~\\.codex\\config.toml",
            "2026-10-25 18:18:00 cleaned temp D:\\AI\\Temp: 1200 files, 3072 MB deleted, 4 in use",
            "2026-10-25 18:17:00 capped OpenAI Codex / ChatGPT at 4 GB");
        Assert.Equal(["CPU priority changed 2 times", "Turned off rea", "Turned rea on again", "Cleaned up 3.0 GB of old temp files", "Capped OpenAI Codex / ChatGPT at 4 GB"],
            rows.Select(r => r.Title));
        Assert.Contains("4 in use, kept", rows[3].Detail);
    }

    [Fact]
    public void Trims_on_different_days_are_not_merged()
    {
        var rows = Read(
            "2026-10-26 00:01:00 low RAM: closed 0 orphans, trimmed 1 AI processes, freed 100 MB, free 5.0 -> 5.1 GB",
            "2026-10-25 23:59:00 low RAM: closed 0 orphans, trimmed 1 AI processes, freed 100 MB, free 5.0 -> 5.1 GB");
        Assert.Equal(2, rows.Count);
    }
}
