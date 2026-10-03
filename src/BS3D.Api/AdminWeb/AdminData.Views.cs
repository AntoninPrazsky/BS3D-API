using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// The boards and the players (issue #5, requirement 12). An address hash only ever stands in a comparison or a
/// count inside SQL — how many addresses a player used, who shares one with whom — and never in a result.
/// </summary>
public sealed partial class AdminData
{
    /// <param name="MonthPlayers">The rows of this month's board, as the game's boards count them.</param>
    /// <param name="Unfinished">Unfinished attempts (0 stars, #8), hidden players' and those no board shows included, as <paramref name="Clears"/> counts every clear.</param>
    /// <param name="Chapter">The level's chapter (a level set's <c>block</c>), when a ceiling table names one for its file.</param>
    public sealed record BoardRow(BoardKey Key, string? Name, int? Ceiling, int? Shots, int MonthPlayers, int AllPlayers,
        int Clears, int Unfinished, DateTimeOffset? LastPlayed, bool Known, string? Chapter = null);

    /// <summary>A hidden player's best on a board by the boards' rule: their best clear, or their best unfinished attempt if they have none.</summary>
    public sealed record HiddenEntry(Guid Player, string Name, int Score, int Stars, int Clears, int Unfinished);

    /// <param name="Scores">The all-time board's rows by their score's share of the ceiling, when a table names one (#9).</param>
    public sealed record BoardView(BoardKey Key, CeilingRow? Ceiling, string Month,
        IReadOnlyList<BoardEntry> MonthEntries, int MonthTotal, IReadOnlyList<BoardEntry> AllEntries, int AllTotal,
        IReadOnlyList<HiddenEntry> Hidden, ScoreBins? Scores = null);

    /// <summary>
    /// A board's rows in tenths of its ceiling, clears and unfinished attempts apart: <c>[0]</c> holds 0 to 10 %, <c>[9]</c>
    /// 90 % up to the ceiling itself.
    /// </summary>
    public sealed record ScoreBins(int[] Clears, int[] Unfinished);

    /// <summary>One level of the funnel (#9), every version of it together.</summary>
    /// <param name="Cleared">Visible players who cleared it, on any version.</param>
    /// <param name="Unfinished">Visible players who tried it and never cleared any version: their row on its boards is unfinished.</param>
    public sealed record FunnelLevel(string File, string Name, string? Chapter, int Cleared, int Unfinished);

    public sealed record PlayerRow(Guid Id, string Name, DateTimeOffset Created, bool Hidden, int Clears, int Unfinished,
        DateTimeOffset? LastPlayed, int Addresses, IReadOnlyList<(Guid Id, string Name)> SharesWith);

    /// <summary>One accepted submission: a clear, or with 0 stars an unfinished attempt (#8).</summary>
    public sealed record PlayerSubmission(DateTimeOffset At, BoardKey Board, int Score, int Stars, int Shots, double Seconds,
        string GameVersion, string? UserAgent, string Address);

    /// <summary>A board the player has sent to: their best by the boards' rule (<see cref="HiddenEntry"/>'s), and where they stand.</summary>
    public sealed record PlayerBoard(BoardKey Board, int Best, int BestStars, BoardRank Month, BoardRank AllTime);

    public sealed record PlayerView(PlayerRow Player, IReadOnlyList<PlayerBoard> Boards, IReadOnlyList<PlayerSubmission> Submissions);

    /// <summary>Entries a board view lists per period; the view says how many there are in all.</summary>
    public const int BoardLimit = 100;

    /// <summary>The players list's sort orders: a fixed set, so a query string never reaches the SQL.</summary>
    public static readonly IReadOnlyDictionary<string, string> PlayerOrders = new Dictionary<string, string>
    {
        ["name"] = "p.name COLLATE NOCASE, p.created_at",
        ["created"] = "p.created_at DESC",
        ["last"] = "last_played DESC, p.name COLLATE NOCASE",
        ["clears"] = "clears DESC, p.name COLLATE NOCASE",
    };

    public Ceilings LoadCeilings() => Ceilings.Load(options.CeilingsDirectory, NullLogger.Instance);

