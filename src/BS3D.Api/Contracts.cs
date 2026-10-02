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
    public const string UnknownPlayer = "unknown-player";
    public const string SubmissionOfAnotherPlayer = "submission-of-another-player";
}
