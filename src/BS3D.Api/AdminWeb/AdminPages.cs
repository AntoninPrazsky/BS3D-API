using System.Globalization;
using System.Reflection;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// The admin page's views (issue #5, requirement 12), read-only and without JavaScript. Everything that came from a
/// request — a nickname, a game version, a refusal's detail — is encoded by <see cref="Html.M"/> and isolated in
/// <c>&lt;bdi&gt;</c>, so a right-to-left override in a nickname cannot reorder the row around it.
/// </summary>
public static partial class AdminPages
{
    public static readonly string Version =
        typeof(AdminPages).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    /// <summary>The version with its commit cut to seven characters, for the footer.</summary>
    internal static readonly string ShortVersion = Version.Split('+') is [var number, var commit] ? $"{number}+{commit[..Math.Min(7, commit.Length)]}" : Version;

    /// <summary>The tabs; a page that is not one of them names the one it belongs to.</summary>
    private static readonly (string Href, string Name)[] Sections = [("/", "Overview"), ("/live", "Live"), ("/charts", "Charts"), ("/boards", "Boards"), ("/players", "Players"), ("/notes", "Notes")];

    public static string Overview(AdminData.Overview o, DateTimeOffset now)
    {
        Markup warning = o.SchemaVersion == ScoreStore.SchemaVersion ? default
            : Html.M($"<p class=\"warn\">The database is at schema {o.SchemaVersion}; this page reads schema {ScoreStore.SchemaVersion}. It never migrates: update the service first.</p>");

        Markup backup = o.NewestBackup is { } b
            ? Html.M($"<strong>{Ago(now - b.Written)}</strong><small>{b.Name} · {Size(b.Bytes)}</small>")
            : Html.M($"<strong>none</strong><small>none found next to the database</small>");
        (Markup offBox, Markup offBoxWarning) = CopyStat(o.LastOffBox, now, "off the box", "every copy is on this disk",
            Html.M($"<p class=\"warn\">Every backup is on this Pi's own disk, and a dead disk takes them all. Set up the card: <code>sudo /opt/bs3d-api/current/deploy/backup-card.sh /dev/mmcblk0</code></p>"));
        (Markup offSite, Markup offSiteWarning) = CopyStat(o.LastOffSite, now, "off the site", "every copy is in this house",
            Html.M($"<p class=\"warn\">Every backup is in this house, and a fire or a theft takes them all. Set up the copy off the site: <code>sudo /opt/bs3d-api/current/deploy/backup-offsite.sh sftp://account@host:port//folder</code></p>"));

        Dictionary<string, AdminData.Day> byDate = o.Days.ToDictionary(d => d.Date);
        Markup days = Html.Join(Enumerable.Range(0, AdminData.Days).Select(i =>
        {
            string date = ScoreStore.DayOf(now.AddDays(-i));
            AdminData.Day day = byDate.GetValueOrDefault(date) ?? new AdminData.Day(date, 0, 0, 0);
            return Html.M($"<tr><td>{date}</td><td class=\"n\">{day.Clears}</td><td class=\"n\">{day.Unfinished}</td><td class=\"n\">{day.NewPlayers}</td></tr>");
        }));

        Markup refusals = o.Refusals.Count == 0
            ? Html.M($"<tr><td colspan=\"3\">None in {AdminData.Days} days.</td></tr>")
            : Html.Join(o.Refusals.Select(r => Html.M($"<tr><td>{r.Date}</td><td>{r.Reason}</td><td class=\"n\">{r.Count}</td></tr>")));

        return Layout("Overview", Html.M($"""
            {warning}{offBoxWarning}{offSiteWarning}
            <div class="stats">
            <div class="stat"><span>Players</span><strong>{o.Players}</strong><small>{o.HiddenPlayers} hidden</small></div>
            <div class="stat"><span>Accepted</span><strong>{o.Submissions}</strong><small>{o.Unfinished} unfinished · {o.HiddenSubmissions} by hidden players</small></div>
            <div class="stat"><span>Notes</span><strong><a href="/notes">{o.Notes}</a></strong><small>pictures {Size(o.NotePictureBytes)}</small></div>
            <div class="stat"><span>Database</span><strong>{Size(o.DatabaseBytes)}</strong><small>schema {o.SchemaVersion}</small></div>
            <div class="stat"><span>Newest backup</span>{backup}</div>
            <div class="stat"><span>Off the box</span>{offBox}</div>
            <div class="stat"><span>Off site</span>{offSite}</div>
            </div>
            <div class="cols">
            <section><h2>The last {AdminData.Days} days (UTC)</h2>
            <div class="panel"><table><tr><th>Day</th><th class="n">Clears</th><th class="n">Unfinished</th><th class="n">New players</th></tr>{days}</table></div></section>
            <section><h2>Refusals (UTC days)</h2>
            <div class="panel"><table><tr><th>Day</th><th>Reason</th><th class="n">Count</th></tr>{refusals}</table></div></section>
            </div>
            """), refresh: false);
    }

