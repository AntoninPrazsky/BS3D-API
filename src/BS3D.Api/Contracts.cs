namespace BS3D.Api;

// Contract v1 (BS3D#542), as the wire carries it. The game's own copy is BS3D's Game/Online/ScoreSubmission.cs;
// change the contract there and here in step. JSON is camelCase through the web defaults.

/// <summary>The body of <c>POST /v1/scores</c>: one cleared level.</summary>
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
/// nickname in the form the service keeps, which the game writes back.
/// </summary>
public sealed record SubmissionAnswer(bool Accepted, bool PersonalBest, BoardRank Month, BoardRank AllTime, string Name);

/// <summary>A place on a board. Rank 0 is "not on it" — a hidden player's.</summary>
public sealed record BoardRank(int Rank, int Total);

/// <summary>Every refusal's body: a reason code a client can switch on and a log can be grepped for.</summary>
public sealed record Refusal(string Reason);

/// <summary>The body of <c>PUT /v1/players/{id}</c>, and its answer.</summary>
public sealed record NameBody(string? Name);

/// <summary>One page of a board, and the asking player's own row when asked for.</summary>
public sealed record BoardPage(string Period, string? Month, int Total, IReadOnlyList<BoardEntry> Entries, BoardMe? Me);

public sealed record BoardEntry(int Rank, string Name, int Score, int Stars, DateTimeOffset At);

public sealed record BoardMe(int Rank, int Score, int Stars);

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
