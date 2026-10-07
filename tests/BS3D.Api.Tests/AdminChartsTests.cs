using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using BS3D.Api.AdminWeb;

namespace BS3D.Api.Tests;

/// <summary>
/// The charts page (#9): the series, which can be exact, apart from their drawing. The clock stands at 2026-09-15 12:00,
/// so the 14-day range runs from 2026-09-02, and 2026-09-10 is its ninth day (index 8).
/// </summary>
public sealed class AdminChartsTests
{
    private static DateTimeOffset On(int month, int day) => new(2026, month, day, 10, 0, 0, TimeSpan.Zero);
    private static BoardKey Level(int n) => new($"L{n}.json", "0123456789abcdef", 1);

    private static void Refused(ScoreStore store, Microsoft.Data.Sqlite.SqliteConnection c, DateTimeOffset at, string reason, int count) =>
        store.WriteRefusals(c, new Refusals.Batch(new Dictionary<(string, string), int> { [(ScoreStore.DayOf(at), reason)] = count }, [], 0),
            at, TimeSpan.FromDays(400), 100);

    /// <summary>
    /// Ann played on 08-01, before the range, and again on 09-12. Bob came on 09-10 (a clear and a loss) and cleared another
    /// level on 09-12. Cid came on 09-12 and only lost. Refusals on 08-01, 09-11 and 09-12.
    /// </summary>
    private static Task<AdminPage> Seeded() => AdminPage.StartAsync((store, c, now) =>
    {
        Guid ann = AdminPage.AddPlayer(store, c, On(8, 1), "Ann");
        AdminPage.AddClear(store, c, On(8, 1), ann, 100, board: Level(1));
        Guid bob = AdminPage.AddPlayer(store, c, On(9, 10), "Bob");
        AdminPage.AddClear(store, c, On(9, 10), bob, 200, board: Level(1));
        AdminPage.AddClear(store, c, On(9, 10).AddMinutes(5), bob, 300, board: Level(2), stars: 0);
        AdminPage.AddClear(store, c, On(9, 12), ann, 150, board: Level(1));
        AdminPage.AddClear(store, c, On(9, 12), bob, 400, board: Level(2));
        Guid cid = AdminPage.AddPlayer(store, c, On(9, 12), "Cid");
        AdminPage.AddClear(store, c, On(9, 12), cid, 50, board: Level(3), stars: 0);
        Refused(store, c, On(8, 1), Reasons.OverCeiling, 5);
        Refused(store, c, On(9, 11), Reasons.OverCeiling, 3);
        Refused(store, c, On(9, 12), Reasons.OverCeiling, 2);
        Refused(store, c, On(9, 12), Reasons.BadName, 1);
    });

    [Fact]
    public async Task Each_day_counts_its_own_and_the_totals_start_from_before_the_range()
    {
        await using AdminPage page = await Seeded();
        AdminData.Charts ch = new AdminData(page.Options).ReadCharts("14d");

        Assert.Equal(("2026-09-02", "2026-09-15", 14), (ch.Days[0], ch.Days[^1], ch.Days.Count));
        const int sep10 = 8, sep12 = 10;

        Assert.Equal((1, 1), (ch.NewPlayers[sep10], ch.NewPlayers[sep12]));
        Assert.Equal((1, 2, 3), (ch.PlayersTotal[0], ch.PlayersTotal[sep10], ch.PlayersTotal[^1]));

        Assert.Equal((1, 1, 2, 1), (ch.Clears[sep10], ch.Unfinished[sep10], ch.Clears[sep12], ch.Unfinished[sep12]));
        Assert.Equal((1, 4, 2), (ch.ClearsTotal[0], ch.ClearsTotal[^1], ch.UnfinishedTotal[^1]));

        // 09-12: Cid's first day; Ann (from August) and Bob (from 09-10) returning
        Assert.Equal((1, 0), (ch.FirstDayPlayers[sep10], ch.ReturningPlayers[sep10]));
        Assert.Equal((1, 2), (ch.FirstDayPlayers[sep12], ch.ReturningPlayers[sep12]));

        // L1 was first cleared in August, L2 on 09-12; L3 has only a loss and is no cleared board
        Assert.Equal((1, 1, 2, 2), (ch.BoardsCleared[0], ch.BoardsCleared[sep10], ch.BoardsCleared[sep12], ch.BoardsCleared[^1]));

        // Every submission took 60 s: two on 09-10, three on 09-12
        Assert.Equal((2.0, 3.0), (ch.Minutes[sep10], ch.Minutes[sep12]));

        // Most refused first, not by name; August's five are outside the range
        Assert.Equal([Reasons.OverCeiling, Reasons.BadName], ch.Refusals.Select(r => r.Reason).ToList());
        Assert.Equal((3, 2, 5), (ch.Refusals[0].Counts[sep10 + 1], ch.Refusals[0].Counts[sep12], ch.Refusals[0].Counts.Sum()));
        Assert.Equal(1, ch.Refusals[1].Counts[sep12]);
    }

