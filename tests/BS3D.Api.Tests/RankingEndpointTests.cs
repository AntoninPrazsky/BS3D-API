using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace BS3D.Api.Tests;

/// <summary>
/// <c>GET /v1/ranking</c> (#11, the game's Top Players, BS3D#827). The rule itself is <see cref="RankingTests"/>'; these
/// are what only the route can get wrong: the page, the count and the asker's own row from one ranking, the period read
/// as a board reads it, the asking build's view (#17), and the boards no table names left out on the service's setting.
/// </summary>
public sealed class RankingEndpointTests
{
    private const string Two = "Two.json";
    private const string TwoHash = "fedcba9876543210";

    private static async Task<RankingPage> Ranking(Api api, string query, string? agent = null)
    {
        HttpClient client = api.CreateClient();
        if (agent != null) client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", agent);
        return (await client.GetFromJsonAsync<RankingPage>("/v1/ranking?" + query))!;
    }

    [Fact]
    public async Task Each_level_s_best_clear_is_added_up_and_ranked_by_the_total()
    {
        using Api api = TwoBoards();
        var ann = Api.NewPlayer();
        var bob = Api.NewPlayer();
        await api.Accepted(ann, Api.Clear(ann.Id, 300, name: "Ann", stars: 2));
        await api.Accepted(ann, Api.Clear(ann.Id, 500, name: "Ann", stars: 4));
        await api.Accepted(ann, Api.Clear(ann.Id, 200, name: "Ann", file: Two, hash: TwoHash));
        await api.Accepted(bob, Api.Clear(bob.Id, 600, name: "Bob"));

        RankingPage page = await Ranking(api, "rules=1&period=all");

        Assert.Equal(("all", (string?)null, 1, 2), (page.Period, page.Month, page.Rules, page.Total));
        Assert.Equal([new RankingEntry(1, "Ann", 2, 7, 700, api.Clock.GetUtcNow()), new RankingEntry(2, "Bob", 1, 3, 600, api.Clock.GetUtcNow())],
            page.Entries);
        Assert.Null(page.Me);
    }

