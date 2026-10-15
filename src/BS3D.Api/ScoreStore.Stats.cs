using Microsoft.Data.Sqlite;

namespace BS3D.Api;

/// <summary>
/// The play counts (BS3D#709): how often each board is started, retried, cleared, lost and by what, left and skipped, by
/// every game whose player has online scores on, added up. <b>Outside the append-only log</b> and outside the players:
/// <c>play_stats</c> is one row of running totals per board, added to by each batch, and <c>stats_batches</c> holds only
/// the batch ids already counted. Neither has a column for a player, an address, a version or a time, so nothing stored
/// can be tied to a person or to a moment: there is nothing of a player's here for their removal to take, and the privacy
/// sentence in the game says so.
/// </summary>
public sealed partial class ScoreStore
{
    private const string StatsSchema = """
        CREATE TABLE IF NOT EXISTS play_stats (
            level_file TEXT NOT NULL,
            level_hash TEXT NOT NULL,
            rules_version INTEGER NOT NULL,
            starts INTEGER NOT NULL DEFAULT 0,
            retries INTEGER NOT NULL DEFAULT 0,
            clears INTEGER NOT NULL DEFAULT 0,
            lost_out_of_balls INTEGER NOT NULL DEFAULT 0,
            lost_line INTEGER NOT NULL DEFAULT 0,
            abandons INTEGER NOT NULL DEFAULT 0,
            skips INTEGER NOT NULL DEFAULT 0,
            first_clears INTEGER NOT NULL DEFAULT 0,
            starts_to_first_clear INTEGER NOT NULL DEFAULT 0,
            shots INTEGER NOT NULL DEFAULT 0,
            seconds REAL NOT NULL DEFAULT 0,
            PRIMARY KEY (level_file, level_hash, rules_version));
        CREATE TABLE IF NOT EXISTS stats_batches (
            batch_id TEXT PRIMARY KEY);
        """;

    /// <summary>
    /// Adds one batch's rows to the running totals, in one transaction with its id: false, and nothing added, when the id
    /// was counted before (a resend out of the game's outbox whose first answer was lost).
    /// </summary>
    public bool AddStats(SqliteConnection c, Guid batchId, IReadOnlyList<StatsLevel> rows)
    {
        Begin(c);
        try
        {
            int inserted = Command(c, "INSERT OR IGNORE INTO stats_batches (batch_id) VALUES ($b)", ("$b", batchId.ToString())).ExecuteNonQuery();
            if (inserted == 0)
            {
                Rollback(c);
                return false;
            }

            foreach (StatsLevel r in rows)
                Command(c, """
                    INSERT INTO play_stats (level_file, level_hash, rules_version, starts, retries, clears, lost_out_of_balls, lost_line,
                        abandons, skips, first_clears, starts_to_first_clear, shots, seconds)
                    VALUES ($f, $h, $r, $starts, $retries, $clears, $balls, $line, $abandons, $skips, $first, $tries, $shots, $seconds)
                    ON CONFLICT (level_file, level_hash, rules_version) DO UPDATE SET
                        starts = starts + excluded.starts, retries = retries + excluded.retries, clears = clears + excluded.clears,
                        lost_out_of_balls = lost_out_of_balls + excluded.lost_out_of_balls, lost_line = lost_line + excluded.lost_line,
                        abandons = abandons + excluded.abandons, skips = skips + excluded.skips,
                        first_clears = first_clears + excluded.first_clears,
                        starts_to_first_clear = starts_to_first_clear + excluded.starts_to_first_clear,
                        shots = shots + excluded.shots, seconds = seconds + excluded.seconds
                    """,
                    ("$f", r.File!), ("$h", r.Hash!), ("$r", r.Rules), ("$starts", r.Starts), ("$retries", r.Retries),
                    ("$clears", r.Clears), ("$balls", r.LostOutOfBalls), ("$line", r.LostLine), ("$abandons", r.Abandons),
                    ("$skips", r.Skips), ("$first", r.FirstClears), ("$tries", r.StartsToFirstClear), ("$shots", r.Shots),
                    ("$seconds", r.Seconds)).ExecuteNonQuery();

            Commit(c);
            return true;
        }
        catch
        {
            Rollback(c);
            throw;
        }
    }

    /// <summary>One board's running totals, as <c>play_stats</c> holds them.</summary>
    public sealed record PlayStatsRow(
        BoardKey Board, long Starts, long Retries, long Clears, long LostOutOfBalls, long LostLine, long Abandons, long Skips,
        long FirstClears, long StartsToFirstClear, long Shots, double Seconds);

    /// <summary>Every board's totals, in no particular order; <see cref="PlayStatsReport"/> orders them.</summary>
    public static List<PlayStatsRow> PlayStats(SqliteConnection c)
    {
        using SqliteCommand cmd = Command(c, """
            SELECT level_file, level_hash, rules_version, starts, retries, clears, lost_out_of_balls, lost_line, abandons, skips,
                   first_clears, starts_to_first_clear, shots, seconds
            FROM play_stats
            """);
        using SqliteDataReader r = cmd.ExecuteReader();

        List<PlayStatsRow> rows = new();
        while (r.Read())
            rows.Add(new PlayStatsRow(new BoardKey(r.GetString(0), r.GetString(1), r.GetInt32(2)), r.GetInt64(3), r.GetInt64(4),
                r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11),
                r.GetInt64(12), r.GetDouble(13)));
        return rows;
    }
}
