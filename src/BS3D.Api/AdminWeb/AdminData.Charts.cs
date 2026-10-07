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
    /// <param name="Versions">
    /// Each day's submissions by game version, in per cent of the day's (0 on a day with none), bottom to top: every
    /// local build together (<see cref="LocalBuilds"/>), the releases not charted apart (<see cref="OtherReleases"/>),
    /// then the <see cref="ReleaseSeries"/> releases sent most over the range, in the order each first arrived. A series
    /// with nothing in the range is left out.
    /// </param>
    /// <param name="BackupBytes">The size of each day's last backup on the box, NaN on a day with none kept.</param>
    /// <param name="Copies">The newest good copy's age, on the box, off it and off the site, those set up.</param>
    public sealed record Charts(string Range, IReadOnlyList<string> Days,
        int[] NewPlayers, int[] FirstDayPlayers, int[] ReturningPlayers, int[] Clears, int[] Unfinished, double[] Minutes,
        int[] PlayersTotal, int[] ClearsTotal, int[] UnfinishedTotal, int[] BoardsCleared,
        IReadOnlyList<(string Reason, int[] Counts)> Refusals,
        IReadOnlyList<(string Version, double[] Share)> Versions, double[] BackupBytes, IReadOnlyList<CopyAges> Copies);

    /// <summary>
    /// A copy's age (#6) over the range: the oldest its newest good copy got during each UTC day, in days, NaN while no good
    /// copy is known yet. A day is marked when a copy failed on it (<c>failed</c>), or when the newest good one got older
    /// than its days between copies and half a day (<c>late</c>), as a night it was due on went by without one.
    /// </summary>
    public sealed record CopyAges(string Name, double[] Age, string?[] Marks);

    /// <summary>
    /// The releases charted apart, a series each: players play releases, so a new one's uptake and an old one's last
    /// players (when its ceiling table could go) are what the chart is for. Six colours hold them, the rest and the
    /// local builds.
    /// </summary>
    public const int ReleaseSeries = 4;

    /// <summary>
    /// Every <c>dev</c> and <c>dev-&lt;sha&gt;</c> together: a build of the owner's or an agent's own, a new one most
    /// days, which charted apart would push the releases out of the chart.
    /// </summary>
    public const string LocalBuilds = "Local builds (dev)";

    public const string OtherReleases = "Other releases";

    /// <summary>
    /// How much older than its days between copies the newest good copy may get before the day is marked: the half day
    /// <c>deploy/backup.sh</c> takes off when a copy off the site is due, which covers the timer's jitter and a change of
    /// summer time, and is less than the day a missed night adds.
    /// </summary>
    public static readonly TimeSpan CopySlack = TimeSpan.FromHours(12);

    /// <summary>Days of backups the box keeps: <c>deploy/backup.sh</c>'s <c>BACKUP_KEEP_DAYS</c> unless it is set.</summary>
    public const int BackupKeepDays = 30;

    /// <summary>The lines of a copy's log (<c>backups/last-offbox.log</c>) read at most: <c>deploy/backup.sh</c> keeps 1000.</summary>
    public const int CopyLogLines = 1000;

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

        List<(int Day, string Version, int Count, long First)> sent = new();
        Each("SELECT substr(received_at, 1, 10) AS d, game_version, COUNT(*), MIN(rowid) FROM submissions WHERE d >= $from GROUP BY d, game_version",
            (i, r) => sent.Add((i, r.GetString(1), r.GetInt32(2), r.GetInt64(3))));
        // The releases sent most over the range, ties to the name so the choice does not hang on the rows' order, drawn in
        // the order each first arrived; the other releases and the local builds go together, under them
        static bool Local(string version) => version.StartsWith("dev", StringComparison.Ordinal);
        List<string> charted = sent.Where(s => !Local(s.Version)).GroupBy(s => s.Version)
            .OrderByDescending(g => g.Sum(s => s.Count)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(ReleaseSeries).OrderBy(g => g.Min(s => s.First)).Select(g => g.Key).ToList();
        string SeriesOf(string version) => Local(version) ? LocalBuilds : charted.Contains(version) ? version : OtherReleases;
        int[] daySent = Series();
        foreach (var s in sent) daySent[s.Day] += s.Count;
        List<(string, double[])> shares = new List<string> { LocalBuilds, OtherReleases }.Concat(charted)
            .Where(v => sent.Any(s => SeriesOf(s.Version) == v)).Select(v =>
            {
                double[] share = new double[n];
                foreach (var s in sent.Where(s => SeriesOf(s.Version) == v)) share[s.Day] += 100.0 * s.Count / daySent[s.Day];
                return (v, share);
            }).ToList();

        // The backups on the box, by the time each was written, as the overview's newest backup is
        List<(DateTimeOffset Written, long Bytes)> backups = Backups();
        double[] backupBytes = Enumerable.Repeat(double.NaN, n).ToArray();
        foreach ((DateTimeOffset written, long bytes) in backups.OrderBy(b => b.Written))
            if (index.TryGetValue(DayText(written.UtcDateTime), out int i)) backupBytes[i] = bytes;

        DateTimeOffset now = options.Clock.GetUtcNow();
        List<CopyAges> copies = [Ages("On the box", days, now, backups.Select(b => b.Written).ToList(), [], every: 1)];
        foreach ((string name, string record) in new[] { ("Off the box", "last-offbox"), ("Off the site", "last-offsite") })
        {
            // A copy not set up has no age to draw, nor has one with no record yet
            if (CopyRecord(record) is not { Result: not "none" } last) continue;
            List<OffBox> log = CopyLog(record + ".log");
            List<DateTimeOffset> good = log.Select(l => l.Ok).Append(last.Ok).OfType<DateTimeOffset>().ToList();
            List<DateTimeOffset> failed = log.Where(l => l.Result == "failed").Select(l => l.Attempt).OfType<DateTimeOffset>().ToList();
            copies.Add(Ages(name, days, now, good, failed, last.Every));
        }

        int Before(string sql) => (int)Scalar(c, sql, "$from", from);
        return new Charts(range, days, newPlayers, firstDay, returning, clears, unfinished, minutes,
            Running(Before("SELECT COUNT(*) FROM players WHERE substr(created_at, 1, 10) < $from"), newPlayers),
            Running(Before("SELECT COUNT(*) FROM submissions WHERE stars > 0 AND substr(received_at, 1, 10) < $from"), clears),
            Running(Before("SELECT COUNT(*) FROM submissions WHERE stars = 0 AND substr(received_at, 1, 10) < $from"), unfinished),
            Running(Before("""
                SELECT COUNT(*) FROM (SELECT MIN(substr(received_at, 1, 10)) AS d FROM submissions WHERE stars > 0
                                      GROUP BY level_file, level_hash, rules_version) WHERE d < $from
                """), boards),
            refusals.OrderByDescending(p => p.Value.Sum()).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)).ToList(),
            shares, backupBytes, copies);
    }

    /// <summary>
    /// A copy's age on each of <paramref name="days"/>, from the moments good copies were made. The age grows until the
    /// next copy, so the oldest it gets in a day is just before one of that day's copies or at the day's end (now, today).
    /// Before the first good copy known it is not known: a copy made before the log was kept is not drawn as missing.
    /// </summary>
    public static CopyAges Ages(string name, IReadOnlyList<string> days, DateTimeOffset now, IReadOnlyList<DateTimeOffset> good,
        IReadOnlyList<DateTimeOffset> failed, int every)
    {
        List<DateTimeOffset> copies = good.Where(g => g <= now).Distinct().Order().ToList();
        double[] age = new double[days.Count];
        string?[] marks = new string?[days.Count];
        TimeSpan late = TimeSpan.FromDays(every) + CopySlack;
        for (int i = 0; i < days.Count; i++)
        {
            DateTimeOffset start = new(DateTime.ParseExact(days[i], "yyyy-MM-dd", CultureInfo.InvariantCulture), TimeSpan.Zero);
            DateTimeOffset end = start.AddDays(1) < now ? start.AddDays(1) : now;
            TimeSpan? oldest = null;
            for (int k = 0; k < copies.Count; k++)
                if (copies[k] > start && copies[k] <= end && k > 0 && (oldest == null || copies[k] - copies[k - 1] > oldest))
                    oldest = copies[k] - copies[k - 1];
            int newest = copies.FindLastIndex(g => g <= end);
            if (newest >= 0 && (oldest == null || end - copies[newest] > oldest)) oldest = end - copies[newest];

            age[i] = oldest?.TotalDays ?? double.NaN;
            marks[i] = failed.Any(f => f >= start && f < start.AddDays(1)) ? "failed" : oldest > late ? "late" : null;
        }
        return new CopyAges(name, age, marks);
    }

    /// <summary>The backups on the box, <c>backups/scores-*.db</c>, with the time each was written; none when unreadable.</summary>
    private List<(DateTimeOffset Written, long Bytes)> Backups()
    {
        try
        {
            return new DirectoryInfo(BackupFolder).EnumerateFiles("scores-*.db")
                .Select(f => (new DateTimeOffset(f.LastWriteTimeUtc), f.Length)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// A copy's log, which <c>deploy/backup.sh</c> appends one line to on every run, <c>attempt=… result=… ok=… every=…</c>,
    /// the last <see cref="CopyLogLines"/> kept; a line that does not read is skipped, and no log is no lines.
    /// </summary>
    private List<OffBox> CopyLog(string name)
    {
        try
        {
            string path = Path.Combine(BackupFolder, name);
            return File.Exists(path)
                ? File.ReadLines(path).TakeLast(CopyLogLines).Select(line => CopyFields(line.Split(' ').Select(f => f.Split('=', 2)))).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
