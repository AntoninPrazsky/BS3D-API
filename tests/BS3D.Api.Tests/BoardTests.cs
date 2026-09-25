using System.Net;

namespace BS3D.Api.Tests;

/// <summary>
/// The ranking cases issue #1 names first — because a ranking can be wrong on every level while every number in it
/// looks right (BS3D#173 was that failure in the game's own scoring).
/// </summary>
public sealed class BoardTests
{
    [Fact]
    public async Task A_first_clear_ranks_first_of_one_on_both_boards_and_is_a_personal_best()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        SubmissionAnswer answer = await api.Accepted(player, Api.Clear(player.Id, 1200, name: "  Pražský   Tester "));

        Assert.True(answer.Accepted);
        Assert.True(answer.PersonalBest);
        Assert.Equal(new BoardRank(1, 1), answer.Month);
        Assert.Equal(new BoardRank(1, 1), answer.AllTime);
        Assert.Equal("Pražský Tester", answer.Name);
    }

    [Fact]
    public async Task A_month_board_ignores_last_months_better_score()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 900));
        api.Clock.Advance(TimeSpan.FromDays(20));  //into October
        SubmissionAnswer october = await api.Accepted(player, Api.Clear(player.Id, 500));

        Assert.False(october.PersonalBest);

        BoardPage month = await api.Board("month");
        Assert.Equal("2026-10", month.Month);
        Assert.Equal(500, Assert.Single(month.Entries).Score);

        BoardPage allTime = await api.Board("all");
        Assert.Equal(900, Assert.Single(allTime.Entries).Score);

        BoardPage september = await api.Board("month", month: "2026-09");
        Assert.Equal(900, Assert.Single(september.Entries).Score);
    }

    [Fact]
    public async Task A_tie_goes_to_the_earlier_submission()
    {
        using Api api = new();
        var first = Api.NewPlayer();
        var second = Api.NewPlayer();

        await api.Accepted(first, Api.Clear(first.Id, 700, name: "First"));
        api.Clock.Advance(TimeSpan.FromMinutes(5));
        SubmissionAnswer later = await api.Accepted(second, Api.Clear(second.Id, 700, name: "Second"));

        Assert.Equal(new BoardRank(2, 2), later.AllTime);
        Assert.Equal(["First", "Second"], (await api.Board("all")).Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task A_lower_second_clear_changes_no_rank_and_the_board_keeps_one_row_per_player()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        var rival = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 800, name: "Player"));
        await api.Accepted(rival, Api.Clear(rival.Id, 600, name: "Rival"));
        SubmissionAnswer again = await api.Accepted(player, Api.Clear(player.Id, 100, name: "Player"));

        Assert.False(again.PersonalBest);
        Assert.Equal(new BoardRank(1, 2), again.AllTime);

        BoardPage board = await api.Board("all");
        Assert.Equal(2, board.Total);
        Assert.Equal([800, 600], board.Entries.Select(e => e.Score));
    }

    [Fact]
    public async Task A_retried_submission_counts_once_and_is_answered_as_it_was_the_first_time()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        SubmissionRequest clear = Api.Clear(player.Id, 1000);

        HttpResponseMessage first = await api.Post(player, clear);
        HttpResponseMessage retry = await api.Post(player, clear);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Single(api.Store.Export(api.Store.Open()));
    }

    [Fact]
    public async Task A_submission_id_cannot_be_replayed_by_another_player()
    {
        using Api api = new();
        var owner = Api.NewPlayer();
        var thief = Api.NewPlayer();
        SubmissionRequest clear = Api.Clear(owner.Id, 1000);

        await api.Accepted(owner, clear);
        HttpResponseMessage replay = await api.Post(thief, clear with { PlayerId = thief.Id });

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task A_hidden_player_vanishes_from_every_board_and_every_count()
    {
        using Api api = new();
        var cheat = Api.NewPlayer();
        var honest = Api.NewPlayer();

        await api.Accepted(cheat, Api.Clear(cheat.Id, 40_000, name: "Cheat"));
        await api.Accepted(honest, Api.Clear(honest.Id, 900, name: "Honest"));

        using (var c = api.Store.Open()) api.Store.SetHidden(c, cheat.Id, true);

        BoardPage board = await api.Board("all", player: honest.Id);
        Assert.Equal(1, board.Total);
        Assert.Equal("Honest", Assert.Single(board.Entries).Name);
        Assert.Equal(new BoardMe(1, 900, 3), board.Me);
    }

    [Fact]
    public async Task A_page_is_limited_and_offset_and_carries_the_askers_own_row()
    {
        using Api api = new();
        List<(Guid Id, string Token)> players = Enumerable.Range(0, 5).Select(_ => Api.NewPlayer()).ToList();
        for (int i = 0; i < players.Count; i++)
            await api.Accepted(players[i], Api.Clear(players[i].Id, 1000 - i * 100, name: $"Player{i}"));

        BoardPage page = await api.Board("all", player: players[4].Id, limit: 2, offset: 1);

        Assert.Equal(5, page.Total);
        Assert.Equal([2, 3], page.Entries.Select(e => e.Rank));
        Assert.Equal(new BoardMe(5, 600, 3), page.Me);
    }

    [Fact]
    public async Task A_board_needs_its_hash_and_rules()
    {
        using Api api = new();

        HttpResponseMessage response = await api.CreateClient().GetAsync($"/v1/boards/{Api.File}?period=all");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
