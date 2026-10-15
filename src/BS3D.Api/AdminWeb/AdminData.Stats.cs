using Microsoft.Data.Sqlite;

namespace BS3D.Api.AdminWeb;

public sealed partial class AdminData
{
    /// <summary>
    /// The play counts' report (BS3D#709) in <paramref name="sort"/>'s order, or null on a database the service has not
    /// brought to schema 4 yet: the page never migrates, so there is no <c>play_stats</c> to read.
    /// </summary>
    public List<PlayStatsReport.Line>? ReadPlayStats(Ceilings ceilings, string sort)
    {
        using SqliteConnection c = Open();
        if (Scalar(c, "PRAGMA user_version") < 4) return null;

        return PlayStatsReport.Build(ScoreStore.PlayStats(c), ceilings, sort);
    }
}
