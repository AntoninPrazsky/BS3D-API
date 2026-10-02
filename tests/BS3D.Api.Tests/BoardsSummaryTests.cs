using System.Net;
using System.Net.Http.Json;

namespace BS3D.Api.Tests;

/// <summary>
/// <c>GET /v1/boards</c> (#7): every board's #1 and the asking player's place, in one answer for the game's High Scores
/// screen. It ranks by the boards' own rule, partitioned by board, so the cases are the ranking cases again — each board
/// on its own, the month, a hidden player, unfinished attempts (#8) — plus what only a summary can get wrong: two boards
/// mixed into one ranking, a player's place on a board they never cleared, and a board no ceiling table names.
/// </summary>
public sealed class BoardsSummaryTests
{
    private const string SecondFile = "Two.json";
    private const string SecondHash = "fedcba9876543210";

    [Fact]
    public async Task Each_board_has_its_own_first_and_count_and_the_askers_own_place()
    {
        using Api api = TwoBoards();
        var leader = Api.NewPlayer();
        var player = Api.NewPlayer();

        await api.Accepted(leader, Api.Clear(leader.Id, 900, name: "Leader"));
        await api.Accepted(player, Api.Clear(player.Id, 700, name: "Player"));
        await api.Accepted(player, Api.Clear(player.Id, 400, name: "Player", file: SecondFile, hash: SecondHash));

        BoardsSummary summary = await Summary(api, "all", player.Id);

        Assert.Equal("all", summary.Period);
        Assert.Null(summary.Month);
        Assert.Equal(2, summary.Boards.Count);

        BoardSummary one = summary.Boards.Single(b => b.File == Api.File);
        Assert.Equal((Api.Hash, Api.Rules, 2), (one.Hash, one.Rules, one.Total));
        Assert.Equal(new BoardTop("Leader", 900, 3), one.Top);
        Assert.Equal(new BoardMe(2, 700, 3), one.Me);

        BoardSummary two = summary.Boards.Single(b => b.File == SecondFile);
        Assert.Equal(1, two.Total);
        Assert.Equal(new BoardTop("Player", 400, 3), two.Top);
        Assert.Equal(new BoardMe(1, 400, 3), two.Me);
    }

    [Fact]
    public async Task A_board_the_asker_never_cleared_has_no_place_and_no_player_means_no_place_anywhere()
    {
        using Api api = TwoBoards();
        var other = Api.NewPlayer();
        var player = Api.NewPlayer();

        await api.Accepted(other, Api.Clear(other.Id, 900, name: "Other", file: SecondFile, hash: SecondHash));
        await api.Accepted(player, Api.Clear(player.Id, 500, name: "Player"));

        BoardsSummary asked = await Summary(api, "all", player.Id);
        Assert.Null(asked.Boards.Single(b => b.File == SecondFile).Me);

        BoardsSummary anonymous = await Summary(api, "all", null);
        Assert.All(anonymous.Boards, b => Assert.Null(b.Me));
    }

