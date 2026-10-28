using System.Net;
using System.Text.RegularExpressions;
using BS3D.Api.AdminWeb;

namespace BS3D.Api.Tests;

/// <summary>The admin page's boards and players views (issue #5, requirement 12).</summary>
public sealed class AdminViewTests
{
    private static readonly DateTimeOffset LastMonth = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly BoardKey Unknown = new("Unknown.json", "0000000000000000", 1);

    /// <summary>The names in one table of a page, in order: the text of each row's second cell.</summary>
    private static List<string> Names(string section) =>
        Regex.Matches(section, @"<tr><td class=""n"">\d+</td><td><bdi>([^<]*)</bdi>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();

    /// <summary>The players list's row of <paramref name="name"/>, found by its first cell (a name also appears in other rows' "shares" cell).</summary>
    private static string RowOf(string html, string name) =>
        Regex.Matches(html, @"<tr><td><a href=""/player\?id=[^""]+""><bdi>(?<name>[^<]*)</bdi>.*?</tr>", RegexOptions.Singleline)
            .Single(m => WebUtility.HtmlDecode(m.Groups["name"].Value) == name).Value;

    private static string Between(string html, string from, string to)
    {
        int start = html.IndexOf(from, StringComparison.Ordinal);
        int end = html.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return html[start..(end < 0 ? html.Length : end)];
    }

    [Fact]
    public async Task The_boards_are_every_board_a_table_names_and_any_board_with_clears_that_none_does()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            Guid ann = AdminPage.AddPlayer(store, c, now, "Ann");
            AdminPage.AddClear(store, c, now, ann, 100);
            AdminPage.AddClear(store, c, now, ann, 200);
            Guid bob = AdminPage.AddPlayer(store, c, now, "Bob");
            AdminPage.AddClear(store, c, LastMonth, bob, 300);
            Guid hid = AdminPage.AddPlayer(store, c, now, "Hid");
            store.SetHidden(c, hid, true);
            AdminPage.AddClear(store, c, now, hid, 400);
            AdminPage.AddClear(store, c, now, ann, 50, board: Unknown);
        });
        page.WriteCeilingTable();
        await page.LogInAsync();

        string html = await page.Client.GetStringAsync("/boards");

        // One: Ann this month; Ann and Bob all time; the hidden player in no count, though her clear is one of the 4
        Assert.Matches(@">One</a></td>\s*<td><code>One\.json#0123456789abcdef r1</code></td><td class=""n"">50,000</td>\s*<td class=""n"">30</td><td class=""n"">1</td><td class=""n"">2</td><td class=""n"">4</td>", html);
        Assert.Matches(@">Two</a></td>\s*<td><code>Two\.json#fedcba9876543210 r1</code></td><td class=""n"">40,000</td>\s*<td class=""n"">20</td><td class=""n"">0</td><td class=""n"">0</td><td class=""n"">0</td>", html);
        Assert.Matches(@">Unknown\.json</a> <span class=""flag"">in no ceiling table</span>", html);
    }

    /// <summary>The boards list's levels, top to bottom.</summary>
    private static List<string> Levels(string html) =>
        Regex.Matches(html, @"<tr><td><a href=""/board\?[^""]*"">([^<]*)</a>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();

    /// <summary>The boards list's chapter headings, top to bottom.</summary>
    private static List<string> Chapters(string html) =>
        Regex.Matches(html, @"<tr class=""group""><th colspan=""9""><bdi>([^<]*)</bdi>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();

    private static void WriteTable(AdminPage page, string name, params string[] levels)
    {
        Directory.CreateDirectory(page.Options.CeilingsDirectory);
        File.WriteAllText(Path.Combine(page.Options.CeilingsDirectory, name),
            $$"""{ "format": "bs3d-ceilings", "version": 1, "rulesVersion": 1, "hashLength": 16, "levels": [ {{string.Join(", ", levels)}} ] }""");
    }

    /// <summary>A table's row for <paramref name="file"/>, named after it, in <paramref name="block"/> when one is given.</summary>
    private static string Level(string file, string hash, string? block = null) =>
        $$"""{ "file": "{{file}}", "name": "{{file[..^5]}}", "hash": "{{hash}}", "rulesVersion": 1, "shots": 20, "ceiling": 40000, "minShots": 2{{(block == null ? "" : $", \"block\": \"{block}\"")}} }""";

    [Fact]
    public async Task Without_chapters_the_boards_come_in_play_order()
    {
        await using AdminPage page = await AdminPage.StartAsync();
        WriteTable(page, "BS3D-v9.9.9-ceilings.json", Level("Zed.json", "1111111111111111"), Level("Ann.json", "2222222222222222"), Level("Bob.json", "3333333333333333"));
        await page.LogInAsync();

        string html = await page.Client.GetStringAsync("/boards");

        Assert.Equal(["Zed", "Ann", "Bob"], Levels(html));
        Assert.Empty(Chapters(html));
        Assert.Contains("in play order (the tables name no chapters)", html);
    }

    [Fact]
    public async Task The_boards_come_chapter_by_chapter_where_a_table_names_them()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Ann"), 50, board: Unknown));
        // A newer development build's table names chapters, and the older release's, read after it (names sort so), does
        // not: Bob's older version stands with the newer one in its chapter, and the older table takes no chapter away
        WriteTable(page, "BS3D-v9.9.8-ceilings.json", Level("Bob.json", "4444444444444444"), Level("Zed.json", "1111111111111111"));
        WriteTable(page, "BS3D-dev-abc1234-ceilings.json", Level("Zed.json", "1111111111111111", "The Valley"), Level("Ann.json", "2222222222222222", "The Valley"),
            Level("Bob.json", "3333333333333333", "The Tower"), Level("Cid.json", "5555555555555555"));
        await page.LogInAsync();

        string html = await page.Client.GetStringAsync("/boards");

        Assert.Equal(["The Valley", "The Tower", "Without a chapter", "In no ceiling table"], Chapters(html));
        Assert.Equal(["Zed", "Ann", "Bob", "Bob", "Cid", "Unknown.json"], Levels(html));
        Assert.Contains("<bdi>The Tower</bdi> <span>2 boards</span>", html);
        Assert.Contains("chapter by chapter in play order", html);
    }

    [Fact]
    public async Task The_funnel_counts_each_level_once_with_its_versions_by_chapter_and_leaves_hidden_players_out()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            Guid ann = AdminPage.AddPlayer(store, c, now, "Ann");
            AdminPage.AddClear(store, c, now, ann, 100, board: new BoardKey("Zed.json", "1111111111111111", 1));
            AdminPage.AddClear(store, c, now, ann, 100, board: new BoardKey("Ann.json", "2222222222222222", 1), stars: 0);
            // Bob lost an older version of Zed and cleared the newer: he cleared the level
            Guid bob = AdminPage.AddPlayer(store, c, now, "Bob");
            AdminPage.AddClear(store, c, now, bob, 100, board: new BoardKey("Zed.json", "9999999999999999", 1), stars: 0);
            AdminPage.AddClear(store, c, now, bob, 100, board: new BoardKey("Zed.json", "1111111111111111", 1));
            // Cid lost Ann on two versions: one player who has not cleared it yet
            Guid cid = AdminPage.AddPlayer(store, c, now, "Cid");
            AdminPage.AddClear(store, c, now, cid, 100, board: new BoardKey("Ann.json", "2222222222222222", 1), stars: 0);
            AdminPage.AddClear(store, c, now, cid, 100, board: new BoardKey("Ann.json", "8888888888888888", 1), stars: 0);
            Guid hid = AdminPage.AddPlayer(store, c, now, "Hid");
            store.SetHidden(c, hid, true);
            AdminPage.AddClear(store, c, now, hid, 100, board: new BoardKey("Ann.json", "2222222222222222", 1));
        });
        // Cid comes first in play order but names no chapter: after every chapter, as in the list
        WriteTable(page, "BS3D-dev-abc1234-ceilings.json", Level("Cid.json", "5555555555555555"), Level("Zed.json", "1111111111111111", "The Valley"),
            Level("Ann.json", "2222222222222222", "The Valley"), Level("Bob.json", "3333333333333333", "The Tower"));
        await page.LogInAsync();

        AdminData data = new(page.Options);
        Assert.Equal([("Zed", "The Valley", 2, 0), ("Ann", "The Valley", 0, 2), ("Bob", "The Tower", 0, 0), ("Cid", null, 0, 0)],
            data.ReadFunnel(data.LoadCeilings()).Select(l => (l.Name, l.Chapter, l.Cleared, l.Unfinished)).ToList());

        string html = await page.Client.GetStringAsync("/boards");
        string chart = Between(html, "<h2>Players per level", "<h2>Stars per level");
        Assert.Contains("<svg class=\"chart wide\"", chart);
        Assert.Contains("<ol class=\"chapters\"><li><b>1</b> The Valley</li><li><b>2</b> The Tower</li><li><b>3</b> Without a chapter</li></ol>", chart);
        Assert.Equal(2, Regex.Matches(chart, "<line class=\"sep\"").Count);
        Assert.Contains("<title>Ann · Not cleared yet: 2</title>", chart);
    }

    [Fact]
    public async Task Stars_shots_and_time_per_level_come_from_each_visible_player_s_best_clear_of_any_version()
    {
        BoardKey zedOld = new("Zed.json", "9999999999999999", 1), zed = new("Zed.json", "1111111111111111", 1);
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            // Ann's best is on the newer version: 4 stars, 8 shots, 40 s; her older, lower clear does not count
            Guid ann = AdminPage.AddPlayer(store, c, now, "Ann");
            AdminPage.AddClear(store, c, now, ann, 200, board: zedOld, stars: 2, shots: 12, seconds: 50);
            AdminPage.AddClear(store, c, now, ann, 300, board: zed, stars: 4, shots: 8, seconds: 40);
            AdminPage.AddClear(store, c, now, ann, 250, board: zed, stars: 3, shots: 9, seconds: 35);         // later, but lower
            Guid bob = AdminPage.AddPlayer(store, c, now, "Bob");
            AdminPage.AddClear(store, c, now, bob, 100, board: zed, stars: 1, shots: 15, seconds: 90);
            Guid cid = AdminPage.AddPlayer(store, c, now, "Cid");
            AdminPage.AddClear(store, c, now, cid, 150, board: zed, stars: 3, shots: 10, seconds: 30);
            AdminPage.AddClear(store, c, now, cid, 900, board: zed, stars: 0, shots: 20, seconds: 300);     // a loss, however high
            Guid hid = AdminPage.AddPlayer(store, c, now, "Hid");
            store.SetHidden(c, hid, true);
            AdminPage.AddClear(store, c, now, hid, 999, board: zed, stars: 1, shots: 20, seconds: 500);
            // Ann.json: two clears, an even count
            AdminPage.AddClear(store, c, now, ann, 100, board: new BoardKey("Ann.json", "2222222222222222", 1), stars: 2, shots: 6, seconds: 20);
            AdminPage.AddClear(store, c, now, bob, 100, board: new BoardKey("Ann.json", "2222222222222222", 1), stars: 2, shots: 9, seconds: 25);
        });
        WriteTable(page, "BS3D-v9.9.9-ceilings.json", Level("Zed.json", "1111111111111111"), Level("Ann.json", "2222222222222222"),
            Level("Bob.json", "3333333333333333"));
        await page.LogInAsync();

        AdminData data = new(page.Options);
        List<AdminData.FunnelLevel> levels = data.ReadFunnel(data.LoadCeilings()).ToList();

        AdminData.FunnelLevel z = levels.Single(l => l.Name == "Zed");
        Assert.Equal((3, 10.0, 40.0, 20), (z.Cleared, z.MedianShots, z.MedianSeconds, z.Budget));
        Assert.Equal([1, 0, 1, 1], z.Stars);
        AdminData.FunnelLevel a = levels.Single(l => l.Name == "Ann");
        Assert.Equal((7.5, 22.5), (a.MedianShots, a.MedianSeconds));
        Assert.Equal([0, 2, 0, 0], a.Stars);
        AdminData.FunnelLevel b = levels.Single(l => l.Name == "Bob");
        Assert.Equal(((double?)null, (double?)null), (b.MedianShots, b.MedianSeconds));
        Assert.Equal([0, 0, 0, 0], b.Stars);

        string html = await page.Client.GetStringAsync("/boards");
        string stars = Between(html, "<h2>Stars per level", "<h2>Shots per level");
        Assert.Contains("<title>Zed · 4 stars: 33.33</title>", stars);
        Assert.Contains("<title>Ann · 2 stars: 100</title>", stars);
        Assert.DoesNotContain("<title>Bob ·", stars);
        Assert.Contains("<tr><td>Bob</td><td class=\"n\">–</td><td class=\"n\">–</td><td class=\"n\">–</td><td class=\"n\">–</td></tr>", stars);
        string shots = Between(html, "<h2>Shots per level", "<h2>Time per level");
        Assert.Contains("<title>Zed · Median shots: 10</title>", shots);
        Assert.Contains("<title>Zed · Left of the budget: 10</title>", shots);
        Assert.Contains("<title>Ann · Left of the budget: 12.5</title>", shots);
        Assert.Contains("<title>Zed · Median seconds: 40</title>", Between(html, "<h2>Time per level", "<h2>Boards</h2>"));
        Assert.DoesNotContain("NaN", html);
    }

    [Fact]
    public async Task A_players_progress_counts_a_level_once_and_charts_their_best_against_the_ceiling()
    {
        BoardKey zedOld = new("Zed.json", "9999999999999999", 1), zed = new("Zed.json", "1111111111111111", 1);
        Guid ann = Guid.Empty, bob = Guid.Empty;
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            ann = AdminPage.AddPlayer(store, c, now, "Ann");
            AdminPage.AddClear(store, c, new DateTimeOffset(2026, 9, 10, 23, 30, 0, TimeSpan.Zero), ann, 10_000, board: zedOld);
            // The newer version of a level she has cleared: no new level, and the better share is the level's
            AdminPage.AddClear(store, c, new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero), ann, 30_000, board: zed);
            // A loss higher than her clear after it: a loss is no best clear
            AdminPage.AddClear(store, c, new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero), ann, 9_000, board: new BoardKey("Ann.json", "2222222222222222", 1), stars: 0);
            AdminPage.AddClear(store, c, new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero), ann, 4_000, board: new BoardKey("Ann.json", "2222222222222222", 1));
            AdminPage.AddClear(store, c, new DateTimeOffset(2026, 9, 14, 9, 30, 0, TimeSpan.Zero), ann, 700, board: Unknown);
            // A worse clear later: the best stays the best
            AdminPage.AddClear(store, c, new DateTimeOffset(2026, 9, 15, 7, 0, 0, TimeSpan.Zero), ann, 8_000, board: zed);
            bob = AdminPage.AddPlayer(store, c, now, "Bob");
        });
        WriteTable(page, "BS3D-v9.9.9-ceilings.json", Level("Zed.json", "1111111111111111"), Level("Zed.json", "9999999999999999"),
            Level("Ann.json", "2222222222222222"));
        await page.LogInAsync();

        AdminData data = new(page.Options);
        AdminData.PlayerProgress p = data.ReadProgress(ann, data.LoadCeilings())!;

        Assert.Equal(["2026-09-10", "2026-09-11", "2026-09-12", "2026-09-13", "2026-09-14", "2026-09-15"], p.Days);
        Assert.Equal([1, 1, 1, 1, 3, 3], p.LevelsCleared);
        Assert.Equal([1, 0, 1, 0, 2, 1], p.Clears);
        Assert.Equal([0, 0, 1, 0, 0, 0], p.Unfinished);
        // In play order, the unknown board left out: Zed's best is 30 000 of 40 000
        Assert.Equal([("Zed", (string?)null, 75.0), ("Ann", null, 10.0)], p.Best);
        Assert.Null(data.ReadProgress(bob, data.LoadCeilings()));

        string html = await page.Client.GetStringAsync($"/player?id={ann}");
        Assert.Contains("<h2>Levels cleared, in all</h2>", html);
        Assert.Contains("<title>Zed · Best clear: 75</title>", html);
        Assert.DoesNotContain("<h2>Levels cleared, in all</h2>", await page.Client.GetStringAsync($"/player?id={bob}"));
    }

    [Fact]
    public async Task A_boards_scores_are_charted_in_tenths_of_its_ceiling_by_the_boards_rows()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Ann"), Api.Ceiling);          // the ceiling itself: the last tenth
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Bob"), Api.Ceiling / 10 - 1); // just under a tenth: the first
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Cid"), Api.Ceiling / 2, stars: 0);
            Guid dan = AdminPage.AddPlayer(store, c, now, "Dan");
            AdminPage.AddClear(store, c, now, dan, 100);
            AdminPage.AddClear(store, c, now, dan, 49_000, stars: 0);   // after his clear: not his row
            Guid hid = AdminPage.AddPlayer(store, c, now, "Hid");
            store.SetHidden(c, hid, true);
            AdminPage.AddClear(store, c, now, hid, 30_000);
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Eve"), 300, board: Unknown);
        });
        page.WriteCeilingTable();
        await page.LogInAsync();

        AdminData data = new(page.Options);
        AdminData.ScoreBins bins = data.ReadBoard(new BoardKey(Api.File, Api.Hash, Api.Rules), data.LoadCeilings())!.Scores!;
        Assert.Equal([2, 0, 0, 0, 0, 0, 0, 0, 0, 1], bins.Clears);
        Assert.Equal([0, 0, 0, 0, 0, 1, 0, 0, 0, 0], bins.Unfinished);
        Assert.Contains("<title>90–100 % · Clears: 1</title>", WebUtility.HtmlDecode(await page.Client.GetStringAsync($"/board?file={Api.File}&hash={Api.Hash}&rules={Api.Rules}")));

        // No ceiling, no tenths
        Assert.Null(data.ReadBoard(Unknown, data.LoadCeilings())!.Scores);
        Assert.DoesNotContain("Best scores against the ceiling", await page.Client.GetStringAsync($"/board?file={Unknown.File}&hash={Unknown.Hash}&rules=1"));
    }

    [Fact]
    public async Task The_boards_say_so_when_no_ceiling_table_was_read()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Ann"), 100));
        await page.LogInAsync();

        string without = await page.Client.GetStringAsync("/boards");
        page.WriteCeilingTable();
        string with = await page.Client.GetStringAsync("/boards");

        Assert.Contains("No ceiling table in ", without);
        Assert.DoesNotContain("No ceiling table", with);
    }

    [Fact]
    public async Task A_board_ranks_as_the_game_sees_it_with_the_hidden_players_apart()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Ann"), 1000);
            AdminPage.AddClear(store, c, now.AddMinutes(1), AdminPage.AddPlayer(store, c, now, "Cid"), 1000);   // a tie: Ann was earlier
            Guid bob = AdminPage.AddPlayer(store, c, LastMonth, "Bob");
            AdminPage.AddClear(store, c, LastMonth, bob, 5000);                                                 // last month only
            AdminPage.AddClear(store, c, now, bob, 10);
            Guid hid = AdminPage.AddPlayer(store, c, now, "Hid");
            store.SetHidden(c, hid, true);
            AdminPage.AddClear(store, c, now, hid, 99999);
        });
        page.WriteCeilingTable();
        await page.LogInAsync();

        string html = await page.Client.GetStringAsync($"/board?file={Api.File}&hash={Api.Hash}&rules={Api.Rules}");

        Assert.Equal(["Ann", "Cid", "Bob"], Names(Between(html, "<h2>2026-09", "<h2>All time")));
        Assert.Equal(["Bob", "Ann", "Cid"], Names(Between(html, "<h2>All time", "<h2>Hidden")));
        Assert.Contains("<bdi>Hid</bdi></a></td><td class=\"n\">99,999</td>", Between(html, "<h2>Hidden", "</table>"));
        Assert.DoesNotContain("<bdi>Ann", Between(html, "<h2>Hidden", "</table>"));
        Assert.DoesNotContain("Hid", Between(html, "<h2>2026-09", "<h2>Hidden"));
    }

    [Fact]
    public async Task Unfinished_attempts_are_marked_and_the_players_counted_as_the_game_counts_them()
    {
        Guid ann = Guid.Empty;
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            ann = AdminPage.AddPlayer(store, c, LastMonth, "Ann");
            AdminPage.AddClear(store, c, LastMonth, ann, 300);
            AdminPage.AddClear(store, c, now, ann, 900, stars: 0);       // a loss after her clear: on no board this month
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Bob"), 47_500, stars: 0);   // never cleared: his loss is his row
            Guid hid = AdminPage.AddPlayer(store, c, now, "Hid");
            store.SetHidden(c, hid, true);
            AdminPage.AddClear(store, c, now, hid, 40_000, stars: 0);
            AdminPage.AddClear(store, c, now, hid, 200, stars: 1);
        });
        page.WriteCeilingTable();
        await page.LogInAsync();

        // Players this month: Bob; all time: Ann and Bob. Every clear (Ann's, Hid's) and every unfinished attempt (3) counted
        Assert.Matches(@">One</a></td>\s*<td><code>One\.json#0123456789abcdef r1</code></td><td class=""n"">50,000</td>\s*<td class=""n"">30</td><td class=""n"">1</td><td class=""n"">2</td><td class=""n"">2</td><td class=""n"">3</td>",
            await page.Client.GetStringAsync("/boards"));

        string board = await page.Client.GetStringAsync($"/board?file={Api.File}&hash={Api.Hash}&rules={Api.Rules}");
        Assert.Equal(["Bob"], Names(Between(board, "<h2>2026-09", "<h2>All time")));
        Assert.Equal(["Ann", "Bob"], Names(Between(board, "<h2>All time", "<h2>Hidden")));
        Assert.Contains("<td class=\"n\">47,500</td><td class=\"n\"><span class=\"chip muted\">unfinished</span></td>", Between(board, "<h2>All time", "<h2>Hidden"));
        // The hidden player's best is their clear, by the boards' rule, not their higher loss
        Assert.Contains("<bdi>Hid</bdi></a></td><td class=\"n\">200</td><td class=\"n\">1</td><td class=\"n\">1</td><td class=\"n\">1</td>", Between(board, "<h2>Hidden", "</table>"));

        string player = await page.Client.GetStringAsync($"/player?id={ann}");
        Assert.Matches(@"<td class=""n"">300</td><td class=""n"">3</td>\s*<td>not on it \(1\)</td><td>#1 of 2</td>", player);
        Assert.Contains("<td class=\"n\">900</td><td class=\"n\"><span class=\"chip muted\">unfinished</span></td>", Between(player, "<h2>Submissions", "</table>"));

        Assert.Contains("One.json: 900, unfinished", await page.Client.GetStringAsync("/live"));
    }

    [Fact]
    public async Task The_players_show_their_clears_addresses_and_who_shares_one_never_the_address()
    {
        Guid ann = Guid.Empty, bob = Guid.Empty;
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            ann = AdminPage.AddPlayer(store, c, now, "Ann");
            AdminPage.AddClear(store, c, now, ann, 100, "ADDRESS1aaaaaaaa");
            AdminPage.AddClear(store, c, now, ann, 200, "ADDRESS2bbbbbbbb");
            bob = AdminPage.AddPlayer(store, c, now, "Bob");
            AdminPage.AddClear(store, c, now, bob, 300, "ADDRESS1aaaaaaaa");
            AdminPage.AddPlayer(store, c, now, "Cid");
        });
        await page.LogInAsync();

        string html = await page.Client.GetStringAsync("/players");

        string annRow = RowOf(html, "Ann"), bobRow = RowOf(html, "Bob"), cidRow = RowOf(html, "Cid");
        Assert.Equal(2, Regex.Matches(annRow, @"<td class=""n"">2</td>").Count);         // two clears, two addresses
        Assert.EndsWith($"<a href=\"/player?id={bob}\"><bdi>Bob</bdi></a></td></tr>", annRow);
        Assert.EndsWith($"<a href=\"/player?id={ann}\"><bdi>Ann</bdi></a></td></tr>", bobRow);
        Assert.EndsWith("<td></td></tr>", cidRow);
        Assert.Single(Regex.Matches(annRow, $"/player\\?id={ann}"));                   // her own name, not among those she shares with
        Assert.DoesNotContain("ADDRESS", html);
    }

    [Fact]
    public async Task A_player_letters_their_addresses_and_shows_where_they_stand()
    {
        Guid ann = Guid.Empty, hid = Guid.Empty;
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            ann = AdminPage.AddPlayer(store, c, now, "Ann");
            AdminPage.AddClear(store, c, now, ann, 100, "ADDRESS1aaaaaaaa");
            AdminPage.AddClear(store, c, now.AddMinutes(1), ann, 200, "ADDRESS2bbbbbbbb");
            AdminPage.AddClear(store, c, now.AddMinutes(2), ann, 150, "ADDRESS1aaaaaaaa");
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Bob"), 500);
            hid = AdminPage.AddPlayer(store, c, now, "Hid");
            store.SetHidden(c, hid, true);
            AdminPage.AddClear(store, c, now, hid, 900);
        });
        await page.LogInAsync();

        string html = await page.Client.GetStringAsync($"/player?id={ann}");

        // Newest first: the third submission came from the first address again
        Assert.Equal(["A", "B", "A"], Regex.Matches(Between(html, "<h2>Submissions", "</table>"), @"<td>([A-Z])</td></tr>").Select(m => m.Groups[1].Value).Reverse().ToList());
        Assert.Contains("<td>#2 of 2</td><td>#2 of 2</td>", html);
        Assert.DoesNotContain("ADDRESS", html);
        Assert.Contains("<td>hidden</td><td>hidden</td>", await page.Client.GetStringAsync($"/player?id={hid}"));
    }

    [Fact]
    public async Task A_name_mixing_scripts_is_flagged()
    {
        const string lookalike = "K\u0430rel";   // the second letter is Cyrillic
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            AdminPage.AddPlayer(store, c, now, "Karel");
            AdminPage.AddPlayer(store, c, now, lookalike);
            AdminPage.AddPlayer(store, c, now, "Žluťoučký kůň");
        });
        await page.LogInAsync();

        string html = await page.Client.GetStringAsync("/players");

        Assert.Single(Regex.Matches(html, "mixed scripts"));
        Assert.Contains("mixed scripts", RowOf(html, lookalike));
        Assert.True(AdminPages.MixesScripts(lookalike));
        Assert.False(AdminPages.MixesScripts("Karel"));
        Assert.False(AdminPages.MixesScripts("Žluťoučký kůň"));
    }

    [Fact]
    public async Task A_sort_key_comes_from_a_fixed_set_and_nothing_else_reaches_the_query()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Ann"), 100);
            Guid zed = AdminPage.AddPlayer(store, c, now, "Zed");
            AdminPage.AddClear(store, c, now, zed, 100);
            AdminPage.AddClear(store, c, now, zed, 200);
        });
        await page.LogInAsync();

        string byClears = await page.Client.GetStringAsync("/players?sort=clears");
        HttpResponseMessage injected = await page.Client.GetAsync("/players?sort=" + Uri.EscapeDataString("name; DROP TABLE players"));

        Assert.True(byClears.IndexOf("<bdi>Zed", StringComparison.Ordinal) < byClears.IndexOf("<bdi>Ann", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, injected.StatusCode);
        string byName = await injected.Content.ReadAsStringAsync();
        Assert.True(byName.IndexOf("<bdi>Ann", StringComparison.Ordinal) < byName.IndexOf("<bdi>Zed", StringComparison.Ordinal));
        Assert.Equal(2, page.Store.Export(page.Store.Open()).Select(r => r["player_id"]).Distinct().Count());
    }

    [Theory]
    [InlineData("/player?id=not-a-guid")]
    [InlineData("/player?id=00000000-0000-0000-0000-000000000001")]
    [InlineData("/board?file=Nowhere.json&hash=0000000000000000&rules=1")]
    [InlineData("/board?file=One.json")]
    public async Task What_is_not_there_is_not_found(string path)
    {
        await using AdminPage page = await AdminPage.StartAsync();
        page.WriteCeilingTable();
        await page.LogInAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await page.Client.GetAsync(path)).StatusCode);
    }
}
