using System.Globalization;
using System.Net;

namespace BS3D.Api.Tests;

/// <summary>
/// Rules v2 pays a clear for its time (#19, BS3D#1045), so a forged short duration is worth points: a clear faster than its
/// table's <c>minSeconds</c> is refused, and so is one over the ceiling at the seconds it claims; an unfinished attempt,
/// which is paid no time bonus, is held to the ceiling without it.
/// </summary>
public sealed class TimeCheckTests
{
    // The fixture's board with a par of 40 s: the bonus pays 12 000 at 0 s, 150 a second less, and nothing from 80 s on,
    // so the ceiling at t seconds is 38 000 + 150 × (80 − t)
    private const int TimeBonus = 12_000, ZeroAtPars = 2;
    private const double MinSeconds = 6;
    private const int WithoutBonus = Api.Ceiling - TimeBonus;

    private const string Row = "\"par\": 40, \"minSeconds\": 6, \"timeBonus\": 12000";
    private const string Header = "\"timeBonusZeroAtPars\": 2,";

    private static string Table(string row = Row, string header = Header) => $$"""
        { "format": "bs3d-ceilings", "version": 1, "rulesVersion": 1, "hashLength": 16, {{header}} "levels": [
          { "file": "{{Api.File}}", "name": "One", "hash": "{{Api.Hash}}", "rulesVersion": {{Api.Rules}}, "shots": {{Api.Budget}},
            "ceiling": {{Api.Ceiling}}, "minShots": {{Api.MinShots}}, {{row}} } ] }
        """;

