using System.Globalization;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// The charts page (#9): SVG drawn here, without a script or an inline style, since the CSP allows neither. Colours are
/// classes of the style sheet (<c>c1</c> to <c>c6</c>), so light and dark follow the system as the rest of the page
/// does. Every bar and point carries an SVG <c>&lt;title&gt;</c>, which the browser shows on hover without a script, and
/// every chart's numbers stand in a table under it.
/// </summary>
public static partial class AdminPages
{
    /// <summary>
    /// One series: its name, its colour class and one value per day, NaN on a day without one (a line breaks there). A
    /// line's point can carry a mark, drawn in the failure colour and named in its title and in the numbers.
    /// </summary>
    public sealed record ChartSeries(string Name, string Colour, IReadOnlyList<double> Values, IReadOnlyList<string?>? Marks = null);

    // The drawing's own units: the SVG scales to the panel's width, so these are proportions, not pixels. A chart across
    // the whole page is drawn twice as wide rather than scaled up, so that its text stays the size of the others'
    private const double NarrowWidth = 560, WideWidth = 1120, ChartHeight = 200, Left = 44, Right = 8, Top = 10, Bottom = 24;
    private const double PlotHeight = ChartHeight - Top - Bottom;

    /// <summary>The widest a bar is drawn, so that a few days' bars stay bars rather than blocks.</summary>
    private const double MaxBarWidth = 28;

    /// <summary>The most day labels under a chart; the last day always has one.</summary>
    public const int DayLabels = 7;

    public static string Charts(AdminData.Charts ch)
    {
        Markup Range(string key, string label) => key == ch.Range
            ? Html.M($"<span aria-current=\"true\">{label}</span>")
            : Html.M($"<a href=\"/charts?range={key}\">{label}</a>");
        IReadOnlyList<string> d = ch.Days;
        static IReadOnlyList<double> Of(int[] values) => values.Select(v => (double)v).ToList();
        string[] palette = ["c4", "c2", "c5", "c1", "c3", "c6"];

        Markup refusals = ch.Refusals.Count == 0
            ? Html.M($"<p class=\"note\">None in this range.</p>")
            : BarChart("Refusals per day", d, ch.Refusals.Select((r, i) => new ChartSeries(r.Reason, palette[i % palette.Length], Of(r.Counts))).ToList(), integer: true);

        // The local builds in grey and the other releases in red under the releases charted apart, in the order they arrived
        string[] releaseColours = ["c1", "c2", "c3", "c5"];
        int charted = 0;
        Markup versions = ch.Versions.Count == 0
            ? Html.M($"<p class=\"note\">None in this range.</p>")
            : BarChart("Game versions per day, in per cent of the day's submissions", d, ch.Versions.Select(v => new ChartSeries(v.Version,
                v.Version switch { AdminData.LocalBuilds => "c6", AdminData.OtherReleases => "c4", _ => releaseColours[charted++ % releaseColours.Length] },
                v.Share)).ToList(), integer: false);

        // In the unit the largest backup reads best in, as the overview's sizes are
        double largest = ch.BackupBytes.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        (string unit, double per) = largest < 1024 * 1024 ? ("KB", 1024.0) : ("MB", 1024.0 * 1024);
        Markup sizes = LineChart($"Backup size per day, {unit}", d, [new(unit, "c1", ch.BackupBytes.Select(b => b / per).ToList())], integer: false);

        string[] copyColours = ["c1", "c3", "c5"];
        Markup copies = LineChart("Age of the newest copy per day, in days", d,
            ch.Copies.Select((c, i) => new ChartSeries(c.Name, copyColours[i % copyColours.Length], c.Age, c.Marks)).ToList(), integer: false);

        return Layout("Charts", Html.M($"""
            <div class="ranges">{Range("14d", "14 days")}{Range("90d", "90 days")}{Range("all", "All")}</div>
            <p class="note">One value per UTC day, {d[0]} to {d[^1]}; no range starts before the first day anything was recorded. What the service received, hidden players included: the boards themselves leave hidden players out. A bar or a point shows its number under the pointer, and each chart's numbers are under it.</p>
            <div class="cols">
            <section><h2>Players, in all</h2>{LineChart("Players in all", d, [new("Players", "c1", Of(ch.PlayersTotal))])}</section>
            <section><h2>Players who played, per day</h2>{BarChart("Players who played per day", d, [new("Returning", "c1", Of(ch.ReturningPlayers)), new("First day", "c3", Of(ch.FirstDayPlayers))], integer: true)}</section>
            <section><h2>Clears and unfinished attempts, in all</h2>{LineChart("Clears and unfinished attempts in all", d, [new("Clears", "c1", Of(ch.ClearsTotal)), new("Unfinished", "c2", Of(ch.UnfinishedTotal))])}</section>
            <section><h2>Clears and unfinished attempts, per day</h2>{BarChart("Clears and unfinished attempts per day", d, [new("Clears", "c1", Of(ch.Clears)), new("Unfinished", "c2", Of(ch.Unfinished))], integer: true)}</section>
            <section><h2>Boards with a clear</h2>{LineChart("Boards with a clear", d, [new("Boards", "c3", Of(ch.BoardsCleared))])}</section>
            <section><h2>Minutes played, per day</h2>{BarChart("Minutes played per day", d, [new("Minutes", "c5", ch.Minutes)], integer: false)}</section>
            <section><h2>Refusals, per day</h2>{refusals}</section>
            <section><h2>Game versions, % of each day's submissions</h2><p class="note">The {AdminData.ReleaseSeries} releases sent most in this range apart, newest on top; every local build (<code>dev-…</code>, the owner's and the agents') together in grey.</p>{versions}</section>
            <section><h2>Database size, from the backups</h2><p class="note">Each day's last backup on the box, which keeps {AdminData.BackupKeepDays} days of them: a day without one has no point.</p>{sizes}</section>
            <section><h2>Age of the newest copy</h2><p class="note">The oldest the newest good copy got each day, in days. On the box from the backups kept there; off the box and off the site from the log <code>deploy/backup.sh</code> keeps of its runs, so they start with its first line. A red point is a day a copy failed, or one when the newest good copy got older than its days between copies and half a day: a night it was due went by without one. A copy not set up is not drawn.</p>{copies}</section>
            </div>
            """), refresh: false);
    }

