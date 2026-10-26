using System.Net;

namespace BS3D.Api.Tests;

/// <summary>
/// No more play claimed than has passed (#16): a player's submissions spend a budget of seconds that refills with the
/// clock, and one sent past it waits — a 429, which the game's outbox keeps — rather than being refused for good.
/// </summary>
public sealed class PlayTimeTests
{
    private static Api WithAllowance(double seconds = 600) =>
        new(new() { ["Scores:PlayTimeAllowanceSeconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static async Task<HttpResponseMessage> Waits(Api api, (Guid Id, string Token) player, SubmissionRequest body)
    {
        HttpResponseMessage response = await api.Post(player, body);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(Reasons.PlayTime, await Api.ReasonOf(response));
        return response;
    }

    [Fact]
    public async Task Clears_paced_at_their_durations_are_all_accepted()
    {
        using Api api = WithAllowance();
        var player = Api.NewPlayer();

        for (int i = 0; i < 30; i++)
        {
            await api.Accepted(player, Api.Clear(player.Id, 100 + i, seconds: 60));
            api.Clock.Advance(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task A_burst_spends_the_allowance_then_waits_as_long_as_the_next_clear_took()
    {
        using Api api = WithAllowance();
        var player = Api.NewPlayer();

        for (int i = 0; i < 10; i++) await api.Accepted(player, Api.Clear(player.Id, 100 + i, seconds: 60));
        HttpResponseMessage eleventh = await Waits(api, player, Api.Clear(player.Id, 200, seconds: 60));

        Assert.Equal(TimeSpan.FromSeconds(60), eleventh.Headers.RetryAfter?.Delta);
        Assert.Equal(10, api.Store.Export(api.Store.Open()).Count());

        api.Clock.Advance(TimeSpan.FromSeconds(59));
        await Waits(api, player, Api.Clear(player.Id, 200, seconds: 60));
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        await api.Accepted(player, Api.Clear(player.Id, 200, seconds: 60));
    }

    [Fact]
    public async Task The_script_of_2026_10_25_would_have_waited_after_its_tenth_clear()
    {
        //150 clears of 60.00 s, one every 7.1 s: 2.5 hours claimed in under 18 minutes
        using Api api = WithAllowance();
        var player = Api.NewPlayer();
        int accepted = 0, waited = 0;

        for (int i = 0; i < 150; i++)
        {
            HttpResponseMessage response = await api.Post(player, Api.Clear(player.Id, 100 + i, stars: 1, shots: 20, seconds: 60));
            if (response.StatusCode == HttpStatusCode.Created) accepted++;
            else if (response.StatusCode == HttpStatusCode.TooManyRequests) waited++;
            api.Clock.Advance(TimeSpan.FromSeconds(7.1));
        }

        //Ten from the allowance, then one a minute of the 17.6 minutes that passed
        Assert.InRange(accepted, 25, 30);
        Assert.Equal(150, accepted + waited);
    }

    [Fact]
    public async Task A_clear_longer_than_the_allowance_passes_on_a_full_budget_and_leaves_it_in_debt()
    {
        using Api api = WithAllowance();
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 100, seconds: 900));
        HttpResponseMessage next = await Waits(api, player, Api.Clear(player.Id, 110, seconds: 60));

        Assert.Equal(TimeSpan.FromSeconds(360), next.Headers.RetryAfter?.Delta);
        api.Clock.Advance(TimeSpan.FromSeconds(360));
        await api.Accepted(player, Api.Clear(player.Id, 110, seconds: 60));
    }

    [Fact]
    public async Task A_retried_submission_is_answered_as_before_and_costs_nothing()
    {
        using Api api = WithAllowance();
        var player = Api.NewPlayer();
        SubmissionRequest clear = Api.Clear(player.Id, 100, seconds: 540);

        await api.Accepted(player, clear);
        HttpResponseMessage retried = await api.Post(player, clear);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        await api.Accepted(player, Api.Clear(player.Id, 110, seconds: 60));
    }

    [Fact]
    public async Task An_unfinished_attempt_spends_its_seconds_too()
    {
        using Api api = WithAllowance();
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 100, stars: 0, shots: 5, seconds: 600));
        await Waits(api, player, Api.Clear(player.Id, 110, seconds: 60));
    }

    [Fact]
    public async Task A_submission_refused_for_good_spends_nothing()
    {
        using Api api = WithAllowance();
        var player = Api.NewPlayer();

        HttpResponseMessage over = await api.Post(player, Api.Clear(player.Id, Api.Ceiling + 1, seconds: 600));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);

        await api.Accepted(player, Api.Clear(player.Id, 100, seconds: 600));
    }

    [Fact]
    public async Task Each_player_has_a_budget_of_their_own()
    {
        using Api api = WithAllowance();
        var first = Api.NewPlayer();
        var second = Api.NewPlayer();

        await api.Accepted(first, Api.Clear(first.Id, 100, seconds: 600));
        await Waits(api, first, Api.Clear(first.Id, 110, seconds: 60));
        await api.Accepted(second, Api.Clear(second.Id, 100, seconds: 600));
    }

    [Fact]
    public async Task A_wait_is_logged_with_the_player_and_the_seconds_never_the_address()
    {
        IPAddress client = IPAddress.Parse("203.0.113.79");
        using Api api = new(new() { ["Scores:PlayTimeAllowanceSeconds"] = "600" }) { ClientAddress = client };
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 100, seconds: 600));
        await Waits(api, player, Api.Clear(player.Id, 110, seconds: 60.5));

        string line = Assert.Single(api.Log.Lines, l => l.Contains(Reasons.PlayTime));
        Assert.Contains($"player {player.Id} claims 60.5 s of play", line);
        Assert.DoesNotContain(api.Log.Lines, l => l.Contains(client.ToString()));
    }
}
