using Microsoft.Data.Sqlite;

namespace BS3D.Api;

/// <summary>
/// The notes (#10, BS3D#813): what players wrote about what they saw, with the game's context and the picture they
/// let go with it. <b>Outside the append-only log</b> of the scores: a note can be deleted (<c>admin delete-note</c>), and
/// a note linked to a player goes with that player when they remove themselves, because the removal is of everything
/// they sent (BS3D#548). An unlinked note has no player to go with.
/// </summary>
public sealed partial class ScoreStore
{
    private const string NotesSchema = """
        CREATE TABLE IF NOT EXISTS notes (
            id INTEGER PRIMARY KEY,
            note_id TEXT NOT NULL UNIQUE,
            received_at TEXT NOT NULL,
            player_id TEXT REFERENCES players(id) ON DELETE CASCADE,
            claimed_name TEXT,
            text TEXT NOT NULL,
            game_version TEXT NOT NULL,
            context TEXT NOT NULL,
            ip_hash TEXT NOT NULL,
            picture BLOB,
            picture_bytes INTEGER NOT NULL DEFAULT 0,
            picture_width INTEGER,
            picture_height INTEGER,
            answer TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_notes_received ON notes(received_at);
        """;

    /// <param name="PlayerId">The player the note is linked to, only when its token matched one that exists.</param>
    /// <param name="ClaimedName">The name the note carried, kept apart from the player's: unlinked, it is only a claim.</param>
    public sealed record NewNote(
        Guid NoteId, DateTimeOffset ReceivedAt, Guid? PlayerId, string? ClaimedName, string Text, string GameVersion,
        string Context, string IpHash, byte[]? Picture, int PictureWidth, int PictureHeight, string Answer);

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

    /// <summary>The pictures' bytes stored together, against <see cref="ScoresOptions.NotesMaxStoredPictureBytes"/>.</summary>
    public long StoredPictureBytes(SqliteConnection c) =>
        Convert.ToInt64(Command(c, "SELECT COALESCE(SUM(picture_bytes), 0) FROM notes").ExecuteScalar());

    public void InsertNote(SqliteConnection c, NewNote n) =>
        Command(c, """
            INSERT INTO notes (note_id, received_at, player_id, claimed_name, text, game_version, context, ip_hash,
                picture, picture_bytes, picture_width, picture_height, answer)
            VALUES ($id, $at, $p, $name, $text, $ver, $ctx, $ip, $pic, $bytes, $w, $h, $answer)
            """,
            ("$id", n.NoteId.ToString()), ("$at", Stamp(n.ReceivedAt)), ("$p", (object?)n.PlayerId?.ToString() ?? DBNull.Value),
            ("$name", (object?)n.ClaimedName ?? DBNull.Value), ("$text", n.Text), ("$ver", n.GameVersion), ("$ctx", n.Context),
            ("$ip", n.IpHash), ("$pic", (object?)n.Picture ?? DBNull.Value), ("$bytes", n.Picture?.Length ?? 0),
            ("$w", n.Picture != null ? n.PictureWidth : DBNull.Value), ("$h", n.Picture != null ? n.PictureHeight : DBNull.Value),
            ("$answer", n.Answer)).ExecuteNonQuery();

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
            notes.Add(new StoredNote(r.GetInt64(0), Guid.Parse(r.GetString(1)), DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt32(9)));
        return notes;
    }

    /// <summary>A note's picture, or null when it has none or there is no such note.</summary>
    public byte[]? NotePicture(SqliteConnection c, long id) =>
        Command(c, "SELECT picture FROM notes WHERE id = $id", ("$id", id)).ExecuteScalar() as byte[];

    /// <summary>Deletes one note and its picture. Returns whether there was one.</summary>
    public bool DeleteNote(SqliteConnection c, long id) =>
        Command(c, "DELETE FROM notes WHERE id = $id", ("$id", id)).ExecuteNonQuery() > 0;
}
