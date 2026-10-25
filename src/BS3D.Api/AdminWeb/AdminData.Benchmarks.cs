using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// The benchmarks (BS3D#988): what the Benchmarks tab lists and a result's page shows, read out of each result as the game
/// sent it. A field the game did not send, or a result that does not read, is shown as missing, never as a 500.
/// </summary>
public sealed partial class AdminData
{
    /// <summary>One scene of a result: its name and its median frame in milliseconds, when it has both.</summary>
    public sealed record BenchmarkScene(string Scene, double? MedianMs);

    /// <summary>One result in the list: the machine in a few words and the figures a row has room for.</summary>
    public sealed record BenchmarkRow(long Id, DateTimeOffset At, string GameVersion, string? Gpu, string? Cpu, string? Os,
        string? Screen, string? Tier, string? Recommended, IReadOnlyList<BenchmarkScene> Scenes, double? PhysicsMedianMs);

    /// <summary>
    /// A result's page: the row, the machine field by field, the scenes as a table (the columns every scene has, in the order
    /// the game wrote them), the physics field by field, and whatever else the result carried.
    /// </summary>
    public sealed record BenchmarkView(BenchmarkRow Row, IReadOnlyList<(string Key, string Value)> System,
        IReadOnlyList<string> SegmentColumns, IReadOnlyList<IReadOnlyList<string>> Segments, IReadOnlyList<(string Key, string Value)> Physics,
        IReadOnlyList<(string Key, string Value)> Other);

    /// <summary>The results the list shows at most, newest first.</summary>
    public const int BenchmarksListed = 200;

    /// <summary>The newest results and how many are stored, or null on a database the service has not brought to schema 5.</summary>
    public (IReadOnlyList<BenchmarkRow> Rows, int Total)? ReadBenchmarks()
    {
        using SqliteConnection c = Open();
        if (Scalar(c, "PRAGMA user_version") < 5) return null;
        List<BenchmarkRow> rows = ScoreStore.NewestBenchmarks(c, BenchmarksListed).Select(b => Parse(b).Row).ToList();
        return (rows, (int)Scalar(c, "SELECT COUNT(*) FROM benchmarks"));
    }

    public BenchmarkView? ReadBenchmark(long id)
    {
        using SqliteConnection c = Open();
        if (Scalar(c, "PRAGMA user_version") < 5) return null;
        return ScoreStore.FindBenchmark(c, id) is { } stored ? Parse(stored) : null;
    }

    private static BenchmarkView Parse(ScoreStore.StoredBenchmark b)
    {
        List<(string, string)> system = new(), physics = new(), other = new();
        List<string> columns = new();
        List<Dictionary<string, string>> segments = new();
        List<BenchmarkScene> scenes = new();
        string? gpu = null, cpu = null, os = null, screen = null, tier = null, recommended = null;
        double? physicsMedian = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(b.Result);
            foreach (JsonProperty p in document.RootElement.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "system" when p.Value.ValueKind == JsonValueKind.Object:
                        system.AddRange(Pairs(p.Value));
                        gpu = Text(p.Value, "gpu");
                        cpu = Text(p.Value, "cpu");
                        os = Text(p.Value, "os");
                        screen = Screen(p.Value);
                        tier = Tier(p.Value);
                        break;
                    case "segments" when p.Value.ValueKind == JsonValueKind.Array:
                        foreach (JsonElement segment in p.Value.EnumerateArray())
                        {
                            if (segment.ValueKind != JsonValueKind.Object) continue;
                            Dictionary<string, string> row = new();
                            foreach ((string key, string value) in Pairs(segment))
                            {
                                if (!columns.Contains(key)) columns.Add(key);
                                row[key] = value;
                            }
                            segments.Add(row);
                            if (Text(segment, "scene") is { } scene) scenes.Add(new BenchmarkScene(scene, Number(segment, "medianMs")));
                        }
                        break;
                    case "physics" when p.Value.ValueKind == JsonValueKind.Object:
                        physics.AddRange(Pairs(p.Value));
                        physicsMedian = Number(p.Value, "medianMs");
                        break;
                    default:
                        if (p.Name == "recommended" && p.Value.ValueKind == JsonValueKind.String) recommended = p.Value.GetString();
                        other.Add((p.Name, Value(p.Value)));
                        break;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            other.Clear();
            other.Add(("(unreadable)", b.Result));
        }

        BenchmarkRow rowOf = new(b.Id, b.ReceivedAt, b.GameVersion, gpu, cpu, os, screen, tier, recommended, scenes, physicsMedian);
        return new BenchmarkView(rowOf, system, columns,
            segments.Select(s => (IReadOnlyList<string>)columns.Select(k => s.GetValueOrDefault(k, "")).ToList()).ToList(), physics, other);
    }

    private static IEnumerable<(string, string)> Pairs(JsonElement element) =>
        element.EnumerateObject().Select(p => (p.Name, Value(p.Value)));

    private static string Value(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) ? n : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>"3840x1600, 75 Hz, full screen, scale 1.25": the back buffer and how the game was shown.</summary>
    private static string? Screen(JsonElement system)
    {
        if (Text(system, "backBuffer") is not { } buffer) return null;
        List<string> parts = [buffer];
        if (Number(system, "refreshHz") is double hz) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{hz:0} Hz"));
        if (Flag(system, "fullscreen") is bool full) parts.Add(full ? "full screen" : "window");
        if (Number(system, "scale") is double scale && Math.Abs(scale - 1) > 0.001) parts.Add(string.Create(CultureInfo.InvariantCulture, $"scale {scale:0.##}"));
        return string.Join(", ", parts);
    }

    /// <summary>"High ×2", "Low (Potato)": the tier the run measured, its supersampling, and the Raspberry Pi's renderer.</summary>
    private static string? Tier(JsonElement system)
    {
        if (Text(system, "quality") is not { } quality) return null;
        string tier = quality;
        if (Number(system, "supersample") is double ss && ss > 1) tier += string.Create(CultureInfo.InvariantCulture, $" ×{ss:0}");
        if (Flag(system, "potato") == true) tier += " (Potato)";
        return tier;
    }
}
