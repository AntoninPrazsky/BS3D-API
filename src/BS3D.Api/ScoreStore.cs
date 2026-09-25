using Microsoft.Data.Sqlite;

namespace BS3D.Api;

/// <summary>
/// The service's whole state: one SQLite file, two tables (issue #1).
/// <para>
/// <b>Boards are views over an append-only log, not tables that get updated.</b> Every accepted submission is a row
/// of <c>submissions</c> and stays one — a clear below the player's best is kept as the record of what was sent and
/// changes no rank — and a board is a query: the best clear per player on a key, over one UTC month or over all
/// time, ranked with window functions. So a ranking rule can change without a migration of what was recorded, and a
/// board that reads wrong can be recomputed rather than repaired. The only columns ever updated are
/// <c>players.name</c> and <c>players.hidden</c>, and a submission's stored <c>answer</c>, written once in the same
/// transaction that inserts it — kept so a retried submission is answered with what it was answered the first time.
/// </para>
/// <para>
/// <b>Ties go to the earlier submission</b>, by insertion order (<c>rowid</c>), which is also what "earlier" means to
/// a player: two equal scores rank in the order they arrived.
/// </para>
/// </summary>
public sealed class ScoreStore(string path)
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    public SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    /// <summary>Creates or migrates the schema; applied at every start (issue #3's update is new files and a restart).</summary>
    public void EnsureSchema()
    {
        using SqliteConnection c = Open();
        Execute(c, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        Execute(c, """
            CREATE TABLE IF NOT EXISTS players (
                id TEXT PRIMARY KEY,
                token_hash TEXT NOT NULL,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                hidden INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS submissions (
                id TEXT PRIMARY KEY,
                player_id TEXT NOT NULL REFERENCES players(id) ON DELETE CASCADE,
                level_file TEXT NOT NULL,
                level_hash TEXT NOT NULL,
                rules_version INTEGER NOT NULL,
                score INTEGER NOT NULL,
                stars INTEGER NOT NULL,
                shots_used INTEGER NOT NULL,
                duration_seconds REAL NOT NULL,
                game_version TEXT NOT NULL,
                ip_hash TEXT NOT NULL,
                user_agent TEXT,
                received_at TEXT NOT NULL,
                month TEXT NOT NULL,
                answer TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_submissions_board ON submissions(level_file, level_hash, rules_version, month);
            CREATE INDEX IF NOT EXISTS ix_submissions_player ON submissions(player_id);
            """);
        Execute(c, $"PRAGMA user_version = {SchemaVersion};");
    }

    public sealed record Player(Guid Id, string TokenHash, string Name, bool Hidden);

    public Player? FindPlayer(SqliteConnection c, Guid id)
    {
        using SqliteCommand cmd = Command(c, "SELECT token_hash, name, hidden FROM players WHERE id = $id", ("$id", id.ToString()));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? new Player(id, r.GetString(0), r.GetString(1), r.GetInt64(2) != 0) : null;
    }

    public void CreatePlayer(SqliteConnection c, Guid id, string tokenHash, string name, DateTimeOffset now) =>
        Command(c, "INSERT INTO players (id, token_hash, name, created_at) VALUES ($id, $t, $n, $at)",
            ("$id", id.ToString()), ("$t", tokenHash), ("$n", name), ("$at", now.ToString("o"))).ExecuteNonQuery();

    public void RenamePlayer(SqliteConnection c, Guid id, string name) =>
        Command(c, "UPDATE players SET name = $n WHERE id = $id", ("$id", id.ToString()), ("$n", name)).ExecuteNonQuery();

    public int SetHidden(SqliteConnection c, Guid id, bool hidden) =>
        Command(c, "UPDATE players SET hidden = $h WHERE id = $id", ("$id", id.ToString()), ("$h", hidden ? 1 : 0)).ExecuteNonQuery();

    /// <summary>The player and every submission of theirs (the foreign key cascades). Returns the submissions removed.</summary>
    public int DeletePlayer(SqliteConnection c, Guid id)
    {
        Begin(c);
        try
        {
            int removed = Convert.ToInt32(Command(c, "SELECT COUNT(*) FROM submissions WHERE player_id = $id", ("$id", id.ToString())).ExecuteScalar());
            Command(c, "DELETE FROM players WHERE id = $id", ("$id", id.ToString())).ExecuteNonQuery();
            Commit(c);
            return removed;
        }
        catch
        {
            Rollback(c);
            throw;
        }
    }

    /// <summary>A submission already recorded under this id: whose it is and what it was answered with.</summary>
    public (Guid PlayerId, string Answer)? FindSubmission(SqliteConnection c, Guid id)
    {
        using SqliteCommand cmd = Command(c, "SELECT player_id, answer FROM submissions WHERE id = $id", ("$id", id.ToString()));
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? (Guid.Parse(r.GetString(0)), r.GetString(1)) : null;
    }

    /// <summary>The player's best score on a board over all time, or null for a first clear there.</summary>
    public int? BestScore(SqliteConnection c, BoardKey board, Guid player)
    {
        object? best = Command(c, """
            SELECT MAX(score) FROM submissions
            WHERE level_file = $f AND level_hash = $h AND rules_version = $r AND player_id = $p
            """, ("$f", board.File), ("$h", board.Hash), ("$r", board.Rules), ("$p", player.ToString())).ExecuteScalar();
        return best is null or DBNull ? null : Convert.ToInt32(best);
    }

    public sealed record NewSubmission(
        Guid Id, Guid PlayerId, BoardKey Board, int Score, int Stars, int ShotsUsed, double DurationSeconds,
        string GameVersion, string IpHash, string? UserAgent, DateTimeOffset ReceivedAt);

    public void Insert(SqliteConnection c, NewSubmission s) =>
        Command(c, """
            INSERT INTO submissions (id, player_id, level_file, level_hash, rules_version, score, stars, shots_used,
                duration_seconds, game_version, ip_hash, user_agent, received_at, month, answer)
            VALUES ($id, $p, $f, $h, $r, $score, $stars, $shots, $dur, $ver, $ip, $ua, $at, $month, '')
            """,
            ("$id", s.Id.ToString()), ("$p", s.PlayerId.ToString()), ("$f", s.Board.File), ("$h", s.Board.Hash),
            ("$r", s.Board.Rules), ("$score", s.Score), ("$stars", s.Stars), ("$shots", s.ShotsUsed),
            ("$dur", s.DurationSeconds), ("$ver", s.GameVersion), ("$ip", s.IpHash), ("$ua", (object?)s.UserAgent ?? DBNull.Value),
            ("$at", s.ReceivedAt.ToString("o")), ("$month", MonthOf(s.ReceivedAt))).ExecuteNonQuery();

    public void SetAnswer(SqliteConnection c, Guid id, string answer) =>
        Command(c, "UPDATE submissions SET answer = $a WHERE id = $id", ("$id", id.ToString()), ("$a", answer)).ExecuteNonQuery();

    /// <summary>
    /// The best clear per visible player on a board — over one month, or over all time when <paramref name="month"/>
    /// is null — ranked by score and then by arrival. Every board query goes through this one statement.
    /// </summary>
    private const string Ranked = """
        WITH best AS (
            SELECT s.player_id, p.name, s.score, s.stars, s.received_at, s.rowid AS seq,
                   ROW_NUMBER() OVER (PARTITION BY s.player_id ORDER BY s.score DESC, s.rowid ASC) AS pick
            FROM submissions s JOIN players p ON p.id = s.player_id
            WHERE s.level_file = $f AND s.level_hash = $h AND s.rules_version = $r AND p.hidden = 0
              AND ($month IS NULL OR s.month = $month)),
        ranked AS (
            SELECT player_id, name, score, stars, received_at,
                   ROW_NUMBER() OVER (ORDER BY score DESC, seq ASC) AS rank,
                   COUNT(*) OVER () AS total
            FROM best WHERE pick = 1)
        """;

    /// <summary>Where <paramref name="player"/> stands on a board; rank 0 when they are not on it.</summary>
    public (BoardRank Rank, int Score, int Stars) RankOf(SqliteConnection c, BoardKey board, string? month, Guid player)
    {
        using SqliteCommand cmd = BoardCommand(c, board, month, Ranked + " SELECT rank, total, score, stars FROM ranked WHERE player_id = $p");
        cmd.Parameters.AddWithValue("$p", player.ToString());
        using SqliteDataReader r = cmd.ExecuteReader();
        if (r.Read()) return (new BoardRank(r.GetInt32(0), r.GetInt32(1)), r.GetInt32(2), r.GetInt32(3));

        return (new BoardRank(0, Total(c, board, month)), 0, 0);
    }

    public int Total(SqliteConnection c, BoardKey board, string? month) =>
        Convert.ToInt32(BoardCommand(c, board, month, Ranked + " SELECT COUNT(*) FROM ranked").ExecuteScalar());

    public List<BoardEntry> Page(SqliteConnection c, BoardKey board, string? month, int limit, int offset)
    {
        using SqliteCommand cmd = BoardCommand(c, board, month,
            Ranked + " SELECT rank, name, score, stars, received_at FROM ranked ORDER BY rank LIMIT $limit OFFSET $offset");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        List<BoardEntry> entries = new();
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
            entries.Add(new BoardEntry(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), DateTimeOffset.Parse(r.GetString(4))));
        return entries;
    }

    /// <summary>Every submission with its player's current name, oldest first — the admin CLI's export.</summary>
    public IEnumerable<Dictionary<string, object?>> Export(SqliteConnection c)
    {
        using SqliteCommand cmd = Command(c, """
            SELECT s.id, s.player_id, p.name, p.hidden, s.level_file, s.level_hash, s.rules_version, s.score, s.stars,
                   s.shots_used, s.duration_seconds, s.game_version, s.ip_hash, s.user_agent, s.received_at
            FROM submissions s JOIN players p ON p.id = s.player_id ORDER BY s.rowid
            """);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            Dictionary<string, object?> row = new();
            for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            yield return row;
        }
    }

    //Transactions as SQL rather than SqliteTransaction objects: Microsoft.Data.Sqlite refuses a command whose
    //Transaction property is unset while one of those is open, and every command here is built by one helper.
    //IMMEDIATE takes the write lock at the start, so two submissions cannot interleave their read-then-write.
    public static void Begin(SqliteConnection c) => Execute(c, "BEGIN IMMEDIATE");
    public static void Commit(SqliteConnection c) => Execute(c, "COMMIT");
    public static void Rollback(SqliteConnection c) => Execute(c, "ROLLBACK");

    /// <summary>A month as the boards name it: the UTC calendar month, never a client's clock.</summary>
    public static string MonthOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM");

    private static SqliteCommand BoardCommand(SqliteConnection c, BoardKey board, string? month, string sql) =>
        Command(c, sql, ("$f", board.File), ("$h", board.Hash), ("$r", board.Rules), ("$month", (object?)month ?? DBNull.Value));

    private static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object Value)[] parameters)
    {
        SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private static void Execute(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = Command(c, sql);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>A board's key: the level's file, its hash and the rules version (BS3D#542).</summary>
public readonly record struct BoardKey(string File, string Hash, int Rules);