    private static async Task Refused(Api api, (Guid Id, string Token) player, SubmissionRequest body, string reason)
    {
        HttpResponseMessage response = await api.Post(player, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(reason, await Api.ReasonOf(response));
    }

    [Theory]
    [InlineData(6, 49_100)]
    [InlineData(34, 44_900)]
    [InlineData(34.5, 44_825)]
    [InlineData(40, 44_000)]
    [InlineData(80, WithoutBonus)]
    [InlineData(300, WithoutBonus)]
    public async Task A_clear_at_the_ceiling_at_its_claimed_time_is_accepted_and_one_point_more_refused(double seconds, int bound)
    {
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table());
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, bound, stars: 1, seconds: seconds));
        await Refused(api, player, Api.Clear(player.Id, bound + 1, stars: 1, seconds: seconds), Reasons.OverCeiling);
    }

    [Fact]
    public async Task A_clear_faster_than_min_seconds_is_refused()
    {
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table());
        var player = Api.NewPlayer();

        // 5.99 s is still over the fixture's floor of a quarter second a shot (2.5 s for 10 shots)
        await Refused(api, player, Api.Clear(player.Id, 10_000, stars: 1, seconds: 5.99), Reasons.BadDuration);
        await api.Accepted(player, Api.Clear(player.Id, 10_000, stars: 1, seconds: MinSeconds));
    }

    [Fact]
    public async Task An_unfinished_attempt_is_held_to_the_ceiling_without_the_bonus_and_not_to_min_seconds()
    {
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table());
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, WithoutBonus, stars: 0, shots: 5, seconds: 2));
        await Refused(api, player, Api.Clear(player.Id, WithoutBonus + 1, stars: 0, shots: 5, seconds: 2), Reasons.OverCeiling);
    }

    [Fact]
    public async Task A_board_whose_tables_state_no_par_gets_no_time_check()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, Api.Ceiling, stars: 1, seconds: 3));
        await api.Accepted(player, Api.Clear(player.Id, Api.Ceiling, stars: 0, shots: 5, seconds: 1));
    }

    [Theory]
    [InlineData("\"par\": 40, \"minSeconds\": 6", "\"timeBonusZeroAtPars\": 2,")]                        // no timeBonus
    [InlineData("\"par\": 40, \"minSeconds\": 6, \"timeBonus\": 12000", "")]                             // no zeroAtPars
    [InlineData("\"par\": 40, \"minSeconds\": 6, \"timeBonus\": 60000", "\"timeBonusZeroAtPars\": 2,")]  // over the ceiling
    [InlineData("\"par\": 0, \"minSeconds\": 6, \"timeBonus\": 12000", "\"timeBonusZeroAtPars\": 2,")]   // no par
    [InlineData("\"par\": 40, \"minSeconds\": -1, \"timeBonus\": 12000", "\"timeBonusZeroAtPars\": 2,")] // a negative time
    public async Task A_par_without_a_time_bonus_it_can_be_checked_by_is_logged_and_goes_unchecked(string row, string header)
    {
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table(row, header));
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, Api.Ceiling, stars: 1, seconds: 3));
        Assert.Contains(api.Log.Lines, l => l.Contains("has a par but no time bonus it can be checked by"));
    }

    [Fact]
    public async Task Two_tables_that_state_a_board_differently_pass_what_either_passes_and_nothing_else()
    {
        // At 40 s the bonus pays 6 000 with zeroAtPars 2 and 8 000 with 3: 44 000 and 46 000 under the ceiling
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", Table());
        api.AddTable("BS3D-v9.9.11-ceilings.json", Table(header: "\"timeBonusZeroAtPars\": 3,"));
        var player = Api.NewPlayer();

        await api.Accepted(player, Api.Clear(player.Id, 46_000, stars: 1, seconds: 40));
        await Refused(api, player, Api.Clear(player.Id, 46_001, stars: 1, seconds: 40), Reasons.OverCeiling);
    }

    [Fact]
    public void A_row_added_with_a_par_it_cannot_check_adds_no_time_check()
    {
        // Load logs and strips such a row; Add, which a caller may reach without Load, takes no rule from it either
        Ceilings ceilings = new();
        ceilings.Add(new CeilingRow(Api.File, "One", Api.Hash, Api.Rules, Api.Budget, Api.Ceiling, Api.MinShots,
            Par: 40, MinSeconds: 6), ZeroAtPars);
        ceilings.Add(new CeilingRow(Api.File, "One", Api.Hash, Api.Rules, Api.Budget, Api.Ceiling, Api.MinShots,
            Par: 40, MinSeconds: 6, TimeBonus: TimeBonus), timeBonusZeroAtPars: null);

        Assert.Null(ceilings.TimeRefusal(new BoardKey(Api.File, Api.Hash, Api.Rules), Api.Ceiling, 1, 1));
    }

    /// <summary>
    /// The game's own bonus, <c>ScoreKeeper.TimeBonusAt</c> in BS3D (#1045), as it pays it: in whole seconds begun, in
    /// whole points.
    /// </summary>
    private static int GameTimeBonus(int max, int par, int zeroAtPars, float seconds)
    {
        long zeroAt = (long)zeroAtPars * par;
        double begun = Math.Ceiling(Math.Max(0.0, seconds));
        return begun >= zeroAt ? 0 : (int)(max * (zeroAt - (long)begun) / zeroAt);
    }

    [Theory]
    [InlineData(20, 3, 23_800, 52_891)]     // Pennant, from ScoreSim's v2 table of BS3D main ddc5d052
    [InlineData(60, 9, 43_300, 96_435)]     // Cairn, the same table
    [InlineData(37, 5.55, 12_345, 99_991)]  // odd figures, so no division comes out whole by chance
    public void No_clear_the_game_pays_is_refused_and_one_point_more_at_a_whole_second_is(int par, double minSeconds, int bonus, int ceiling)
    {
        Ceilings ceilings = new();
        ceilings.Add(new CeilingRow(Api.File, "One", Api.Hash, Api.Rules, Api.Budget, ceiling, Api.MinShots,
            Par: par, MinSeconds: minSeconds, TimeBonus: bonus), ZeroAtPars);
        BoardKey board = new(Api.File, Api.Hash, Api.Rules);

        // Every thousandth of a second from the floor to past twice par, sent as the game sends it: rounded to hundredths
        // as a float (OnlineScores.NewSubmission), then read either straight or as the JSON text of that float
        for (int ms = (int)Math.Ceiling(minSeconds * 1000); ms <= (ZeroAtPars * par + 5) * 1000; ms++)
        {
            float clock = ms / 1000f;
            int score = ceiling - bonus + GameTimeBonus(bonus, par, ZeroAtPars, clock);
            float sent = MathF.Round(clock, 2);

            Assert.Null(ceilings.TimeRefusal(board, score, 1, sent));
            Assert.Null(ceilings.TimeRefusal(board, score, 1, double.Parse(sent.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)));

            if (ms % 1000 == 0) Assert.Equal(Reasons.OverCeiling, ceilings.TimeRefusal(board, score + 1, 1, sent));
        }
    }
}
