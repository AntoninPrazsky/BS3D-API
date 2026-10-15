using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace BS3D.Api.Tests;

/// <summary>
/// <c>POST /v1/stats</c> (BS3D#709): anonymous play counts added to one row per board, idempotent by the batch id, held to
/// bounds because anyone can send them, and kept with nothing that names a player, an address or a moment.
/// </summary>
public sealed class StatsTests
{
    public static StatsLevel Row(string file = Api.File, string hash = Api.Hash, int rules = Api.Rules, int starts = 3, int retries = 2,
        int clears = 1, int balls = 1, int line = 0, int abandons = 1, int skips = 0, int firstClears = 1, int shots = 45,
        double seconds = 312.5) =>
        new(file, hash, rules, starts, retries, clears, balls, line, abandons, skips, firstClears, shots, seconds);

    public static StatsBatch Batch(params StatsLevel?[] rows) => new(Guid.NewGuid(), rows);

    private static async Task<HttpResponseMessage> Post(Api api, StatsBatch batch, string? token = null) =>
        await api.ClientFor(token).PostAsJsonAsync("/v1/stats", batch);

    private static ScoreStore.PlayStatsRow? Totals(Api api, string file = Api.File)
    {
        using SqliteConnection c = api.Store.Open();
        return ScoreStore.PlayStats(c).SingleOrDefault(r => r.Board.File == file);
    }

    [Fact]
    public async Task A_batch_is_counted_and_a_resend_is_counted_once()
    {
        using Api api = new();
        StatsBatch batch = Batch(Row());

        HttpResponseMessage first = await Post(api, batch);
        HttpResponseMessage again = await Post(api, batch);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(new StatsAnswer(true, 1, 0, false), await first.Content.ReadFromJsonAsync<StatsAnswer>());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(new StatsAnswer(true, 0, 0, true), await again.Content.ReadFromJsonAsync<StatsAnswer>());

        ScoreStore.PlayStatsRow row = Totals(api)!;
        Assert.Equal((3L, 2L, 1L, 1L, 0L, 1L, 0L, 1L, 45L), (row.Starts, row.Retries, row.Clears, row.LostOutOfBalls, row.LostLine,
            row.Abandons, row.Skips, row.FirstClears, row.Shots));
        Assert.Equal(312.5, row.Seconds, 6);
    }

    [Fact]
    public async Task Batches_add_up_per_board()
    {
        using Api api = new(new() { ["Scores:RequireKnownBoard"] = "false" });

        await Post(api, Batch(Row(), Row(file: "Two.json", starts: 1, retries: 0, clears: 0, firstClears: 0)));
        await Post(api, Batch(Row(starts: 2, retries: 1, clears: 0, balls: 0, line: 2, abandons: 0, skips: 1, firstClears: 0,
            shots: 20, seconds: 87.5)));

        ScoreStore.PlayStatsRow one = Totals(api)!;
        Assert.Equal((5L, 3L, 1L, 1L, 2L, 1L, 1L, 65L), (one.Starts, one.Retries, one.Clears, one.LostOutOfBalls, one.LostLine, one.Abandons,
            one.Skips, one.Shots));
        Assert.Equal(400.0, one.Seconds, 6);
        Assert.Equal(1L, Totals(api, "Two.json")!.Starts);
    }

