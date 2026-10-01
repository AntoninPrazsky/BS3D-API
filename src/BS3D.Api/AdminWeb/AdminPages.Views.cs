using System.Globalization;

namespace BS3D.Api.AdminWeb;

/// <summary>The boards and players views (issue #5, requirement 12).</summary>
public static partial class AdminPages
{
    /// <param name="noTables">The ceilings directory, when no table was read from it: then every board is "in no ceiling table".</param>
    public static string Boards(IReadOnlyList<AdminData.BoardRow> boards, string month, string? noTables = null)
    {
        int unknown = boards.Count(b => !b.Known);
        Markup missing = noTables == null ? default
            : Html.M($"<p class=\"warn\">No ceiling table in <code>{noTables}</code> (Scores__CeilingsDirectory): only the boards with clears are listed.</p>");
        // Each chapter under its name, when a table names chapters; the boards no table names last, under theirs
        bool chaptered = boards.Any(b => b.Chapter != null);
        static string Group(AdminData.BoardRow b) => !b.Known ? "In no ceiling table" : b.Chapter ?? "Without a chapter";
        Markup Heading(int i) => !chaptered || (i > 0 && Group(boards[i - 1]) == Group(boards[i])) ? default
            : Html.M($"<tr class=\"group\"><th colspan=\"8\"><bdi>{Group(boards[i])}</bdi> <span>{boards.Skip(i).TakeWhile(b => Group(b) == Group(boards[i])).Count()} boards</span></th></tr>");
        Markup rows = Html.Join(boards.Select((b, i) => Html.M($"""
            {Heading(i)}<tr><td><a href="{BoardLink(b.Key)}">{b.Name ?? b.Key.File}</a>{(b.Known ? default : Html.M($" <span class=\"flag\">in no ceiling table</span>"))}</td>
            <td><code>{b.Key.File}#{b.Key.Hash} r{b.Key.Rules}</code></td><td class="n">{(b.Ceiling is int c ? c.ToString("N0", CultureInfo.InvariantCulture) : "")}</td>
            <td class="n">{b.Shots}</td><td class="n">{b.MonthPlayers}</td><td class="n">{b.AllPlayers}</td><td class="n">{b.Clears}</td><td class="t">{Stamp(b.LastClear)}</td></tr>
            """)));
        return Layout("Boards", Html.M($"""
            {missing}<p class="note">{boards.Count} boards: every board a ceiling table names{(unknown > 0 ? Html.M($", and {unknown} with clears that no table names") : default)}, {(chaptered ? "chapter by chapter in play order" : "in play order (the tables name no chapters)")}. Players count each visible player once; hidden players are not counted, as on the game's boards.</p>
            <div class="panel"><table><tr><th>Level</th><th>Board</th><th class="n">Ceiling</th><th class="n">Shots</th><th class="n">Players {month}</th><th class="n">Players all time</th><th class="n">Clears</th><th>Last clear (UTC)</th></tr>{rows}</table></div>
            """), refresh: false);
    }

    public static string Board(AdminData.BoardView b)
    {
        Markup Entries(IReadOnlyList<BoardEntry> entries, int total) => entries.Count == 0
            ? Html.M($"<p class=\"note\">Nobody is on this board.</p>")
            : Html.M($"""
                <div class="panel"><table><tr><th class="n">Rank</th><th>Player</th><th class="n">Score</th><th class="n">Stars</th><th>Cleared (UTC)</th></tr>{Html.Join(entries.Select(e => Html.M($"""
                    <tr><td class="n">{e.Rank}</td><td><bdi>{e.Name}</bdi></td><td class="n">{e.Score.ToString("N0", CultureInfo.InvariantCulture)}</td><td class="n">{e.Stars}</td><td class="t">{Stamp(e.At)}</td></tr>
                    """)))}</table></div>{(total > entries.Count ? Html.M($"<p class=\"note\">The first {entries.Count} of {total}.</p>") : default)}
                """);

        Markup hidden = b.Hidden.Count == 0 ? default : Html.M($"""
            <h2>Hidden players (on no board)</h2>
            <div class="panel"><table><tr><th>Player</th><th class="n">Best</th><th class="n">Stars</th><th class="n">Clears</th></tr>{Html.Join(b.Hidden.Select(h => Html.M($"""
                <tr><td><a href="{PlayerLink(h.Player)}"><bdi>{h.Name}</bdi></a></td><td class="n">{h.Score.ToString("N0", CultureInfo.InvariantCulture)}</td><td class="n">{h.Stars}</td><td class="n">{h.Clears}</td></tr>
                """)))}</table></div>
            """);

        Markup ceiling = b.Ceiling is { } c
            ? Html.M($"<span class=\"note\">ceiling {c.Ceiling.ToString("N0", CultureInfo.InvariantCulture)} · {c.MinShots}–{c.Shots} shots</span>")
            : Html.M($"<span class=\"flag\">in no ceiling table</span>");
        return Layout(b.Ceiling?.Name ?? b.Key.File, Html.M($"""
            <p><code class="key">{b.Key.File}#{b.Key.Hash} r{b.Key.Rules}</code> {ceiling}</p>
            <p class="note">As the game sees it (the same query as <code>GET /v1/boards</code>): the best clear of each visible player, ties to the earlier.</p>
            <div class="cols">
            <section><h2>{b.Month} ({b.MonthTotal})</h2>{Entries(b.MonthEntries, b.MonthTotal)}</section>
            <section><h2>All time ({b.AllTotal})</h2>{Entries(b.AllEntries, b.AllTotal)}</section>
            </div>
            {hidden}
            """), refresh: false, section: "Boards");
    }

