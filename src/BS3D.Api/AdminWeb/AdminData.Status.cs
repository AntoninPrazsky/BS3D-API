using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// The status snapshot (#13): a small file of aggregates for the server's front panel (BS3D-LCD#1), which
/// <c>BS3D.Api admin status-feed</c> writes every 15 s. An admin process reads it, on the read-only connection, like
/// every other view of the admin page; the service the tunnel reaches has no part of it.
/// <para>
/// <b>Never in it:</b> <c>token_hash</c>, <c>ip_hash</c> and user agents (never selected); hidden players and their
/// submissions (in no count, no list, no ranking); a note's text, picture or context (only how many notes there are,
/// and since #15 when each of the newest came and who sent it). The nicknames are the ones the public boards show (the
/// owner, 2026-10-16).
/// </para>
/// </summary>
public sealed partial class AdminData
{
    /// <summary>The snapshot's format; a reader checks it. A new key is a decision, and <c>StatusTests</c> pin the list.</summary>
    public const int StatusFormat = 1;

    /// <summary>The newest clears listed.</summary>
    public const int StatusLatest = 5;

    /// <summary>The players of each ranking listed: the panel's rolling Top players (BS3D-LCD#5).</summary>
    public const int StatusRanked = 10;

    /// <summary>The newest submissions listed, clears and unfinished attempts together: the panel's three pages of three (#15, BS3D-LCD#7).</summary>
    public const int StatusRecent = 9;

    /// <summary>The players who played last listed, as many as <see cref="StatusRecent"/> (#15, BS3D-LCD#7).</summary>
    public const int StatusRecentPlayers = 9;

    /// <summary>The newest notes listed with their senders: the panel's alarm for a new note (#15, BS3D-LCD#8).</summary>
    public const int StatusRecentNotes = 10;

    public static readonly JsonSerializerOptions StatusJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="Hours">Submissions per hour, the last 24 hours, oldest first, the current hour last.</param>
    public sealed record Status(
        int Format, DateTimeOffset WrittenAt, StatusService Service, StatusTotals Totals, StatusToday Today,
        IReadOnlyDictionary<string, int> RefusalsLastHour, int[] Hours, DateTimeOffset? LastSubmissionAt,
        IReadOnlyList<StatusClear> Latest, StatusTop Top, StatusBackups Backups, StatusPlay? Play,
        IReadOnlyList<StatusClear> Recent, IReadOnlyList<StatusRecentPlayer> RecentPlayers, IReadOnlyList<StatusNote> NotesRecent);

    /// <param name="Schema">The database's <c>user_version</c>; <paramref name="ExpectedSchema"/> is the one this release reads.</param>
    public sealed record StatusService(string Version, long Schema, int ExpectedSchema, long DbBytes);

    /// <param name="Boards">The boards the ceiling tables name.</param>
    /// <param name="BoardsWithClear">Boards a visible player has cleared.</param>
    public sealed record StatusTotals(int Players, int Clears, int Attempts, int Boards, int BoardsWithClear, int Notes);

    /// <summary>
    /// The day in the Pi's own zone (Europe/Prague), the front panel's clock's day: from local midnight to now. The admin
    /// page's days are UTC; the two differ only around midnight.
    /// </summary>
    public sealed record StatusToday(
        string Day, string Zone, int Clears, int Attempts, int NewPlayers, int ActivePlayers, double SecondsPlayed,
        IReadOnlyDictionary<string, int> Refusals);

    /// <summary>A submission: in <c>latest</c> a clear, in <c>recent</c> a clear or an unfinished attempt (0 stars, #8).</summary>
    /// <param name="Level">The level's name from the ceiling tables, else its file.</param>
    public sealed record StatusClear(string Level, string Player, int Stars, int Score, DateTimeOffset At);

    /// <summary>A player who played lately, with today's clears and attempts (the Pi's zone's day, as <see cref="StatusToday"/>).</summary>
    /// <param name="FirstDay">Their first submission came today.</param>
    public sealed record StatusRecentPlayer(string Player, DateTimeOffset LastAt, int Clears, int Attempts, bool FirstDay);

    /// <summary>
    /// One of the newest notes: when it came and who sent it, as the Notes tab names them, and nothing else of it. The
    /// panel compares <paramref name="At"/> with the moment it last acknowledged, since <c>notes.id</c> has no
    /// <c>AUTOINCREMENT</c> and the next note may take a deleted one's id.
    /// </summary>
    /// <param name="FromKind"><c>player</c> (linked to a visible player), <c>claimed</c> (a newcomer's name, unverified),
    /// <c>hidden</c> (a hidden player's) or <c>anonymous</c>.</param>
    /// <param name="From">The nickname for <c>player</c> and <c>claimed</c>, else null: a hidden player is never named.</param>
    public sealed record StatusNote(DateTimeOffset At, string FromKind, string? From);

    /// <param name="Rules">The newest rules version the ceiling tables name: the one the game in the field plays.</param>
    /// <param name="Month">The UTC month, as the boards and the ranking count it.</param>
    public sealed record StatusTop(int Rules, string Month, IReadOnlyList<StatusPlayer> ThisMonth, IReadOnlyList<StatusPlayer> AllTime);

