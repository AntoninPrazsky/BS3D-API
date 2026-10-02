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
    public async Task All_starts_on_the_first_day_anything_was_recorded_and_an_unknown_range_is_ninety_days()
    {
        await using AdminPage page = await Seeded();
        AdminData data = new(page.Options);

        AdminData.Charts all = data.ReadCharts("all");
        Assert.Equal(("2026-08-01", "2026-09-15"), (all.Days[0], all.Days[^1]));
        Assert.Equal((1, 3, 4), (all.PlayersTotal[0], all.PlayersTotal[^1], all.ClearsTotal[^1]));
        Assert.Equal(10, all.Refusals[0].Counts.Sum());

        foreach (string? range in new[] { null, "", "7d", "90d' OR 1=1 --" })
        {
            AdminData.Charts ch = data.ReadCharts(range);
            Assert.Equal(("90d", 90, "2026-09-15"), (ch.Range, ch.Days.Count, ch.Days[^1]));
        }
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

    [Fact]
    public async Task The_page_draws_every_chart_and_names_its_range()
    {
        await using AdminPage page = await Seeded();
        await page.LogInAsync();

        string html = await page.Client.GetStringAsync("/charts?range=14d");

        Assert.Equal(7, Regex.Matches(html, "<svg class=\"chart\"").Count);
        Assert.Contains("<span aria-current=\"true\">14 days</span>", html);
        Assert.Contains("<a href=\"/charts?range=all\">All</a>", html);
        Assert.Contains("2026-09-02 to 2026-09-15", html);
        Assert.Contains("<a href=\"/charts\" aria-current=\"page\">Charts</a>", html);

        string hostile = await page.Client.GetStringAsync("/charts?range=%3Cscript%3E");
        Assert.DoesNotContain("<script", hostile);
        Assert.Contains("<span aria-current=\"true\">90 days</span>", hostile);
    }
}
