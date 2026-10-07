using BS3D.Api.AdminWeb;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BS3D.Api.Tests;

/// <summary>
/// The admin page (issue #5) on a test server, over a fresh database a <see cref="ScoreStore"/> fills, with a clock the
/// test moves. Requests come as the browser on the Pi sends them: to 127.0.0.1:5001.
/// </summary>
public sealed class AdminPage : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"));

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));
    public StringWriter Output { get; } = new();
    public ScoreStore Store { get; }
    public AdminWebOptions Options { get; }
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public AdminSession Session => App.Services.GetRequiredService<AdminSession>();

    private AdminPage(TimeZoneInfo? zone)
    {
        Directory.CreateDirectory(_folder);
        Store = new ScoreStore(Path.Combine(_folder, "scores.db"));
        Store.EnsureSchema();
        Options = new AdminWebOptions
        {
            Database = Path.Combine(_folder, "scores.db"),
            CeilingsDirectory = Path.Combine(_folder, "ceilings"),
            Clock = Clock,
            // UTC unless a test names a zone, so no page depends on the zone of the machine the tests run on
            TimeZone = zone ?? TimeZoneInfo.Utc,
            Output = Output,
        };
    }

    /// <summary>A page over a database <paramref name="fill"/> has written, showing moments in <paramref name="zone"/> (UTC).</summary>
    public static async Task<AdminPage> StartAsync(Action<ScoreStore, SqliteConnection, DateTimeOffset>? fill = null, TimeZoneInfo? zone = null)
    {
        AdminPage page = new(zone);
        if (fill != null)
        {
            using SqliteConnection c = page.Store.Open();
            fill(page.Store, c, page.Clock.GetUtcNow());
        }
        page.App = AdminWebApp.Build(page.Options, b => b.WebHost.UseTestServer());
        await page.App.StartAsync();
        page.Client = page.App.GetTestClient();
        page.Client.BaseAddress = new Uri("http://127.0.0.1:5001/");
        return page;
    }

    /// <summary>Opens the printed link, and sends the session's cookie from then on. Returns the cookie's header.</summary>
    public async Task<string> LogInAsync()
    {
        HttpResponseMessage response = await Client.GetAsync($"/login?key={Session.IssueKey()}");
        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        return setCookie;
    }

    public bool Stopping => App.Lifetime.ApplicationStopping.IsCancellationRequested;

    /// <summary>Lets a background loop that the clock just woke run to its end, where the test expects nothing to happen.</summary>
    public static async Task Settle() => await Task.Delay(200);

    /// <summary>Whether the page starts stopping within <paramref name="seconds"/>: the loop the clock woke runs on the thread pool.</summary>
    public async Task<bool> StopsWithin(int seconds = 5)
    {
        for (int i = 0; i < seconds * 20 && !Stopping; i++) await Task.Delay(50);
        return Stopping;
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (App != null) await App.DisposeAsync();
        Store.ClearPool();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    public static Guid AddPlayer(ScoreStore store, SqliteConnection c, DateTimeOffset now, string name, string tokenHash = "T0KENHASH0000000000000000000000000000000000000000000000000000000")
    {
        Guid id = Guid.NewGuid();
        store.CreatePlayer(c, id, tokenHash, name, now);
        return id;
    }

    /// <summary>An accepted submission, straight into the store: a clear, or with <paramref name="stars"/> 0 an unfinished attempt.</summary>
    public static void AddClear(ScoreStore store, SqliteConnection c, DateTimeOffset now, Guid player, int score, string ipHash = "1PHASH0000000000",
        BoardKey? board = null, int stars = 3) =>
        store.Insert(c, new ScoreStore.NewSubmission(Guid.NewGuid(), player, board ?? new BoardKey(Api.File, Api.Hash, Api.Rules), score, stars, 10, 60,
            "v0.2.1", ipHash, "BS3D/v0.2.1", now));

    /// <summary>A ceiling table naming <c>One.json</c> (the board <see cref="Api"/> uses) and <c>Two.json</c>.</summary>
    public void WriteCeilingTable()
    {
        Directory.CreateDirectory(Options.CeilingsDirectory);
        File.WriteAllText(Path.Combine(Options.CeilingsDirectory, "BS3D-v9.9.9-ceilings.json"), $$"""
            { "format": "bs3d-ceilings", "version": 1, "rulesVersion": 1, "hashLength": 16, "levels": [
              { "file": "{{Api.File}}", "name": "One", "hash": "{{Api.Hash}}", "rulesVersion": {{Api.Rules}}, "shots": 30, "ceiling": 50000, "minShots": 3 },
              { "file": "Two.json", "name": "Two", "hash": "fedcba9876543210", "rulesVersion": 1, "shots": 20, "ceiling": 40000, "minShots": 2 } ] }
            """);
    }
}
