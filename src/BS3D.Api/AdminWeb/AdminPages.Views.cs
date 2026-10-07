using System.Globalization;

namespace BS3D.Api.AdminWeb;

/// <summary>The boards and players views (issue #5, requirement 12).</summary>
public static partial class AdminPages
{
    /// <param name="noTables">The ceilings directory, when no table was read from it: then every board is "in no ceiling table".</param>
    /// <param name="funnel">Every named level's players (#9), drawn above the list when there is one.</param>
    public static string Boards(IReadOnlyList<AdminData.BoardRow> boards, string month, TimeZoneInfo zone, string? noTables = null,
        IReadOnlyList<AdminData.FunnelLevel>? funnel = null)
    {
        Markup chart = funnel is not { Count: > 0 } ? default : Html.M($"""
            <h2>Players per level, all time</h2>
            {BarChart("Players per level, all time", LevelAxis(funnel.Select(l => (l.Name, l.Chapter)).ToList()),
                [new("Cleared", "c1", funnel.Select(l => (double)l.Cleared).ToList()), new("Not cleared yet", "c2", funnel.Select(l => (double)l.Unfinished).ToList())], integer: true)}
            <p class="note">Each level once, its versions together, in the order of the list below: where the bars drop is where players stop. Hidden players are not counted. "Not cleared yet" is a player who tried the level and never cleared it.</p>
            """);
        int unknown = boards.Count(b => !b.Known);
        Markup missing = noTables == null ? default
            : Html.M($"<p class=\"warn\">No ceiling table in <code>{noTables}</code> (Scores__CeilingsDirectory): only the boards with submissions are listed.</p>");
        // Each chapter under its name, when a table names chapters; the boards no table names last, under theirs
        bool chaptered = boards.Any(b => b.Chapter != null);
        static string Group(AdminData.BoardRow b) => !b.Known ? "In no ceiling table" : b.Chapter ?? "Without a chapter";
        Markup Heading(int i) => !chaptered || (i > 0 && Group(boards[i - 1]) == Group(boards[i])) ? default
            : Html.M($"<tr class=\"group\"><th colspan=\"9\"><bdi>{Group(boards[i])}</bdi> <span>{boards.Skip(i).TakeWhile(b => Group(b) == Group(boards[i])).Count()} boards</span></th></tr>");
        Markup rows = Html.Join(boards.Select((b, i) => Html.M($"""
            {Heading(i)}<tr><td><a href="{BoardLink(b.Key)}">{b.Name ?? b.Key.File}</a>{(b.Known ? default : Html.M($" <span class=\"flag\">in no ceiling table</span>"))}</td>
            <td><code>{b.Key.File}#{b.Key.Hash} r{b.Key.Rules}</code></td><td class="n">{(b.Ceiling is int c ? c.ToString("N0", CultureInfo.InvariantCulture) : "")}</td>
            <td class="n">{b.Shots}</td><td class="n">{b.MonthPlayers}</td><td class="n">{b.AllPlayers}</td><td class="n">{b.Clears}</td><td class="n">{b.Unfinished}</td><td class="t">{Stamp(b.LastPlayed, zone)}</td></tr>
            """)));
        return Layout("Boards", Html.M($"""
            {missing}{chart}<h2>Boards</h2><p class="note">{boards.Count} boards: every board a ceiling table names{(unknown > 0 ? Html.M($", and {unknown} with submissions that no table names") : default)}, {(chaptered ? "chapter by chapter in play order" : "in play order (the tables name no chapters)")}. Players are the rows of the game's boards: each visible player once, hidden players not counted, and an unfinished attempt only for a player who has never cleared the board. Clears and unfinished attempts count every one sent.</p>
            <div class="panel"><table><tr><th>Level</th><th>Board</th><th class="n">Ceiling</th><th class="n">Shots</th><th class="n">Players {month}</th><th class="n">Players all time</th><th class="n">Clears</th><th class="n">Unfinished</th><th>Last played ({ZoneName(zone)})</th></tr>{rows}</table></div>
            """), refresh: false);
    }

