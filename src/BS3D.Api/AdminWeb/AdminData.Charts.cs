using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// What the charts page draws (#9): one value per UTC day of a fixed range, every series as long as <see cref="Charts.Days"/>.
/// Counts, never addresses: the same columns the other views read.
/// </summary>
public sealed partial class AdminData
{
    /// <param name="Days">The range's UTC days, oldest first, today last.</param>
    /// <param name="FirstDayPlayers">Players whose first submission was that day.</param>
    /// <param name="ReturningPlayers">Players who sent something that day and had sent before it.</param>
    /// <param name="Minutes">The submissions' <c>duration_seconds</c> that day, in minutes: the time the levels sent took.</param>
    /// <param name="PlayersTotal">Players at the end of each day, those before the range included; the totals below likewise.</param>
    /// <param name="BoardsCleared">Boards with at least one clear (the first clear's day counts).</param>
    /// <param name="Refusals">Refusals per reason, the reasons in order of their count over the range.</param>
    public sealed record Charts(string Range, IReadOnlyList<string> Days,
        int[] NewPlayers, int[] FirstDayPlayers, int[] ReturningPlayers, int[] Clears, int[] Unfinished, double[] Minutes,
        int[] PlayersTotal, int[] ClearsTotal, int[] UnfinishedTotal, int[] BoardsCleared,
        IReadOnlyList<(string Reason, int[] Counts)> Refusals);

    /// <summary>
    /// The charts' ranges, in days (0: from the first day anything was recorded, where every range starts at the latest).
    /// A fixed set, so a query string never reaches the SQL.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> ChartRanges = new Dictionary<string, int> { ["14d"] = 14, ["90d"] = 90, ["all"] = 0 };

    public const string DefaultChartRange = "90d";

    /// <summary>Days an "all" range covers at most: ten years of one value per day.</summary>
    public const int MaxChartDays = 3660;

    public Charts ReadCharts(string? range)
    {
        range = range != null && ChartRanges.ContainsKey(range) ? range : DefaultChartRange;
        using SqliteConnection c = Open();
        DateTime today = options.Clock.GetUtcNow().UtcDateTime.Date;
        // No range starts before the first day anything was recorded: the days before it are the service not yet running,
        // and drawn as zeros they would squeeze a young service's real days against the right edge
        DateTime start = FirstDay(c) ?? today;
        DateTime first = ChartRanges[range] > 0 && today.AddDays(1 - ChartRanges[range]) > start ? today.AddDays(1 - ChartRanges[range]) : start;
        if ((today - first).TotalDays >= MaxChartDays) first = today.AddDays(1 - MaxChartDays);
        string from = DayText(first);
        List<string> days = Enumerable.Range(0, (int)(today - first).TotalDays + 1).Select(i => DayText(first.AddDays(i))).ToList();
        Dictionary<string, int> index = days.Select((d, i) => (d, i)).ToDictionary(p => p.d, p => p.i);
        int n = days.Count;

        int[] Series() => new int[n];
        int[] newPlayers = Series(), firstDay = Series(), returning = Series(), clears = Series(), unfinished = Series(), boards = Series();
        double[] minutes = new double[n];

        // Each row: its day, then its numbers; a day outside the range cannot come back, since every query starts at $from
        void Each(string sql, Action<int, SqliteDataReader> take)
        {
            using SqliteCommand cmd = Command(c, sql, "$from", from);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                if (index.TryGetValue(r.GetString(0), out int i)) take(i, r);
        }

        Each("SELECT substr(created_at, 1, 10) AS d, COUNT(*) FROM players WHERE d >= $from GROUP BY d", (i, r) => newPlayers[i] = r.GetInt32(1));
        Each("""
            SELECT substr(received_at, 1, 10) AS d, COUNT(CASE WHEN stars > 0 THEN 1 END), COUNT(CASE WHEN stars = 0 THEN 1 END),
                   SUM(duration_seconds)
            FROM submissions WHERE d >= $from GROUP BY d
            """, (i, r) => (clears[i], unfinished[i], minutes[i]) = (r.GetInt32(1), r.GetInt32(2), r.GetDouble(3) / 60));
        // A player's first day is the day of their first submission, not of their player row: a rename can come later
        Each("""
            WITH firsts AS (SELECT player_id, MIN(substr(received_at, 1, 10)) AS first FROM submissions GROUP BY player_id)
            SELECT substr(s.received_at, 1, 10) AS d, COUNT(DISTINCT CASE WHEN f.first = substr(s.received_at, 1, 10) THEN s.player_id END),
                   COUNT(DISTINCT CASE WHEN f.first < substr(s.received_at, 1, 10) THEN s.player_id END)
            FROM submissions s JOIN firsts f ON f.player_id = s.player_id WHERE d >= $from GROUP BY d
            """, (i, r) => (firstDay[i], returning[i]) = (r.GetInt32(1), r.GetInt32(2)));
        Each("""
            SELECT d, COUNT(*) FROM (SELECT MIN(substr(received_at, 1, 10)) AS d FROM submissions WHERE stars > 0
                                     GROUP BY level_file, level_hash, rules_version)
            WHERE d >= $from GROUP BY d
            """, (i, r) => boards[i] = r.GetInt32(1));

        Dictionary<string, int[]> refusals = new();
        Each("SELECT day, reason, count FROM refusal_days WHERE day >= $from", (i, r) =>
        {
            string reason = r.GetString(1);
            if (!refusals.TryGetValue(reason, out int[]? counts)) refusals[reason] = counts = Series();
            counts[i] += r.GetInt32(2);
        });

        int Before(string sql) => (int)Scalar(c, sql, "$from", from);
        return new Charts(range, days, newPlayers, firstDay, returning, clears, unfinished, minutes,
            Running(Before("SELECT COUNT(*) FROM players WHERE substr(created_at, 1, 10) < $from"), newPlayers),
            Running(Before("SELECT COUNT(*) FROM submissions WHERE stars > 0 AND substr(received_at, 1, 10) < $from"), clears),
            Running(Before("SELECT COUNT(*) FROM submissions WHERE stars = 0 AND substr(received_at, 1, 10) < $from"), unfinished),
            Running(Before("""
                SELECT COUNT(*) FROM (SELECT MIN(substr(received_at, 1, 10)) AS d FROM submissions WHERE stars > 0
                                      GROUP BY level_file, level_hash, rules_version) WHERE d < $from
                """), boards),
            refusals.OrderByDescending(p => p.Value.Sum()).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)).ToList());
    }

    /// <summary>The first UTC day anything was recorded: a player, a submission or a refusal.</summary>
    private static DateTime? FirstDay(SqliteConnection c)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(d) FROM (SELECT MIN(substr(created_at, 1, 10)) AS d FROM players
                                UNION ALL SELECT MIN(substr(received_at, 1, 10)) FROM submissions
                                UNION ALL SELECT MIN(day) FROM refusal_days)
            """;
        return cmd.ExecuteScalar() is string day ? DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    private static int[] Running(int start, int[] perDay)
    {
        int[] total = new int[perDay.Length];
        for (int i = 0, sum = start; i < perDay.Length; i++) total[i] = sum += perDay[i];
        return total;
    }

    private static string DayText(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long Scalar(SqliteConnection c, string sql, string name, object value)
    {
        using SqliteCommand cmd = Command(c, sql, name, value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }
}
