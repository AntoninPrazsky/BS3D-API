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
    /// <summary>One series: its name, its colour class and one value per day.</summary>
    public sealed record ChartSeries(string Name, string Colour, IReadOnlyList<double> Values);

    // The drawing's own units: the SVG scales to the panel's width, so these are proportions, not pixels
    private const double ChartWidth = 560, ChartHeight = 200, Left = 44, Right = 8, Top = 10, Bottom = 24;
    private const double PlotWidth = ChartWidth - Left - Right, PlotHeight = ChartHeight - Top - Bottom;

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
            </div>
            """), refresh: false);
    }

    /// <summary>Stacked bars, one per day, the series bottom to top in the order given.</summary>
    public static Markup BarChart(string label, IReadOnlyList<string> days, IReadOnlyList<ChartSeries> series, bool integer)
    {
        int n = days.Count;
        (double top, double step) = Scale(Enumerable.Range(0, n).Select(i => series.Sum(s => s.Values[i])).DefaultIfEmpty(0).Max(), integer);
        double slot = PlotWidth / n, width = Math.Clamp(slot * 0.72, 1, MaxBarWidth);
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
                bars.Add(Html.M($"<rect class=\"{s.Colour}\" x=\"{Left + i * slot + (slot - width) / 2:0.##}\" y=\"{y:0.##}\" width=\"{width:0.##}\" height=\"{height:0.##}\"><title>{days[i]} · {s.Name}: {Number(value, integer)}</title></rect>"));
            }
        }
        return Chart(label, days, series, top, step, integer, Html.Join(bars));
    }

    /// <summary>One line per series, through each day's value, with a point per day.</summary>
    public static Markup LineChart(string label, IReadOnlyList<string> days, IReadOnlyList<ChartSeries> series)
    {
        int n = days.Count;
        (double top, double step) = Scale(series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max(), integer: true);
        double Y(double value) => Top + PlotHeight - value / top * PlotHeight;
        List<Markup> lines = new();
        foreach (ChartSeries s in series)
        {
            string points = string.Join(" ", Enumerable.Range(0, n).Select(i => string.Create(CultureInfo.InvariantCulture, $"{X(i, n):0.##},{Y(s.Values[i]):0.##}")));
            lines.Add(Html.M($"<polyline class=\"line {s.Colour}\" points=\"{points}\"/>"));
            lines.AddRange(Enumerable.Range(0, n).Select(i => Html.M(
                $"<circle class=\"{s.Colour}\" cx=\"{X(i, n):0.##}\" cy=\"{Y(s.Values[i]):0.##}\" r=\"2.5\"><title>{days[i]} · {s.Name}: {Number(s.Values[i], true)}</title></circle>")));
        }
        return Chart(label, days, series, top, step, integer: true, Html.Join(lines));
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

    /// <summary>The middle of a day's slot: a bar's centre and a line's point fall on the same place in every chart.</summary>
    private static double X(int i, int n) => Left + (i + 0.5) * PlotWidth / n;

    private static string Number(double value, bool integer) =>
        value.ToString(integer ? "0" : "0.##", CultureInfo.InvariantCulture);

    private static Markup Chart(string label, IReadOnlyList<string> days, IReadOnlyList<ChartSeries> series, double top, double step,
        bool integer, Markup plot)
    {
        int n = days.Count;
        Markup grid = Html.Join(Enumerable.Range(0, (int)Math.Round(top / step) + 1).Select(k =>
        {
            double value = k * step, y = Top + PlotHeight - value / top * PlotHeight;
            return Html.M($"<line class=\"grid\" x1=\"{Left:0.##}\" x2=\"{ChartWidth - Right:0.##}\" y1=\"{y:0.##}\" y2=\"{y:0.##}\"/><text class=\"y\" x=\"{Left - 6:0.##}\" y=\"{y + 4:0.##}\">{Number(value, integer)}</text>");
        }));
        int every = Math.Max(1, (int)Math.Ceiling(n / (double)DayLabels));
        // The last day's label ends at the right edge rather than centring on its day, where it would be cut off
        Markup labels = Html.Join(Enumerable.Range(0, n).Where(i => (n - 1 - i) % every == 0).Select(i => i == n - 1
            ? Html.M($"<text class=\"x last\" x=\"{ChartWidth - 1:0.##}\" y=\"{ChartHeight - 6:0.##}\">{days[i][5..]}</text>")
            : Html.M($"<text class=\"x\" x=\"{X(i, n):0.##}\" y=\"{ChartHeight - 6:0.##}\">{days[i][5..]}</text>")));
        Markup legend = Html.Join(series.Select(s => Html.M($"<li><span class=\"swatch {s.Colour}\"></span>{s.Name}</li>")));
        Markup head = Html.Join(series.Select(s => Html.M($"<th class=\"n\">{s.Name}</th>")));
        Markup rows = Html.Join(Enumerable.Range(0, n).Reverse().Select(i => Html.M(
            $"<tr><td>{days[i]}</td>{Html.Join(series.Select(s => Html.M($"<td class=\"n\">{Number(s.Values[i], integer)}</td>")))}</tr>")));
        return Html.M($"""
            <div class="panel chart-panel"><svg class="chart" viewBox="0 0 {ChartWidth:0} {ChartHeight:0}" role="img" aria-label="{label}"><title>{label}</title>{grid}{plot}{labels}</svg>
            <ul class="legend">{legend}</ul>
            <details class="numbers"><summary>The numbers</summary><table><tr><th>Day (UTC)</th>{head}</tr>{rows}</table></details></div>
            """);
    }
}
