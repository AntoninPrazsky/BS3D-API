namespace BS3D.Api.Tests;

/// <summary>
/// Unfinished attempts (#8, BS3D#716): a level the player did not finish, sent with its final score and 0 stars. The
/// owner's rules: every clear ranks above every unfinished attempt, and a player shows an unfinished row only while they
/// have never cleared the board, in any month. The cases are the ones a board can get wrong while every number in it
/// looks right: a near-ceiling loss above a scrappy clear, a veteran shown as not having finished, an earlier month's
/// board keeping a row its player has since beaten.
/// </summary>
public sealed class UnfinishedTests
{
    [Fact]
    public async Task An_unfinished_attempt_is_accepted_below_a_clears_floors_and_at_the_ceiling()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        // A line loss before the first shot, a loss faster than any clear can be, and a loss at the ceiling itself
        await api.Accepted(player, Api.Clear(player.Id, 0, stars: 0, shots: 0, seconds: 0));
        await api.Accepted(player, Api.Clear(player.Id, 150, stars: 0, shots: Api.MinShots - 1, seconds: 0.1));
        await api.Accepted(player, Api.Clear(player.Id, Api.Ceiling, stars: 0, shots: Api.Budget));

        BoardEntry row = Assert.Single((await api.Board("all")).Entries);
        Assert.Equal((Api.Ceiling, 0), (row.Score, row.Stars));
    }

    [Fact]
    public async Task Every_clear_ranks_above_every_unfinished_attempt()
    {
        using Api api = new();
        var nearly = Api.NewPlayer();
        var scrappy = Api.NewPlayer();
        var halfway = Api.NewPlayer();
        var clean = Api.NewPlayer();

        // 95 % of the ceiling, lost: alone on the board, it is first
        SubmissionAnswer first = await api.Accepted(nearly, Api.Clear(nearly.Id, 47_500, name: "Nearly", stars: 0));
        Assert.Equal(new BoardRank(1, 1), first.AllTime);

        // A one-star clear far below it goes above it
        SubmissionAnswer clear = await api.Accepted(scrappy, Api.Clear(scrappy.Id, 800, name: "Scrappy", stars: 1));
        Assert.Equal(new BoardRank(1, 2), clear.AllTime);
        Assert.Equal(new BoardRank(1, 2), clear.Month);

        SubmissionAnswer below = await api.Accepted(halfway, Api.Clear(halfway.Id, 30_000, name: "Halfway", stars: 0));
        Assert.Equal(new BoardRank(3, 3), below.AllTime);
        await api.Accepted(clean, Api.Clear(clean.Id, 1_200, name: "Clean", stars: 3));

        foreach (string period in new[] { "month", "all" })
        {
            BoardPage board = await api.Board(period);
            Assert.Equal(4, board.Total);
            Assert.Equal([("Clean", 1_200, 3), ("Scrappy", 800, 1), ("Nearly", 47_500, 0), ("Halfway", 30_000, 0)],
                board.Entries.Select(e => (e.Name, e.Score, e.Stars)).ToList());
            Assert.Equal([1, 2, 3, 4], board.Entries.Select(e => e.Rank).ToList());
        }

        // A player's own row says the same as the page it is on
        Assert.Equal(new BoardMe(3, 47_500, 0), (await api.Board("all", player: nearly.Id)).Me);
    }

    [Fact]
    public async Task A_player_shows_their_best_unfinished_attempt_of_the_period_while_they_have_no_clear()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 400, stars: 0));
        await api.Accepted(player, Api.Clear(player.Id, 700, stars: 0));
        api.Clock.Advance(TimeSpan.FromDays(20));  //into October
        await api.Accepted(player, Api.Clear(player.Id, 200, stars: 0));

        Assert.Equal(700, Assert.Single((await api.Board("month", month: "2026-09")).Entries).Score);
        Assert.Equal(200, Assert.Single((await api.Board("month")).Entries).Score);
        Assert.Equal(700, Assert.Single((await api.Board("all")).Entries).Score);
    }

    [Fact]
    public async Task A_clear_takes_the_players_unfinished_rows_off_every_board_an_earlier_months_too()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        var other = Api.NewPlayer();

        // September: the player loses, above the score another player clears with
        await api.Accepted(player, Api.Clear(player.Id, 600, name: "Player", stars: 0));
        await api.Accepted(other, Api.Clear(other.Id, 500, name: "Other"));
        Assert.Equal(2, (await api.Board("month", month: "2026-09")).Total);

        // October: the player clears it, lower than both
        api.Clock.Advance(TimeSpan.FromDays(20));
        SubmissionAnswer cleared = await api.Accepted(player, Api.Clear(player.Id, 300, name: "Player", stars: 2));
        Assert.Equal(new BoardRank(1, 1), cleared.Month);
        Assert.Equal(new BoardRank(2, 2), cleared.AllTime);

        BoardPage september = await api.Board("month", month: "2026-09");
        Assert.Equal("Other", Assert.Single(september.Entries).Name);
        Assert.Equal(1, september.Total);
        Assert.Equal(new BoardRank(0, 1), RankOn(api, player.Id, "2026-09"));

        Assert.Equal([("Player", 300, 2)], (await api.Board("month")).Entries.Select(e => (e.Name, e.Score, e.Stars)).ToList());
        Assert.Equal([("Other", 500, 3), ("Player", 300, 2)], (await api.Board("all")).Entries.Select(e => (e.Name, e.Score, e.Stars)).ToList());
    }

    [Fact]
    public async Task A_loss_after_a_clear_is_kept_but_shown_nowhere()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 300));
        api.Clock.Advance(TimeSpan.FromDays(20));  //into October, where the player has no clear
        SubmissionAnswer loss = await api.Accepted(player, Api.Clear(player.Id, 900, stars: 0));

        Assert.False(loss.PersonalBest);
        Assert.Equal(new BoardRank(0, 0), loss.Month);
        Assert.Equal(new BoardRank(1, 1), loss.AllTime);

        Assert.Empty((await api.Board("month")).Entries);
        BoardEntry allTime = Assert.Single((await api.Board("all")).Entries);
        Assert.Equal((300, 3), (allTime.Score, allTime.Stars));
        Assert.Equal(2, api.Store.Export(api.Store.Open()).Count());
    }

    [Fact]
    public async Task A_personal_best_is_a_better_row_on_the_all_time_board()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        async Task<bool> Best(int score, int stars) => (await api.Accepted(player, Api.Clear(player.Id, score, stars: stars))).PersonalBest;

        Assert.True(await Best(500, 0));    //the first attempt
        Assert.False(await Best(400, 0));
        Assert.False(await Best(500, 0));   //equal is not better
        Assert.True(await Best(600, 0));
        Assert.True(await Best(300, 1));    //the first clear, below the unfinished 600: it still replaces it
        Assert.False(await Best(250, 2));
        Assert.False(await Best(5_000, 0)); //no unfinished attempt counts once the board is cleared
        Assert.True(await Best(350, 1));
    }

    private static BoardRank RankOn(Api api, Guid player, string? month)
    {
        using var c = api.Store.Open();
        return api.Store.RankOf(c, new BoardKey(Api.File, Api.Hash, Api.Rules), month, player).Rank;
    }
}
