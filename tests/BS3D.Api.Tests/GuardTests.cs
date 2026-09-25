using System.Net;
using System.Net.Http.Json;

namespace BS3D.Api.Tests;

/// <summary>What the service refuses, because the client is open source and anything it can send curl can send (issue #2).</summary>
public sealed class GuardTests
{
    public static TheoryData<string, SubmissionRequest> Implausible()
    {
        Guid p = Guid.Empty;  //replaced per test
        return new()
        {
            { Reasons.UnknownBoard, Api.Clear(p, 100, hash: "ffffffffffffffff") },
            { Reasons.UnknownBoard, Api.Clear(p, 100, rules: 2) },
            { Reasons.OverCeiling, Api.Clear(p, Api.Ceiling + 1) },
            { Reasons.OverCeiling, Api.Clear(p, -1) },
            { Reasons.BadStars, Api.Clear(p, 100, stars: 0) },
            { Reasons.BadStars, Api.Clear(p, 100, stars: 5) },
            { Reasons.BadShots, Api.Clear(p, 100, shots: Api.MinShots - 1) },
            { Reasons.BadShots, Api.Clear(p, 100, shots: Api.Budget + 1) },
            { Reasons.BadDuration, Api.Clear(p, 100, shots: Api.MinShots, seconds: 0.5) },
            { Reasons.BadDuration, Api.Clear(p, 100, seconds: -1) },   //JSON cannot carry NaN; a negative time is the forgeable case
            { Reasons.BadName, Api.Clear(p, 100, name: "ab") },
            { Reasons.BadName, Api.Clear(p, 100, name: "Seventeen chars!!") },
            { Reasons.BadName, Api.Clear(p, 100, name: "Admin") },
            { Reasons.BadVersion, Api.Clear(p, 100, version: "1.0") },
        };
    }

    [Theory]
    [MemberData(nameof(Implausible))]
    public async Task An_implausible_submission_is_refused_with_its_reason(string reason, SubmissionRequest template)
    {
        using Api api = new();
        var player = Api.NewPlayer();

        HttpResponseMessage response = await api.Post(player, template with { PlayerId = player.Id });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(reason, await Api.ReasonOf(response));
        Assert.Empty(api.Store.Export(api.Store.Open()));
    }

    [Fact]
    public async Task The_ceiling_itself_and_the_fewest_shots_are_accepted()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, Api.Ceiling, shots: Api.MinShots, seconds: Api.MinShots * 0.25));
    }

    [Fact]
    public async Task An_unknown_board_is_accepted_where_known_boards_are_not_required()
    {
        using Api api = new(new() { ["Scores:RequireKnownBoard"] = "false" });
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 180, shots: 0, hash: "608c4bf895eec372", version: "dev-9d059bd"));
    }

    [Fact]
    public async Task No_token_and_a_wrong_token_are_refused()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));

        HttpResponseMessage none = await api.ClientFor(null).PostAsJsonAsync("/v1/scores", Api.Clear(player.Id, 200));
        HttpResponseMessage wrong = await api.Post((player.Id, Api.NewPlayer().Token), Api.Clear(player.Id, 200));

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(Reasons.NoToken, await Api.ReasonOf(none));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(Reasons.WrongToken, await Api.ReasonOf(wrong));
    }

    [Fact]
    public async Task A_player_over_their_rate_waits_and_is_let_through_after_the_minute()
    {
        using Api api = new(new() { ["Scores:SubmissionsPerMinutePerPlayer"] = "2" });
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 100));
        await api.Accepted(player, Api.Clear(player.Id, 110));
        HttpResponseMessage third = await api.Post(player, Api.Clear(player.Id, 120));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        api.Clock.Advance(TimeSpan.FromSeconds(61));
        await api.Accepted(player, Api.Clear(player.Id, 130));
    }

    [Fact]
    public async Task The_audit_row_keeps_a_salted_address_hash_and_the_game_version_never_the_address()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100, version: "v0.2.0-beta"));

        Dictionary<string, object?> row = Assert.Single(api.Store.Export(api.Store.Open()));
        Assert.Equal(16, ((string)row["ip_hash"]!).Length);
        Assert.Equal("v0.2.0-beta", row["game_version"]);
    }
}
