using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BS3D.Api;

/// <summary>
/// The service's administration, as a command line and never as an endpoint (issue #1): no HTTP surface exists that
/// can hide, rename or export anything. Run on the box, against the same database the service uses —
/// <c>BS3D.Api admin hide-player &lt;id&gt;</c>, <c>admin show-player &lt;id&gt;</c>, <c>admin rename &lt;id&gt; &lt;name&gt;</c>,
/// <c>admin export</c> (one JSON line per submission, oldest first).
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

            case ["export"]:
                foreach (Dictionary<string, object?> row in store.Export(c))
                    output.WriteLine(JsonSerializer.Serialize(row));
                return 0;

            default:
                output.WriteLine("usage: admin hide-player <id> | show-player <id> | rename <id> <name> | export");
                return 2;
        }
    }

    private static int Report(TextWriter output, int changed, string what)
    {
        output.WriteLine(changed > 0 ? what : "no such player");
        return changed > 0 ? 0 : 1;
    }
}
