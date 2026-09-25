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
}
