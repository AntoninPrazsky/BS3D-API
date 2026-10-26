namespace BS3D.Api;

// Contract v1 (BS3D#542), as the wire carries it. The game's own copy is BS3D's Game/Online/ScoreSubmission.cs;
// change the contract there and here in step. JSON is camelCase through the web defaults.

/// <summary>
/// The body of <c>POST /v1/scores</c>: one played level. <c>Stars</c> 1 to 4 is a clear; 0 is an attempt that did not
/// finish the level (#8, BS3D#716), sent with its final score, additive to contract v1: an old game never sends 0.
/// </summary>
public sealed record SubmissionRequest(
    Guid SubmissionId,
    Guid PlayerId,
    string? Name,
    LevelKey? Level,
    int RulesVersion,
    int Score,
    int Stars,
    int ShotsUsed,
    double DurationSeconds,
    string? GameVersion);

/// <summary>Which board: the level set entry's file and its <c>LevelIdentity</c> hash.</summary>
public sealed record LevelKey(string? File, string? Hash);

/// <summary>
/// What a submission is answered with — its rank on both boards and whether it is the player's best, and the
/// nickname in the form the service keeps, which the game writes back. <c>PersonalBest</c> means the row the player
/// shows on the all-time board just got better: a first clear or a better one, or, for a player who has never cleared
/// the board, a better unfinished attempt (#8).
/// </summary>
public sealed record SubmissionAnswer(bool Accepted, bool PersonalBest, BoardRank Month, BoardRank AllTime, string Name);

/// <summary>
/// A place on a board. Rank 0 is "not on it": a hidden player, or an unfinished attempt by a player who has cleared
/// the board, on the board of a month they have no clear in (#8).
/// </summary>
public sealed record BoardRank(int Rank, int Total);

/// <summary>Every refusal's body: a reason code a client can switch on and a log can be grepped for.</summary>
public sealed record Refusal(string Reason);

/// <summary>The body of <c>PUT /v1/players/{id}</c>, and its answer.</summary>
public sealed record NameBody(string? Name);

/// <summary>One page of a board, and the asking player's own row when asked for.</summary>
public sealed record BoardPage(string Period, string? Month, int Total, IReadOnlyList<BoardEntry> Entries, BoardMe? Me);

/// <summary>One row of a board. <c>Stars</c> 0 is an unfinished attempt, ranked below every clear (#8).</summary>
public sealed record BoardEntry(int Rank, string Name, int Score, int Stars, DateTimeOffset At);

public sealed record BoardMe(int Rank, int Score, int Stars);

/// <summary>
/// The answer of <c>GET /v1/boards</c> (#7, BS3D#685): every board with at least one visible row, each with its #1 (a
/// clear, or an unfinished attempt while no one shown has cleared it) and the asking player's own place, for the game's
/// High Scores screen - one request where the per-board GET would take one per level. Additive to contract v1; nothing
/// above it changed.
/// </summary>
public sealed record BoardsSummary(string Period, string? Month, IReadOnlyList<BoardSummary> Boards);

/// <summary>One board in <see cref="BoardsSummary"/>. <see cref="Me"/> is null without a player or off the board.</summary>
public sealed record BoardSummary(string File, string Hash, int Rules, int Total, BoardTop Top, BoardMe? Me);

/// <summary>A board's #1.</summary>
public sealed record BoardTop(string Name, int Score, int Stars);

/// <summary>
/// The body of <c>POST /v1/notes</c> (#10, BS3D#813): a line a player typed about what they were looking at, with the
/// game's context and, unless the player unticked it, a JPEG of that frame. Additive to contract v1. Anyone may send one:
/// <see cref="PlayerId"/> with the bearer token links it to an existing player, and <see cref="Name"/> without a link is
/// only what the note says it is.
/// </summary>
/// <param name="Context">A JSON object the game fills (the level, the scene, the quality, ...), stored as it came.</param>
/// <param name="Screenshot">The JPEG as base64, or null.</param>
public sealed record NoteRequest(
    Guid NoteId,
    string? Text,
    string? GameVersion,
    System.Text.Json.JsonElement? Context,
    string? Screenshot,
    Guid? PlayerId,
    string? Name);

/// <summary>
/// What a note is answered with. <c>ScreenshotStored</c> is false when none was sent, and when the pictures stored have
/// reached their cap: the note is kept without its picture rather than refused.
/// </summary>
public sealed record NoteAnswer(Guid NoteId, bool ScreenshotStored);