    /// <summary>
    /// Every board a ceiling table names, chapter by chapter in play order, then every board with a submission on it that
    /// no table names. Place and chapter belong to a level's file (<see cref="Ceilings.Levels"/>), so every version of a
    /// level stands together, in its chapter. The players are counted by the game's own summary (<c>GET /v1/boards</c>),
    /// so a count is the length of the board the game shows.
    /// </summary>
    public IReadOnlyList<BoardRow> ReadBoards(Ceilings ceilings)
    {
        using SqliteConnection c = Open();
        ScoreStore store = new(options.Database);
        Dictionary<BoardKey, int> month = store.Summary(c, ScoreStore.MonthOf(options.Clock.GetUtcNow()), null)
            .ToDictionary(b => new BoardKey(b.File, b.Hash, b.Rules), b => b.Total);
        Dictionary<BoardKey, int> all = store.Summary(c, null, null).ToDictionary(b => new BoardKey(b.File, b.Hash, b.Rules), b => b.Total);

        Dictionary<BoardKey, (int Clears, int Unfinished, DateTimeOffset? Last)> counts = new();
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT level_file, level_hash, rules_version, COUNT(CASE WHEN stars > 0 THEN 1 END),
                       COUNT(CASE WHEN stars = 0 THEN 1 END), MAX(received_at)
                FROM submissions GROUP BY level_file, level_hash, rules_version
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                counts[new BoardKey(r.GetString(0), r.GetString(1), r.GetInt32(2))] = (r.GetInt32(3), r.GetInt32(4), Time(r.GetString(5)));
        }

        BoardRow Row(BoardKey key, CeilingRow? row, bool known, string? chapter)
        {
            var n = counts.GetValueOrDefault(key);
            return new BoardRow(key, row?.Name, row?.Ceiling, row?.Shots, month.GetValueOrDefault(key), all.GetValueOrDefault(key),
                n.Clears, n.Unfinished, n.Last, known, chapter);
        }

