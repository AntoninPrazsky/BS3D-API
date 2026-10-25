using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BS3D.Api;

/// <summary>
/// <c>POST /v1/benchmarks</c> (BS3D#988, additive to contract v1): one finished run of the game's benchmark, the machine
/// it ran on and its figures, stored as it came (<see cref="ScoreStore.InsertBenchmark"/>) for the owner to read on the
/// admin page's Benchmarks tab. The game sends it for any player who starts a benchmark (the page says what goes), <b>without
/// a token, an id or a name</b>, so this takes none and stores no player and no address, not even its hash; <b>not even a
/// refusal names the address</b>, as a play-count batch's does not.
/// <para>
/// Anyone can send one, so it is held to what a disk at the owner's home should take from strangers: its own request size,
/// limits per network by the minute and by a sliding day (an IPv6 address by its /64, as a note's), a global count per UTC
/// day, a cap on the results stored, a well-formed version, and a result that is a small JSON object whose every name and
/// string is text, with the machine (<c>system</c>, an object) and the scenes (<c>segments</c>, an array) a game's result
/// always has. A repeated id is answered as a duplicate before every limit but the network's, so a retry of the day's last
/// result still gets its answer. Nothing about an accepted result is logged.
/// </para>
/// </summary>
public static partial class Endpoints
{
    private static void MapBenchmarkEndpoint(WebApplication app)
    {
        // A result is about 3 KB, near the 4 KB Kestrel holds every other request to (Program.cs), so it has its own limit,
        // applied by the routing middleware before the body is read, as a note's
        long limit = app.Services.GetRequiredService<IOptions<ScoresOptions>>().Value.BenchmarkMaxRequestBytes;
        app.MapPost("/v1/benchmarks", PostBenchmark).WithMetadata(new RequestSizeLimitAttribute(limit));
    }

    private static IResult PostBenchmark(BenchmarkRequest? body, HttpContext http, ScoreStore store, IOptions<ScoresOptions> options,
        RateLimits limits, TimeProvider clock, ILogger<ScoreStore> log)
    {
        ScoresOptions o = options.Value;

        if (body == null || body.BenchmarkId == Guid.Empty || body.Result is not JsonElement result)
            return Refuse(log, http, 400, Reasons.BadRequest, "missing benchmark id or result");

        // By the network, and the refusal names no address
        string network = AddressHasher.NetworkOf(http);
        if (!limits.TryTake("benchmark-minute:" + network, o.BenchmarksPerMinutePerAddress, out TimeSpan wait)
            || !limits.TryTake("benchmark-day:" + network, o.BenchmarksPerDayPerAddress, TimeSpan.FromDays(1), out wait))
            return RateLimited(log, http, wait, "a network");

        if (body.GameVersion == null || !GameVersionPattern().IsMatch(body.GameVersion))
            return Refuse(log, http, 422, Reasons.BadVersion, body.GameVersion ?? "(none)");

        if (result.ValueKind != JsonValueKind.Object) return Refuse(log, http, 422, Reasons.BadResult, result.ValueKind.ToString());
        // Every name and string decoded once: the raw text is stored as it came, and a lone surrogate's escape in it would
        // break every reader of the result (the admin page and the CLI), as it would a note's context
        if (!IsText(result)) return Refuse(log, http, 422, Reasons.BadResult, "a string that is not text");
        string raw = result.GetRawText();
        int bytes = Encoding.UTF8.GetByteCount(raw);
        if (bytes > o.BenchmarkMaxResultBytes) return Refuse(log, http, 422, Reasons.ResultTooLarge, $"{bytes} bytes");
        if (!result.TryGetProperty("system", out JsonElement system) || system.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("segments", out JsonElement segments) || segments.ValueKind != JsonValueKind.Array)
            return Refuse(log, http, 422, Reasons.BadResult, "no system object or no segments array");

        using SqliteConnection c = store.Open();
        BenchmarkAnswer duplicate = new(body.BenchmarkId, Duplicate: true);
        if (store.HasBenchmark(c, body.BenchmarkId)) return Results.Json(duplicate, Json, statusCode: 200);

        DateTimeOffset now = clock.GetUtcNow();
        ScoreStore.Begin(c);
        try
        {
            // The day's count is read inside the write lock, so two results cannot both be the last one
            DateTimeOffset day = new(now.UtcDateTime.Date, TimeSpan.Zero);
            if (store.BenchmarksSince(c, day) >= o.BenchmarksPerDay)
            {
                ScoreStore.Rollback(c);
                return RateLimited(log, http, day.AddDays(1) - now, "every network (the day's benchmarks)");
            }

            // The results stored at their cap: refused, and kept in the game's outbox until the owner makes room
            if (store.BenchmarksStored(c) >= o.BenchmarksMaxStored)
            {
                ScoreStore.Rollback(c);
                http.Response.Headers.RetryAfter = "3600";
                return Refuse(log, http, 429, Reasons.BenchmarksFull, $"{o.BenchmarksMaxStored} benchmarks stored");
            }

            bool stored = store.InsertBenchmark(c, body.BenchmarkId, now, body.GameVersion, raw);
            ScoreStore.Commit(c);
            return stored
                ? Results.Json(new BenchmarkAnswer(body.BenchmarkId, Duplicate: false), Json, statusCode: 201)
                : Results.Json(duplicate, Json, statusCode: 200);
        }
        catch
        {
            ScoreStore.Rollback(c);
            throw;
        }
    }
}
