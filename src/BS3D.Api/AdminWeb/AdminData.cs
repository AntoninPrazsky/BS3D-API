using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// What the admin page reads (issue #5, requirements 11–13): a read-only connection to the live database, and only
/// named columns. <c>token_hash</c> and <c>ip_hash</c> are never selected, so no page can show them.
/// </summary>
public sealed partial class AdminData(AdminWebOptions options)
{
    public sealed record Overview(
        long SchemaVersion, int Players, int HiddenPlayers, int Submissions, int Unfinished, int HiddenSubmissions,
        long DatabaseBytes, Backup? NewestBackup, IReadOnlyList<Day> Days, IReadOnlyList<RefusalDay> Refusals);

    /// <summary>One UTC day: the clears and unfinished attempts (0 stars, #8) accepted, and the players new that day.</summary>
    public sealed record Day(string Date, int Clears, int Unfinished, int NewPlayers);

    public sealed record RefusalDay(string Date, string Reason, int Count);

    public sealed record Backup(string Name, long Bytes, DateTimeOffset Written);

    /// <summary>One line of the live view: an accepted submission (a clear or an unfinished attempt) or a refusal.</summary>
    public sealed record Event(DateTimeOffset At, bool Accepted, string What, string Detail);

    /// <summary>Days the overview's tables cover, today included.</summary>
    public const int Days = 14;

    public SqliteConnection Open()
    {
        SqliteConnection c = new(new SqliteConnectionStringBuilder
        {
            DataSource = options.Database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        // ScoreStore.Open sets this on its own connections only; a read-only one starts without it
        Scalar(c, "PRAGMA busy_timeout = 5000");
        return c;
    }

    public Overview ReadOverview()
    {
        using SqliteConnection c = Open();
        string since = ScoreStore.DayOf(options.Clock.GetUtcNow().AddDays(-(Days - 1)));

        Dictionary<string, (int Clears, int Unfinished, int Players)> days = new();
        foreach (var (day, n) in Pairs(c, "SELECT substr(received_at, 1, 10) AS d, COUNT(*) FROM submissions WHERE d >= $since AND stars > 0 GROUP BY d", since))
            days[day] = days.GetValueOrDefault(day) with { Clears = n };
        foreach (var (day, n) in Pairs(c, "SELECT substr(received_at, 1, 10) AS d, COUNT(*) FROM submissions WHERE d >= $since AND stars = 0 GROUP BY d", since))
            days[day] = days.GetValueOrDefault(day) with { Unfinished = n };
        foreach (var (day, n) in Pairs(c, "SELECT substr(created_at, 1, 10) AS d, COUNT(*) FROM players WHERE d >= $since GROUP BY d", since))
            days[day] = days.GetValueOrDefault(day) with { Players = n };

        List<RefusalDay> refusals = new();
        using (SqliteCommand cmd = Command(c, "SELECT day, reason, count FROM refusal_days WHERE day >= $since ORDER BY day DESC, count DESC, reason", "$since", since))
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read()) refusals.Add(new RefusalDay(r.GetString(0), r.GetString(1), r.GetInt32(2)));

        return new Overview(
            Scalar(c, "PRAGMA user_version"),
            (int)Scalar(c, "SELECT COUNT(*) FROM players"),
            (int)Scalar(c, "SELECT COUNT(*) FROM players WHERE hidden = 1"),
            (int)Scalar(c, "SELECT COUNT(*) FROM submissions"),
            (int)Scalar(c, "SELECT COUNT(*) FROM submissions WHERE stars = 0"),
            (int)Scalar(c, "SELECT COUNT(*) FROM submissions s JOIN players p ON p.id = s.player_id WHERE p.hidden = 1"),
            FileBytes(options.Database) + FileBytes(options.Database + "-wal"),
            NewestBackup(),
            days.OrderByDescending(d => d.Key).Select(d => new Day(d.Key, d.Value.Clears, d.Value.Unfinished, d.Value.Players)).ToList(),
            refusals);
    }

    /// <summary>The newest accepted submissions and refusals, newest first.</summary>
    public IReadOnlyList<Event> ReadLive(int count)
    {
        using SqliteConnection c = Open();
        List<Event> events = new();
        using (SqliteCommand cmd = Command(c, """
            SELECT s.received_at, p.name, p.hidden, s.level_file, s.score, s.stars, s.game_version
            FROM submissions s JOIN players p ON p.id = s.player_id ORDER BY s.rowid DESC LIMIT $n
            """, "$n", count))
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read())
                events.Add(new Event(Time(r.GetString(0)), true, $"{r.GetString(1)}{(r.GetInt64(2) == 1 ? " (hidden)" : "")}",
                    $"{r.GetString(3)}: {r.GetInt32(4)}, {(r.GetInt32(5) > 0 ? $"{r.GetInt32(5)}★" : "unfinished")}, {r.GetString(6)}"));
        using (SqliteCommand cmd = Command(c, "SELECT at, status, reason, method, path, detail FROM refusal_log ORDER BY id DESC LIMIT $n", "$n", count))
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read())
                events.Add(new Event(Time(r.GetString(0)), false, $"{r.GetInt32(1)} {r.GetString(2)}",
                    $"{r.GetString(3)} {r.GetString(4)}: {Shown(r.GetString(5))}".Trim()));
        return events.OrderByDescending(e => e.At).Take(count).ToList();
    }

    /// <summary>
    /// A refusal's detail as the page may show it. A rate-limited address is named in the log by the salted hash the
    /// audit column keeps, so the owner can match it up in the database; the page shows no address hash at all.
    /// </summary>
    private static string Shown(string detail) =>
        detail.StartsWith("address ", StringComparison.Ordinal) ? "an address (its hash is not shown)" : detail;

    private Backup? NewestBackup()
    {
        string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Database))!, "backups");
        try
        {
            FileInfo? newest = new DirectoryInfo(folder).EnumerateFiles("scores-*.db").MaxBy(f => f.LastWriteTimeUtc);
            return newest == null ? null : new Backup(newest.Name, newest.Length, new DateTimeOffset(newest.LastWriteTimeUtc));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long FileBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static DateTimeOffset Time(string stored) =>
        DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static IEnumerable<(string, int)> Pairs(SqliteConnection c, string sql, string since)
    {
        using SqliteCommand cmd = Command(c, sql, "$since", since);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<(string, int)> rows = new();
        while (r.Read()) rows.Add((r.GetString(0), r.GetInt32(1)));
        return rows;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, string name, object value)
    {
        SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }
}
