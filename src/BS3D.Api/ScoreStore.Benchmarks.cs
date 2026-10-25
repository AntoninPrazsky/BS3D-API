using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BS3D.Api;

/// <summary>
/// The benchmarks (BS3D#988): every finished run of the game's Extras &gt; Benchmark that a game sent, one row each, with
/// the machine it ran on and its figures as the game sent them. <b>Outside the append-only log and outside the players</b>:
/// <c>benchmarks</c> has no column for a player or an address (not even its hash), so nothing stored can be tied to a
/// person, and a player's removal has nothing here to take. What it keeps beside the result is when it arrived and the
/// game's version, which say which build measured which machine.
/// </summary>
public sealed partial class ScoreStore
{
    private const string BenchmarksSchema = """
        CREATE TABLE IF NOT EXISTS benchmarks (
            id INTEGER PRIMARY KEY,
            benchmark_id TEXT NOT NULL UNIQUE,
            received_at TEXT NOT NULL,
            game_version TEXT NOT NULL,
            result TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_benchmarks_received ON benchmarks(received_at);
        """;

    /// <summary>A stored result, as the admin CLI and the Benchmarks tab read it.</summary>
    public sealed record StoredBenchmark(long Id, Guid BenchmarkId, DateTimeOffset ReceivedAt, string GameVersion, string Result);

    /// <summary>Whether a result with this id is stored: a resend out of the game's outbox whose first answer was lost.</summary>
    public bool HasBenchmark(SqliteConnection c, Guid benchmarkId) =>
        Command(c, "SELECT 1 FROM benchmarks WHERE benchmark_id = $id", ("$id", benchmarkId.ToString())).ExecuteScalar() != null;

    /// <summary>The results received since <paramref name="since"/>: the global day's count.</summary>
    public int BenchmarksSince(SqliteConnection c, DateTimeOffset since) =>
        Convert.ToInt32(Command(c, "SELECT COUNT(*) FROM benchmarks WHERE received_at >= $since", ("$since", Stamp(since))).ExecuteScalar(),
            CultureInfo.InvariantCulture);

    /// <summary>The results stored, against <see cref="ScoresOptions.BenchmarksMaxStored"/>.</summary>
    public int BenchmarksStored(SqliteConnection c) =>
        Convert.ToInt32(Command(c, "SELECT COUNT(*) FROM benchmarks").ExecuteScalar(), CultureInfo.InvariantCulture);

    /// <summary>
    /// Stores one result, inside the caller's transaction: false, and nothing stored, when its id is stored already (two
    /// resends arriving at once).
    /// </summary>
    public bool InsertBenchmark(SqliteConnection c, Guid benchmarkId, DateTimeOffset receivedAt, string gameVersion, string result) =>
        Command(c, """
            INSERT OR IGNORE INTO benchmarks (benchmark_id, received_at, game_version, result) VALUES ($id, $at, $ver, $result)
            """,
            ("$id", benchmarkId.ToString()), ("$at", Stamp(receivedAt)), ("$ver", gameVersion), ("$result", result)).ExecuteNonQuery() == 1;

    /// <summary>The results after <paramref name="afterId"/>, oldest first.</summary>
    public List<StoredBenchmark> Benchmarks(SqliteConnection c, long afterId) =>
        ReadBenchmarks(Command(c, "SELECT id, benchmark_id, received_at, game_version, result FROM benchmarks WHERE id > $after ORDER BY id",
            ("$after", afterId)));

    /// <summary>The newest <paramref name="limit"/> results, newest first.</summary>
    public static List<StoredBenchmark> NewestBenchmarks(SqliteConnection c, int limit) =>
        ReadBenchmarks(Command(c, "SELECT id, benchmark_id, received_at, game_version, result FROM benchmarks ORDER BY id DESC LIMIT $n",
            ("$n", limit)));

    /// <summary>One result by its row id, or null.</summary>
    public static StoredBenchmark? FindBenchmark(SqliteConnection c, long id) =>
        ReadBenchmarks(Command(c, "SELECT id, benchmark_id, received_at, game_version, result FROM benchmarks WHERE id = $id",
            ("$id", id))).SingleOrDefault();

    private static List<StoredBenchmark> ReadBenchmarks(SqliteCommand command)
    {
        using SqliteCommand cmd = command;
        using SqliteDataReader r = cmd.ExecuteReader();
        List<StoredBenchmark> rows = new();
        while (r.Read())
            rows.Add(new StoredBenchmark(r.GetInt64(0), Guid.Parse(r.GetString(1)),
                DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                r.GetString(3), r.GetString(4)));
        return rows;
    }
}