    [Fact]
    public async Task No_range_starts_before_the_first_day_anything_was_recorded_and_an_unknown_range_is_ninety_days()
    {
        await using AdminPage page = await Seeded();
        AdminData data = new(page.Options);

        AdminData.Charts all = data.ReadCharts("all");
        Assert.Equal(("2026-08-01", "2026-09-15"), (all.Days[0], all.Days[^1]));
        Assert.Equal((1, 3, 4), (all.PlayersTotal[0], all.PlayersTotal[^1], all.ClearsTotal[^1]));
        Assert.Equal(10, all.Refusals[0].Counts.Sum());

        // 90 days back from 09-15 is 06-18, before anything was recorded: the range starts on 08-01 as "all" does
        foreach (string? range in new[] { null, "", "7d", "90d' OR 1=1 --" })
        {
            AdminData.Charts ch = data.ReadCharts(range);
            Assert.Equal(("90d", "2026-08-01", "2026-09-15"), (ch.Range, ch.Days[0], ch.Days[^1]));
        }
        Assert.Equal(14, data.ReadCharts("14d").Days.Count);
    }

    [Fact]
    public async Task An_empty_database_charts_today_alone_with_nothing_on_it()
    {
        await using AdminPage page = await AdminPage.StartAsync();
        await page.LogInAsync();

        AdminData.Charts ch = new AdminData(page.Options).ReadCharts("all");
        Assert.Equal(["2026-09-15"], ch.Days);
        Assert.Equal((0, 0, 0), (ch.PlayersTotal[0], ch.ClearsTotal[0], ch.BoardsCleared[0]));

        string html = await page.Client.GetStringAsync("/charts?range=all");
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
        Assert.Contains("None in this range.", html);
    }

    [Theory]
    [InlineData(0, true, 1, 1)]
    [InlineData(1, true, 1, 1)]
    [InlineData(3, true, 3, 1)]
    [InlineData(7, true, 8, 2)]
    [InlineData(12, true, 15, 5)]
    [InlineData(1234, true, 1500, 500)]
    [InlineData(0.3, false, 0.3, 0.1)]
    [InlineData(47, false, 60, 20)]
    public void The_axis_steps_by_one_two_or_five_into_at_most_five(double max, bool integer, double top, double step)
    {
        (double t, double s) = AdminPages.Scale(max, integer);

        Assert.Equal(top, t, 9);
        Assert.Equal(step, s, 9);
        Assert.True(t >= max && t / s <= 5.000001, $"top {t}, step {s}");
    }