/// <summary>
/// The body of <c>POST /v1/stats</c> (BS3D#709): a batch of play counts, one row per board, from a game whose
/// player has online scores on. Additive to contract v1. <b>Nothing in it names a player</b>: no player id, no token, no
/// nickname, no version and no clock; the game sends it without an <c>Authorization</c> header, under the User-Agent
/// <c>BS3D</c> with no version, on a connection of its own. <see cref="BatchId"/> is random per batch and only lets a
/// resend count once. The game's copy is BS3D's <c>StatsBatchBody</c>.
/// </summary>
public sealed record StatsBatch(Guid BatchId, IReadOnlyList<StatsLevel?>? Levels);

/// <summary>
/// One board's counts in a <see cref="StatsBatch"/>, the change since the game's last batch: attempts started, the starts
/// that retried a level the last attempt had not cleared, clears, losses by cause (out of balls, the cluster at the line),
/// attempts left before their result page, skips, clears of a level the player had never cleared, and the shots and
/// seconds over every attempt. BS3D's <c>LevelPlayCounts</c>, field for field but its <c>startsToFirstClear</c>, which the
/// game keeps on the player's machine: it counts starts from before the player turned online scores on.
/// </summary>
public sealed record StatsLevel(
    string? File,
    string? Hash,
    int Rules,
    int Starts,
    int Retries,
    int Clears,
    int LostOutOfBalls,
    int LostLine,
    int Abandons,
    int Skips,
    int FirstClears,
    int Shots,
    double Seconds);

/// <summary>
/// What a batch is answered with: accepted, how many of its rows were counted and how many were on no board a ceiling table
/// names (left out, not refused), and whether the batch id had been counted before (then nothing was added).
/// </summary>
public sealed record StatsAnswer(bool Accepted, int Counted, int Ignored, bool Duplicate);

/// <summary>
/// The body of <c>POST /v1/benchmarks</c> (BS3D#988, additive to contract v1): one finished run of the game's benchmark,
/// Extras &gt; Benchmark. <see cref="Result"/> is the object the game saves as <c>Benchmark.json</c>: the machine it ran on
/// (<c>system</c>: build, platform, OS, CPU, memory, GPU, driver, back buffer, refresh, quality tier and the like), each
/// scene's frame times (<c>segments</c>) and the physics' step times (<c>physics</c>). <b>Nothing in it names a player</b>:
/// no player id, no token, no nickname; the game sends it without an <c>Authorization</c> header. <see cref="BenchmarkId"/>
/// is random per result and only lets a resend be stored once. The game's copy is BS3D's <c>OnlineBenchmarks</c>.
/// </summary>
public sealed record BenchmarkRequest(Guid BenchmarkId, string? GameVersion, System.Text.Json.JsonElement? Result);

/// <summary>What a result is answered with: its id, and whether that id had been stored before (then nothing was added).</summary>
public sealed record BenchmarkAnswer(Guid BenchmarkId, bool Duplicate);

/// <summary>The answer of <c>GET /v1/health</c>.</summary>
public sealed record HealthAnswer(string Status, int Contract, int Schema, int Boards);

/// <summary>The reason codes, in one place so the tests and the log say the same words.</summary>
public static class Reasons
{
    public const string BadRequest = "bad-request";
    public const string NoToken = "no-token";
    public const string WrongToken = "wrong-token";
    public const string BadName = "bad-name";
    public const string BadVersion = "bad-version";
    public const string UnknownBoard = "unknown-board";
    public const string OverCeiling = "over-ceiling";
    public const string BadStars = "bad-stars";
    public const string BadShots = "bad-shots";
    public const string BadDuration = "bad-duration";
    public const string RateLimited = "rate-limited";
    public const string PlayTime = "play-time";
    public const string UnknownPlayer = "unknown-player";
    public const string SubmissionOfAnotherPlayer = "submission-of-another-player";
    public const string EmptyNote = "empty-note";
    public const string NoteTooLong = "note-too-long";
    public const string BadText = "bad-text";
    public const string BadContext = "bad-context";
    public const string ContextTooLarge = "context-too-large";
    public const string BadPicture = "bad-picture";
    public const string PictureTooLarge = "picture-too-large";
    public const string NotesFull = "notes-full";
    public const string BadCounts = "bad-counts";
    public const string TooManyLevels = "too-many-levels";
    public const string BadResult = "bad-result";
    public const string ResultTooLarge = "result-too-large";
    public const string BenchmarksFull = "benchmarks-full";
}
