using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// <c>BS3D.Api admin status-feed [--every &lt;seconds&gt;] [--out &lt;file&gt;]</c> (#13): the status snapshot for the
/// server's front panel (BS3D-LCD#1). With <c>--out</c> it writes <see cref="AdminData.ReadStatus"/> there every
/// <c>--every</c> seconds (15) until SIGTERM or SIGINT, which <c>deploy/bs3d-api-status.service</c> runs as
/// <c>bs3d-api</c>; without it, it prints one snapshot and exits.
/// <para>
/// An admin process, dispatched in <c>Program.cs</c> before the public service is built, like <c>admin web</c>: it opens
/// the database read-only and never migrates it, so a database at another schema is reported in the snapshot
/// (<c>service.schema</c> beside <c>service.expected_schema</c>), not changed. Its settings come from the environment
/// (<c>Scores__Database</c>, <c>Scores__CeilingsDirectory</c>, <c>Scores__RequireKnownBoard</c>), as the page's do.
/// </para>
/// </summary>
public static class StatusFeed
{
    public const int DefaultEvery = 15;

    private const string Usage = "usage: BS3D.Api admin status-feed [--every <1-3600>] [--out <file>]";

    public static async Task<int> RunAsync(string[] args)
    {
        TextWriter output = Console.Out;
        int every = DefaultEvery;
        string? file = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--every" when i + 1 < args.Length
                    && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out every) && every is >= 1 and <= 3600:
                    i++;
                    break;
                case "--out" when i + 1 < args.Length:
                    file = Path.GetFullPath(args[++i]);
                    break;
                default:
                    output.WriteLine(Usage);
                    return 2;
            }
        }

        ScoresOptions scores = new();
        new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection(ScoresOptions.Section).Bind(scores);
        if (!File.Exists(scores.Database))
        {
            output.WriteLine($"No database at {Path.GetFullPath(scores.Database)} (Scores__Database).");
            return 1;
        }
        AdminData data = new(new AdminWebOptions { Database = scores.Database, CeilingsDirectory = scores.CeilingsDirectory, Output = output });

        if (file == null)
        {
            output.WriteLine(Json(data, scores));
            return 0;
        }

        using CancellationTokenSource stop = new();
        using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        output.WriteLine($"Writing the status snapshot to {file} every {every} s.");
        string? failing = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                Write(file, Json(data, scores));
                if (failing != null) output.WriteLine("The status snapshot is written again.");
                failing = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Once per kind of failure, not every 15 s: a snapshot that stops being written goes stale, and the panel says so
                if (failing != e.Message) output.WriteLine($"The status snapshot was not written: {e.Message}");
                failing = e.Message;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(every), stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
        output.WriteLine("The status feed has stopped.");
        return 0;

        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            stop.Cancel();
        }
    }

    public static string Json(AdminData data, ScoresOptions scores) =>
        JsonSerializer.Serialize(data.ReadStatus(Ceilings.Load(scores.CeilingsDirectory, NullLogger.Instance), scores.RequireKnownBoard),
            AdminData.StatusJson);

    /// <summary>
    /// Writes <paramref name="json"/> to <paramref name="file"/> so that no reader ever sees half of it: into a temporary
    /// file beside it, created 0640 (its owner and its group, the readers'), then renamed over the old one.
    /// </summary>
    public static void Write(string file, string json)
    {
        string temporary = file + ".tmp";
        FileStreamOptions create = new() { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        using (StreamWriter writer = new(temporary, System.Text.Encoding.UTF8, create))
            writer.Write(json + "\n");
        // A file left by a run under another umask keeps its mode when it is created again; set it every time
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.Move(temporary, file, overwrite: true);
    }
}
