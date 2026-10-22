using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BS3D.Api;

public sealed partial class ScoreStore
{
    /// <summary>
    /// A player's row in the ranking across the campaign (#11): the levels they have cleared, the stars and the score
    /// of those clears added up, and when they reached that total.
    /// </summary>
    public sealed record RankingRow(int Rank, string Name, int Levels, int Stars, long Score, DateTimeOffset At);

    /// <summary>
    /// The players ranked across every level (#11, the game's Top Players screen, BS3D#827): per level, a player's best
    /// clear in the period on <paramref name="rules"/>; those added up; ranked by the total score, then by who reached it
    /// first (the latest row among those best clears). Over one UTC month, or over all time when
    /// <paramref name="month"/> is null. 0-star rows (#8) count for nothing, and hidden players are in no count.
    /// <para>
    /// <paramref name="known"/> is the boards a ceiling table names, when boards must be known
    /// (<c>Scores:RequireKnownBoard</c>), or null: a row from an unknown board is left out before the sum, never after
    /// it, since a ranking adds up across boards.
    /// </para>
    /// <para>
    /// <b>Provisional (the owner, 2026-10-08): the versions of a level count together</b>, the best of them, as the admin
    /// page's funnel counts them, so a clear on an old, perhaps easier version keeps counting after the level is
    /// regenerated. The likely alternative is to count only the hash the newest ceiling table names for each file. The
    /// grouping is the one <c>PARTITION BY</c> in <c>best</c> below.
    /// </para>
    /// <para>The status snapshot's Top players (#13) reads it; <c>GET /v1/ranking</c> (#11) is to answer with it.</para>
    /// </summary>
    public List<RankingRow> Ranking(SqliteConnection c, int rules, string? month, IEnumerable<BoardKey>? known, int limit, int offset = 0)
    {
        string? boards = known == null ? null
            : JsonSerializer.Serialize(known.Where(b => b.Rules == rules).Select(b => new[] { b.File, b.Hash }));
        using SqliteCommand cmd = Command(c, """
            WITH known AS (
                SELECT json_extract(value, '$[0]') AS f, json_extract(value, '$[1]') AS h FROM json_each($known)),
            clears AS (
                SELECT s.rowid AS seq, s.player_id, p.name, s.level_file, s.score, s.stars
                FROM submissions s JOIN players p ON p.id = s.player_id
                WHERE p.hidden = 0 AND s.stars > 0 AND s.rules_version = $rules
                  AND ($month IS NULL OR s.month = $month)
                  -- IN, not EXISTS: SQLite builds the list's index once, where EXISTS read the JSON again for every row
                  -- (7 s rather than 0.6 s over 100 000 submissions on the Pi)
                  AND ($known IS NULL OR (s.level_file, s.level_hash) IN (SELECT f, h FROM known))),
            -- Provisional (#11): a level's versions count together, so a player's best is per level file, whatever its
            -- hash. Counting only the newest version would partition by the file and the hash the newest table names.
            best AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY player_id, level_file ORDER BY score DESC, seq ASC) AS pick
                FROM clears),
            totals AS (
                SELECT player_id, MAX(name) AS name, COUNT(*) AS levels, SUM(stars) AS stars, SUM(score) AS score,
                       MAX(seq) AS reached
                FROM best WHERE pick = 1 GROUP BY player_id)
            SELECT ROW_NUMBER() OVER (ORDER BY t.score DESC, t.reached ASC) AS rank, t.name, t.levels, t.stars, t.score,
                   (SELECT received_at FROM submissions WHERE rowid = t.reached)
            FROM totals t
            ORDER BY rank
            LIMIT $limit OFFSET $offset
            """, ("$rules", rules), ("$month", (object?)month ?? DBNull.Value), ("$known", (object?)boards ?? DBNull.Value),
            ("$limit", limit), ("$offset", offset));

        List<RankingRow> rows = new();
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new RankingRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4),
                DateTimeOffset.Parse(r.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        return rows;
    }
}