    [Fact]
    public async Task No_token_is_needed_and_one_sent_links_nothing()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        Assert.Equal(HttpStatusCode.Created, (await Post(api, Batch(Row()))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Batch(Row()), player.Token)).StatusCode);

        //A token is not even looked at: no player was made, nothing was submitted
        using SqliteConnection c = api.Store.Open();
        Assert.Null(api.Store.FindPlayer(c, player.Id));
        Assert.Empty(api.Store.Export(c));
        Assert.Equal(6L, Totals(api)!.Starts);
    }

    [Fact]
    public async Task A_row_on_no_known_board_is_left_out_and_the_rest_counted()
    {
        using Api api = new();

        HttpResponseMessage response = await Post(api, Batch(Row(), Row(hash: "ffffffffffffffff"), Row(rules: 2)));

        Assert.Equal(new StatsAnswer(true, 1, 2, false), await response.Content.ReadFromJsonAsync<StatsAnswer>());
        using SqliteConnection c = api.Store.Open();
        Assert.Single(ScoreStore.PlayStats(c));
    }

    [Fact]
    public async Task Where_boards_must_be_known_a_strange_row_on_no_board_is_left_out_not_refused()
    {
        using Api api = new();

        HttpResponseMessage response = await Post(api, Batch(Row(), Row(file: "One[2J.json"), Row(hash: "0123456789ABCDEF")));

        Assert.Equal(new StatsAnswer(true, 1, 2, false), await response.Content.ReadFromJsonAsync<StatsAnswer>());
    }

    [Fact]
    public async Task Where_boards_need_not_be_known_every_well_formed_row_counts()
    {
        using Api api = new(new() { ["Scores:RequireKnownBoard"] = "false" });

        HttpResponseMessage response = await Post(api, Batch(Row(), Row(file: "Test level.json", hash: "608c4bf895eec372")));

        Assert.Equal(new StatsAnswer(true, 2, 0, false), await response.Content.ReadFromJsonAsync<StatsAnswer>());
    }

    public static TheoryData<int, string, StatsBatch> Refused() => new()
    {
        { 400, Reasons.BadRequest, new StatsBatch(Guid.Empty, [Row()]) },
        { 400, Reasons.BadRequest, new StatsBatch(Guid.NewGuid(), []) },
        { 400, Reasons.BadRequest, new StatsBatch(Guid.NewGuid(), null) },
        { 400, Reasons.BadRequest, Batch((StatsLevel?)null) },
        { 400, Reasons.BadRequest, Batch(Row(file: "One\u001b[2J.json")) },
        { 400, Reasons.BadRequest, Batch(Row(file: "../" + Api.File)) },
        { 400, Reasons.BadRequest, Batch(Row(file: new string('a', 65))) },
        { 400, Reasons.BadRequest, Batch(Row(hash: "0123456789ABCDEF")) },
        { 400, Reasons.BadRequest, Batch(Row(hash: "")) },
        { 400, Reasons.BadRequest, Batch(Row(hash: new string('a', 65))) },
        { 400, Reasons.BadRequest, Batch(Row(), Row()) },
        { 422, Reasons.TooManyLevels, Batch(Enumerable.Range(0, 201).Select(i => (StatsLevel?)Row(file: $"L{i}.json")).ToArray()) },
        { 422, Reasons.BadCounts, Batch(Row(starts: -1, retries: 0)) },
        { 422, Reasons.BadCounts, Batch(Row(abandons: -1)) },
        { 422, Reasons.BadCounts, Batch(Row(shots: -1)) },
        { 422, Reasons.BadCounts, Batch(Row(retries: 4)) },
        { 422, Reasons.BadCounts, Batch(Row(clears: 0)) },
        { 422, Reasons.BadCounts, Batch(Row(seconds: -1)) },
        //One attempt can carry over from the batch before, and no more: three starts end four times at most
        { 422, Reasons.BadCounts, Batch(Row(clears: 2, balls: 2, line: 0, abandons: 1)) },
        { 422, Reasons.BadCounts, Batch(Row(skips: 5)) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task An_impossible_batch_is_refused_with_its_reason_and_adds_nothing(int status, string reason, StatsBatch batch)
    {
        //Where boards need not be known, so a row on no board is stored and its shape is what is checked
        using Api api = new(new() { ["Scores:RequireKnownBoard"] = "false" });

        HttpResponseMessage response = await Post(api, batch);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(reason, await Api.ReasonOf(response));
        using SqliteConnection c = api.Store.Open();
        Assert.Empty(ScoreStore.PlayStats(c));
    }

    public static TheoryData<string, StatsLevel> Implausible() => new()
    {
        { "more seconds than one batch of play", Row(seconds: 1801) },
        { "more starts than one batch holds", Row(starts: 101, retries: 0, seconds: 1800) },
        { "more starts than seconds of play", Row(starts: 20, retries: 0, clears: 1, balls: 0, abandons: 0, seconds: 14) },
        { "more shots than the attempts could fire", Row(shots: 401) },
    };

    [Theory]
    [MemberData(nameof(Implausible))]
    public async Task A_row_past_one_batch_of_play_is_left_out_and_the_rest_counted(string why, StatsLevel row)
    {
        using Api api = new(new() { ["Scores:RequireKnownBoard"] = "false" });

        HttpResponseMessage response = await Post(api, Batch(row, Row(file: "Two.json", starts: 1, retries: 0, clears: 0, firstClears: 0)));

        Assert.True(new StatsAnswer(true, 1, 1, false) == await response.Content.ReadFromJsonAsync<StatsAnswer>(), why);
        Assert.Null(Totals(api));
        Assert.Equal(1L, Totals(api, "Two.json")!.Starts);
    }

    [Fact]
    public async Task The_edge_of_one_batch_of_play_is_still_counted()
    {
        using Api api = new(new() { ["Scores:RequireKnownBoard"] = "false" });

        HttpResponseMessage response = await Post(api, Batch(Row(starts: 100, retries: 0, clears: 0, balls: 0, abandons: 0, firstClears: 0,
            shots: 10_100, seconds: 1800), Row(file: "Two.json", starts: 10, retries: 9, clears: 0, balls: 10, abandons: 0, firstClears: 0, seconds: 5)));

        Assert.Equal(new StatsAnswer(true, 2, 0, false), await response.Content.ReadFromJsonAsync<StatsAnswer>());
    }

    [Fact]
    public async Task A_batch_of_which_nothing_counts_adds_nothing_not_even_its_id()
    {
        using Api api = new();
        StatsBatch batch = Batch(Row(hash: "ffffffffffffffff"), Row(seconds: 5000));

        HttpResponseMessage response = await Post(api, batch);

        Assert.Equal(new StatsAnswer(true, 0, 2, false), await response.Content.ReadFromJsonAsync<StatsAnswer>());
        using SqliteConnection c = api.Store.Open();
        using SqliteCommand count = c.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM stats_batches";
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public async Task One_address_is_limited_by_the_minute()
    {
        using Api api = new(new() { ["Scores:StatsPerMinutePerAddress"] = "2" });

        Assert.Equal(HttpStatusCode.Created, (await Post(api, Batch(Row()))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Batch(Row()))).StatusCode);
        HttpResponseMessage third = await Post(api, Batch(Row()));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(Reasons.RateLimited, await Api.ReasonOf(third));
        Assert.True(third.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        api.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Batch(Row()))).StatusCode);
        Assert.Equal(9L, Totals(api)!.Starts);

        //The refusal names no address, not even by its hash: the line and the refusal log say "a network"
        string hash = new AddressHasher("test-salt").Hash(new Microsoft.AspNetCore.Http.DefaultHttpContext());
        string refused = Assert.Single(api.Log.Lines, line => line.Contains("/v1/stats", StringComparison.Ordinal));
        Assert.Contains("(a network)", refused);
        Assert.DoesNotContain(hash, refused);
    }

    [Fact]
    public async Task An_ipv6_host_is_limited_by_its_64_and_another_64_is_not()
    {
        using Api api = new(new() { ["Scores:StatsPerMinutePerAddress"] = "1" });

        api.ClientAddress = IPAddress.Parse("2001:db8:1:2::1");
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Batch(Row()))).StatusCode);
        api.ClientAddress = IPAddress.Parse("2001:db8:1:2::ffff");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(api, Batch(Row()))).StatusCode);
        api.ClientAddress = IPAddress.Parse("2001:db8:1:3::1");
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Batch(Row()))).StatusCode);
    }

    [Fact]
    public async Task Nothing_kept_names_a_player_an_address_or_a_moment()
    {
        using Api api = new();
        StatsBatch batch = Batch(Row());
        await Post(api, batch);

        using SqliteConnection c = api.Store.Open();
        Assert.Equal(["level_file", "level_hash", "rules_version", "starts", "retries", "clears", "lost_out_of_balls", "lost_line",
            "abandons", "skips", "first_clears", "shots", "seconds"], Columns(c, "play_stats"));
        Assert.Equal(["batch_id"], Columns(c, "stats_batches"));

        //And nothing about an accepted batch is logged, as nothing about an accepted score is
        Assert.DoesNotContain(api.Log.Lines, line => line.Contains("/v1/stats", StringComparison.Ordinal)
            || line.Contains(batch.BatchId.ToString(), StringComparison.Ordinal));
    }

    private static List<string> Columns(SqliteConnection c, string table)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<string> names = new();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    [Fact]
    public async Task The_admin_report_ranks_by_the_rate_asked_for_with_the_counts_beside_it()
    {
        using Api api = new(new() { ["Scores:RequireKnownBoard"] = "false" });
        await Post(api, Batch(
            Row(file: "Easy.json", starts: 10, retries: 0, clears: 9, balls: 1, line: 0, abandons: 0, skips: 0, firstClears: 1),
            Row(file: "Hard.json", starts: 8, retries: 6, clears: 1, balls: 4, line: 2, abandons: 1, skips: 2, firstClears: 1)));

        string Run(params string[] args)
        {
            StringWriter output = new();
            Assert.Equal(0, AdminCli.Run(["stats", .. args], api.Store, new ScoresOptions { CeilingsDirectory = Path.Combine(Path.GetTempPath(), "none") }, output));
            return output.ToString();
        }

        string[] byLoss = Run().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("play counts by loss: 2 board(s), 18 start(s)", byLoss[0]);
        Assert.StartsWith("Hard.json", byLoss[2]);
        Assert.Contains("6 of 7 (86%)", byLoss[2]);       //lost of the attempts that ended
        Assert.Contains("6 of 8 (75%)", byLoss[2]);       //retries of the starts
        Assert.Contains("2 of 8 (25%)", byLoss[2]);       //skips of the starts
        Assert.StartsWith("Easy.json", byLoss[3]);

        string[] byRetries = Run("--sort", "retries").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Hard.json", byRetries[2]);

        StringWriter usage = new();
        Assert.Equal(2, AdminCli.Run(["stats", "--sort", "starts; DROP TABLE play_stats"], api.Store, new ScoresOptions(), usage));
        Assert.StartsWith("usage:", usage.ToString());
    }

    [Fact]
    public async Task The_play_tab_lists_the_boards_by_the_order_asked_with_their_names_and_counts()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            store.AddStats(c, Guid.NewGuid(), [
                Row(starts: 10, retries: 0, clears: 9, balls: 1, line: 0, abandons: 0, skips: 0, firstClears: 1),
                Row(file: "Two.json", hash: "fedcba9876543210", starts: 8, retries: 6, clears: 1, balls: 4, line: 2, abandons: 1, skips: 2,
                    firstClears: 1),
                Row(file: "Gone.json", hash: "0000000000000000", starts: 1, retries: 0, clears: 0, balls: 1, line: 0, abandons: 0, skips: 0,
                    firstClears: 0)]);
        });
        page.WriteCeilingTable();
        await page.LogInAsync();

        string byLoss = await page.Client.GetStringAsync("/stats");
        Assert.True(byLoss.IndexOf("Gone.json", StringComparison.Ordinal) < byLoss.IndexOf("2. Two", StringComparison.Ordinal)
            && byLoss.IndexOf("2. Two", StringComparison.Ordinal) < byLoss.IndexOf("1. One", StringComparison.Ordinal),
            "by loss rate: Gone (1 of 1), Two (6 of 7), One (1 of 10)");
        Assert.Contains("6 of 7 (86%)", byLoss);
        Assert.Contains("in no ceiling table", byLoss);
        Assert.Contains("<span class=\"sorted\">by loss rate</span>", byLoss);
        Assert.Contains("aria-current=\"page\">Play</a>", byLoss);

        string inOrder = await page.Client.GetStringAsync("/stats?sort=order");
        Assert.True(inOrder.IndexOf("1. One", StringComparison.Ordinal) < inOrder.IndexOf("2. Two", StringComparison.Ordinal)
            && inOrder.IndexOf("2. Two", StringComparison.Ordinal) < inOrder.IndexOf("Gone.json", StringComparison.Ordinal));

        //An order the page does not offer reads as the first, never as SQL
        string hostile = await page.Client.GetStringAsync("/stats?sort=starts%3B%20DROP%20TABLE%20play_stats");
        Assert.Contains("<span class=\"sorted\">by loss rate</span>", hostile);
    }

    [Fact]
    public async Task The_play_tab_needs_the_session_and_reads_no_table_before_schema_4()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            using SqliteCommand drop = c.CreateCommand();
            drop.CommandText = "DROP TABLE play_stats; DROP TABLE stats_batches; PRAGMA user_version = 3;";
            drop.ExecuteNonQuery();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await page.Client.GetAsync("/stats")).StatusCode);

        await page.LogInAsync();
        HttpResponseMessage old = await page.Client.GetAsync("/stats");
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Contains("not at schema 4", await old.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Count_does_not_count_the_play_counts()
    {
        using Api api = new();
        StringWriter before = new();
        AdminCli.Run(["count"], api.Store, new ScoresOptions(), before);

        await Post(api, Batch(Row()));
        StringWriter after = new();
        AdminCli.Run(["count"], api.Store, new ScoresOptions(), after);

        Assert.Equal("players 0 submissions 0", before.ToString().Trim());
        Assert.Equal(before.ToString(), after.ToString());
    }
}