    [Fact]
    public async Task A_page_is_limited_and_offset_and_the_askers_row_comes_wherever_it_falls()
    {
        using Api api = new();
        List<(Guid Id, string Token)> players = new();
        for (int i = 1; i <= 5; i++)
        {
            var player = Api.NewPlayer();
            players.Add(player);
            await api.Accepted(player, Api.Clear(player.Id, i * 100, name: $"Player{i}"));
        }

        // Player1 is last, off the page of the second and third
        RankingPage page = await Ranking(api, $"rules=1&period=all&limit=2&offset=1&player={players[0].Id}");

        Assert.Equal(5, page.Total);
        Assert.Equal([(2, "Player4"), (3, "Player3")], page.Entries.Select(e => (e.Rank, e.Name)));
        Assert.Equal(new RankingMe(5, 1, 3, 100), page.Me);

        // On the page as well: the same row both times
        RankingPage first = await Ranking(api, $"rules=1&period=all&limit=2&player={players[4].Id}");
        Assert.Equal(new RankingMe(1, 1, 3, 500), first.Me);
        Assert.Equal(["Player5", "Player4"], first.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_and_still_says_how_many_are_ranked()
    {
        using Api api = new();
        var ann = Api.NewPlayer();
        await api.Accepted(ann, Api.Clear(ann.Id, 100, name: "Ann"));

        RankingPage page = await Ranking(api, "rules=1&period=all&offset=20");
        RankingPage farthest = await Ranking(api, $"rules=1&period=all&offset={int.MaxValue}&player={ann.Id}");

        Assert.Empty(page.Entries);
        Assert.Equal(1, page.Total);
        Assert.Empty(farthest.Entries);
        Assert.Equal((1, new RankingMe(1, 1, 3, 100)), (farthest.Total, farthest.Me));
    }

    [Fact]
    public async Task The_asker_is_null_when_unranked_or_unknown()
    {
        using Api api = new();
        var ann = Api.NewPlayer();
        var stuck = Api.NewPlayer();
        await api.Accepted(ann, Api.Clear(ann.Id, 100, name: "Ann"));
        await api.Accepted(stuck, Api.Clear(stuck.Id, 9_000, name: "Stuck", stars: 0));

        Assert.Null((await Ranking(api, $"rules=1&period=all&player={stuck.Id}")).Me);
        Assert.Null((await Ranking(api, $"rules=1&period=all&player={Guid.NewGuid()}")).Me);
        Assert.Equal(1, (await Ranking(api, $"rules=1&period=all&player={stuck.Id}")).Total);
    }

    [Fact]
    public async Task The_month_is_this_UTC_month_by_default_and_counts_only_its_own_clears()
    {
        using Api api = new();
        var ann = Api.NewPlayer();
        var bob = Api.NewPlayer();
        await api.Accepted(ann, Api.Clear(ann.Id, 900, name: "Ann"));
        api.Clock.Advance(TimeSpan.FromDays(20));
        await api.Accepted(ann, Api.Clear(ann.Id, 100, name: "Ann"));
        await api.Accepted(bob, Api.Clear(bob.Id, 300, name: "Bob"));

        RankingPage october = await Ranking(api, "rules=1");
        Assert.Equal(("month", "2026-10"), (october.Period, october.Month));
        Assert.Equal([("Bob", 300L), ("Ann", 100L)], october.Entries.Select(e => (e.Name, e.Score)));

        RankingPage september = await Ranking(api, "rules=1&period=month&month=2026-09");
        Assert.Equal([("Ann", 900L)], september.Entries.Select(e => (e.Name, e.Score)));

        Assert.Equal([("Ann", 900L), ("Bob", 300L)], (await Ranking(api, "rules=1&period=all")).Entries.Select(e => (e.Name, e.Score)));
    }

    [Fact]
    public async Task A_release_ranks_no_clear_a_dev_build_sent_and_a_dev_build_ranks_both()
    {
        using Api api = TwoBoards();
        var released = Api.NewPlayer();
        var developer = Api.NewPlayer();
        await api.Accepted(released, Api.Clear(released.Id, 100, name: "Released", version: "v0.5.0"));
        await api.Accepted(developer, Api.Clear(developer.Id, 200, name: "Developer", version: "v0.5.0", file: Two, hash: TwoHash));
        await api.Accepted(developer, Api.Clear(developer.Id, 900, name: "Developer", version: "dev-1234abc"));

        RankingPage asRelease = await Ranking(api, $"rules=1&period=all&player={developer.Id}", "BS3D/v0.5.0");
        Assert.Equal([("Developer", 200L), ("Released", 100L)], asRelease.Entries.Select(e => (e.Name, e.Score)));
        Assert.Equal(new RankingMe(1, 1, 3, 200), asRelease.Me);

        RankingPage asDev = await Ranking(api, $"rules=1&period=all&player={developer.Id}", "BS3D/dev-1234abc");
        Assert.Equal([("Developer", 1100L), ("Released", 100L)], asDev.Entries.Select(e => (e.Name, e.Score)));

        // No agent, or a browser's: a release's view
        Assert.Equal(asRelease.Entries, (await Ranking(api, "rules=1&period=all")).Entries);
        Assert.Equal(asRelease.Entries, (await Ranking(api, "rules=1&period=all", "Mozilla/5.0 (X11; Linux aarch64)")).Entries);
    }

    [Fact]
    public async Task A_clear_on_a_board_no_table_names_counts_only_when_boards_need_not_be_known()
    {
        BoardKey unknown = new("Unknown.json", "0000000000000000", Api.Rules);
        foreach (bool require in new[] { true, false })
        {
            using Api api = new(new() { ["Scores:RequireKnownBoard"] = require ? "true" : "false" });
            var ann = Api.NewPlayer();
            await api.Accepted(ann, Api.Clear(ann.Id, 100, name: "Ann"));
            using (SqliteConnection c = api.Store.Open())
            {
                Guid ghost = AdminPage.AddPlayer(api.Store, c, api.Clock.GetUtcNow(), "Ghost");
                AdminPage.AddClear(api.Store, c, api.Clock.GetUtcNow(), ghost, 500, board: unknown);
                AdminPage.AddClear(api.Store, c, api.Clock.GetUtcNow(), ann.Id, 50, board: unknown);
            }

            RankingPage page = await Ranking(api, "rules=1&period=all");

            (string, long)[] expected = require ? [("Ann", 100L)] : [("Ghost", 500L), ("Ann", 150L)];
            Assert.Equal(expected, page.Entries.Select(e => (e.Name, e.Score)));
        }
    }

    [Theory]
    [InlineData("period=all")]
    [InlineData("rules=1&period=week")]
    [InlineData("rules=1&period=month&month=2026-13")]
    [InlineData("rules=1&month=September")]
    public async Task No_rules_a_period_that_is_neither_and_a_month_that_is_none_are_refused(string query)
    {
        using Api api = new();

        HttpResponseMessage response = await api.CreateClient().GetAsync("/v1/ranking?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Reasons.BadRequest, await Api.ReasonOf(response));
    }

    [Fact]
    public async Task Limit_is_clamped_to_one_to_a_hundred()
    {
        using Api api = new();
        for (int i = 1; i <= 3; i++)
        {
            var player = Api.NewPlayer();
            await api.Accepted(player, Api.Clear(player.Id, i * 100, name: $"Player{i}"));
        }

        Assert.Single((await Ranking(api, "rules=1&period=all&limit=0")).Entries);
        Assert.Equal(3, (await Ranking(api, "rules=1&period=all&limit=500")).Entries.Count);
    }

    /// <summary>The test service with a second known board beside <see cref="Api.File"/>.</summary>
    private static Api TwoBoards()
    {
        Api api = new();
        api.AddTable("BS3D-v9.9.8-ceilings.json", $$"""
            { "format": "bs3d-ceilings", "version": 1, "rulesVersion": 1, "hashLength": 16, "levels": [
              { "file": "{{Two}}", "name": "Two", "hash": "{{TwoHash}}", "rulesVersion": {{Api.Rules}}, "shots": {{Api.Budget}},
                "ceiling": {{Api.Ceiling}}, "minShots": {{Api.MinShots}} } ] }
            """);
        return api;
    }
}
