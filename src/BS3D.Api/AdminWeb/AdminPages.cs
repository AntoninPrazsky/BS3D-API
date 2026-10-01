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
    private static readonly (string Href, string Name)[] Sections = [("/", "Overview"), ("/live", "Live"), ("/boards", "Boards"), ("/players", "Players")];

    public static string Overview(AdminData.Overview o, DateTimeOffset now)
    {
        Markup warning = o.SchemaVersion == ScoreStore.SchemaVersion ? default
            : Html.M($"<p class=\"warn\">The database is at schema {o.SchemaVersion}; this page reads schema {ScoreStore.SchemaVersion}. It never migrates: update the service first.</p>");

        Markup backup = o.NewestBackup is { } b
            ? Html.M($"<strong>{Ago(now - b.Written)}</strong><small>{b.Name} · {Size(b.Bytes)}</small>")
            : Html.M($"<strong>none</strong><small>none found next to the database</small>");

        Dictionary<string, AdminData.Day> byDate = o.Days.ToDictionary(d => d.Date);
        Markup days = Html.Join(Enumerable.Range(0, AdminData.Days).Select(i =>
        {
            string date = ScoreStore.DayOf(now.AddDays(-i));
            AdminData.Day day = byDate.GetValueOrDefault(date) ?? new AdminData.Day(date, 0, 0);
            return Html.M($"<tr><td>{date}</td><td class=\"n\">{day.Submissions}</td><td class=\"n\">{day.NewPlayers}</td></tr>");
        }));

        Markup refusals = o.Refusals.Count == 0
            ? Html.M($"<tr><td colspan=\"3\">None in {AdminData.Days} days.</td></tr>")
            : Html.Join(o.Refusals.Select(r => Html.M($"<tr><td>{r.Date}</td><td>{r.Reason}</td><td class=\"n\">{r.Count}</td></tr>")));

        return Layout("Overview", Html.M($"""
            {warning}
            <div class="stats">
            <div class="stat"><span>Players</span><strong>{o.Players}</strong><small>{o.HiddenPlayers} hidden</small></div>
            <div class="stat"><span>Accepted clears</span><strong>{o.Submissions}</strong><small>{o.HiddenSubmissions} by hidden players</small></div>
            <div class="stat"><span>Database</span><strong>{Size(o.DatabaseBytes)}</strong><small>schema {o.SchemaVersion}</small></div>
            <div class="stat"><span>Newest backup</span>{backup}</div>
            </div>
            <div class="cols">
            <section><h2>The last {AdminData.Days} days (UTC)</h2>
            <div class="panel"><table><tr><th>Day</th><th class="n">Accepted clears</th><th class="n">New players</th></tr>{days}</table></div></section>
            <section><h2>Refusals (UTC days)</h2>
            <div class="panel"><table><tr><th>Day</th><th>Reason</th><th class="n">Count</th></tr>{refusals}</table></div></section>
            </div>
            """), refresh: false);
    }

    public static string Live(IReadOnlyList<AdminData.Event> events) =>
        Layout("Live", Html.M($"""
            <p class="note">The newest {events.Count} accepted clears and refusals, newest first. Reloads every 5 seconds.</p>
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