    [Fact]
    public async Task The_month_summary_ignores_last_months_better_score()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 900));
        api.Clock.Advance(TimeSpan.FromDays(20));  //into October
        await api.Accepted(player, Api.Clear(player.Id, 500));

        BoardsSummary month = await Summary(api, "month", player.Id);
        Assert.Equal("2026-10", month.Month);
        Assert.Equal(500, Assert.Single(month.Boards).Top.Score);

        Assert.Equal(900, Assert.Single((await Summary(api, "all", player.Id)).Boards).Top.Score);
        Assert.Equal(900, Assert.Single((await Summary(api, "month", player.Id, month: "2026-09")).Boards).Top.Score);
    }

    [Fact]
    public async Task A_hidden_player_is_no_boards_first_and_in_no_count()
    {
        using Api api = new();
        var cheat = Api.NewPlayer();
        var honest = Api.NewPlayer();

        await api.Accepted(cheat, Api.Clear(cheat.Id, 40_000, name: "Cheat"));
        await api.Accepted(honest, Api.Clear(honest.Id, 900, name: "Honest"));

        using (var c = api.Store.Open()) api.Store.SetHidden(c, cheat.Id, true);

        BoardSummary board = Assert.Single((await Summary(api, "all", honest.Id)).Boards);
        Assert.Equal(1, board.Total);
        Assert.Equal("Honest", board.Top.Name);
        Assert.Equal(new BoardMe(1, 900, 3), board.Me);
    }

    [Fact]
    public async Task A_board_no_ceiling_table_names_is_not_listed()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 900, name: "Player"));

        //Straight into the store, as a level hash an older table named would have arrived: the POST refuses it now
        using (var c = api.Store.Open())
            api.Store.Insert(c, new ScoreStore.NewSubmission(Guid.NewGuid(), player.Id, new BoardKey("Old.json", "1111111111111111", 1),
                300, 2, 10, 60, "v0.1.0", "hash", null, api.Clock.GetUtcNow()));

        BoardsSummary summary = await Summary(api, "all", player.Id);

        Assert.Equal(Api.File, Assert.Single(summary.Boards).File);
    }

    [Fact]
    public async Task The_summary_ranks_unfinished_attempts_as_a_board_does()
    {
        using Api api = TwoBoards();
        var nearly = Api.NewPlayer();
        var scrappy = Api.NewPlayer();
        var stuck = Api.NewPlayer();

        // One: a 95 % loss and a one-star clear far below it. Two: only a loss
        await api.Accepted(nearly, Api.Clear(nearly.Id, 47_500, name: "Nearly", stars: 0));
        await api.Accepted(scrappy, Api.Clear(scrappy.Id, 800, name: "Scrappy", stars: 1));
        await api.Accepted(stuck, Api.Clear(stuck.Id, 100, name: "Stuck", stars: 0, file: SecondFile, hash: SecondHash));

        BoardsSummary summary = await Summary(api, "all", nearly.Id);

        BoardSummary one = summary.Boards.Single(b => b.File == Api.File);
        Assert.Equal(2, one.Total);
        Assert.Equal(new BoardTop("Scrappy", 800, 1), one.Top);
        Assert.Equal(new BoardMe(2, 47_500, 0), one.Me);
        Assert.Equal(one.Me, (await api.Board("all", player: nearly.Id)).Me);

        BoardSummary two = summary.Boards.Single(b => b.File == SecondFile);
        Assert.Equal(new BoardTop("Stuck", 100, 0), two.Top);

        // In October the stuck player clears Two: their September loss leaves September's summary, and the board with it
        api.Clock.Advance(TimeSpan.FromDays(20));
        await api.Accepted(stuck, Api.Clear(stuck.Id, 90, name: "Stuck", stars: 1, file: SecondFile, hash: SecondHash));

        BoardsSummary september = await Summary(api, "month", stuck.Id, month: "2026-09");
        Assert.Equal([Api.File], september.Boards.Select(b => b.File).ToList());
        Assert.Equal(new BoardTop("Stuck", 90, 1), (await Summary(api, "all", stuck.Id)).Boards.Single(b => b.File == SecondFile).Top);
    }

    [Fact]
    public async Task A_period_that_is_neither_month_nor_all_is_refused()
    {
        using Api api = new();

        HttpResponseMessage response = await api.CreateClient().GetAsync("/v1/boards?period=week");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Reasons.BadRequest, await Api.ReasonOf(response));
    }

    /// <summary>The test service with a second known board beside <see cref="Api.File"/>, in a ceiling table of its own.</summary>
    private static Api TwoBoards()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", "ceilings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "BS3D-v9.9.9-ceilings.json"), $$"""
            { "format": "bs3d-ceilings", "version": 1, "rulesVersion": 1, "hashLength": 16, "levels": [
              { "file": "{{Api.File}}", "name": "One", "hash": "{{Api.Hash}}", "rulesVersion": {{Api.Rules}}, "shots": {{Api.Budget}},
                "ceiling": {{Api.Ceiling}}, "minShots": {{Api.MinShots}} },
              { "file": "{{SecondFile}}", "name": "Two", "hash": "{{SecondHash}}", "rulesVersion": {{Api.Rules}}, "shots": {{Api.Budget}},
                "ceiling": {{Api.Ceiling}}, "minShots": {{Api.MinShots}} } ] }
            """);

        return new Api(new() { ["Scores:CeilingsDirectory"] = folder });
    }

    private static async Task<BoardsSummary> Summary(Api api, string period, Guid? player, string? month = null) =>
        (await api.CreateClient().GetFromJsonAsync<BoardsSummary>($"/v1/boards?period={period}"
            + (player != null ? $"&player={player}" : "") + (month != null ? $"&month={month}" : "")))!;
}
