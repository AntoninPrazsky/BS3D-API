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

            case ["export"]:
                foreach (Dictionary<string, object?> row in store.Export(c))
                    output.WriteLine(JsonSerializer.Serialize(row));
                return 0;

            default:
                output.WriteLine("usage: admin hide-player <id> | show-player <id> | rename <id> <name> | export | backup <file> | count");
                return 2;
        }
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