    public static string Players(IReadOnlyList<AdminData.PlayerRow> players, string order)
    {
        Markup Sort(string key, string label) => key == order ? Html.M($"<span class=\"sorted\">{label}</span>") : Html.M($"<a href=\"/players?sort={key}\">{label}</a>");
        Markup rows = Html.Join(players.Select(p => Html.M($"""
            <tr><td><a href="{PlayerLink(p.Id)}"><bdi>{p.Name}</bdi></a>{Flags(p.Name)}<code class="id">{p.Id.ToString()[..8]}</code></td>
            <td class="t">{Stamp(p.Created)}</td><td>{(p.Hidden ? Html.M($"<span class=\"chip muted\">hidden</span>") : default)}</td><td class="n">{p.Clears}</td><td class="t">{Stamp(p.LastClear)}</td>
            <td class="n">{p.Addresses}</td><td>{Shares(p.SharesWith)}</td></tr>
            """)));
        return Layout("Players", Html.M($"""
            <p class="note">{players.Count} players. "Addresses" counts the different addresses a player's clears came from; the addresses themselves are never shown. Behind the home network every player at home shares one.</p>
            <div class="panel"><table><tr><th>{Sort("name", "Player")}</th><th>{Sort("created", "Created (UTC)")}</th><th>Hidden</th><th class="n">{Sort("clears", "Clears")}</th><th>{Sort("last", "Last clear (UTC)")}</th><th class="n">Addresses</th><th>Shares an address with</th></tr>{rows}</table></div>
            """), refresh: false);
    }

    public static string Player(AdminData.PlayerView view)
    {
        AdminData.PlayerRow p = view.Player;
        Markup boards = Html.Join(view.Boards.Select(b => Html.M($"""
            <tr><td><a href="{BoardLink(b.Board)}"><code>{b.Board.File}#{b.Board.Hash}</code></a></td><td class="n">{b.Best.ToString("N0", CultureInfo.InvariantCulture)}</td>
            <td>{Rank(b.Month, p.Hidden)}</td><td>{Rank(b.AllTime, p.Hidden)}</td></tr>
            """)));
        Markup clears = Html.Join(view.Clears.Select(c => Html.M($"""
            <tr><td class="t">{Stamp(c.At)}</td><td><code>{c.Board.File}</code></td><td class="n">{c.Score.ToString("N0", CultureInfo.InvariantCulture)}</td><td class="n">{c.Stars}</td>
            <td class="n">{c.Shots}</td><td class="n">{c.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s</td><td><bdi>{c.GameVersion}</bdi></td><td><bdi>{c.UserAgent ?? ""}</bdi></td><td>{c.Address}</td></tr>
            """)));
        return Layout(p.Name, Html.M($"""
            <div class="panel"><table class="facts">
            <tr><th>Player</th><td><bdi>{p.Name}</bdi>{Flags(p.Name)}</td></tr>
            <tr><th>Id</th><td><code>{p.Id}</code></td></tr>
            <tr><th>Created</th><td>{Stamp(p.Created)} UTC</td></tr>
            <tr><th>On the boards</th><td>{(p.Hidden ? Html.M($"<span class=\"flag\">hidden: on no board and in no count</span>") : Html.M($"yes"))}</td></tr>
            <tr><th>Addresses</th><td>{p.Addresses}, lettered below in the order they first appear</td></tr>
            <tr><th>Shares an address with</th><td>{Shares(p.SharesWith)}</td></tr>
            </table></div>
            <h2>Boards</h2>
            <div class="panel"><table><tr><th>Board</th><th class="n">Best</th><th>This month</th><th>All time</th></tr>{boards}</table></div>
            <h2>Clears ({view.Clears.Count}), newest first</h2>
            <div class="panel"><table><tr><th>When (UTC)</th><th>Level</th><th class="n">Score</th><th class="n">Stars</th><th class="n">Shots</th><th class="n">Time</th><th>Game</th><th>Client</th><th>Address</th></tr>{clears}</table></div>
            """), refresh: false, section: "Players");
    }

    /// <summary>
    /// Whether a nickname mixes letters of more than one script — Latin with Cyrillic or Greek, say — the way a
    /// look-alike of another player's name would. Shown so the wrong player is never hidden (requirement 12).
    /// </summary>
    public static bool MixesScripts(string name) => name.Where(char.IsLetter).Select(Script).Distinct().Count() > 1;

    private static string Script(char letter) => letter switch
    {
        <= '\u024F' or (>= '\u1E00' and <= '\u1EFF') => "Latin",
        >= '\u0370' and <= '\u03FF' or (>= '\u1F00' and <= '\u1FFF') => "Greek",
        >= '\u0400' and <= '\u052F' => "Cyrillic",
        _ => CharUnicodeInfo.GetUnicodeCategory(letter) + ":" + ((int)letter >> 8),
    };

    private static Markup Flags(string name) => MixesScripts(name) ? Html.M($"<span class=\"flag\">mixed scripts</span>") : default;

    private static Markup Shares(IReadOnlyList<(Guid Id, string Name)> others) => others.Count == 0 ? default
        : Html.Join(others.Select((o, i) => Html.M($"{(i > 0 ? ", " : "")}<a href=\"{PlayerLink(o.Id)}\"><bdi>{o.Name}</bdi></a>")));

    private static Markup Rank(BoardRank rank, bool hidden) =>
        hidden ? Html.M($"hidden") : rank.Rank == 0 ? Html.M($"not on it ({rank.Total})") : Html.M($"#{rank.Rank} of {rank.Total}");

    private static string Stamp(DateTimeOffset? at) => at?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";

    private static string BoardLink(BoardKey key) =>
        $"/board?file={Uri.EscapeDataString(key.File)}&hash={Uri.EscapeDataString(key.Hash)}&rules={key.Rules}";

    private static string PlayerLink(Guid id) => $"/player?id={id}";
}