    private static List<(double Y, double Height, string Title)> Rects(Markup chart) =>
        Regex.Matches(chart.Value, @"<rect class=""[^""]+"" x=""[^""]+"" y=""(?<y>[^""]+)"" width=""[^""]+"" height=""(?<h>[^""]+)""><title>(?<t>[^<]*)</title>")
            .Select(m => (double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture), m.Groups["t"].Value))
            .ToList();

    [Fact]
    public void Bars_are_as_tall_as_their_values_and_stack_bottom_up()
    {
        string[] days = ["2026-09-13", "2026-09-14", "2026-09-15"];
        Markup chart = AdminPages.BarChart("Test", days,
            [new("Clears", "c1", [2, 4, 0]), new("<b>Unfinished</b>", "c2", [2, 0, 0])], integer: true);

        var rects = Rects(chart);
        // Three days in the whole width: each bar no wider than a bar is ever drawn
        Assert.All(Regex.Matches(chart.Value, @"<rect [^>]*width=""(?<w>[^""]+)""").Select(m => double.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture)), w => Assert.True(w <= 28, $"width {w}"));
        // Day 1: 2 and 2 stacked; day 2: 4 alone; day 3: nothing, so no bar
        Assert.Equal(3, rects.Count);
        Assert.Equal(rects[0].Height, rects[1].Height, 6);
        Assert.Equal(rects[0].Height + rects[1].Height, rects[2].Height, 6);
        Assert.Equal(rects[0].Y - rects[1].Height, rects[1].Y, 6);
        Assert.Equal(rects[0].Y + rects[0].Height, rects[2].Y + rects[2].Height, 6);
        Assert.Equal("2026-09-13 · &lt;b&gt;Unfinished&lt;/b&gt;: 2", rects[1].Title);
        Assert.DoesNotContain("<b>", chart.Value);
        // The last day's label ends at the edge; the axis reads 0 to 4
        Assert.Contains("<text class=\"x last\"", chart.Value);
        Assert.Contains(">4</text>", chart.Value);
        Assert.Contains("<tr><td>2026-09-14</td><td class=\"n\">4</td><td class=\"n\">0</td></tr>", chart.Value);
    }

    [Fact]
    public void A_line_has_a_point_per_day_and_rises_with_its_values()
    {
        Markup chart = AdminPages.LineChart("Test", ["2026-09-14", "2026-09-15"], [new("Players", "c1", [1, 3])]);

        var points = Regex.Matches(chart.Value, @"<circle class=""c1"" cx=""(?<x>[^""]+)"" cy=""(?<y>[^""]+)""")
            .Select(m => (X: double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture), Y: double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture)))
            .ToList();
        Assert.Equal(2, points.Count);
        Assert.True(points[1].X > points[0].X && points[1].Y < points[0].Y);
        Assert.Contains("<title>2026-09-15 · Players: 3</title>", chart.Value);
    }

    private static void Sent(ScoreStore store, Microsoft.Data.Sqlite.SqliteConnection c, DateTimeOffset at, Guid player, string version, int count = 1)
    {
        for (int i = 0; i < count; i++)
            store.Insert(c, new ScoreStore.NewSubmission(Guid.NewGuid(), player, Level(1), 100, 3, 10, 60, version, "1PHASH0000000000", $"BS3D/{version}", at.AddSeconds(i)));
    }

    [Fact]
    public async Task Versions_are_shares_of_each_day_the_releases_sent_most_apart_in_the_order_they_arrived_and_local_builds_together()
    {
        // In the range: v1.0.0 3, v1.4.0 and v1.3.0 2 each (in that order), v1.6.0 and v1.1.0 1 each, of which v1.1.0
        // takes the fourth place by its name although v1.6.0 came first; local builds on 09-11 and 09-12. August's
        // v0.0.1, sent most of all, is outside the range
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            Guid ann = AdminPage.AddPlayer(store, c, On(8, 1), "Ann");
            Sent(store, c, On(8, 1), ann, "v0.0.1", 5);
            Sent(store, c, On(9, 10), ann, "v1.0.0", 3);
            Sent(store, c, On(9, 10).AddMinutes(5), ann, "v1.6.0");
            foreach (string v in new[] { "v1.4.0", "v1.3.0" }) Sent(store, c, On(9, 11), ann, v, 2);
            Sent(store, c, On(9, 11).AddMinutes(5), ann, "v1.1.0");
            Sent(store, c, On(9, 11).AddMinutes(6), ann, "dev-abc1234", 3);
            Sent(store, c, On(9, 12), ann, "dev");
        });
        AdminData.Charts ch = new AdminData(page.Options).ReadCharts("14d");
        const int sep10 = 8, sep11 = 9, sep12 = 10, sep13 = 11;

        Assert.Equal([AdminData.LocalBuilds, AdminData.OtherReleases, "v1.0.0", "v1.4.0", "v1.3.0", "v1.1.0"], ch.Versions.Select(v => v.Version).ToList());
        double[] Day(int i) => ch.Versions.Select(v => v.Share[i]).ToArray();
        Assert.Equal([0, 25, 75, 0, 0, 0], Day(sep10));
        Assert.Equal([37.5, 0, 0, 25, 25, 12.5], Day(sep11));
        Assert.Equal([100, 0, 0, 0, 0, 0], Day(sep12));
        Assert.Equal([0, 0, 0, 0, 0, 0], Day(sep13));

        // Over all time v0.0.1 is sent most, and August is its day alone
        AdminData.Charts all = new AdminData(page.Options).ReadCharts("all");
        Assert.Equal([AdminData.LocalBuilds, AdminData.OtherReleases, "v0.0.1", "v1.0.0", "v1.4.0", "v1.3.0"], all.Versions.Select(v => v.Version).ToList());
        Assert.Equal(100, all.Versions[2].Share[0]);

        // No local build and no other release: neither is a series
        await using AdminPage two = await AdminPage.StartAsync((store, c, now) =>
        {
            Guid ann = AdminPage.AddPlayer(store, c, On(9, 10), "Ann");
            Sent(store, c, On(9, 10), ann, "v1.0.0");
        });
        Assert.Equal(["v1.0.0"], new AdminData(two.Options).ReadCharts("14d").Versions.Select(v => v.Version).ToList());
    }

    /// <summary>A backup on the box, <paramref name="bytes"/> long, written at <paramref name="written"/>.</summary>
    private static void Backup(AdminPage page, DateTimeOffset written, int bytes)
    {
        string path = Path.Combine(Backups(page), $"scores-{written:yyyyMMdd-HHmmss}.db");
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
    }

    private static string Backups(AdminPage page) =>
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(page.Options.Database)!, "backups")).FullName;

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);

    private static IEnumerable<string?> Marks(params string?[] marks) => marks;

    [Fact]
    public async Task The_backups_give_each_days_size_and_the_copies_their_age_with_a_gap_and_a_failure_marked()
    {
        // A player from before the range, so that the range is the whole 14 days
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) => AdminPage.AddPlayer(store, c, On(9, 1), "Ann"));
        // Nightly at 01:30 and once more by hand on 09-10; none on 09-12, 09-14 or today
        Backup(page, At(10, 1, 30), 1000);
        Backup(page, At(10, 6, 0), 1500);
        Backup(page, At(11, 1, 30), 2048);
        Backup(page, At(13, 1, 30), 3000);
        File.WriteAllText(Path.Combine(Backups(page), "last-offbox"), "attempt=2026-09-15T01:31:00Z\nresult=failed\ndetail=no drive\nok=2026-09-13T01:31:00Z\ncopy=x\nevery=1\n");
        File.WriteAllLines(Path.Combine(Backups(page), "last-offbox.log"), [
            "attempt=2026-09-11T01:31:00Z result=ok ok=2026-09-11T01:31:00Z every=1",
            "attempt=2026-09-12T01:31:00Z result=failed ok=2026-09-11T01:31:00Z every=1",
            "not a line of the log",
            "attempt=2026-09-13T01:31:00Z result=ok ok=2026-09-13T01:31:00Z every=1",
            "attempt=2026-09-14T01:31:00Z result=failed ok=2026-09-13T01:31:00Z every=1",
            "attempt=2026-09-15T01:31:00Z result=failed ok=2026-09-13T01:31:00Z every=1"]);
        // Set up before its log was kept: the record's good copy is all there is
        File.WriteAllText(Path.Combine(Backups(page), "last-offsite"), "attempt=2026-09-14T01:30:00Z\nresult=ok\ndetail=snapshot\nok=2026-09-14T01:30:00Z\ncopy=x\nevery=2\n");

        AdminData.Charts ch = new AdminData(page.Options).ReadCharts("14d");
        const int sep9 = 7, sep10 = 8, sep11 = 9, sep12 = 10, sep13 = 11, sep14 = 12, sep15 = 13;

        // The day's last backup; a day without one has no size
        Assert.Equal((1500.0, 2048.0, 3000.0), (ch.BackupBytes[sep10], ch.BackupBytes[sep11], ch.BackupBytes[sep13]));
        Assert.All(new[] { 0, sep9, sep12, sep14, sep15 }, i => Assert.True(double.IsNaN(ch.BackupBytes[i]), $"day {i}"));

        Assert.Equal(["On the box", "Off the box", "Off the site"], ch.Copies.Select(c => c.Name).ToList());
        AdminData.CopyAges box = ch.Copies[0], off = ch.Copies[1], site = ch.Copies[2];
        static double Hours(double days) => Math.Round(days * 24, 6);

        // On the box: unknown before the oldest backup kept; 18 h at the end of 09-10, 22.5 h at the end of 09-11; the
        // missed night makes 09-12 46.5 h and 09-13 48 h, just before its copy, both late, and today 58.5 h at noon
        Assert.True(double.IsNaN(box.Age[sep9]));
        Assert.Equal([18, 22.5, 46.5, 48, 46.5, 58.5], box.Age[sep10..].Select(Hours).ToArray());
        Assert.Equal(Marks(null, null, "late", "late", "late", "late"), box.Marks[sep10..]);

        // Off the box: from its log's first good copy; the nights that failed marked failed, and 09-13, which began with
        // the copy a night late until 01:31, late; the garbled line skipped
        Assert.True(double.IsNaN(off.Age[sep10]));
        Assert.Equal((22.483333, 46.483333, 48), (Hours(off.Age[sep11]), Hours(off.Age[sep12]), Hours(off.Age[sep13])));
        Assert.Equal(Marks(null, "failed", "late", "failed", "failed"), off.Marks[sep11..]);

        // Off the site, every other day: 34.5 h today is not late
        Assert.True(double.IsNaN(site.Age[sep13]));
        Assert.Equal([22.5, 34.5], site.Age[sep14..].Select(Hours).ToArray());
        Assert.Equal(Marks(null, null), site.Marks[sep14..]);

        // A copy not set up is not drawn
        File.WriteAllText(Path.Combine(Backups(page), "last-offsite"), "attempt=2026-09-15T01:30:00Z\nresult=none\ndetail=no repository\nok=\ncopy=\nevery=2\n");
        Assert.Equal(["On the box", "Off the box"], new AdminData(page.Options).ReadCharts("14d").Copies.Select(c => c.Name).ToList());
    }

    [Fact]
    public void A_nightly_copy_late_by_the_timers_jitter_or_an_hour_of_summer_time_is_not_marked_and_one_a_night_late_is()
    {
        string[] days = ["2026-09-10", "2026-09-11", "2026-09-12", "2026-09-13", "2026-09-14"];
        DateTimeOffset now = At(14, 23, 0);
        // 01:39, then 23 h 51 min, 25 h and 24 h 10 min later
        DateTimeOffset[] nightly = [At(10, 1, 39), At(11, 1, 30), At(12, 2, 30), At(13, 2, 40), At(14, 2, 40)];

        AdminData.CopyAges ages = AdminData.Ages("On the box", days, now, nightly, [], every: 1);
        Assert.All(ages.Marks, Assert.Null);
        Assert.True(ages.Age.Skip(1).All(a => a > 0.9 && a < 1.05), string.Join(", ", ages.Age));

        // Every other day: 2 days and 1 hour is not late, 2 days and 13 hours is
        AdminData.CopyAges site = AdminData.Ages("Off the site", days, now, [At(10, 1, 30), At(12, 2, 30), At(14, 15, 30)], [], every: 2);
        Assert.Equal(Marks(null, null, null, null, "late"), site.Marks);
    }

    [Fact]
    public void A_line_breaks_on_a_day_without_a_value_and_a_marked_point_says_why()
    {
        Markup chart = AdminPages.LineChart("Test", ["2026-09-12", "2026-09-13", "2026-09-14", "2026-09-15"],
            [new("Age", "c1", [1, double.NaN, 2, 3], [null, null, "late", null])], integer: false);

        // The first day alone is a point without a line; the last two are a line
        Assert.Single(Regex.Matches(chart.Value, "<polyline "));
        Assert.Equal(3, Regex.Matches(chart.Value, "<circle ").Count);
        Assert.Contains("<circle class=\"c1 mark\"", chart.Value);
        Assert.Contains("<title>2026-09-14 · Age: 2, late</title>", chart.Value);
        Assert.Contains("<tr><td>2026-09-13</td><td class=\"n\">–</td></tr>", chart.Value);
        Assert.Contains("<td class=\"n\">2 <span class=\"flag\">late</span></td>", chart.Value);
        Assert.DoesNotContain("NaN", chart.Value);
    }

    [Fact]
    public async Task The_page_draws_every_chart_and_names_its_range()
    {
        await using AdminPage page = await Seeded();
        await page.LogInAsync();

        string html = await page.Client.GetStringAsync("/charts?range=14d");

        Assert.Equal(10, Regex.Matches(html, "<svg class=\"chart\"").Count);
        Assert.Contains("<span aria-current=\"true\">14 days</span>", html);
        Assert.Contains("<a href=\"/charts?range=all\">All</a>", html);
        Assert.Contains("2026-09-02 to 2026-09-15", html);
        Assert.Contains("<a href=\"/charts\" aria-current=\"page\">Charts</a>", html);

        string hostile = await page.Client.GetStringAsync("/charts?range=%3Cscript%3E");
        Assert.DoesNotContain("<script", hostile);
        Assert.Contains("<span aria-current=\"true\">90 days</span>", hostile);
    }
}