    /// <summary>
    /// A chart's x axis: each slot's key, named in its bars' titles and the numbers table, the table's first heading,
    /// what is drawn under the plot for a chart of a given width, what stands under the legend, whether the table lists
    /// the last slot first, and whether the chart spans the page.
    /// </summary>
    public sealed record XAxis(IReadOnlyList<string> Keys, string Heading, Func<double, Markup> Labels, Markup Key, bool LastFirst, bool Wide = false)
    {
        public double Width => Wide ? WideWidth : NarrowWidth;
    }

    /// <summary>One slot per UTC day: at most <see cref="DayLabels"/> labels, the last day's ending at the right edge.</summary>
    public static XAxis DayAxis(IReadOnlyList<string> days)
    {
        int n = days.Count, every = Math.Max(1, (int)Math.Ceiling(n / (double)DayLabels));
        // The last day's label ends at the right edge rather than centring on its day, where it would be cut off
        Markup Labels(double width) => Html.Join(Enumerable.Range(0, n).Where(i => (n - 1 - i) % every == 0).Select(i => i == n - 1
            ? Html.M($"<text class=\"x last\" x=\"{width - 1:0.##}\" y=\"{ChartHeight - 6:0.##}\">{days[i][5..]}</text>")
            : Html.M($"<text class=\"x\" x=\"{X(i, n, width):0.##}\" y=\"{ChartHeight - 6:0.##}\">{days[i][5..]}</text>")));
        return new XAxis(days, "Day (UTC)", Labels, default, LastFirst: true);
    }

    /// <summary>One slot per category, each labelled under its slot.</summary>
    public static XAxis CategoryAxis(string heading, IReadOnlyList<(string Key, string Label)> categories)
    {
        int n = categories.Count;
        Markup Labels(double width) => Html.Join(categories.Select((c, i) =>
            Html.M($"<text class=\"x\" x=\"{X(i, n, width):0.##}\" y=\"{ChartHeight - 6:0.##}\">{c.Label}</text>")));
        return new XAxis(categories.Select(c => c.Key).ToList(), heading, Labels, default, LastFirst: false);
    }

