using System.Globalization;

namespace BS3D.Api;

/// <summary>
/// The play counts (BS3D#709) as the owner reads them, on the admin page's Play tab and from <c>admin stats</c>: one line
/// per board with its level's name, place and chapter from the ceiling tables, ordered by one of a fixed set of keys —
/// the loss rate, the retries, the skips, or play order. <b>Every rate stands beside the counts it is a share of</b>: the
/// players are few and anonymous, and a level two attempts have seen is not a trend.
/// </summary>
public static class PlayStatsReport
{
    /// <summary>The orders the report is offered in; anything else reads as the first. Never passed to SQL.</summary>
    public static readonly string[] Sorts = ["loss", "retries", "skips", "order"];

    /// <summary>One board's line: its counts, and its level as the ceiling tables name it (null where none does).</summary>
    public sealed record Line(ScoreStore.PlayStatsRow Row, string? Name, string? Chapter, int? Position)
    {
        public long Losses => Row.LostOutOfBalls + Row.LostLine;

        /// <summary>The attempts that reached a result page: what a loss rate is a share of.</summary>
        public long Ended => Row.Clears + Losses;

        /// <summary>Every attempt closed, its page reached or left before it: what the shots and seconds are spread over.</summary>
        public long Closed => Ended + Row.Abandons;

        public double? LossRate => Ended > 0 ? Losses / (double)Ended : null;
        public double? RetryRate => Row.Starts > 0 ? Row.Retries / (double)Row.Starts : null;
        public double? SkipRate => Row.Starts > 0 ? Row.Skips / (double)Row.Starts : null;
        public double? ShotsPerAttempt => Closed > 0 ? Row.Shots / (double)Closed : null;
        public double? SecondsPerAttempt => Closed > 0 ? Row.Seconds / Closed : null;
    }

    /// <summary>The key <paramref name="sort"/> names, or the first of <see cref="Sorts"/>.</summary>
    public static string SortOf(string? sort) => Sorts.Contains(sort) ? sort! : Sorts[0];

    public static List<Line> Build(IEnumerable<ScoreStore.PlayStatsRow> rows, Ceilings ceilings, string? sort)
    {
        List<Line> lines = rows.Select(row =>
        {
            string? name = ceilings.TryGet(row.Board.File, row.Board.Hash, row.Board.Rules, out CeilingRow known) ? known.Name : null;
            (int Position, string? Block)? place = ceilings.Levels.TryGetValue(row.Board.File, out var p) ? p : null;
            return new Line(row, name, place?.Block, place?.Position);
        }).ToList();

        // Each order falls back to play order, and play order to the file and the board, so the lines never shuffle
        IOrderedEnumerable<Line> ordered = SortOf(sort) switch
        {
            "retries" => lines.OrderByDescending(l => l.Row.Retries).ThenByDescending(l => l.RetryRate ?? -1),
            "skips" => lines.OrderByDescending(l => l.Row.Skips).ThenByDescending(l => l.SkipRate ?? -1),
            "order" => lines.OrderBy(l => l.Position == null).ThenBy(l => l.Position ?? 0),
            _ => lines.OrderByDescending(l => l.LossRate ?? -1).ThenByDescending(l => l.Ended),
        };

        return ordered.ThenBy(l => l.Position == null).ThenBy(l => l.Position ?? 0)
            .ThenBy(l => l.Row.Board.File, StringComparer.Ordinal).ThenBy(l => l.Row.Board.Hash, StringComparer.Ordinal)
            .ThenBy(l => l.Row.Board.Rules).ToList();
    }

    /// <summary>"3 of 5 (60%)", or the count alone when there is nothing it is a share of.</summary>
    public static string Share(long count, long of) =>
        of > 0 ? string.Create(CultureInfo.InvariantCulture, $"{count} of {of} ({100.0 * count / of:0}%)") : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>A figure to one decimal, or a dash.</summary>
    public static string Figure(double? value, string format = "0.0") =>
        value is double v ? v.ToString(format, CultureInfo.InvariantCulture) : "-";

    /// <summary>The level as the line names it: its name and place, else its file.</summary>
    public static string LevelOf(Line line) =>
        line.Name != null ? $"{(line.Position is int p ? $"{p + 1}. " : "")}{line.Name}" : line.Row.Board.File;

    /// <summary>The report as text, for <c>admin stats</c>: a header and one fixed-width line per board.</summary>
    public static IEnumerable<string> Text(List<Line> lines, string sort)
    {
        yield return $"play counts by {SortOf(sort)}: {lines.Count} board(s), {lines.Sum(l => l.Row.Starts)} start(s)";
        yield return string.Create(CultureInfo.InvariantCulture,
            $"{"level",-22} {"chapter",-16} {"board",-22} {"starts",7} {"lost (of ended)",-20} {"balls",6} {"line",5} {"retries (of starts)",-21} {"quits",6} {"skips",-18} {"1st clears",10} {"shots/att",9} {"s/att",6}");

        foreach (Line l in lines)
        {
            string level = LevelOf(l);
            string chapter = l.Chapter ?? "";
            string board = $"{l.Row.Board.File}#{l.Row.Board.Hash[..Math.Min(6, l.Row.Board.Hash.Length)]} r{l.Row.Board.Rules}";
            yield return string.Create(CultureInfo.InvariantCulture,
                $"{Cut(level, 22),-22} {Cut(chapter, 16),-16} {Cut(board, 22),-22} {l.Row.Starts,7} {Share(l.Losses, l.Ended),-20} {l.Row.LostOutOfBalls,6} {l.Row.LostLine,5} {Share(l.Row.Retries, l.Row.Starts),-21} {l.Row.Abandons,6} {Share(l.Row.Skips, l.Row.Starts),-18} {l.Row.FirstClears,10} {Figure(l.ShotsPerAttempt),9} {Figure(l.SecondsPerAttempt, "0"),6}");
        }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
