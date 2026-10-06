using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BS3D.Api.AdminWeb;

/// <summary>The notes (#10): what the Notes tab lists and a note's page shows. Like every read here, never the address hash.</summary>
public sealed partial class AdminData
{
    /// <summary>
    /// One note in the list. <see cref="Name"/> is the linked player's current nickname when <see cref="Linked"/>, else
    /// the name the note claimed, else null (anonymous).
    /// </summary>
    public sealed record NoteRow(long Id, DateTimeOffset At, string? Name, bool Linked, Guid? Player, string GameVersion,
        string? Where, string? Level, string Text, int PictureBytes);

    /// <summary>A note's page: the row, and its context as the game sent it, field by field.</summary>
    public sealed record NoteView(NoteRow Note, IReadOnlyList<(string Key, string Value)> Context, int PictureWidth, int PictureHeight);

    /// <summary>The notes the list shows at most, newest first.</summary>
    public const int NotesListed = 200;

    public (IReadOnlyList<NoteRow> Notes, int Total) ReadNotes()
    {
        using SqliteConnection c = Open();
        // A database the service has not brought to schema 3 has no notes table; the overview says why
        if (Scalar(c, "PRAGMA user_version") < 3) return ([], 0);
        List<NoteRow> notes = new();
        using (SqliteCommand cmd = Command(c, $"{NoteSelect} ORDER BY n.id DESC LIMIT $n", "$n", NotesListed))
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read()) notes.Add(ReadNoteRow(r).Row);
        return (notes, (int)Scalar(c, "SELECT COUNT(*) FROM notes"));
    }

    public NoteView? ReadNote(long id)
    {
        using SqliteConnection c = Open();
        if (Scalar(c, "PRAGMA user_version") < 3) return null;
        using SqliteCommand cmd = Command(c, $"{NoteSelect} WHERE n.id = $id", "$id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        (NoteRow row, string context) = ReadNoteRow(r);
        return new NoteView(row, Fields(context), r.IsDBNull(11) ? 0 : r.GetInt32(11), r.IsDBNull(12) ? 0 : r.GetInt32(12));
    }

    /// <summary>A context's fields, or the whole of it as one when it does not read: a bad note must not be a 500.</summary>
    private static List<(string, string)> Fields(string context)
    {
        List<(string, string)> fields = new();
        try
        {
            using JsonDocument document = JsonDocument.Parse(context);
            foreach (JsonProperty p in document.RootElement.EnumerateObject())
                fields.Add((p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            fields.Clear();
            fields.Add(("(unreadable)", context));
        }
        return fields;
    }

    /// <summary>A note's JPEG, from <c>note-pictures</c> beside the database, or null.</summary>
    public byte[]? ReadNotePicture(long id)
    {
        string file = Path.Combine(ScoreStore.PicturesDirectoryFor(options.Database), id.ToString(CultureInfo.InvariantCulture) + ".jpg");
        return File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    // The columns ReadNoteRow reads, in its order; ip_hash is not one of them
    private const string NoteSelect = """
        SELECT n.id, n.received_at, n.player_id, p.name, n.claimed_name, n.game_version, n.text, n.picture_bytes, n.context,
               json_extract(n.context, '$.where'), json_extract(n.context, '$.level'), n.picture_width, n.picture_height
        FROM notes n LEFT JOIN players p ON p.id = n.player_id
        """;

    private static (NoteRow Row, string Context) ReadNoteRow(SqliteDataReader r)
    {
        bool linked = !r.IsDBNull(2) && !r.IsDBNull(3);
        return (new NoteRow(
            r.GetInt64(0), Time(r.GetString(1)),
            linked ? r.GetString(3) : r.IsDBNull(4) ? null : r.GetString(4),
            linked, linked ? Guid.Parse(r.GetString(2)) : null,
            r.GetString(5),
            r.IsDBNull(9) ? null : Convert.ToString(r.GetValue(9), CultureInfo.InvariantCulture),
            r.IsDBNull(10) ? null : Convert.ToString(r.GetValue(10), CultureInfo.InvariantCulture),
            r.GetString(6), r.GetInt32(7)), r.GetString(8));
    }
}