    /// <summary>
    /// One slot per level, in play order, across the page. Where the levels name chapters, each chapter is numbered under
    /// its levels and set off by a line, and the numbers are keyed under the legend: thirteen chapter names would not fit
    /// under the plot. Without chapters, every tenth level is numbered.
    /// </summary>
    public static XAxis LevelAxis(IReadOnlyList<(string Name, string? Chapter)> levels)
    {
        int n = levels.Count;
        List<(string? Chapter, int First, int Last)> runs = new();
        for (int i = 0; i < n; i++)
            if (runs.Count > 0 && runs[^1].Chapter == levels[i].Chapter) runs[^1] = runs[^1] with { Last = i };
            else runs.Add((levels[i].Chapter, i, i));

        if (runs.All(r => r.Chapter == null))
        {
            Markup Numbers(double width) => Html.Join(Enumerable.Range(0, n).Where(i => i % 10 == 0).Select(i =>
                Html.M($"<text class=\"x\" x=\"{X(i, n, width):0.##}\" y=\"{ChartHeight - 6:0.##}\">{i + 1}</text>")));
            return new XAxis(levels.Select(l => l.Name).ToList(), "Level", Numbers, default, LastFirst: false, Wide: true);
        }

        Markup Labels(double width)
        {
            double slot = (width - Left - Right) / Math.Max(n, 1);
            return Html.Join(runs.Select((r, k) => Html.M($"""
                {(k > 0 ? Html.M($"<line class=\"sep\" x1=\"{Left + r.First * slot:0.##}\" x2=\"{Left + r.First * slot:0.##}\" y1=\"{Top:0.##}\" y2=\"{Top + PlotHeight + 4:0.##}\"/>") : default)}<text class="x" x="{(X(r.First, n, width) + X(r.Last, n, width)) / 2:0.##}" y="{ChartHeight - 6:0.##}">{k + 1}</text>
                """)));
        }
        Markup key = Html.M($"<ol class=\"chapters\">{Html.Join(runs.Select((r, k) => Html.M($"<li><b>{k + 1}</b> {r.Chapter ?? "Without a chapter"}</li>")))}</ol>");
        return new XAxis(levels.Select(l => l.Name).ToList(), "Level", Labels, key, LastFirst: false, Wide: true);
    }

    /// <summary>Stacked bars, one per day, the series bottom to top in the order given.</summary>
    public static Markup BarChart(string label, IReadOnlyList<string> days, IReadOnlyList<ChartSeries> series, bool integer) =>
        BarChart(label, DayAxis(days), series, integer);

    /// <summary>Stacked bars, one per slot of <paramref name="x"/>, the series bottom to top in the order given.</summary>
    public static Markup BarChart(string label, XAxis x, IReadOnlyList<ChartSeries> series, bool integer)
    {
        IReadOnlyList<string> keys = x.Keys;
        int n = keys.Count;
        (double top, double step) = Scale(Enumerable.Range(0, n).Select(i => series.Sum(s => s.Values[i])).DefaultIfEmpty(0).Max(), integer);
        double slot = (x.Width - Left - Right) / Math.Max(n, 1), width = Math.Clamp(slot * 0.72, 1, MaxBarWidth);
        List<Markup> bars = new();
        for (int i = 0; i < n; i++)
        {
            double y = Top + PlotHeight;
            foreach (ChartSeries s in series)
            {
                double value = s.Values[i];
                if (value <= 0) continue;
                double height = value / top * PlotHeight;
                y -= height;
                bars.Add(Html.M($"<rect class=\"{s.Colour}\" x=\"{Left + i * slot + (slot - width) / 2:0.##}\" y=\"{y:0.##}\" width=\"{width:0.##}\" height=\"{height:0.##}\"><title>{keys[i]} · {s.Name}: {Number(value, integer)}</title></rect>"));
            }
        }
        return Chart(label, x, series, top, step, integer, Html.Join(bars));
    }

    /// <summary>
    /// One line per series, through each day's value, with a point per day that has one: a day without a value breaks
    /// the line rather than drawing it down to zero.
    /// </summary>
    public static Markup LineChart(string label, IReadOnlyList<string> days, IReadOnlyList<ChartSeries> series, bool integer = true)
    {
        int n = days.Count;
        (double top, double step) = Scale(series.SelectMany(s => s.Values).Where(double.IsFinite).DefaultIfEmpty(0).Max(), integer);
        double Y(double value) => Top + PlotHeight - value / top * PlotHeight;
        string Point(int i, double value) => string.Create(CultureInfo.InvariantCulture, $"{X(i, n, NarrowWidth):0.##},{Y(value):0.##}");
        List<Markup> lines = new();
        foreach (ChartSeries s in series)
        {
            // Each run of days with values is a line of its own; a lone day is its point alone
            for (int i = 0; i < n; i++)
            {
                if (!double.IsFinite(s.Values[i])) continue;
                int last = i;
                while (last + 1 < n && double.IsFinite(s.Values[last + 1])) last++;
                if (last > i)
                    lines.Add(Html.M($"<polyline class=\"line {s.Colour}\" points=\"{string.Join(" ", Enumerable.Range(i, last - i + 1).Select(k => Point(k, s.Values[k])))}\"/>"));
                i = last;
            }
            lines.AddRange(Enumerable.Range(0, n).Where(i => double.IsFinite(s.Values[i])).Select(i => s.Marks?[i] is { } mark
                ? Html.M($"<circle class=\"{s.Colour} mark\" cx=\"{X(i, n, NarrowWidth):0.##}\" cy=\"{Y(s.Values[i]):0.##}\" r=\"4\"><title>{days[i]} · {s.Name}: {Number(s.Values[i], integer)}, {mark}</title></circle>")
                : Html.M($"<circle class=\"{s.Colour}\" cx=\"{X(i, n, NarrowWidth):0.##}\" cy=\"{Y(s.Values[i]):0.##}\" r=\"2.5\"><title>{days[i]} · {s.Name}: {Number(s.Values[i], integer)}</title></circle>")));
        }
        return Chart(label, DayAxis(days), series, top, step, integer, Html.Join(lines));
    }

