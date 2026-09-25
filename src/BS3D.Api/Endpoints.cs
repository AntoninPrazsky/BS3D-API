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
    /// <summary>The best rating a clear can earn — BS3D's <c>StarRating.MAX</c>. A clear always earns at least one.</summary>
    public const int MaxStars = 4;

    /// <summary>A release's tag (<c>v0.2.0</c>, <c>v0.2.0-beta</c>) or a local build's <c>dev-&lt;short sha&gt;</c>.</summary>
    [GeneratedRegex(@"^(v\d+(\.\d+){1,3}(-[0-9A-Za-z.]+)?|dev(-[0-9a-f]{1,40})?)$")]
    private static partial Regex GameVersionPattern();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapScoreEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/scores", PostScore);
        app.MapGet("/v1/boards/{file}", GetBoard);
        app.MapPut("/v1/players/{id:guid}", PutPlayer);
        app.MapDelete("/v1/players/{id:guid}", DeletePlayer);
    }

    private static IResult PostScore(SubmissionRequest? body, HttpContext http, ScoreStore store, Ceilings ceilings,
        IOptions<ScoresOptions> options, RateLimits limits, AddressHasher addresses, TimeProvider clock, ILogger<ScoreStore> log)
    {
        ScoresOptions o = options.Value;

        if (body == null || body.SubmissionId == Guid.Empty || body.PlayerId == Guid.Empty
            || string.IsNullOrWhiteSpace(body.Level?.File) || string.IsNullOrWhiteSpace(body.Level?.Hash))
            return Refuse(log, http, 400, Reasons.BadRequest, "missing ids or level");

        string? token = Tokens.FromRequest(http.Request);
        if (token == null) return Refuse(log, http, 401, Reasons.NoToken, body.PlayerId.ToString());

        if (!limits.TryTake("address:" + AddressHasher.AddressOf(http), o.SubmissionsPerMinutePerAddress, out TimeSpan wait))
            return RateLimited(log, http, wait);

        using SqliteConnection c = store.Open();

        //A retry out of the game's outbox: answered with what it was answered the first time, and never counted twice
        if (store.FindSubmission(c, body.SubmissionId) is { } known)
            return Retried(store, c, body, token, known, log, http);

        ScoreStore.Player? player = store.FindPlayer(c, body.PlayerId);
        if (player != null && !Tokens.Matches(token, player.TokenHash))
            return Refuse(log, http, 401, Reasons.WrongToken, body.PlayerId.ToString());

        if (!limits.TryTake("player:" + body.PlayerId, o.SubmissionsPerMinutePerPlayer, out wait))
            return RateLimited(log, http, wait);

        string? name = Nicknames.Normalize(body.Name, o.DeniedNames);
        if (name == null) return Refuse(log, http, 422, Reasons.BadName, body.Name ?? "(none)");

        if (body.GameVersion == null || !GameVersionPattern().IsMatch(body.GameVersion))
            return Refuse(log, http, 422, Reasons.BadVersion, body.GameVersion ?? "(none)");

        BoardKey board = new(body.Level!.File!, body.Level.Hash!, body.RulesVersion);
        string? implausible = Implausible(body, board, ceilings, o);
        if (implausible != null) return Refuse(log, http, 422, implausible, $"{board.File}#{board.Hash} r{board.Rules} score {body.Score}");

        DateTimeOffset now = clock.GetUtcNow();

        ScoreStore.Begin(c);
        try
        {
            if (player == null) store.CreatePlayer(c, body.PlayerId, Tokens.Hash(token), name, now);
            else if (player.Name != name) store.RenamePlayer(c, body.PlayerId, name);

            int? previous = store.BestScore(c, board, body.PlayerId);

            store.Insert(c, new ScoreStore.NewSubmission(body.SubmissionId, body.PlayerId, board, body.Score, body.Stars,
                body.ShotsUsed, body.DurationSeconds, body.GameVersion, addresses.Hash(http),
                http.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent[..Math.Min(agent.Length, 200)] : null, now));

            SubmissionAnswer answer = new(
                Accepted: true,
                PersonalBest: previous == null || body.Score > previous,
                Month: store.RankOf(c, board, ScoreStore.MonthOf(now), body.PlayerId).Rank,
                AllTime: store.RankOf(c, board, null, body.PlayerId).Rank,
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
    /// What makes a submission impossible, or null (issue #2). On a known board: over the ceiling, fewer shots than any
    /// clear takes or more than the budget grants, and faster than a human fires that many. Anywhere: stars outside
    /// 1–4, and numbers that are not numbers.
    /// </summary>
    private static string? Implausible(SubmissionRequest s, BoardKey board, Ceilings ceilings, ScoresOptions o)
    {
        if (s.Stars is < 1 or > MaxStars) return Reasons.BadStars;
        if (s.Score < 0) return Reasons.OverCeiling;
        if (s.ShotsUsed < 0) return Reasons.BadShots;
        if (!double.IsFinite(s.DurationSeconds) || s.DurationSeconds < 0) return Reasons.BadDuration;

        if (!ceilings.TryGet(board.File, board.Hash, board.Rules, out CeilingRow row))
            return o.RequireKnownBoard ? Reasons.UnknownBoard : null;

        if (s.Score > row.Ceiling) return Reasons.OverCeiling;
        if (s.ShotsUsed < row.MinShots || s.ShotsUsed > row.Shots) return Reasons.BadShots;
        if (s.DurationSeconds < row.MinShots * o.MinSecondsPerShot) return Reasons.BadDuration;

        return null;
    }

    private static IResult GetBoard(string file, string? hash, int? rules, string? period, string? month, int? limit,
        int? offset, Guid? player, ScoreStore store, TimeProvider clock, ILogger<ScoreStore> log, HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(hash) || rules == null) return Refuse(log, http, 400, Reasons.BadRequest, "hash and rules are required");

        bool allTime = string.Equals(period, "all", StringComparison.OrdinalIgnoreCase);
        if (!allTime && period != null && !string.Equals(period, "month", StringComparison.OrdinalIgnoreCase))
            return Refuse(log, http, 400, Reasons.BadRequest, $"period '{period}'");

        string? boardMonth = null;
        if (!allTime)
        {
            boardMonth = month ?? ScoreStore.MonthOf(clock.GetUtcNow());
            if (!DateTime.TryParseExact(boardMonth, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return Refuse(log, http, 400, Reasons.BadRequest, $"month '{month}'");
        }

        int take = Math.Clamp(limit ?? 20, 1, 100);
        int skip = Math.Max(offset ?? 0, 0);
        BoardKey board = new(file, hash, rules.Value);

        using SqliteConnection c = store.Open();

        BoardMe? me = null;
        if (player is Guid id)
        {
            var (rank, score, stars) = store.RankOf(c, board, boardMonth, id);
            if (rank.Rank > 0) me = new BoardMe(rank.Rank, score, stars);
        }

        return Results.Json(new BoardPage(allTime ? "all" : "month", boardMonth, store.Total(c, board, boardMonth),
            store.Page(c, board, boardMonth, take, skip), me), Json);
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

        int removed = store.DeletePlayer(c, id);
        log.LogInformation("Player {Player} removed at their request, with {Count} submission(s)", id, removed);
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

    private static IResult RateLimited(ILogger log, HttpContext http, TimeSpan wait)
    {
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(wait.TotalSeconds, 1))).ToString(CultureInfo.InvariantCulture);
        return Refuse(log, http, 429, Reasons.RateLimited, AddressHasher.AddressOf(http));
    }

    private static IResult Refuse(ILogger log, HttpContext http, int status, string reason, string detail)
    {
        log.LogInformation("Refused {Method} {Path}: {Status} {Reason} ({Detail})", http.Request.Method, http.Request.Path, status, reason, detail);
        return Results.Json(new Refusal(reason), Json, statusCode: status);
    }
}
