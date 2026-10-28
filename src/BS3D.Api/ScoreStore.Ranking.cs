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
    /// <para>
    /// The status snapshot's Top players (#13) reads it over every build's scores; <c>GET /v1/ranking</c> (#11) answers
    /// with it over the scores the asking build sees (<paramref name="builds"/>, #17): a release never ranks a score a
    /// dev build sent.
    /// </para>
    /// </summary>
    public List<RankingRow> Ranking(SqliteConnection c, int rules, string? month, IEnumerable<BoardKey>? known, int limit, int offset = 0,
        Builds builds = Builds.All) =>
        RankPlayers(c, rules, month, known, limit, offset, null, builds).Page;

    /// <summary>
    /// One page of <see cref="Ranking"/>, how many players it ranks in all, and <paramref name="player"/>'s own row
    /// wherever it falls (null with no player, or when they are not ranked): what <c>GET /v1/ranking</c> answers (#11).
    /// One statement, so the page, the count and the player's place cannot rank differently.
    /// </summary>
    public (int Total, List<RankingRow> Page, RankingRow? Me) RankPlayers(SqliteConnection c, int rules, string? month,
        IEnumerable<BoardKey>? known, int limit, int offset, Guid? player, Builds builds = Builds.All)
    {
        string? boards = known == null ? null
            : JsonSerializer.Serialize(known.Where(b => b.Rules == rules).Select(b => new[] { b.File, b.Hash }));
        using SqliteCommand cmd = Command(c, $"""
            WITH known AS (
                SELECT json_extract(value, '$[0]') AS f, json_extract(value, '$[1]') AS h FROM json_each($known)),
            clears AS (
                SELECT s.rowid AS seq, s.player_id, p.name, s.level_file, s.score, s.stars
                FROM submissions s JOIN players p ON p.id = s.player_id
                WHERE p.hidden = 0 AND s.stars > 0 AND s.rules_version = $rules AND {ShownTo}
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
                FROM best WHERE pick = 1 GROUP BY player_id),
            ranked AS (
                SELECT ROW_NUMBER() OVER (ORDER BY score DESC, reached ASC) AS rank, player_id, name, levels, stars, score, reached
                FROM totals)
            -- The count's row comes even when no player falls on the page, so a page past the end still says how many there are
            SELECT n.total, r.rank, r.name, r.levels, r.stars, r.score,
                   (SELECT received_at FROM submissions WHERE rowid = r.reached), COALESCE(r.player_id = $player, 0)
            FROM (SELECT COUNT(*) AS total FROM totals) n
            LEFT JOIN ranked r ON (r.rank > $offset AND r.rank <= $offset + $limit) OR r.player_id = $player
            ORDER BY r.rank
            """, ("$rules", rules), ("$month", (object?)month ?? DBNull.Value), ("$known", (object?)boards ?? DBNull.Value),
            ("$limit", limit), ("$offset", offset), ("$player", (object?)player?.ToString() ?? DBNull.Value),
            ("$releases", ReleasesOnly(builds)));

        int total = 0;
        List<RankingRow> page = new();
        RankingRow? me = null;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            total = r.GetInt32(0);
            if (r.IsDBNull(1)) continue;

            RankingRow row = new(r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt64(5),
                DateTimeOffset.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture));
            if (row.Rank > offset && row.Rank <= (long)offset + limit) page.Add(row);
            if (r.GetInt64(7) == 1) me = row;
        }

        return (total, page, me);
    }
}