        var known = ceilings.All.Select(row =>
        {
            BoardKey key = new(row.File, row.Hash, row.RulesVersion);
            var (position, chapter) = ceilings.Levels[row.File];
            return (Row: Row(key, row, true, chapter), Position: position);
        }).ToList();
        // A chapter comes where its first level does, its name settling a tie so that two chapters never mix; levels
        // without one come after every chapter
        Dictionary<string, int> opens = known.Where(k => k.Row.Chapter != null)
            .GroupBy(k => k.Row.Chapter!).ToDictionary(g => g.Key, g => g.Min(k => k.Position));
        HashSet<BoardKey> named = known.Select(k => k.Row.Key).ToHashSet();
        return known
            .OrderBy(k => k.Row.Chapter is { } chapter ? opens[chapter] : int.MaxValue)
            .ThenBy(k => k.Row.Chapter, StringComparer.Ordinal).ThenBy(k => k.Position)
            .ThenBy(k => k.Row.Name ?? k.Row.Key.File, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Row.Key.Hash, StringComparer.Ordinal)
            .Select(k => k.Row)
            .Concat(counts.Where(n => !named.Contains(n.Key)).OrderBy(n => n.Key.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Key.Hash, StringComparer.Ordinal)
                .Select(n => Row(n.Key, null, false, null)))
            .ToList();
    }

    /// <summary>One board as <c>GET /v1/boards</c> answers it, this month and all time, and its hidden players apart. Null when nothing knows it.</summary>
    public BoardView? ReadBoard(BoardKey key, Ceilings ceilings)
    {
        using SqliteConnection c = Open();
        ScoreStore boards = new(options.Database);
        string month = ScoreStore.MonthOf(options.Clock.GetUtcNow());
        ceilings.TryGet(key.File, key.Hash, key.Rules, out CeilingRow? ceiling);

        List<HiddenEntry> hidden = new();
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                WITH theirs AS (
                    SELECT p.id, p.name, s.score, s.stars,
                           ROW_NUMBER() OVER (PARTITION BY p.id ORDER BY s.stars > 0 DESC, s.score DESC, s.rowid) AS pick,
                           COUNT(CASE WHEN s.stars > 0 THEN 1 END) OVER (PARTITION BY p.id) AS clears,
                           COUNT(CASE WHEN s.stars = 0 THEN 1 END) OVER (PARTITION BY p.id) AS unfinished
                    FROM submissions s JOIN players p ON p.id = s.player_id
                    WHERE s.level_file = $f AND s.level_hash = $h AND s.rules_version = $r AND p.hidden = 1)
                SELECT id, name, score, stars, clears, unfinished FROM theirs WHERE pick = 1 ORDER BY stars > 0 DESC, score DESC
                """;
            cmd.Parameters.AddWithValue("$f", key.File);
            cmd.Parameters.AddWithValue("$h", key.Hash);
            cmd.Parameters.AddWithValue("$r", key.Rules);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) hidden.Add(new HiddenEntry(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
        }

        int allTotal = boards.Total(c, key, null);
        if (ceiling == null && allTotal == 0 && hidden.Count == 0) return null;

        ScoreBins? bins = null;
        if (ceiling is { Ceiling: > 0 } row)
        {
            bins = new ScoreBins(new int[10], new int[10]);
            foreach (BoardEntry e in boards.Page(c, key, null, int.MaxValue, 0))
                (e.Stars > 0 ? bins.Clears : bins.Unfinished)[Math.Clamp((int)((long)e.Score * 10 / row.Ceiling), 0, 9)]++;
        }
        return new BoardView(key, ceiling, month,
            boards.Page(c, key, month, BoardLimit, 0), boards.Total(c, key, month),
            boards.Page(c, key, null, BoardLimit, 0), allTotal, hidden, bins);
    }

    /// <summary>
    /// Every level a ceiling table names, in the boards list's order (chapter by chapter, play order within), with how many
    /// visible players cleared it and how many tried it and never did, every version of it together: where players stop.
    /// </summary>
    public IReadOnlyList<FunnelLevel> ReadFunnel(Ceilings ceilings)
    {
        using SqliteConnection c = Open();
        Dictionary<string, (int Cleared, int Unfinished)> counts = new();
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT f, SUM(cleared), SUM(1 - cleared) FROM (
                    SELECT s.level_file AS f, MAX(s.stars > 0) AS cleared
                    FROM submissions s JOIN players p ON p.id = s.player_id
                    WHERE p.hidden = 0 GROUP BY s.level_file, s.player_id)
                GROUP BY f
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) counts[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2));
        }

        Dictionary<string, string> names = ceilings.All.GroupBy(row => row.File)
            .ToDictionary(g => g.Key, g => g.Select(row => row.Name).LastOrDefault(name => name != null) ?? g.Key);
        var levels = ceilings.Levels.Select(l => (File: l.Key, l.Value.Position, Chapter: l.Value.Block)).ToList();
        Dictionary<string, int> opens = levels.Where(l => l.Chapter != null).GroupBy(l => l.Chapter!).ToDictionary(g => g.Key, g => g.Min(l => l.Position));
        return levels
            .OrderBy(l => l.Chapter is { } chapter ? opens[chapter] : int.MaxValue).ThenBy(l => l.Chapter, StringComparer.Ordinal)
            .ThenBy(l => l.Position).ThenBy(l => l.File, StringComparer.OrdinalIgnoreCase)
            .Select(l =>
            {
                var n = counts.GetValueOrDefault(l.File);
                return new FunnelLevel(l.File, names.GetValueOrDefault(l.File, l.File), l.Chapter, n.Cleared, n.Unfinished);
            }).ToList();
    }

    /// <summary>Every player, in one of <see cref="PlayerOrders"/> (an unknown order falls back to the name).</summary>
    public IReadOnlyList<PlayerRow> ReadPlayers(string? order)
    {
        using SqliteConnection c = Open();
        Dictionary<Guid, string> names = new();
        List<(Guid Id, string Name, DateTimeOffset Created, bool Hidden, int Clears, int Unfinished, DateTimeOffset? Last, int Addresses)> players = new();
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT p.id, p.name, p.created_at, p.hidden, COUNT(CASE WHEN s.stars > 0 THEN 1 END) AS clears,
                       COUNT(CASE WHEN s.stars = 0 THEN 1 END), MAX(s.received_at) AS last_played, COUNT(DISTINCT s.ip_hash)
                FROM players p LEFT JOIN submissions s ON s.player_id = p.id
                GROUP BY p.id ORDER BY {PlayerOrders.GetValueOrDefault(order ?? "", PlayerOrders["name"])}
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                Guid id = Guid.Parse(r.GetString(0));
                names[id] = r.GetString(1);
                players.Add((id, r.GetString(1), Time(r.GetString(2)), r.GetInt64(3) == 1, r.GetInt32(4), r.GetInt32(5),
                    r.IsDBNull(6) ? null : Time(r.GetString(6)), r.GetInt32(7)));
            }
        }
        ILookup<Guid, Guid> shares = Sharing(c);
        return players.Select(p => new PlayerRow(p.Id, p.Name, p.Created, p.Hidden, p.Clears, p.Unfinished, p.Last, p.Addresses,
            shares[p.Id].Select(other => (other, names[other])).ToList())).ToList();
    }

    /// <summary>One player: every submission, newest first, the addresses lettered A, B… in the order they first appear, and where they stand on each board.</summary>
    public PlayerView? ReadPlayer(Guid id)
    {
        PlayerRow? player = ReadPlayers(null).FirstOrDefault(p => p.Id == id);
        if (player == null) return null;

        using SqliteConnection c = Open();
        List<(DateTimeOffset At, BoardKey Board, int Score, int Stars, int Shots, double Seconds, string Version, string? Agent, string Hash)> rows = new();
        using (SqliteCommand cmd = Command(c, """
            SELECT received_at, level_file, level_hash, rules_version, score, stars, shots_used, duration_seconds, game_version, user_agent, ip_hash
            FROM submissions WHERE player_id = $p ORDER BY rowid
            """, "$p", id.ToString()))
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add((Time(r.GetString(0)), new BoardKey(r.GetString(1), r.GetString(2), r.GetInt32(3)), r.GetInt32(4), r.GetInt32(5),
                    r.GetInt32(6), r.GetDouble(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10)));

        // The hash stays here: what leaves this method is its letter
        Dictionary<string, string> letters = new();
        foreach (var row in rows)
            if (!letters.ContainsKey(row.Hash)) letters[row.Hash] = Letter(letters.Count);

        ScoreStore boards = new(options.Database);
        string month = ScoreStore.MonthOf(options.Clock.GetUtcNow());
        List<PlayerBoard> standing = rows.GroupBy(r => r.Board).Select(g =>
        {
            var best = g.OrderByDescending(r => r.Stars > 0).ThenByDescending(r => r.Score).First();
            return new PlayerBoard(g.Key, best.Score, best.Stars, boards.RankOf(c, g.Key, month, id).Rank, boards.RankOf(c, g.Key, null, id).Rank);
        }).ToList();

        return new PlayerView(player, standing.OrderBy(b => b.Board.File, StringComparer.OrdinalIgnoreCase).ToList(),
            rows.Select(r => new PlayerSubmission(r.At, r.Board, r.Score, r.Stars, r.Shots, r.Seconds, r.Version, r.Agent, letters[r.Hash]))
                .Reverse().ToList());
    }

    /// <summary>Which other players each player shares an address with.</summary>
    private static ILookup<Guid, Guid> Sharing(SqliteConnection c)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT a.player_id, b.player_id
            FROM submissions a JOIN submissions b ON b.ip_hash = a.ip_hash AND b.player_id <> a.player_id
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        List<(Guid, Guid)> pairs = new();
        while (r.Read()) pairs.Add((Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1))));
        return pairs.ToLookup(p => p.Item1, p => p.Item2);
    }

    private static string Letter(int index) =>
        index < 26 ? ((char)('A' + index)).ToString() : string.Create(CultureInfo.InvariantCulture, $"Z{index - 25}");
}
