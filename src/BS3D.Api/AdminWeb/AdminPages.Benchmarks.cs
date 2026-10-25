using System.Globalization;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// The Benchmarks tab (BS3D#988): every finished run of the game's benchmark a game sent, newest first, with the machine in a
/// few words and each scene's median frame rate, and a result's own page with everything it carried. A result is attacker
/// text like a note (anyone can send one), so every field is encoded and isolated in <c>&lt;bdi&gt;</c>.
/// </summary>
public static partial class AdminPages
{
    /// <param name="list">Null on a database not yet at schema 5.</param>
    public static string Benchmarks((IReadOnlyList<AdminData.BenchmarkRow> Rows, int Total)? list, TimeZoneInfo zone)
    {
        if (list is not var (rows, total))
            return Layout("Benchmarks", Html.M($"<p class=\"warn\">The database is not at schema 5 yet, so it holds no benchmarks. It never migrates here: update the service first.</p>"), refresh: false);

        Markup body = rows.Count == 0
            ? Html.M($"<p class=\"note\">No benchmarks yet. A player runs one from the game's Extras &gt; Benchmark; a finished run is sent with the machine it ran on.</p>")
            : Html.M($"""
                <p class="note">{(total > rows.Count ? Html.M($"The newest {rows.Count} of {total}") : Html.M($"{total}"))} benchmarks games sent, newest first ({ZoneName(zone)}), with no player or address. Each scene's figure is its median frame rate; the physics' is its median step, against 8.33 ms. Read them whole on the box with <code>BS3D.Api admin benchmarks</code>.</p>
                <div class="panel"><table><tr><th>Received</th><th>Game</th><th>Graphics</th><th>Processor</th><th>System</th><th>Screen</th><th>Tier</th><th>Median FPS</th><th class="n">Physics</th><th>Offered</th></tr>{Html.Join(rows.Select(Row))}</table></div>
                """);
        return Layout("Benchmarks", body, refresh: false);

        Markup Row(AdminData.BenchmarkRow b) => Html.M($"""
            <tr><td class="t"><a href="{BenchmarkLink(b.Id)}">{Stamp(b.At, zone)}</a></td><td><code>{b.GameVersion}</code></td><td><bdi>{b.Gpu ?? ""}</bdi></td>
            <td><bdi>{b.Cpu ?? ""}</bdi></td><td><bdi>{b.Os ?? ""}</bdi></td><td><bdi>{b.Screen ?? ""}</bdi></td><td><bdi>{b.Tier ?? ""}</bdi></td>
            <td class="wrap">{Scenes(b.Scenes)}</td><td class="n">{(b.PhysicsMedianMs is double ms ? Ms(ms) : "")}</td><td><bdi>{b.Recommended ?? ""}</bdi></td></tr>
            """);
    }

    public static string Benchmark(AdminData.BenchmarkView v, TimeZoneInfo zone)
    {
        AdminData.BenchmarkRow b = v.Row;
        Markup Facts(IReadOnlyList<(string Key, string Value)> fields, string none) => fields.Count == 0
            ? Html.M($"<p class=\"note\">{none}</p>")
            : Html.M($"""<div class="panel"><table class="facts">{Html.Join(fields.Select(f => Html.M($"<tr><th><bdi>{f.Key}</bdi></th><td><bdi>{f.Value}</bdi></td></tr>")))}</table></div>""");
        Markup segments = v.Segments.Count == 0
            ? Html.M($"<p class=\"note\">The result carried no scenes.</p>")
            : Html.M($"""
                <div class="panel"><table><tr>{Html.Join(v.SegmentColumns.Select(k => Html.M($"<th><bdi>{k}</bdi></th>")))}</tr>
                {Html.Join(v.Segments.Select(s => Html.M($"<tr>{Html.Join(s.Select(cell => Html.M($"<td><bdi>{cell}</bdi></td>")))}</tr>")))}</table></div>
                """);

        return Layout($"Benchmark {b.Id}", Html.M($"""
            <p>{Stamp(b.At, zone)} {ZoneName(zone)} · <code>{b.GameVersion}</code> · <bdi>{b.Gpu ?? "an unnamed graphics card"}</bdi></p>
            <h2>The machine</h2>{Facts(v.System, "The result carried no machine.")}
            <h2>The scenes</h2>{segments}
            <h2>The physics</h2>{Facts(v.Physics, "The result carried no physics: the run measured the scenes only.")}
            <h2>The run</h2>{Facts(v.Other, "Nothing else.")}
            """), refresh: false, section: "Benchmarks");
    }

    /// <summary>"Cavern 346 · Neon City 196": each scene's median frame as a rate.</summary>
    private static Markup Scenes(IReadOnlyList<AdminData.BenchmarkScene> scenes) =>
        Html.Join(scenes.Select((s, i) => Html.M($"{(i > 0 ? " · " : "")}<bdi>{s.Scene}</bdi> {(s.MedianMs is double ms && ms > 0 ? (1000 / ms).ToString("0", CultureInfo.InvariantCulture) : "?")}")));

    private static string Ms(double ms) => ms.ToString("0.00", CultureInfo.InvariantCulture) + " ms";

    private static string BenchmarkLink(long id) => string.Create(CultureInfo.InvariantCulture, $"/benchmark?id={id}");
}