    public sealed record StatusPlayer(int Rank, string Name, int Levels, int Stars, long Score);

    /// <summary>What <c>deploy/backup.sh</c> last recorded about the copy off the box and the one off the site (#6).</summary>
    public sealed record StatusBackups(StatusCopy? Offbox, StatusCopy? Offsite);

    public sealed record StatusCopy(string Result, DateTimeOffset? Attempt, DateTimeOffset? Ok, int Every);

    /// <summary>The anonymous play counts (BS3D#709) added up over every board, from schema 4.</summary>
    public sealed record StatusPlay(long Starts, long Clears, double Seconds);

    public Status ReadStatus(Ceilings ceilings, bool requireKnownBoard)
    {
        using SqliteConnection c = Open();
        DateTimeOffset now = options.Clock.GetUtcNow();
        long schema = Scalar(c, "PRAGMA user_version");
        const string Visible = "JOIN players p ON p.id = s.player_id AND p.hidden = 0";

        // Today, in the Pi's zone: from its local midnight, in the stored form (every stored moment is UTC, "o")
        TimeZoneInfo zone = options.TimeZone;
        DateTime local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        DateTimeOffset midnight = new(local.Date, zone.GetUtcOffset(local.Date));
        string since = Stored(midnight);
        string sinceStamp = RefusalStamp(midnight);

        int[] hours = new int[24];
        DateTimeOffset hour = new(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour), TimeSpan.Zero);
        DateTimeOffset first = hour.AddHours(-23);
        using (SqliteCommand cmd = Command(c, $"SELECT substr(s.received_at, 1, 13), COUNT(*) FROM submissions s {Visible} WHERE s.received_at >= $since GROUP BY 1",
            "$since", Stored(first)))
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read())
            {
                DateTimeOffset at = DateTimeOffset.ParseExact(r.GetString(0) + ":00:00Z", "yyyy-MM-dd'T'HH:mm:ssZ", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                int i = (int)(at - first).TotalHours;
                if (i is >= 0 and < 24) hours[i] += r.GetInt32(1);
            }

        Dictionary<string, string> names = new();
        foreach (CeilingRow row in ceilings.All)
            if (row.Name is { Length: > 0 } name) names.TryAdd(row.File, name);
        List<StatusClear> latest = new();
        using (SqliteCommand cmd = Command(c, $"""
            SELECT s.level_file, p.name, s.stars, s.score, s.received_at FROM submissions s {Visible}
            WHERE s.stars > 0 ORDER BY s.rowid DESC LIMIT $n
            """, "$n", StatusLatest))
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read())
                latest.Add(new StatusClear(names.GetValueOrDefault(r.GetString(0)) ?? r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), Time(r.GetString(4))));

        List<StatusClear> recent = new();
        using (SqliteCommand cmd = Command(c, $"""
            SELECT s.level_file, p.name, s.stars, s.score, s.received_at FROM submissions s {Visible}
            ORDER BY s.rowid DESC LIMIT $n
            """, "$n", StatusRecent))
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read())
                recent.Add(new StatusClear(names.GetValueOrDefault(r.GetString(0)) ?? r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), Time(r.GetString(4))));

        List<StatusRecentPlayer> players = new();
        using (SqliteCommand cmd = Command(c, $"""
            SELECT p.name, MAX(s.received_at),
                   SUM(s.stars > 0 AND s.received_at >= $since), SUM(s.stars = 0 AND s.received_at >= $since),
                   MIN(s.received_at) >= $since
            FROM submissions s {Visible}
            GROUP BY s.player_id ORDER BY MAX(s.rowid) DESC LIMIT $n
            """, "$since", since))
        {
            cmd.Parameters.AddWithValue("$n", StatusRecentPlayers);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                players.Add(new StatusRecentPlayer(r.GetString(0), Time(r.GetString(1)), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4) != 0));
        }

        // A newcomer's claim is not a link, but once their id is a hidden player's, the claimed name is that player's
        List<StatusNote> notes = new();
        if (schema >= 3)
            using (SqliteCommand cmd = Command(c, """
                SELECT n.received_at, p.name, p.hidden, n.claimed_name, cp.hidden FROM notes n
                LEFT JOIN players p ON p.id = n.player_id
                LEFT JOIN players cp ON cp.id = n.claimed_player_id
                ORDER BY n.received_at DESC, n.id DESC LIMIT $n
                """, "$n", StatusRecentNotes))
            using (SqliteDataReader r = cmd.ExecuteReader())
                while (r.Read())
                    notes.Add(
                        !r.IsDBNull(1) ? (r.GetInt64(2) != 0 ? new StatusNote(Time(r.GetString(0)), "hidden", null) : new StatusNote(Time(r.GetString(0)), "player", r.GetString(1)))
                        : !r.IsDBNull(4) && r.GetInt64(4) != 0 ? new StatusNote(Time(r.GetString(0)), "hidden", null)
                        : !r.IsDBNull(3) ? new StatusNote(Time(r.GetString(0)), "claimed", r.GetString(3))
                        : new StatusNote(Time(r.GetString(0)), "anonymous", null));

        int rules = ceilings.All.Count > 0 ? ceilings.All.Max(b => b.RulesVersion) : 1;
        string month = ScoreStore.MonthOf(now);
        IEnumerable<BoardKey>? known = requireKnownBoard ? ceilings.All.Select(b => new BoardKey(b.File, b.Hash, b.RulesVersion)).ToList() : null;
        ScoreStore store = new(options.Database);
        static List<StatusPlayer> Top(List<ScoreStore.RankingRow> rows) =>
            rows.Select(r => new StatusPlayer(r.Rank, r.Name, r.Levels, r.Stars, r.Score)).ToList();

        string? last;
        using (SqliteCommand cmd = c.CreateCommand())
        {
            // From the newest row back to the first visible one: the log's order is arrival
            cmd.CommandText = $"SELECT s.received_at FROM submissions s {Visible} ORDER BY s.rowid DESC LIMIT 1";
            last = cmd.ExecuteScalar() as string;
        }

        return new Status(
            StatusFormat,
            now,
            new StatusService(AdminPages.Version, schema, ScoreStore.SchemaVersion, FileBytes(options.Database) + FileBytes(options.Database + "-wal")),
            new StatusTotals(
                (int)Scalar(c, "SELECT COUNT(*) FROM players WHERE hidden = 0"),
                (int)Scalar(c, $"SELECT COUNT(*) FROM submissions s {Visible} WHERE s.stars > 0"),
                (int)Scalar(c, $"SELECT COUNT(*) FROM submissions s {Visible} WHERE s.stars = 0"),
                ceilings.Count,
                (int)Scalar(c, $"SELECT COUNT(*) FROM (SELECT DISTINCT s.level_file, s.level_hash, s.rules_version FROM submissions s {Visible} WHERE s.stars > 0)"),
                schema >= 3 ? (int)Scalar(c, "SELECT COUNT(*) FROM notes") : 0),
            new StatusToday(
                local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                zone.Id,
                Count(c, $"SELECT COUNT(*) FROM submissions s {Visible} WHERE s.stars > 0 AND s.received_at >= $since", since),
                Count(c, $"SELECT COUNT(*) FROM submissions s {Visible} WHERE s.stars = 0 AND s.received_at >= $since", since),
                Count(c, "SELECT COUNT(*) FROM players WHERE hidden = 0 AND created_at >= $since", since),
                Count(c, $"SELECT COUNT(DISTINCT s.player_id) FROM submissions s {Visible} WHERE s.received_at >= $since", since),
                Real(c, $"SELECT COALESCE(SUM(s.duration_seconds), 0) FROM submissions s {Visible} WHERE s.received_at >= $since", ("$since", since)),
                RefusalsSince(c, sinceStamp)),
            RefusalsSince(c, RefusalStamp(now.AddHours(-1))),
            hours,
            last == null ? null : Time(last),
            latest,
            new StatusTop(rules, month,
                Top(store.Ranking(c, rules, month, known, StatusRanked)),
                Top(store.Ranking(c, rules, null, known, StatusRanked))),
            new StatusBackups(Copy(CopyRecord("last-offbox")), Copy(CopyRecord("last-offsite"))),
            // The page never migrates (#5): a database not yet at schema 4 has no play counts, and says so by their absence
            schema >= 4
                ? new StatusPlay(
                    Scalar(c, "SELECT COALESCE(SUM(starts), 0) FROM play_stats"),
                    Scalar(c, "SELECT COALESCE(SUM(clears), 0) FROM play_stats"),
                    Real(c, "SELECT COALESCE(SUM(seconds), 0) FROM play_stats"))
                : null,
            recent,
            players,
            notes);
    }

    private static StatusCopy? Copy(OffBox? record) =>
        record == null ? null : new StatusCopy(record.Result, record.Attempt, record.Ok, record.Every);

    /// <summary>The refusals logged since <paramref name="stamp"/>, by reason: the refusal log keeps them by moment.</summary>
    private static Dictionary<string, int> RefusalsSince(SqliteConnection c, string stamp)
    {
        Dictionary<string, int> counts = new();
        using SqliteCommand cmd = Command(c, "SELECT reason, COUNT(*) FROM refusal_log WHERE at >= $since AND reason <> 'dropped' GROUP BY reason ORDER BY reason", "$since", stamp);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
        return counts;
    }

    /// <summary>A moment as <c>submissions.received_at</c> and <c>players.created_at</c> keep it, so text order is time order.</summary>
    private static string Stored(DateTimeOffset at) => at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>A moment as <c>refusal_log.at</c> keeps it.</summary>
    private static string RefusalStamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static int Count(SqliteConnection c, string sql, string since)
    {
        using SqliteCommand cmd = Command(c, sql, "$since", since);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static double Real(SqliteConnection c, string sql, (string Name, object Value)? parameter = null)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (parameter is { } p) cmd.Parameters.AddWithValue(p.Name, p.Value);
        return Convert.ToDouble(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
