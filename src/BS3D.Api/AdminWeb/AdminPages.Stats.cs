namespace BS3D.Api.AdminWeb;

/// <summary>The Play tab (BS3D#709): the anonymous play counts per board, every rate beside the counts it is a share of.</summary>
public static partial class AdminPages
{
    /// <param name="lines">Null on a database not yet at schema 4.</param>
    public static string PlayStats(IReadOnlyList<PlayStatsReport.Line>? lines, string sort)
    {
        if (lines == null)
            return Layout("Play", Html.M($"<p class=\"warn\">The database is not at schema 4 yet, so it holds no play counts. It never migrates here: update the service first.</p>"), refresh: false);

        Markup Sort(string key, string label) => key == sort ? Html.M($"<span class=\"sorted\">{label}</span>") : Html.M($"<a href=\"/stats?sort={key}\">{label}</a>");
        Markup rows = lines.Count == 0
            ? Html.M($"<tr><td colspan=\"13\">No play counts yet: a game sends them only for a player with online scores on, every ten minutes of play and at its next start.</td></tr>")
            : Html.Join(lines.Select(l => Html.M($"""
                <tr><td>{(l.Name != null ? Html.M($"<bdi>{PlayStatsReport.LevelOf(l)}</bdi>") : Html.M($"<bdi>{l.Row.Board.File}</bdi> <span class=\"flag\">in no ceiling table</span>"))}</td><td><bdi>{l.Chapter ?? ""}</bdi></td>
                <td><code>{l.Row.Board.File}#{l.Row.Board.Hash} r{l.Row.Board.Rules}</code></td><td class="n">{l.Row.Starts}</td><td class="n">{PlayStatsReport.Share(l.Losses, l.Ended)}</td>
                <td class="n">{l.Row.LostOutOfBalls}</td><td class="n">{l.Row.LostLine}</td><td class="n">{PlayStatsReport.Share(l.Row.Retries, l.Row.Starts)}</td><td class="n">{l.Row.Abandons}</td>
                <td class="n">{PlayStatsReport.Share(l.Row.Skips, l.Row.Starts)}</td><td class="n">{PlayStatsReport.Figure(l.TriesToFirstClear)}</td><td class="n">{PlayStatsReport.Figure(l.ShotsPerAttempt)}</td><td class="n">{PlayStatsReport.Figure(l.SecondsPerAttempt, "0")}</td></tr>
                """)));

        return Layout("Play", Html.M($"""
            <p class="note">{lines.Count} boards, {lines.Sum(l => l.Row.Starts)} attempts started. Anonymous counts from every game whose player has online scores on: no player, address or time is kept with them, so they cannot be told apart by player and a few attempts are not a trend. "Lost" is a share of the attempts that reached a result page; retries and skips are shares of the starts. A retry is a start of the level the last attempt had not cleared; a quit is an attempt left before its result page.</p>
            <p class="note">Order: {Sort("loss", "by loss rate")} · {Sort("retries", "by retries")} · {Sort("skips", "by skips")} · {Sort("order", "in play order")}</p>
            <div class="panel"><table><tr><th>Level</th><th>Chapter</th><th>Board</th><th class="n">Starts</th><th class="n">Lost (of ended)</th><th class="n">Out of balls</th><th class="n">At the line</th><th class="n">Retries (of starts)</th><th class="n">Quits</th><th class="n">Skips (of starts)</th><th class="n">Tries to a first clear</th><th class="n">Shots an attempt</th><th class="n">Seconds an attempt</th></tr>{rows}</table></div>
            """), refresh: false);
    }
}