    /// <summary>
    /// A copy older than its days between copies and one more day is warned about: one missed night is not. The copy off
    /// the box goes nightly (2 days), the one off the site every other day (3).
    /// </summary>
    internal static TimeSpan Stale(AdminData.OffBox off) => TimeSpan.FromDays(off.Every + 1);

    /// <summary>
    /// A copy (#6) as <c>deploy/backup.sh</c> records it, off the box or off the site (<paramref name="where"/>): its stat,
    /// and a warning when none is set up (<paramref name="setUp"/>), the last run failed or the newest good copy is
    /// older than <see cref="Stale"/>, because every copy on this disk, or in this house, goes with it.
    /// </summary>
    private static (Markup Stat, Markup Warning) CopyStat(AdminData.OffBox? off, DateTimeOffset now, string where, string notSetUp, Markup setUp)
    {
        string Age(DateTimeOffset? at) => at is { } t ? Ago(now - t) : "never";
        Markup good = off?.Ok is { } last ? Html.M($"The newest good copy is from {Ago(now - last)}") : Html.M($"There is no good copy yet");
        return off switch
        {
            null => (Html.M($"<strong>no record</strong><small>no backup has run since backup.sh records one</small>"), default),
            { Result: "none" } => (Html.M($"<strong>none</strong><small>not set up: {notSetUp}</small>"), setUp),
            { Result: "ok" } when off.Ok is { } ok && now - ok <= Stale(off) =>
                (Html.M($"<strong>{Ago(now - ok)}</strong><small>{off.Copy ?? ""}</small>"), default),
            { Result: "ok" } => (Html.M($"<strong>{Age(off.Ok)}</strong><small>{off.Copy ?? ""}</small>"),
                Html.M($"<p class=\"warn\">The newest copy {where} is from {Age(off.Ok)}: is the backup timer running? <code>systemctl status bs3d-api-backup.timer</code></p>")),
            _ => (Html.M($"<strong>failed</strong><small>{Age(off.Attempt)}: {off.Detail}</small>"),
                Html.M($"<p class=\"warn\">The last copy {where} failed {Age(off.Attempt)}: {off.Detail}. {good}.</p>")),
        };
    }

    public static string Live(IReadOnlyList<AdminData.Event> events) =>
        Layout("Live", Html.M($"""
            <p class="note">The newest {events.Count} accepted submissions (clears and unfinished attempts) and refusals, newest first. Reloads every 5 seconds.</p>
            <div class="panel"><table><tr><th>When (UTC)</th><th>Result</th><th>What</th><th>Detail</th></tr>{Html.Join(events.Select(e => Html.M($"""
                <tr class="{(e.Accepted ? "ok" : "refused")}"><td class="t">{e.At:yyyy-MM-dd HH:mm:ss}</td><td>{(e.Accepted ? Html.M($"<span class=\"chip ok\">accepted</span>") : Html.M($"<span class=\"chip bad\">refused</span>"))}</td><td><bdi>{e.What}</bdi></td><td><bdi>{e.Detail}</bdi></td></tr>
                """)))}</table></div>
            """), refresh: true);

    /// <param name="section">The tab a page belongs to when it is not one itself: a board's page is under Boards.</param>
    internal static string Layout(string title, Markup body, bool refresh, string? section = null)
    {
        section ??= title;
        Markup tabs = Html.Join(Sections.Select(s => s.Name == section
            ? Html.M($"<a href=\"{s.Href}\" aria-current=\"page\">{s.Name}</a>")
            : Html.M($"<a href=\"{s.Href}\">{s.Name}</a>")));
        Markup crumb = section == title ? default
            : Html.M($"<a class=\"crumb\" href=\"{Sections.Single(s => s.Name == section).Href}\">{section}</a>");
        return Html.M($"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>{title} · BS3D admin</title>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            {(refresh ? Html.M($"<meta http-equiv=\"refresh\" content=\"5;url=/live?auto=1\">") : default)}
            <link rel="stylesheet" href="/style.css"></head>
            <body><header class="top"><div class="bar"><a class="brand" href="/"><span class="mark">BS3D</span>admin</a><nav>{tabs}</nav><span class="badge">read-only · 127.0.0.1</span></div></header>
            <main>{crumb}<h1><bdi>{title}</bdi>{(refresh ? Html.M($"<span class=\"live\">every 5 s</span>") : default)}</h1>{body}</main>
            <footer>BS3D.Api {ShortVersion} · read-only · this machine only</footer></body></html>
            """).Value;
    }

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MB"),
    };

    internal static string Ago(TimeSpan span) => span.TotalMinutes < 1 ? "just now"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
        : $"{(int)span.TotalDays} d ago";
}
