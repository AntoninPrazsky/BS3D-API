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
    /// <param name="Chapter">The level's chapter (a level set's <c>block</c>), when a ceiling table names one for its file.</param>
    public sealed record BoardRow(BoardKey Key, string? Name, int? Ceiling, int? Shots, int MonthPlayers, int AllPlayers,
        int Clears, DateTimeOffset? LastClear, bool Known, string? Chapter = null);

    public sealed record HiddenEntry(Guid Player, string Name, int Score, int Stars, int Clears);

    public sealed record BoardView(BoardKey Key, CeilingRow? Ceiling, string Month,
        IReadOnlyList<BoardEntry> MonthEntries, int MonthTotal, IReadOnlyList<BoardEntry> AllEntries, int AllTotal,
        IReadOnlyList<HiddenEntry> Hidden);

    public sealed record PlayerRow(Guid Id, string Name, DateTimeOffset Created, bool Hidden, int Clears,
        DateTimeOffset? LastClear, int Addresses, IReadOnlyList<(Guid Id, string Name)> SharesWith);

    public sealed record PlayerClear(DateTimeOffset At, BoardKey Board, int Score, int Stars, int Shots, double Seconds,
        string GameVersion, string? UserAgent, string Address);

    public sealed record PlayerBoard(BoardKey Board, int Best, BoardRank Month, BoardRank AllTime);

    public sealed record PlayerView(PlayerRow Player, IReadOnlyList<PlayerBoard> Boards, IReadOnlyList<PlayerClear> Clears);

    /// <summary>Entries a board view lists per period; the view says how many there are in all.</summary>
    public const int BoardLimit = 100;

    /// <summary>The players list's sort orders: a fixed set, so a query string never reaches the SQL.</summary>
    public static readonly IReadOnlyDictionary<string, string> PlayerOrders = new Dictionary<string, string>
    {
        ["name"] = "p.name COLLATE NOCASE, p.created_at",
        ["created"] = "p.created_at DESC",
        ["last"] = "last_clear DESC, p.name COLLATE NOCASE",
        ["clears"] = "clears DESC, p.name COLLATE NOCASE",
    };

    public Ceilings LoadCeilings() => Ceilings.Load(options.CeilingsDirectory, NullLogger.Instance);

    /// <summary>
    /// Every board a ceiling table names, chapter by chapter in play order, then every board with a clear on it that no
    /// table names. Place and chapter belong to a level's file (<see cref="Ceilings.Levels"/>), so every version of a
    /// level stands together, in its chapter.
    /// </summary>
    public IReadOnlyList<BoardRow> ReadBoards(Ceilings ceilings)
    {
        using SqliteConnection c = Open();
        Dictionary<BoardKey, (int Month, int All, int Clears, DateTimeOffset? Last)> counts = new();
        using (SqliteCommand cmd = Command(c, """
            SELECT s.level_file, s.level_hash, s.rules_version,
                   COUNT(DISTINCT CASE WHEN p.hidden = 0 AND s.month = $month THEN s.player_id END),
                   COUNT(DISTINCT CASE WHEN p.hidden = 0 THEN s.player_id END),
                   COUNT(*), MAX(s.received_at)
            FROM submissions s JOIN players p ON p.id = s.player_id
            GROUP BY s.level_file, s.level_hash, s.rules_version
            """, "$month", ScoreStore.MonthOf(options.Clock.GetUtcNow())))
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read())
                counts[new BoardKey(r.GetString(0), r.GetString(1), r.GetInt32(2))] = (r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), Time(r.GetString(6)));

        var known = ceilings.All.Select(row =>
        {
            BoardKey key = new(row.File, row.Hash, row.RulesVersion);
            var n = counts.GetValueOrDefault(key);
            var (position, chapter) = ceilings.Levels[row.File];
            return (Row: new BoardRow(key, row.Name, row.Ceiling, row.Shots, n.Month, n.All, n.Clears, n.Last, Known: true, chapter), Position: position);
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
                .Select(n => new BoardRow(n.Key, null, null, null, n.Value.Month, n.Value.All, n.Value.Clears, n.Value.Last, Known: false)))
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
                SELECT p.id, p.name, MAX(s.score), COUNT(*),
                       (SELECT s2.stars FROM submissions s2 WHERE s2.player_id = p.id AND s2.level_file = $f AND s2.level_hash = $h
                          AND s2.rules_version = $r ORDER BY s2.score DESC, s2.rowid LIMIT 1)
                FROM submissions s JOIN players p ON p.id = s.player_id
                WHERE s.level_file = $f AND s.level_hash = $h AND s.rules_version = $r AND p.hidden = 1
                GROUP BY p.id ORDER BY 3 DESC
                """;
            cmd.Parameters.AddWithValue("$f", key.File);
            cmd.Parameters.AddWithValue("$h", key.Hash);
            cmd.Parameters.AddWithValue("$r", key.Rules);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) hidden.Add(new HiddenEntry(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt32(2), r.GetInt32(4), r.GetInt32(3)));
        }

        int allTotal = boards.Total(c, key, null);
        if (ceiling == null && allTotal == 0 && hidden.Count == 0) return null;
        return new BoardView(key, ceiling, month,
            boards.Page(c, key, month, BoardLimit, 0), boards.Total(c, key, month),
            boards.Page(c, key, null, BoardLimit, 0), allTotal, hidden);
    }

    /// <summary>Every player, in one of <see cref="PlayerOrders"/> (an unknown order falls back to the name).</summary>
    public IReadOnlyList<PlayerRow> ReadPlayers(string? order)
    {
        using SqliteConnection c = Open();
        Dictionary<Guid, string> names = new();
        List<(Guid Id, string Name, DateTimeOffset Created, bool Hidden, int Clears, DateTimeOffset? Last, int Addresses)> players = new();
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT p.id, p.name, p.created_at, p.hidden, COUNT(s.id) AS clears, MAX(s.received_at) AS last_clear,
                       COUNT(DISTINCT s.ip_hash)
                FROM players p LEFT JOIN submissions s ON s.player_id = p.id
                GROUP BY p.id ORDER BY {PlayerOrders.GetValueOrDefault(order ?? "", PlayerOrders["name"])}
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                Guid id = Guid.Parse(r.GetString(0));
                names[id] = r.GetString(1);
                players.Add((id, r.GetString(1), Time(r.GetString(2)), r.GetInt64(3) == 1, r.GetInt32(4),
                    r.IsDBNull(5) ? null : Time(r.GetString(5)), r.GetInt32(6)));
            }
        }
        ILookup<Guid, Guid> shares = Sharing(c);
        return players.Select(p => new PlayerRow(p.Id, p.Name, p.Created, p.Hidden, p.Clears, p.Last, p.Addresses,
            shares[p.Id].Select(other => (other, names[other])).ToList())).ToList();
    }

    /// <summary>One player: every clear, newest first, the addresses lettered A, B… in the order they first appear, and where they stand on each board.</summary>
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
        List<PlayerBoard> standing = rows.GroupBy(r => r.Board).Select(g => new PlayerBoard(g.Key, g.Max(r => r.Score),
            boards.RankOf(c, g.Key, month, id).Rank, boards.RankOf(c, g.Key, null, id).Rank)).ToList();

        return new PlayerView(player, standing.OrderBy(b => b.Board.File, StringComparer.OrdinalIgnoreCase).ToList(),
            rows.Select(r => new PlayerClear(r.At, r.Board, r.Score, r.Stars, r.Shots, r.Seconds, r.Version, r.Agent, letters[r.Hash]))
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
