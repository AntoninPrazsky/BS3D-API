using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BS3D.Api;

/// <summary>
/// The notes (#10, BS3D#813): what players wrote about what they saw, with the game's context and the picture they let
/// go with it. <b>Outside the append-only log</b> of the scores: a note can be deleted (<c>admin delete-note</c>), and a
/// note goes with the player who sent it when they remove themselves, because the removal is of everything they sent
/// (BS3D#548) — a note linked to them, and a note sent before the service knew them that carried their id and token.
/// <para>
/// <b>A note's picture is a file, not a row</b>: <c>note-pictures/&lt;id&gt;.jpg</c> beside the database
/// (<see cref="PicturesDirectoryFor"/>). In the database, the nightly backup would copy every picture every night and
/// keep thirty copies on the box and a year's on the card, and a sum of the pictures' sizes would read every one of them
/// (SQLite walks a BLOB's pages to reach a column after it), inside the write lock, for each note. As files they are
/// held to their cap exactly, are not in the backups (a note is read within days; the words and the context are), and
/// cost nothing to count.
/// </para>
/// </summary>
public sealed partial class ScoreStore
{
    private const string NotesSchema = """
        CREATE TABLE IF NOT EXISTS notes (
            id INTEGER PRIMARY KEY,
            note_id TEXT NOT NULL UNIQUE,
            received_at TEXT NOT NULL,
            player_id TEXT REFERENCES players(id) ON DELETE CASCADE,
            claimed_player_id TEXT,
            claimed_token_hash TEXT,
            claimed_name TEXT,
            text TEXT NOT NULL,
            game_version TEXT NOT NULL,
            context TEXT NOT NULL,
            ip_hash TEXT NOT NULL,
            picture_bytes INTEGER NOT NULL DEFAULT 0,
            picture_width INTEGER,
            picture_height INTEGER,
            answer TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_notes_received ON notes(received_at);
        CREATE INDEX IF NOT EXISTS ix_notes_claimed ON notes(claimed_player_id);
        """;

