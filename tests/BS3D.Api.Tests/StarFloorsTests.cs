using System.Net;

namespace BS3D.Api.Tests;

/// <summary>
/// A clear's stars are its score's (#16): the game rates a clear by its score alone, so where a ceiling table states a
/// board's star floors (BS3D#1024), a clear claiming any other number of stars is refused.
/// </summary>
public sealed class StarFloorsTests
{
    private static string Table(params int[] floors) => $$"""
        { "format": "bs3d-ceilings", "version": 1, "rulesVersion": 1, "hashLength": 16, "levels": [
          { "file": "{{Api.File}}", "name": "One", "hash": "{{Api.Hash}}", "rulesVersion": {{Api.Rules}}, "shots": {{Api.Budget}},
            "ceiling": {{Api.Ceiling}}, "minShots": {{Api.MinShots}}, "starFloors": [{{string.Join(", ", floors)}}] } ] }
        """;

    private static async Task Refused(Api api, (Guid Id, string Token) player, int score, int stars)
    {
        HttpResponseMessage response = await api.Post(player, Api.Clear(player.Id, score, stars: stars));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(Reasons.BadStars, await Api.ReasonOf(response));
    }

    [Theory]
    [InlineData(999, 1)]
    [InlineData(1000, 2)]
    [InlineData(2999, 3)]
    [InlineData(3000, 4)]
    [InlineData(Api.Ceiling, 4)]
    public async Task A_clear_whose_stars_its_score_earns_is_accepted(int score, int stars)
    {
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table(1000, 2000, 3000));
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, score, stars: stars));
    }

    [Theory]
    [InlineData(999, 2)]
    [InlineData(1000, 1)]
    [InlineData(1000, 3)]
    [InlineData(3000, 3)]
    [InlineData(Api.Ceiling, 1)]   //the script of 2026-10-25: one star on a score above every 4-star clear
    public async Task A_clear_claiming_other_stars_than_its_score_earns_is_refused(int score, int stars)
    {
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table(1000, 2000, 3000));

        await Refused(api, Api.NewPlayer(), score, stars);
    }

    [Fact]
    public async Task An_unfinished_attempt_is_not_rated()
    {
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table(1000, 2000, 3000));
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 3000, stars: 0, shots: 5));
    }

    [Fact]
    public async Task Where_no_table_states_floors_the_stars_go_unchecked()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, Api.Ceiling, stars: 1));
    }

    [Fact]
    public async Task After_a_retune_the_floors_of_either_table_are_accepted_and_no_others()
    {
        //A retune of StarRating moves the floors under an unchanged board key; a release already out rates by the old ones
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table(1000, 2000, 3000));
        api.AddTable("BS3D-v9.9.11-ceilings.json", Table(1500, 2500, 3500));
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 1200, stars: 2));
        await api.Accepted(player, Api.Clear(player.Id, 1200, stars: 1));
        await Refused(api, player, 1200, 3);
    }

    [Fact]
    public async Task Floors_that_are_not_three_rising_scores_are_logged_and_go_unchecked()
    {
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table(3000, 2000, 1000));
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 100, stars: 4));
        Assert.Contains(api.Log.Lines, l => l.Contains("star floors that are not three rising scores"));
    }
}
