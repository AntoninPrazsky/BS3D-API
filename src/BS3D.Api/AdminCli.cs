using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BS3D.Api;

/// <summary>
/// The service's administration, as a command line and never as an endpoint (issue #1): no HTTP surface exists that
/// can hide, rename or export anything. Run on the box, against the same database the service uses —
/// <c>BS3D.Api admin hide-player &lt;id&gt;</c>, <c>admin show-player &lt;id&gt;</c>, <c>admin rename &lt;id&gt; &lt;name&gt;</c>,
/// <c>admin export</c> (one JSON line per submission, oldest first), <c>admin backup &lt;file&gt;</c> (a consistent copy of
/// the live database, taken while the service runs — SQLite's own backup API, so the Pi needs no <c>sqlite3</c> package,
/// issue #3) and <c>admin count</c> (players and submissions, what an update is checked against: it must lose no row).
/// <para>
/// The notes (#10): <c>admin notes [--after &lt;id&gt;] [--out &lt;folder&gt;]</c> prints one JSON line per note, oldest first
/// and without the address hash, and with <c>--out</c> writes each note's picture there as <c>&lt;id&gt;.jpg</c>, which is how
/// an agent reads them on the box; <c>admin delete-note &lt;id&gt;</c> removes a note and its picture. <b><c>count</c> does not
/// count notes, and must not start to</b>: <c>update.sh</c> compares its line before and after an update as text, and the
/// line before is printed by the release being replaced.
/// </para>
/// </summary>
public static class AdminCli
{
    public static int Run(string[] args, ScoreStore store, ScoresOptions options, TextWriter output)
    {
        store.EnsureSchema();
        using SqliteConnection c = store.Open();

        switch (args)
        {
            case ["hide-player", var id] when Guid.TryParse(id, out Guid player):
                return Report(output, store.SetHidden(c, player, true), $"hidden {player} from every board and count");

            case ["show-player", var id] when Guid.TryParse(id, out Guid player):
                return Report(output, store.SetHidden(c, player, false), $"{player} is on the boards again");

            case ["rename", var id, var raw] when Guid.TryParse(id, out Guid player):
                string? name = Nicknames.Normalize(raw, options.DeniedNames);
                if (name == null)
                {
                    output.WriteLine($"'{raw}' is not a nickname");
                    return 1;
                }
                store.RenamePlayer(c, player, name);
                return Report(output, store.FindPlayer(c, player) != null ? 1 : 0, $"renamed {player} to '{name}'");

            case ["backup", var target]:
                string full = Path.GetFullPath(target);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                if (File.Exists(full)) File.Delete(full);
                using (SqliteConnection copy = new($"Data Source={full};Pooling=False"))
                {
                    copy.Open();
                    c.BackupDatabase(copy);
                }
                output.WriteLine($"backed up to {full}");
                return 0;

            case ["count"]:
                output.WriteLine($"players {Scalar(c, "SELECT COUNT(*) FROM players")} submissions {Scalar(c, "SELECT COUNT(*) FROM submissions")}");
                return 0;

            case ["notes", .. var rest]:
                return Notes(rest, store, c, output);

            case ["delete-note", var id] when long.TryParse(id, out long note):
                bool deleted = store.DeleteNote(c, note);
                output.WriteLine(deleted ? $"deleted note {note} and its picture" : "no such note");
                return deleted ? 0 : 1;

            case ["export"]:
                foreach (Dictionary<string, object?> row in store.Export(c))
                    output.WriteLine(JsonSerializer.Serialize(row));
                return 0;

            default:
                output.WriteLine(Usage);
                return 2;
        }
    }

    private const string Usage = "usage: admin hide-player <id> | show-player <id> | rename <id> <name> | export | backup <file> | count"
        + " | notes [--after <id>] [--out <folder>] | delete-note <id>";

    // A note's text in a terminal: the letters as letters (the relaxed encoder), every control character escaped (JSON
    // always does), and every format and separator character escaped too (Terminal): a C0 or C1 control is a terminal
    // escape, and a right-to-left override reorders the line around it
    private static readonly JsonSerializerOptions NoteJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// A JSON line with every format, line-separator and paragraph-separator character written as its escape. They occur
    /// only inside the line's strings, where the escape means the same character, so the line is the same JSON; the
    /// relaxed encoder lets them through, and in a terminal they act.
    /// </summary>
    private static string Terminal(string json)
    {
        System.Text.StringBuilder line = new(json.Length);
        foreach (char c in json)
        {
            if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
                line.Append(@"\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            else line.Append(c);
        }
        return line.ToString();
    }

    /// <summary>A note's context as JSON, or as its text when it does not parse: one bad note must not stop the listing.</summary>
    private static object ContextOf(string context)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(context);
            JsonElement root = document.RootElement.Clone();
            _ = JsonSerializer.Serialize(root, NoteJson);
            return root;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return context;
        }
    }

    private static int Notes(string[] args, ScoreStore store, SqliteConnection c, TextWriter output)
    {
        long after = 0;
        string? folder = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--after" when i + 1 < args.Length && long.TryParse(args[i + 1], out after):
                    i++;
                    break;
                case "--out" when i + 1 < args.Length:
                    folder = Path.GetFullPath(args[++i]);
                    break;
                default:
                    output.WriteLine(Usage);
                    return 2;
            }
        }

        if (folder != null) Directory.CreateDirectory(folder);
        foreach (ScoreStore.StoredNote n in store.Notes(c, after))
        {
            string? file = null;
            if (folder != null && n.PictureBytes > 0 && store.NotePicture(n.Id) is { } picture)
            {
                file = Path.Combine(folder, $"{n.Id}.jpg");
                File.WriteAllBytes(file, picture);
            }

            output.WriteLine(Terminal(JsonSerializer.Serialize(new
            {
                n.Id,
                n.NoteId,
                n.ReceivedAt,
                n.PlayerId,
                n.PlayerName,
                n.ClaimedName,
                n.GameVersion,
                n.Text,
                Context = ContextOf(n.Context),
                n.PictureBytes,
                Picture = file,
            }, NoteJson)));
        }
        return 0;
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static int Report(TextWriter output, int changed, string what)
    {
        output.WriteLine(changed > 0 ? what : "no such player");
        return changed > 0 ? 0 : 1;
    }
}
