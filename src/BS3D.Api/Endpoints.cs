using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BS3D.Api;

/// <summary>
/// Contract v1's endpoints (BS3D#542, issue #1), with everything issue #2 says the service must enforce because the
/// client is open source and cannot be trusted: a whitelisted board, a score under its ceiling, plausible stars,
/// shots and duration, a nickname by the rule, a well-formed version, rate limits, and a token for every player.
/// Every refusal is one log line with its reason code; nothing about an accepted one is logged.
/// </summary>
public static partial class Endpoints
{
    /// <summary>
    /// The best rating a clear can earn — BS3D's <c>StarRating.MAX</c>. A clear always earns at least one, so 0 marks an
    /// attempt that did not finish the level (#8).
    /// </summary>
    public const int MaxStars = 4;

    /// <summary>A release's tag (<c>v0.2.0</c>, <c>v0.2.0-beta</c>) or a local build's <c>dev-&lt;short sha&gt;</c>.</summary>
    [GeneratedRegex(@"^(v\d+(\.\d+){1,3}(-[0-9A-Za-z.]+)?|dev(-[0-9a-f]{1,40})?)$")]
    private static partial Regex GameVersionPattern();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapScoreEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/scores", PostScore);
        app.MapGet("/v1/boards/{file}", GetBoard);
        app.MapGet("/v1/boards", GetBoards);
        app.MapGet("/v1/ranking", GetRanking);
        app.MapPut("/v1/players/{id:guid}", PutPlayer);
        app.MapDelete("/v1/players/{id:guid}", DeletePlayer);
        MapNoteEndpoint(app);
        MapStatsEndpoint(app);
        MapBenchmarkEndpoint(app);
    }

    private static IResult PostScore(SubmissionRequest? body, HttpContext http, ScoreStore store, Ceilings ceilings,
        IOptions<ScoresOptions> options, RateLimits limits, PlayTimeBudgets budgets, AddressHasher addresses, TimeProvider clock,
        ILogger<ScoreStore> log)
    {
        ScoresOptions o = options.Value;

        if (body == null || body.SubmissionId == Guid.Empty || body.PlayerId == Guid.Empty
            || string.IsNullOrWhiteSpace(body.Level?.File) || string.IsNullOrWhiteSpace(body.Level?.Hash))
            return Refuse(log, http, 400, Reasons.BadRequest, "missing ids or level");

        string? token = Tokens.FromRequest(http.Request);
        if (token == null) return Refuse(log, http, 401, Reasons.NoToken, body.PlayerId.ToString());

        if (!limits.TryTake("address:" + AddressHasher.AddressOf(http), o.SubmissionsPerMinutePerAddress, out TimeSpan wait))
            return RateLimited(log, http, wait, "address " + addresses.Hash(http));

        using SqliteConnection c = store.Open();

        //A retry out of the game's outbox: answered with what it was answered the first time, and never counted twice
        if (store.FindSubmission(c, body.SubmissionId) is { } known)
            return Retried(store, c, body, token, known, log, http);

        ScoreStore.Player? player = store.FindPlayer(c, body.PlayerId);
        if (player != null && !Tokens.Matches(token, player.TokenHash))
            return Refuse(log, http, 401, Reasons.WrongToken, body.PlayerId.ToString());

        if (!limits.TryTake("player:" + body.PlayerId, o.SubmissionsPerMinutePerPlayer, out wait))
            return RateLimited(log, http, wait, "player " + body.PlayerId);

        string? name = Nicknames.Normalize(body.Name, o.DeniedNames);
        if (name == null) return Refuse(log, http, 422, Reasons.BadName, body.Name ?? "(none)");

        if (body.GameVersion == null || !GameVersionPattern().IsMatch(body.GameVersion))
            return Refuse(log, http, 422, Reasons.BadVersion, body.GameVersion ?? "(none)");

        BoardKey board = new(body.Level!.File!, body.Level.Hash!, body.RulesVersion);
        string? implausible = Implausible(body, board, ceilings, o);
        if (implausible != null) return Refuse(log, http, 422, implausible, $"{board.File}#{board.Hash} r{board.Rules} score {body.Score}");

        //No more play claimed than has passed, beyond what an outbox may send at once (#16). After every final refusal,
        //so a submission that can never pass spends nothing; and a 429, never final, so the game keeps a clear its outbox
        //sent too soon and sends it again at its next drain
        if (!budgets.TrySpend(body.PlayerId, body.DurationSeconds, o.PlayTimeAllowanceSeconds, out wait))
            return RateLimited(log, http, wait,
                FormattableString.Invariant($"player {body.PlayerId} claims {body.DurationSeconds:0.##} s of play"), Reasons.PlayTime);

        DateTimeOffset now = clock.GetUtcNow();

        ScoreStore.Begin(c);
        try
        {
            if (player == null) store.CreatePlayer(c, body.PlayerId, Tokens.Hash(token), name, now);
            else if (player.Name != name) store.RenamePlayer(c, body.PlayerId, name);

            //A clear is ranked, and measured as a best, among what the build that sent it shows (#17)
            ScoreStore.Builds builds = ScoreStore.SeenBy(body.GameVersion);
            (int? bestClear, int? bestUnfinished) = store.BestScores(c, board, body.PlayerId, builds);

            store.Insert(c, new ScoreStore.NewSubmission(body.SubmissionId, body.PlayerId, board, body.Score, body.Stars,
                body.ShotsUsed, body.DurationSeconds, body.GameVersion, addresses.Hash(http),
                http.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent[..Math.Min(agent.Length, 200)] : null, now));

            SubmissionAnswer answer = new(
                Accepted: true,
                //The row the player shows on the all-time board got better: an unfinished attempt counts only while
                //they have never cleared the board, and never against a clear
                PersonalBest: body.Stars > 0
                    ? bestClear == null || body.Score > bestClear
                    : bestClear == null && (bestUnfinished == null || body.Score > bestUnfinished),
                Month: store.RankOf(c, board, ScoreStore.MonthOf(now), body.PlayerId, builds).Rank,
                AllTime: store.RankOf(c, board, null, body.PlayerId, builds).Rank,
                Name: name);

            store.SetAnswer(c, body.SubmissionId, JsonSerializer.Serialize(answer, Json));
            ScoreStore.Commit(c);

            return Results.Json(answer, Json, statusCode: 201);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            //The same submission id arrived twice at once and the other one won the insert: answer as a retry
            ScoreStore.Rollback(c);
            return store.FindSubmission(c, body.SubmissionId) is { } raced
                ? Retried(store, c, body, token, raced, log, http)
                : Refuse(log, http, 400, Reasons.BadRequest, e.Message);
        }
        catch
        {
            ScoreStore.Rollback(c);
            throw;
        }
    }

    private static IResult Retried(ScoreStore store, SqliteConnection c, SubmissionRequest body, string token,
        (Guid PlayerId, string Answer) known, ILogger log, HttpContext http)
    {
        ScoreStore.Player? owner = store.FindPlayer(c, known.PlayerId);

        if (known.PlayerId != body.PlayerId || owner == null || !Tokens.Matches(token, owner.TokenHash))
            return Refuse(log, http, 409, Reasons.SubmissionOfAnotherPlayer, body.SubmissionId.ToString());

        return Results.Text(known.Answer, "application/json", statusCode: 200);
    }

    /// <summary>
    /// What makes a submission impossible, or null (issue #2). On a known board: over the ceiling, more shots than the
    /// budget grants, and for a clear, fewer shots than any clear takes, faster than a human fires that many, and stars
    /// its score does not earn where a table states the board's star floors (#16). Anywhere: stars outside 0–4, and
    /// numbers that are not numbers.
    /// </summary>
    private static string? Implausible(SubmissionRequest s, BoardKey board, Ceilings ceilings, ScoresOptions o)
    {
        if (s.Stars is < 0 or > MaxStars) return Reasons.BadStars;
        if (s.Score < 0) return Reasons.OverCeiling;
        if (s.ShotsUsed < 0) return Reasons.BadShots;
        if (!double.IsFinite(s.DurationSeconds) || s.DurationSeconds < 0) return Reasons.BadDuration;

        if (!ceilings.TryGet(board.File, board.Hash, board.Rules, out CeilingRow row))
            return o.RequireKnownBoard ? Reasons.UnknownBoard : null;

        if (s.Score > row.Ceiling) return Reasons.OverCeiling;
        if (s.ShotsUsed > row.Shots) return Reasons.BadShots;
        if (s.Stars == 0) return null;

        //The floors are a clear's: an unfinished attempt can end after one shot, or before any (#8)
        if (s.ShotsUsed < row.MinShots) return Reasons.BadShots;
        if (s.DurationSeconds < row.MinShots * o.MinSecondsPerShot) return Reasons.BadDuration;

        //The game rates a clear by its score alone (StarRating.Rate), so a score earns exactly one number of stars (#16)
        if (!ceilings.StarsMatch(board, s.Score, s.Stars)) return Reasons.BadStars;

        return null;
    }

    private static IResult GetBoard(string file, string? hash, int? rules, string? period, string? month, int? limit,
        int? offset, Guid? player, ScoreStore store, TimeProvider clock, ILogger<ScoreStore> log, HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(hash) || rules == null) return Refuse(log, http, 400, Reasons.BadRequest, "hash and rules are required");
        if (ReadPeriod(period, month, clock, log, http, out bool allTime, out string? boardMonth) is { } refused) return refused;

        int take = Math.Clamp(limit ?? 20, 1, 100);
        int skip = Math.Max(offset ?? 0, 0);
        BoardKey board = new(file, hash, rules.Value);
        ScoreStore.Builds builds = BuildsAskedBy(http);

        using SqliteConnection c = store.Open();

        BoardMe? me = null;
        if (player is Guid id)
        {
            var (rank, score, stars) = store.RankOf(c, board, boardMonth, id, builds);
            if (rank.Rank > 0) me = new BoardMe(rank.Rank, score, stars);
        }

        return Results.Json(new BoardPage(allTime ? "all" : "month", boardMonth, store.Total(c, board, boardMonth, builds),
            store.Page(c, board, boardMonth, take, skip, builds), me), Json);
    }

    /// <summary>
    /// Every board's #1 and the asking player's place on it, in one answer (#7): the game's High Scores screen, which would
    /// otherwise ask the per-board GET once per level - over a hundred requests against a Cloudflare rule of fifty in ten
    /// seconds. The period and the month are read exactly as <see cref="GetBoard"/> reads them. With
    /// <see cref="ScoresOptions.RequireKnownBoard"/> on, only boards a ceiling table names are listed: a board from a level
    /// hash no table names any more is not one the game can show.
    /// </summary>
    private static IResult GetBoards(string? period, string? month, Guid? player, ScoreStore store, Ceilings ceilings,
        IOptions<ScoresOptions> options, TimeProvider clock, ILogger<ScoreStore> log, HttpContext http)
    {
        if (ReadPeriod(period, month, clock, log, http, out bool allTime, out string? boardMonth) is { } refused) return refused;

        using SqliteConnection c = store.Open();
        List<BoardSummary> boards = store.Summary(c, boardMonth, player, BuildsAskedBy(http));

        if (options.Value.RequireKnownBoard) boards.RemoveAll(b => !ceilings.TryGet(b.File, b.Hash, b.Rules, out _));

        return Results.Json(new BoardsSummary(allTime ? "all" : "month", boardMonth, boards), Json);
    }

    /// <summary>
    /// The players ranked across the campaign (#11, the game's Top Players screen, BS3D#827): per level a player's best
    /// clear in the period on <paramref name="rules"/>, added up, by <see cref="ScoreStore.Ranking"/>'s rule, among the
    /// scores the asking build sees (#17). The period, the month, the limit and the offset are read exactly as
    /// <see cref="GetBoard"/> reads them. With <see cref="ScoresOptions.RequireKnownBoard"/> on, a clear on a board no
    /// ceiling table names is left out before the sum.
    /// </summary>
    private static IResult GetRanking(int? rules, string? period, string? month, int? limit, int? offset, Guid? player,
        ScoreStore store, Ceilings ceilings, IOptions<ScoresOptions> options, TimeProvider clock, ILogger<ScoreStore> log,
        HttpContext http)
    {
        if (rules == null) return Refuse(log, http, 400, Reasons.BadRequest, "rules is required");
        if (ReadPeriod(period, month, clock, log, http, out bool allTime, out string? boardMonth) is { } refused) return refused;

        int take = Math.Clamp(limit ?? 20, 1, 100);
        int skip = Math.Max(offset ?? 0, 0);
        List<BoardKey>? known = options.Value.RequireKnownBoard
            ? ceilings.All.Select(b => new BoardKey(b.File, b.Hash, b.RulesVersion)).ToList()
            : null;

        using SqliteConnection c = store.Open();
        var (total, page, me) = store.RankPlayers(c, rules.Value, boardMonth, known, take, skip, player, BuildsAskedBy(http));

        return Results.Json(new RankingPage(allTime ? "all" : "month", boardMonth, rules.Value, total,
            page.Select(r => new RankingEntry(r.Rank, r.Name, r.Levels, r.Stars, r.Score, r.At)).ToList(),
            me is { } mine ? new RankingMe(mine.Rank, mine.Levels, mine.Stars, mine.Score) : null), Json);
    }

    /// <summary>
    /// A GET's period, read alike by a board, every board and the ranking: <c>month</c> (the default) or <c>all</c>; for a
    /// month, <paramref name="month"/> as <c>yyyy-MM</c>, or this UTC month when none is given. The refusal of a period or
    /// a month that is neither, else null.
    /// </summary>
    private static IResult? ReadPeriod(string? period, string? month, TimeProvider clock, ILogger log, HttpContext http,
        out bool allTime, out string? boardMonth)
    {
        allTime = string.Equals(period, "all", StringComparison.OrdinalIgnoreCase);
        boardMonth = null;
        if (!allTime && period != null && !string.Equals(period, "month", StringComparison.OrdinalIgnoreCase))
            return Refuse(log, http, 400, Reasons.BadRequest, $"period '{period}'");
        if (allTime) return null;

        boardMonth = month ?? ScoreStore.MonthOf(clock.GetUtcNow());
        return DateTime.TryParseExact(boardMonth, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? null
            : Refuse(log, http, 400, Reasons.BadRequest, $"month '{month}'");
    }

    /// <summary>
    /// The scores a board's GET shows (#17): every build's to a dev build of the game, and releases' only to anyone else -
    /// a release, a browser, a script, no agent at all. The game names its build in the <c>User-Agent</c> of every
    /// request, <c>BS3D/&lt;version&gt;</c> (its <c>OnlineScores.NewClient</c>, every release since v0.3.0), so the
    /// releases already out see release scores with no update, and the view a stranger gets by default is a release's.
    /// </summary>
    private static ScoreStore.Builds BuildsAskedBy(HttpContext http)
    {
        const string product = "BS3D/";
        string agent = http.Request.Headers.UserAgent.ToString();
        if (!agent.StartsWith(product, StringComparison.Ordinal)) return ScoreStore.Builds.Releases;

        string version = agent[product.Length..].Split(' ', 2)[0];
        return GameVersionPattern().IsMatch(version) ? ScoreStore.SeenBy(version) : ScoreStore.Builds.Releases;
    }

    private static IResult PutPlayer(Guid id, NameBody? body, HttpContext http, ScoreStore store, IOptions<ScoresOptions> options,
        ILogger<ScoreStore> log)
    {
        using SqliteConnection c = store.Open();
        if (Authorize(store, c, id, http, log) is { } refused) return refused;

        string? name = Nicknames.Normalize(body?.Name, options.Value.DeniedNames);
        if (name == null) return Refuse(log, http, 422, Reasons.BadName, body?.Name ?? "(none)");

        store.RenamePlayer(c, id, name);
        return Results.Json(new NameBody(name), Json);
    }

    private static IResult DeletePlayer(Guid id, HttpContext http, ScoreStore store, ILogger<ScoreStore> log)
    {
        using SqliteConnection c = store.Open();
        if (Authorize(store, c, id, http, log) is { } refused) return refused;

        ScoreStore.Removal removed = store.DeletePlayer(c, id);
        log.LogInformation("Player {Player} removed at their request, with {Count} submission(s)", id, removed.Submissions);
        return Results.NoContent();
    }

    /// <summary>A rename or a removal: the player must exist (404) and the token must be theirs (401).</summary>
    private static IResult? Authorize(ScoreStore store, SqliteConnection c, Guid id, HttpContext http, ILogger log)
    {
        string? token = Tokens.FromRequest(http.Request);
        if (token == null) return Refuse(log, http, 401, Reasons.NoToken, id.ToString());

        ScoreStore.Player? player = store.FindPlayer(c, id);
        if (player == null) return Refuse(log, http, 404, Reasons.UnknownPlayer, id.ToString());
        if (!Tokens.Matches(token, player.TokenHash)) return Refuse(log, http, 401, Reasons.WrongToken, id.ToString());

        return null;
    }

    /// <summary>
    /// A 429 with its wait: a rate limit, or a play-time budget spent (#16). The log line names the address by its
    /// salted hash, the same one the audit column keeps, and never the address itself: behind the tunnel that is the
    /// player's own IP, and the journal is no register of addresses either (issue #2).
    /// </summary>
    private static IResult RateLimited(ILogger log, HttpContext http, TimeSpan wait, string who, string reason = Reasons.RateLimited)
    {
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(wait.TotalSeconds, 1))).ToString(CultureInfo.InvariantCulture);
        return Refuse(log, http, 429, reason, who);
    }

    private static IResult Refuse(ILogger log, HttpContext http, int status, string reason, string detail)
    {
        // Cut as the refusal log cuts it: a detail can be a client's text, and a note's request may be 600 KB of it (#10)
        if (detail.Length > Refusals.TextLength) detail = detail[..Refusals.TextLength] + "…";
        log.LogInformation("Refused {Method} {Path}: {Status} {Reason} ({Detail})", http.Request.Method, http.Request.Path, status, reason, detail);
        http.RequestServices.GetRequiredService<Refusals>().Record(http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(),
            status, reason, http.Request.Method, http.Request.Path.Value ?? "", detail);
        return Results.Json(new Refusal(reason), Json, statusCode: status);
    }
}
