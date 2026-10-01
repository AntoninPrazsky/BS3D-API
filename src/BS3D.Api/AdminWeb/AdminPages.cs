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

    public static string Overview(AdminData.Overview o, DateTimeOffset now)
    {
        Markup warning = o.SchemaVersion == ScoreStore.SchemaVersion ? default
            : Html.M($"<p class=\"warn\">The database is at schema {o.SchemaVersion}; this page reads schema {ScoreStore.SchemaVersion}. It never migrates: update the service first.</p>");

        Markup backup = o.NewestBackup is { } b
            ? Html.M($"{b.Name}, {Size(b.Bytes)}, {Ago(now - b.Written)}")
            : Html.M($"none found next to the database");

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
            <table class="facts">
            <tr><th>Players</th><td>{o.Players} ({o.HiddenPlayers} hidden)</td></tr>
            <tr><th>Accepted clears</th><td>{o.Submissions} ({o.HiddenSubmissions} by hidden players)</td></tr>
            <tr><th>Database</th><td>{Size(o.DatabaseBytes)}, schema {o.SchemaVersion}</td></tr>
            <tr><th>Newest backup</th><td>{backup}</td></tr>
            <tr><th>This page</th><td>BS3D.Api {Version}</td></tr>
            </table>
            <h2>The last {AdminData.Days} days (UTC)</h2>
            <table><tr><th>Day</th><th>Accepted clears</th><th>New players</th></tr>{days}</table>
            <h2>Refusals (UTC days)</h2>
            <table><tr><th>Day</th><th>Reason</th><th>Count</th></tr>{refusals}</table>
            """), refresh: false);
    }

    public static string Live(IReadOnlyList<AdminData.Event> events) =>
        Layout("Live", Html.M($"""
            <p class="note">The newest {events.Count} accepted clears and refusals, newest first. Reloads every 5 seconds.</p>
            <table><tr><th>When (UTC)</th><th></th><th>What</th><th>Detail</th></tr>{Html.Join(events.Select(e => Html.M($"""
                <tr class="{(e.Accepted ? "ok" : "refused")}"><td>{e.At:yyyy-MM-dd HH:mm:ss}</td><td>{(e.Accepted ? "accepted" : "refused")}</td><td><bdi>{e.What}</bdi></td><td><bdi>{e.Detail}</bdi></td></tr>
                """)))}</table>
            """), refresh: true);

    public const string Css = """
        :root { color-scheme: light dark; --fg: #1b1f23; --bg: #fafaf8; --muted: #5d6670; --line: #d8dbde; --bad: #a3261b; --ok: #1f6d3a; }
        @media (prefers-color-scheme: dark) { :root { --fg: #e6e8ea; --bg: #15181b; --muted: #9aa3ab; --line: #30363b; --bad: #f08a80; --ok: #7fd19a; } }
        body { margin: 0; font: 15px/1.45 system-ui, sans-serif; color: var(--fg); background: var(--bg); }
        header { display: flex; gap: 1.5rem; align-items: baseline; flex-wrap: wrap; padding: .8rem 1rem; border-bottom: 1px solid var(--line); }
        header nav a { margin-right: 1rem; color: inherit; }
        main { padding: 1rem; max-width: 70rem; }
        h1 { font-size: 1.4rem; margin: .2rem 0 1rem; } h2 { font-size: 1.05rem; margin: 1.6rem 0 .5rem; }
        table { border-collapse: collapse; } th, td { text-align: left; padding: .25rem .8rem .25rem 0; border-bottom: 1px solid var(--line); vertical-align: top; }
        td.n { text-align: right; font-variant-numeric: tabular-nums; }
        .note { color: var(--muted); } .warn { color: var(--bad); font-weight: 600; } .flag { color: var(--bad); font-size: .85em; } code { font-size: .9em; }
        tr.refused td:nth-child(2) { color: var(--bad); } tr.ok td:nth-child(2) { color: var(--ok); }
        """;

    internal static string Layout(string title, Markup body, bool refresh) => Html.M($"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>{title} · BS3D admin</title>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        {(refresh ? Html.M($"<meta http-equiv=\"refresh\" content=\"5;url=/live?auto=1\">") : default)}
        <link rel="stylesheet" href="/style.css"></head>
        <body><header><strong>BS3D admin</strong><nav><a href="/">Overview</a><a href="/live">Live</a><a href="/boards">Boards</a><a href="/players">Players</a></nav><span class="note">read-only · this machine only</span></header>
        <main><h1>{title}</h1>{body}</main></body></html>
        """).Value;

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
