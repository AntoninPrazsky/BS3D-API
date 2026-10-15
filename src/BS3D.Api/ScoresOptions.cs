namespace BS3D.Api;

/// <summary>
/// The service's configuration, section <c>Scores</c> of <c>appsettings.json</c>, overridden by environment variables
/// (<c>Scores__Database</c>, …) — which is how the systemd unit on the Pi sets them (issue #3). Nothing secret has a
/// value in the repository: the salt comes from the box.
/// </summary>
public sealed class ScoresOptions
{
    public const string Section = "Scores";

    /// <summary>The SQLite file — the service's whole state.</summary>
    public string Database { get; set; } = "scores.db";

    /// <summary>
    /// A folder of ceiling tables, <c>BS3D-&lt;version&gt;-ceilings.json</c> as every game release carries them
    /// (BS3D#549). Every table in it is loaded — the union — because old game versions stay in the wild and their
    /// boards stay valid.
    /// </summary>
    public string CeilingsDirectory { get; set; } = "ceilings";

    /// <summary>
    /// Whether a submission must name a board a ceiling table knows. True everywhere but a developer's machine:
    /// a level the game was pointed at with <c>levelfile=</c> is on no table, and a local run of the API still has
    /// to take it to be tested against the game.
    /// </summary>
    public bool RequireKnownBoard { get; set; } = true;

    /// <summary>
    /// Salts the hash of a client's address, so the audit column is not a register of addresses (issue #2). Must be
    /// set outside Development; the service refuses to start without one.
    /// </summary>
    public string AddressSalt { get; set; } = string.Empty;

    /// <summary>Submissions one address may make in a minute before a 429.</summary>
    public int SubmissionsPerMinutePerAddress { get; set; } = 30;

    /// <summary>Submissions one player may make in a minute. A level takes minutes to clear, not seconds.</summary>
    public int SubmissionsPerMinutePerPlayer { get; set; } = 10;

    /// <summary>
    /// The fewest seconds one shot can take, which with a level's fewest shots gives the floor a duration may not go
    /// under. The game imposes no cadence, so this is a judgment and not a proof: a quarter of a second is below any
    /// human's aim-and-fire and exists to refuse a forged duration of zero.
    /// </summary>
    public double MinSecondsPerShot { get; set; } = 0.25;

    /// <summary>Nicknames refused outright, compared case-insensitively against the normalized name.</summary>
    public List<string> DeniedNames { get; set; } = new() { "admin", "administrator", "moderator", "system", "bs3d" };

    /// <summary>
    /// Seconds between two writes of the refusals gathered in memory (issue #5). A flood of bad requests costs one
    /// write per flush, not one per request.
    /// </summary>
    public int RefusalFlushSeconds { get; set; } = 5;

    /// <summary>
    /// Refusals kept in memory for <c>refusal_log</c> between two flushes. Past it they are counted, not kept, and the
    /// flush writes one "dropped" row for them; <c>refusal_days</c> counts every one regardless.
    /// </summary>
    public int RefusalQueueCapacity { get; set; } = 500;

    /// <summary>Days of refusals <c>refusal_log</c> keeps; each flush deletes older rows.</summary>
    public int RefusalLogDays { get; set; } = 7;

    /// <summary>Rows <c>refusal_log</c> keeps at most; each flush deletes the oldest beyond it.</summary>
    public int RefusalLogMaxRows { get; set; } = 10_000;

    // Notes (#10, BS3D#813). Anyone may send one, text and a picture, onto a disk at the owner's home: these are what
    // keeps that from being a free upload service.

    /// <summary>The longest note, in UTF-16 units after NFC and trimming. A few sentences, not an essay.</summary>
    public int NoteMaxLength { get; set; } = 1000;

    /// <summary>The longest context, in UTF-8 bytes as it is stored: the game's twenty fields, about 600 bytes, with room to grow.</summary>
    public int NoteMaxContextBytes { get; set; } = 2048;

    /// <summary>The largest picture, decoded. The game sends a JPEG at most 1280 pixels wide.</summary>
    public int NoteMaxPictureBytes { get; set; } = 400_000;

    /// <summary>The widest and tallest picture taken: a check that the JPEG is a game's frame and not a poster.</summary>
    public int NoteMaxPictureSide { get; set; } = 2048;

    /// <summary>
    /// The largest request <c>POST /v1/notes</c> takes, above the 4 KB every other request is held to: the largest
    /// picture in base64 (4/3 of it) with the text and the context, and a margin.
    /// </summary>
    public long NoteMaxRequestBytes { get; set; } = 600_000;

    /// <summary>Notes one address may send in a minute: a player who wrote two in a row is fine, a script is not.</summary>
    public int NotesPerMinutePerAddress { get; set; } = 3;

    /// <summary>Notes one address may send in 24 hours, a sliding window held in memory (a restart forgets it).</summary>
    public int NotesPerDayPerAddress { get; set; } = 30;

    /// <summary>Notes the service takes in one UTC day from everyone together, counted in the database.</summary>
    public int NotesPerDay { get; set; } = 500;

    /// <summary>
    /// The notes stored at most; past it a note is refused (429, <c>notes-full</c>) and stays in the game's outbox. It
    /// bounds what the notes add to the database, which the nightly backup copies thirty times on the box and a year's
    /// worth on the card: at most this many texts and contexts, about 15 MB, where a real note is a tenth of that.
    /// </summary>
    public int NotesMaxStored { get; set; } = 5000;

    /// <summary>
    /// The pictures stored together, at most, as files in <c>note-pictures</c> beside the database, of which the backup
    /// keeps one copy on the card and none off the site (#12). Past it a note is still kept, without its picture, and its
    /// answer says so: the owner reads the notes and deletes old ones (<c>admin delete-note</c>) to make room. 10 GiB, the
    /// owner's figure for the card and the Pi's disk (2026-10-08, it was 512 MiB): above what <see cref="NotesMaxStored"/>
    /// notes of <see cref="NoteMaxPictureBytes"/> each can fill, about 1.9 GiB, so it is the notes' cap that binds.
    /// </summary>
    public long NotesMaxStoredPictureBytes { get; set; } = 10L << 30;

    // Play counts (BS3D#709): anonymous, so anyone can post them; these bound what one request can add.

    /// <summary>
    /// The largest request <c>POST /v1/stats</c> takes, above the 4 KB every other request is held to: a row is about 250
    /// bytes of JSON, so <see cref="StatsMaxLevels"/> rows and a margin.
    /// </summary>
    public long StatsMaxRequestBytes { get; set; } = 64 * 1024;

    /// <summary>The most boards one batch may carry; the game sends no more (<c>OnlineScores.StatsMaxLevels</c>) and keeps the rest for its next.</summary>
    public int StatsMaxLevels { get; set; } = 200;

    /// <summary>
    /// The most any one count of a row may be in one batch: the game sends a batch every ten minutes of play, and keeps
    /// counting into one while the service is away, so this is weeks of one player on one level, and it bounds what a
    /// forged batch can add to a level's counts.
    /// </summary>
    public int StatsMaxCount { get; set; } = 10_000;

    /// <summary>The most shots a row may carry in one batch: <see cref="StatsMaxCount"/> attempts of a long level's budget.</summary>
    public int StatsMaxShots { get; set; } = 1_000_000;

    /// <summary>The most seconds a row may carry in one batch: a thousand hours.</summary>
    public double StatsMaxSeconds { get; set; } = 3_600_000;

    /// <summary>Batches one address may send in a minute: a player sends one every ten minutes, a household a few.</summary>
    public int StatsPerMinutePerAddress { get; set; } = 10;
}