    /// <summary>The folder a database's note pictures are kept in: <c>note-pictures</c> beside it.</summary>
    public static string PicturesDirectoryFor(string database) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database))!, "note-pictures");

    /// <summary>This store's note pictures' folder.</summary>
    public string PicturesDirectory => PicturesDirectoryFor(path);

    /// <summary>Where note <paramref name="id"/>'s picture is, whether or not it has one.</summary>
    public string PicturePath(long id) => Path.Combine(PicturesDirectory, id.ToString(CultureInfo.InvariantCulture) + ".jpg");

    /// <param name="PlayerId">The player the note is linked to, only when its token matched one that exists.</param>
    /// <param name="ClaimedPlayerId">An id the service does not know yet, with <paramref name="ClaimedTokenHash"/>: what
    /// lets that player's removal take the note once they exist.</param>
    /// <param name="ClaimedName">The name the note carried, kept apart from the player's: unlinked, it is only a claim.</param>
    public sealed record NewNote(
        Guid NoteId, DateTimeOffset ReceivedAt, Guid? PlayerId, Guid? ClaimedPlayerId, string? ClaimedTokenHash, string? ClaimedName,
        string Text, string GameVersion, string Context, string IpHash, byte[]? Picture, int PictureWidth, int PictureHeight, string Answer);

    /// <summary>A stored note without its picture, as the admin CLI lists it.</summary>
    public sealed record StoredNote(
        long Id, Guid NoteId, DateTimeOffset ReceivedAt, Guid? PlayerId, string? PlayerName, string? ClaimedName,
        string Text, string GameVersion, string Context, int PictureBytes);

    /// <summary>The answer a note id was first given, or null when it is new.</summary>
    public string? FindNoteAnswer(SqliteConnection c, Guid noteId) =>
        Command(c, "SELECT answer FROM notes WHERE note_id = $id", ("$id", noteId.ToString())).ExecuteScalar() as string;

    /// <summary>The notes received since <paramref name="since"/>: the global day's count.</summary>
    public int NotesSince(SqliteConnection c, DateTimeOffset since) =>
        Convert.ToInt32(Command(c, "SELECT COUNT(*) FROM notes WHERE received_at >= $since", ("$since", Stamp(since))).ExecuteScalar());

    /// <summary>The notes stored, against <see cref="ScoresOptions.NotesMaxStored"/>.</summary>
    public int NotesStored(SqliteConnection c) => Convert.ToInt32(Command(c, "SELECT COUNT(*) FROM notes").ExecuteScalar());

    /// <summary>The pictures' bytes stored together, against <see cref="ScoresOptions.NotesMaxStoredPictureBytes"/>.</summary>
    public long StoredPictureBytes(SqliteConnection c) =>
        Convert.ToInt64(Command(c, "SELECT COALESCE(SUM(picture_bytes), 0) FROM notes").ExecuteScalar());

    /// <summary>
    /// Inserts a note and writes its picture, inside the caller's transaction: the picture is written whole under a
    /// temporary name and renamed, and when anything fails the caller's rollback is the row's and this deletes the file.
    /// Returns the note's id.
    /// </summary>
    public long InsertNote(SqliteConnection c, NewNote n)
    {
        Command(c, """
            INSERT INTO notes (note_id, received_at, player_id, claimed_player_id, claimed_token_hash, claimed_name, text,
                game_version, context, ip_hash, picture_bytes, picture_width, picture_height, answer)
            VALUES ($id, $at, $p, $cp, $ct, $name, $text, $ver, $ctx, $ip, $bytes, $w, $h, $answer)
            """,
            ("$id", n.NoteId.ToString()), ("$at", Stamp(n.ReceivedAt)), ("$p", (object?)n.PlayerId?.ToString() ?? DBNull.Value),
            ("$cp", (object?)n.ClaimedPlayerId?.ToString() ?? DBNull.Value), ("$ct", (object?)n.ClaimedTokenHash ?? DBNull.Value),
            ("$name", (object?)n.ClaimedName ?? DBNull.Value), ("$text", n.Text), ("$ver", n.GameVersion), ("$ctx", n.Context),
            ("$ip", n.IpHash), ("$bytes", n.Picture?.Length ?? 0),
            ("$w", n.Picture != null ? n.PictureWidth : DBNull.Value), ("$h", n.Picture != null ? n.PictureHeight : DBNull.Value),
            ("$answer", n.Answer)).ExecuteNonQuery();
        long id = (long)Command(c, "SELECT last_insert_rowid()").ExecuteScalar()!;

        if (n.Picture != null)
        {
            string file = PicturePath(id);
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(PicturesDirectory);
                File.WriteAllBytes(temp, n.Picture);
                File.Move(temp, file, overwrite: true);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }
        }
        return id;
    }

    /// <summary>The notes after <paramref name="afterId"/>, oldest first, without their pictures and never their address hash.</summary>
    public List<StoredNote> Notes(SqliteConnection c, long afterId)
    {
        using SqliteCommand cmd = Command(c, """
            SELECT n.id, n.note_id, n.received_at, n.player_id, p.name, n.claimed_name, n.text, n.game_version, n.context, n.picture_bytes
            FROM notes n LEFT JOIN players p ON p.id = n.player_id WHERE n.id > $after ORDER BY n.id
            """, ("$after", afterId));
        using SqliteDataReader r = cmd.ExecuteReader();
        List<StoredNote> notes = new();
        while (r.Read())
            notes.Add(new StoredNote(r.GetInt64(0), Guid.Parse(r.GetString(1)), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt32(9)));
        return notes;
    }

    /// <summary>A note's picture, or null when it has none.</summary>
    public byte[]? NotePicture(long id) => File.Exists(PicturePath(id)) ? File.ReadAllBytes(PicturePath(id)) : null;

    /// <summary>Deletes one note and its picture. Returns whether there was one.</summary>
    public bool DeleteNote(SqliteConnection c, long id)
    {
        bool deleted = Command(c, "DELETE FROM notes WHERE id = $id", ("$id", id)).ExecuteNonQuery() > 0;
        DeletePicture(id);
        return deleted;
    }

    /// <summary>
    /// The ids of every note that goes with a player's removal: linked to them, or sent before the service knew them
    /// with their id and the token they now hold. Read before the player is deleted, which cascades to the linked ones.
    /// </summary>
    private List<long> NotesOfPlayer(SqliteConnection c, Guid player)
    {
        using SqliteCommand cmd = Command(c, """
            SELECT n.id FROM notes n
            WHERE n.player_id = $id
               OR (n.claimed_player_id = $id AND n.claimed_token_hash = (SELECT token_hash FROM players WHERE id = $id))
            """, ("$id", player.ToString()));
        using SqliteDataReader r = cmd.ExecuteReader();
        List<long> ids = new();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    private void DeletePicture(long id)
    {
        try { File.Delete(PicturePath(id)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