    /// <summary>
    /// The axis's top and step: a step of 1, 2 or 5 times a power of ten that divides the top into at most five, and
    /// for counts never under 1. A chart of nothing still has an axis, from 0 to one step.
    /// </summary>
    public static (double Top, double Step) Scale(double max, bool integer)
    {
        double rough = Math.Max(max, 0) / 4;
        if (rough <= 0) return (integer ? 1 : 0.1, integer ? 1 : 0.1);
        double power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double f = rough / power;
        double step = (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * power;
        if (integer) step = Math.Max(1, Math.Round(step));
        return (Math.Ceiling(max / step - 1e-9) * step, step);
    }

    /// <summary>The middle of a slot: a bar's centre and a line's point fall on the same place in every chart.</summary>
    private static double X(int i, int n, double width) => Left + (i + 0.5) * (width - Left - Right) / Math.Max(n, 1);

    private static string Number(double value, bool integer) =>
        value.ToString(integer ? "0" : "0.##", CultureInfo.InvariantCulture);

    /// <summary>A series' value on a slot in the numbers table, with its mark when it has one.</summary>
    private static Markup Cell(ChartSeries s, int i, bool integer) =>
        !double.IsFinite(s.Values[i]) ? Html.M($"<td class=\"n\">–</td>")
        : s.Marks?[i] is { } mark ? Html.M($"<td class=\"n\">{Number(s.Values[i], integer)} <span class=\"flag\">{mark}</span></td>")
        : Html.M($"<td class=\"n\">{Number(s.Values[i], integer)}</td>");

    private static Markup Chart(string label, XAxis x, IReadOnlyList<ChartSeries> series, double top, double step, bool integer, Markup plot)
    {
        int n = x.Keys.Count;
        double chartWidth = x.Width;
        Markup grid = Html.Join(Enumerable.Range(0, (int)Math.Round(top / step) + 1).Select(k =>
        {
            double value = k * step, y = Top + PlotHeight - value / top * PlotHeight;
            return Html.M($"<line class=\"grid\" x1=\"{Left:0.##}\" x2=\"{chartWidth - Right:0.##}\" y1=\"{y:0.##}\" y2=\"{y:0.##}\"/><text class=\"y\" x=\"{Left - 6:0.##}\" y=\"{y + 4:0.##}\">{Number(value, integer)}</text>");
        }));
        Markup legend = Html.Join(series.Select(s => Html.M($"<li><span class=\"swatch {s.Colour}\"></span>{s.Name}</li>")));
        Markup head = Html.Join(series.Select(s => Html.M($"<th class=\"n\">{s.Name}</th>")));
        IEnumerable<int> order = x.LastFirst ? Enumerable.Range(0, n).Reverse() : Enumerable.Range(0, n);
        Markup rows = Html.Join(order.Select(i => Html.M(
            $"<tr><td>{x.Keys[i]}</td>{Html.Join(series.Select(s => Cell(s, i, integer)))}</tr>")));
        return Html.M($"""
            <div class="panel chart-panel"><svg class="{(x.Wide ? "chart wide" : "chart")}" viewBox="0 0 {chartWidth:0} {ChartHeight:0}" role="img" aria-label="{label}"><title>{label}</title>{grid}{plot}{x.Labels(chartWidth)}</svg>
            <ul class="legend">{legend}</ul>{x.Key}
            <details class="numbers"><summary>The numbers</summary><table><tr><th>{x.Heading}</th>{head}</tr>{rows}</table></details></div>
            """);
    }
}
