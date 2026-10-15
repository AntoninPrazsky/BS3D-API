using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BS3D.Api;

/// <summary>
/// <c>POST /v1/stats</c> (BS3D#709, additive to contract v1): a batch of anonymous play counts — how often each board was
/// started, retried, cleared, lost and by what, left and skipped, with its shots and seconds — added to the running totals
/// of <c>play_stats</c> (<see cref="ScoreStore.AddStats"/>). The game sends one only for a player with online scores on,
/// and <b>without a token or anything else that names the player</b>, so this takes none and stores nothing that could
/// tie the counts to one: no address, no time, no version. Idempotent by the batch id; held, like a score, to a request
/// size, a per-address rate and bounds on every count. A row on no board a ceiling table names is left out rather than
/// refused, so one stray level does not cost a batch its other rows; with <see cref="ScoresOptions.RequireKnownBoard"/> off
/// (a developer's machine) such a row counts when its file and hash are of a plain shape.
/// </summary>
public static partial class Endpoints
{
    /// <summary>A level set entry's file as the game names one: letters, digits, spaces, <c>._-</c>, at most 64.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9 _.\-]{1,64}$")]
    private static partial Regex StatsFilePattern();

    /// <summary>A <c>LevelIdentity</c> hash: lowercase hex, at most 64.</summary>
    [GeneratedRegex(@"^[0-9a-f]{1,64}$")]
    private static partial Regex StatsHashPattern();

    private static void MapStatsEndpoint(WebApplication app)
    {
        // A batch of 200 rows is about 50 KB, over the 4 KB Kestrel holds every other request to (Program.cs); the routing
        // middleware applies the endpoint's own limit before the body is read, as for a note
        long limit = app.Services.GetRequiredService<IOptions<ScoresOptions>>().Value.StatsMaxRequestBytes;
        app.MapPost("/v1/stats", PostStats).WithMetadata(new RequestSizeLimitAttribute(limit));
    }

    private static IResult PostStats(StatsBatch? body, HttpContext http, ScoreStore store, Ceilings ceilings,
        IOptions<ScoresOptions> options, RateLimits limits, AddressHasher addresses, ILogger<ScoreStore> log)
    {
        ScoresOptions o = options.Value;

        if (body == null || body.BatchId == Guid.Empty || body.Levels == null || body.Levels.Count == 0)
            return Refuse(log, http, 400, Reasons.BadRequest, "missing batch id or levels");

        if (!limits.TryTake("stats:" + AddressHasher.AddressOf(http), o.StatsPerMinutePerAddress, out TimeSpan wait))
            return RateLimited(log, http, wait, "address " + addresses.Hash(http));

        if (body.Levels.Count > o.StatsMaxLevels)
            return Refuse(log, http, 422, Reasons.TooManyLevels, $"{body.Levels.Count} levels");

        List<StatsLevel> counted = new(body.Levels.Count);
        HashSet<BoardKey> boards = new();
        int ignored = 0;

        foreach (StatsLevel? row in body.Levels)
        {
            if (row == null || string.IsNullOrEmpty(row.File) || string.IsNullOrEmpty(row.Hash) || row.File.Length > 64 || row.Hash.Length > 64)
                return Refuse(log, http, 400, Reasons.BadRequest, "a level without a file and a hash");

            BoardKey board = new(row.File, row.Hash, row.Rules);
            if (!boards.Add(board)) return Refuse(log, http, 400, Reasons.BadRequest, $"{Printable(board.File)} r{board.Rules} twice");

            if (BadCounts(row, o) is { } why) return Refuse(log, http, 422, Reasons.BadCounts, $"{Printable(board.File)} r{board.Rules}: {why}");

            // A board a ceiling table names is the owner's own name, whatever its letters; any other is left out where
            // boards must be known, and where they need not be (a developer's machine) it is stored only in a plain
            // shape, since admin stats prints it into a terminal
            if (!ceilings.TryGet(board.File, board.Hash, board.Rules, out _))
            {
                if (o.RequireKnownBoard)
                {
                    ignored++;
                    continue;
                }

                if (!StatsFilePattern().IsMatch(row.File) || !StatsHashPattern().IsMatch(row.Hash))
                    return Refuse(log, http, 400, Reasons.BadRequest, "a level on no known board without a plain file and hash");
            }

            counted.Add(row);
        }

        using SqliteConnection c = store.Open();
        bool added = store.AddStats(c, body.BatchId, counted);

        // Nothing about an accepted batch is logged, as nothing about an accepted score is
        return Results.Json(added
                ? new StatsAnswer(Accepted: true, Counted: counted.Count, Ignored: ignored, Duplicate: false)
                : new StatsAnswer(Accepted: true, Counted: 0, Ignored: 0, Duplicate: true),
            Json, statusCode: added ? 201 : 200);
    }

    /// <summary>A client's file name as a log line may carry it: letters, digits and <c>._- </c> kept, anything else <c>?</c>.</summary>
    private static string Printable(string text) =>
        new(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' ? c : '?').ToArray());

    /// <summary>
    /// What makes a row's counts impossible, or null: a negative count, any count over its bound for one batch, retries or
    /// first clears beyond what they are a part of, and seconds that are not a finite, non-negative number.
    /// </summary>
    private static string? BadCounts(StatsLevel r, ScoresOptions o)
    {
        int[] counts = [r.Starts, r.Retries, r.Clears, r.LostOutOfBalls, r.LostLine, r.Abandons, r.Skips, r.FirstClears, r.StartsToFirstClear];
        if (counts.Any(n => n < 0) || r.Shots < 0) return "a negative count";
        if (counts.Any(n => n > o.StatsMaxCount) || r.Shots > o.StatsMaxShots) return "a count over its bound";
        if (r.Retries > r.Starts) return "more retries than starts";
        if (r.FirstClears > r.Clears) return "more first clears than clears";
        if (r.FirstClears == 0 && r.StartsToFirstClear > 0) return "starts to a first clear without one";
        if (!double.IsFinite(r.Seconds) || r.Seconds < 0 || r.Seconds > o.StatsMaxSeconds) return "seconds out of range";
        return null;
    }
}
