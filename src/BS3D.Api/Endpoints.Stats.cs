using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BS3D.Api;

/// <summary>
/// <c>POST /v1/stats</c> (BS3D#709, additive to contract v1): a batch of play counts — how often each board was started,
/// retried, cleared, lost and by what, left and skipped, with its shots and seconds — added to the running totals of
/// <c>play_stats</c> (<see cref="ScoreStore.AddStats"/>). The game sends one only for a player with online scores on, on a
/// schedule of its own apart from every request that names the player, and <b>without a token, an id, a name or a
/// version</b>, so this takes none and stores no player, address, time or version with the counts. <b>Not even a refusal
/// names the address</b>: a 429 here is logged as "a network", where a score's names the salted hash.
/// <para>
/// Anyone can send a batch, so one request is held to what one batch of real play can add: its own request size, a limit
/// per network by the minute (an IPv6 address by its /64, as a note's), counts that are not negative and add up (retries
/// no more than starts, endings and skips no more than the starts and the one attempt a batch can carry over), and a row
/// past what one batch can hold (<see cref="Implausible"/>) left out. A row on no board a ceiling table names is left out
/// too, rather than refused, so one stray level does not cost a batch its other rows; with
/// <see cref="ScoresOptions.RequireKnownBoard"/> off (a developer's machine) such a row counts when its file and hash are
/// of a plain shape. A batch of which nothing counts adds nothing, its id included.
/// </para>
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
        IOptions<ScoresOptions> options, RateLimits limits, ILogger<ScoreStore> log)
    {
        ScoresOptions o = options.Value;

        if (body == null || body.BatchId == Guid.Empty || body.Levels == null || body.Levels.Count == 0)
            return Refuse(log, http, 400, Reasons.BadRequest, "missing batch id or levels");

        // By the network, as a note's limits count (an IPv6 host is handed a whole /64), and the refusal names no address
        if (!limits.TryTake("stats:" + AddressHasher.NetworkOf(http), o.StatsPerMinutePerAddress, out TimeSpan wait))
            return RateLimited(log, http, wait, "a network");

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

            if (BadCounts(row) is { } why) return Refuse(log, http, 422, Reasons.BadCounts, $"{Printable(board.File)} r{board.Rules}: {why}");

            // More than one batch of play can add: left out like a row on no board, so a forged batch cannot outweigh the
            // real counts, and a real one carried through a long absence of the service loses that row and no other
            if (Implausible(row, o))
            {
                ignored++;
                continue;
            }

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
    /// What makes a row's counts impossible from any game, or null: a negative count or time, retries beyond the starts,
    /// first clears beyond the clears, and more endings (clears, losses, quits) or skips than the starts and the one
    /// attempt a batch can carry over — one attempt is open at a time, so at most one started in the batch before.
    /// </summary>
    private static string? BadCounts(StatsLevel r)
    {
        int[] counts = [r.Starts, r.Retries, r.Clears, r.LostOutOfBalls, r.LostLine, r.Abandons, r.Skips, r.FirstClears];
        if (counts.Any(n => n < 0) || r.Shots < 0) return "a negative count";
        if (!double.IsFinite(r.Seconds) || r.Seconds < 0) return "negative seconds";
        if (r.Retries > r.Starts) return "more retries than starts";
        if (r.FirstClears > r.Clears) return "more first clears than clears";
        if ((long)r.Clears + r.LostOutOfBalls + r.LostLine + r.Abandons > (long)r.Starts + 1) return "more endings than starts";
        if (r.Skips > (long)r.Starts + 1) return "more skips than starts";
        return null;
    }

    /// <summary>
    /// Whether a row holds more than one batch of real play can: more seconds than <see cref="ScoresOptions.StatsMaxSeconds"/>,
    /// more starts than <see cref="ScoresOptions.StatsMaxStarts"/> or than one a second of its play past the first few, or
    /// more shots than <see cref="ScoresOptions.StatsMaxShotsPerAttempt"/> an attempt.
    /// </summary>
    private static bool Implausible(StatsLevel r, ScoresOptions o) =>
        r.Seconds > o.StatsMaxSeconds
        || r.Starts > o.StatsMaxStarts
        || r.Starts > StatsFreeStarts + r.Seconds
        || r.Shots > ((long)r.Starts + 1) * o.StatsMaxShotsPerAttempt;

    /// <summary>Starts a row may hold beyond one a second of its play: a few instant Restarts are real.</summary>
    private const int StatsFreeStarts = 5;
}