    public static string Board(AdminData.BoardView b, TimeZoneInfo zone)
    {
        Markup Entries(IReadOnlyList<BoardEntry> entries, int total) => entries.Count == 0
            ? Html.M($"<p class=\"note\">Nobody is on this board.</p>")
            : Html.M($"""
                <div class="panel"><table><tr><th class="n">Rank</th><th>Player</th><th class="n">Score</th><th class="n">Stars</th><th>Sent ({ZoneName(zone)})</th></tr>{Html.Join(entries.Select(e => Html.M($"""
                    <tr><td class="n">{e.Rank}</td><td><bdi>{e.Name}</bdi></td><td class="n">{e.Score.ToString("N0", CultureInfo.InvariantCulture)}</td><td class="n">{Stars(e.Stars)}</td><td class="t">{Stamp(e.At, zone)}</td></tr>
                    """)))}</table></div>{(total > entries.Count ? Html.M($"<p class=\"note\">The first {entries.Count} of {total}.</p>") : default)}
                """);

        Markup hidden = b.Hidden.Count == 0 ? default : Html.M($"""
            <h2>Hidden players (on no board)</h2>
            <div class="panel"><table><tr><th>Player</th><th class="n">Best</th><th class="n">Stars</th><th class="n">Clears</th><th class="n">Unfinished</th></tr>{Html.Join(b.Hidden.Select(h => Html.M($"""
                <tr><td><a href="{PlayerLink(h.Player)}"><bdi>{h.Name}</bdi></a></td><td class="n">{h.Score.ToString("N0", CultureInfo.InvariantCulture)}</td><td class="n">{Stars(h.Stars)}</td><td class="n">{h.Clears}</td><td class="n">{h.Unfinished}</td></tr>
                """)))}</table></div>
            """);

        Markup ceiling = b.Ceiling is { } c
            ? Html.M($"<span class=\"note\">ceiling {c.Ceiling.ToString("N0", CultureInfo.InvariantCulture)} · {c.MinShots}–{c.Shots} shots</span>")
            : Html.M($"<span class=\"flag\">in no ceiling table</span>");
        return Layout(b.Ceiling?.Name ?? b.Key.File, Html.M($"""
            <p><code class="key">{b.Key.File}#{b.Key.Hash} r{b.Key.Rules}</code> {ceiling}</p>
            <p class="note">As the game sees it (the same query as <code>GET /v1/boards</code>): each visible player's best clear, or, for a player who has never cleared this board, their best unfinished attempt; every clear above every unfinished attempt, ties to the earlier.</p>
            {ScoreChart(b)}
            <div class="cols">
            <section><h2>{b.Month} ({b.MonthTotal})</h2>{Entries(b.MonthEntries, b.MonthTotal)}</section>
            <section><h2>All time ({b.AllTotal})</h2>{Entries(b.AllEntries, b.AllTotal)}</section>
            </div>
            {hidden}
            """), refresh: false, section: "Boards");
    }

    /// <summary>The all-time board's rows by their score's share of the ceiling (#9): a cluster near the top is the ceiling met, or probed.</summary>
    private static Markup ScoreChart(AdminData.BoardView b)
    {
        if (b.Scores is not { } bins || b.AllTotal == 0) return default;
        var tenths = Enumerable.Range(0, 10).Select(i => ($"{i * 10}–{(i + 1) * 10} %", $"{i * 10}")).ToList();
        return Html.M($"""
            <div class="cols">
            <section><h2>Best scores against the ceiling, all time</h2>
            {BarChart("Best scores against the ceiling", CategoryAxis("Share of the ceiling", tenths),
                [new("Clears", "c1", bins.Clears.Select(n => (double)n).ToList()), new("Unfinished", "c2", bins.Unfinished.Select(n => (double)n).ToList())], integer: true)}</section>
            <section><h2>What it shows</h2><p class="note">Each player's row on the all-time board, by its score as a share of the ceiling, {b.Ceiling!.Ceiling.ToString("N0", CultureInfo.InvariantCulture)}, in tenths: 30 is 30 to 40 %, and the last tenth runs up to the ceiling itself. Many rows near the top mean the ceiling is close to what players make, or that someone is probing it.</p></section>
            </div>
            """);
    }

    public static string Players(IReadOnlyList<AdminData.PlayerRow> players, string order, TimeZoneInfo zone)
    {
        Markup Sort(string key, string label) => key == order ? Html.M($"<span class=\"sorted\">{label}</span>") : Html.M($"<a href=\"/players?sort={key}\">{label}</a>");
        Markup rows = Html.Join(players.Select(p => Html.M($"""
            <tr><td><a href="{PlayerLink(p.Id)}"><bdi>{p.Name}</bdi></a>{Flags(p.Name)}<code class="id">{p.Id.ToString()[..8]}</code></td>
            <td class="t">{Stamp(p.Created, zone)}</td><td>{(p.Hidden ? Html.M($"<span class=\"chip muted\">hidden</span>") : default)}</td><td class="n">{p.Clears}</td><td class="n">{p.Unfinished}</td><td class="t">{Stamp(p.LastPlayed, zone)}</td>
            <td class="n">{p.Addresses}</td><td>{Shares(p.SharesWith)}</td></tr>
            """)));
        return Layout("Players", Html.M($"""
            <p class="note">{players.Count} players. "Addresses" counts the different addresses a player's submissions came from; the addresses themselves are never shown. Behind the home network every player at home shares one.</p>
            <div class="panel"><table><tr><th>{Sort("name", "Player")}</th><th>{Sort("created", $"Created ({ZoneName(zone)})")}</th><th>Hidden</th><th class="n">{Sort("clears", "Clears")}</th><th class="n">Unfinished</th><th>{Sort("last", $"Last played ({ZoneName(zone)})")}</th><th class="n">Addresses</th><th>Shares an address with</th></tr>{rows}</table></div>
            """), refresh: false);
    }

