using System.Text.Json;
using System.Text.Json.Serialization;

namespace BS3D.Api;

/// <summary>
/// The boards the service knows, from the ceiling tables every game release carries (BS3D#549, written by its
/// <c>Tools/ScoreSim --ceilings</c>): each budgeted level's file, its <c>LevelIdentity</c> hash and the rules version,
/// with the score no clear can reach and the fewest shots any clear takes. A submission for a board no table names
/// does not exist to the service — an invented level with ten thousand balls cannot be ranked (issue #2).
/// </summary>
public sealed class Ceilings
{
    private readonly Dictionary<(string File, string Hash, int Rules), CeilingRow> _boards = new();
    private readonly Dictionary<string, (int Position, string? Block)> _levels = new();

    public int Count => _boards.Count;

    /// <summary>Every board known, for the admin page's list (issue #5).</summary>
    public IReadOnlyCollection<CeilingRow> All => _boards.Values;

    /// <summary>
    /// Where each level's file stands in play order, and its chapter (a level set's <c>block</c>): from the last table
    /// read that names the file, one that names a chapter before one that does not. A level's every version shares it,
    /// and the admin page lists the boards by it (issue #5). Nothing checks or ranks by it.
    /// </summary>
    public IReadOnlyDictionary<string, (int Position, string? Block)> Levels => _levels;

    public bool TryGet(string file, string hash, int rules, out CeilingRow row) =>
        _boards.TryGetValue((file, hash, rules), out row!);

    /// <summary>Every table in <paramref name="directory"/>; a file that is not one is logged and skipped.</summary>
    public static Ceilings Load(string directory, ILogger logger)
    {
        Ceilings ceilings = new();

        if (!Directory.Exists(directory))
        {
            logger.LogWarning("No ceilings directory at {Directory}: no board is known", Path.GetFullPath(directory));
            return ceilings;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            try
            {
                CeilingTable? table = JsonSerializer.Deserialize<CeilingTable>(File.ReadAllText(path));
                if (table?.Format != CeilingTable.FormatMarker || table.Levels == null)
                {
                    logger.LogWarning("{Path} is not a ceiling table; skipped", path);
                    continue;
                }

                // ScoreSim writes the rows in play order
                for (int i = 0; i < table.Levels.Count; i++)
                {
                    ceilings.Add(table.Levels[i]);
                    ceilings.Place(table.Levels[i], i);
                }

                logger.LogInformation("Ceilings: {Count} board(s) from {Path}", table.Levels.Count, Path.GetFileName(path));
            }
            catch (JsonException e)
            {
                logger.LogWarning("{Path} would not read as a ceiling table ({Message}); skipped", path, e.Message);
            }
        }

        return ceilings;
    }

    /// <summary>
    /// One board. A board two tables both name keeps the larger ceiling, which is only ever the safe direction: a
    /// bound is allowed to be loose upward, never downward.
    /// </summary>
    public void Add(CeilingRow row)
    {
        var key = (row.File, row.Hash, row.RulesVersion);

        if (!_boards.TryGetValue(key, out CeilingRow? known) || row.Ceiling > known.Ceiling)
            _boards[key] = row;
    }

    private void Place(CeilingRow row, int position)
    {
        if (row.Block != null || !_levels.TryGetValue(row.File, out var placed) || placed.Block == null)
            _levels[row.File] = (position, row.Block);
    }

    private sealed record CeilingTable(
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("levels")] List<CeilingRow>? Levels)
    {
        public const string FormatMarker = "bs3d-ceilings";
    }
}

/// <summary>One level of a ceiling table, as ScoreSim writes it, with its chapter in a table that names one (<see cref="Ceilings.Levels"/>).</summary>
public sealed record CeilingRow(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("rulesVersion")] int RulesVersion,
    [property: JsonPropertyName("shots")] int Shots,
    [property: JsonPropertyName("ceiling")] int Ceiling,
    [property: JsonPropertyName("minShots")] int MinShots,
    [property: JsonPropertyName("block")] string? Block = null);