    public static string Player(AdminData.PlayerView view, TimeZoneInfo zone)
    {
        AdminData.PlayerRow p = view.Player;
        Markup boards = Html.Join(view.Boards.Select(b => Html.M($"""
            <tr><td><a href="{BoardLink(b.Board)}"><code>{b.Board.File}#{b.Board.Hash}</code></a></td><td class="n">{b.Best.ToString("N0", CultureInfo.InvariantCulture)}</td><td class="n">{Stars(b.BestStars)}</td>
            <td>{Rank(b.Month, p.Hidden)}</td><td>{Rank(b.AllTime, p.Hidden)}</td></tr>
            """)));
        Markup submissions = Html.Join(view.Submissions.Select(c => Html.M($"""
            <tr><td class="t">{Stamp(c.At, zone)}</td><td><code>{c.Board.File}</code></td><td class="n">{c.Score.ToString("N0", CultureInfo.InvariantCulture)}</td><td class="n">{Stars(c.Stars)}</td>
            <td class="n">{c.Shots}</td><td class="n">{c.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s</td><td><bdi>{c.GameVersion}</bdi></td><td><bdi>{c.UserAgent ?? ""}</bdi></td><td>{c.Address}</td></tr>
            """)));
        return Layout(p.Name, Html.M($"""
            <div class="panel"><table class="facts">
            <tr><th>Player</th><td><bdi>{p.Name}</bdi>{Flags(p.Name)}</td></tr>
            <tr><th>Id</th><td><code>{p.Id}</code></td></tr>
            <tr><th>Created</th><td>{Stamp(p.Created, zone)} {ZoneName(zone)}</td></tr>
            <tr><th>On the boards</th><td>{(p.Hidden ? Html.M($"<span class=\"flag\">hidden: on no board and in no count</span>") : Html.M($"yes"))}</td></tr>
            <tr><th>Addresses</th><td>{p.Addresses}, lettered below in the order they first appear</td></tr>
            <tr><th>Shares an address with</th><td>{Shares(p.SharesWith)}</td></tr>
            </table></div>
            <h2>Boards</h2>
            <div class="panel"><table><tr><th>Board</th><th class="n">Best</th><th class="n">Stars</th><th>This month</th><th>All time</th></tr>{boards}</table></div>
            <h2>Submissions ({view.Submissions.Count}), newest first</h2>
            <div class="panel"><table><tr><th>When ({ZoneName(zone)})</th><th>Level</th><th class="n">Score</th><th class="n">Stars</th><th class="n">Shots</th><th class="n">Time</th><th>Game</th><th>Client</th><th>Address</th></tr>{submissions}</table></div>
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

    /// <summary>A row's stars, or for 0 the word: a clear always earns at least one (#8).</summary>
    private static Markup Stars(int stars) => stars > 0 ? Html.M($"{stars}") : Html.M($"<span class=\"chip muted\">unfinished</span>");

    private static Markup Flags(string name) => MixesScripts(name) ? Html.M($"<span class=\"flag\">mixed scripts</span>") : default;

    private static Markup Shares(IReadOnlyList<(Guid Id, string Name)> others) => others.Count == 0 ? default
        : Html.Join(others.Select((o, i) => Html.M($"{(i > 0 ? ", " : "")}<a href=\"{PlayerLink(o.Id)}\"><bdi>{o.Name}</bdi></a>")));

    private static Markup Rank(BoardRank rank, bool hidden) =>
        hidden ? Html.M($"hidden") : rank.Rank == 0 ? Html.M($"not on it ({rank.Total})") : Html.M($"#{rank.Rank} of {rank.Total}");

    /// <summary>A moment as the owner reads it: in <paramref name="zone"/>, its daylight saving time included.</summary>
    internal static string Stamp(DateTimeOffset? at, TimeZoneInfo zone, bool seconds = false) =>
        at is { } t ? TimeZoneInfo.ConvertTime(t, zone).ToString(seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// The zone in a label: "UTC", or its IANA id's city and "time" (Europe/Prague: "Prague time"). A Windows id, where the
    /// page runs in development, is converted to its IANA one first.
    /// </summary>
    internal static string ZoneName(TimeZoneInfo zone)
    {
        string id = zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out string? iana) ? iana : zone.Id;
        if (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime && (id is "UTC" or "UCT" or "Universal" or "Zulu" || id.StartsWith("Etc/", StringComparison.Ordinal)))
            return "UTC";
        int slash = id.LastIndexOf('/');
        return slash >= 0 ? $"{id[(slash + 1)..].Replace('_', ' ')} time" : "local time";
    }

    private static string BoardLink(BoardKey key) =>
        $"/board?file={Uri.EscapeDataString(key.File)}&hash={Uri.EscapeDataString(key.Hash)}&rules={key.Rules}";

    private static string PlayerLink(Guid id) => $"/player?id={id}";
}
