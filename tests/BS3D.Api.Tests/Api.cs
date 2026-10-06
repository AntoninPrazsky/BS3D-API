using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BS3D.Api.Tests;

/// <summary>
/// The service on a fresh database, with one known board and a clock the test moves. One per test, so no test sees
/// another's rows.
/// </summary>
public sealed class Api : WebApplicationFactory<Program>
{
    public const string File = "One.json";
    public const string Hash = "0123456789abcdef";
    public const int Rules = 1;
    public const int Budget = 30;
    public const int Ceiling = 50_000;
    public const int MinShots = 3;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _settings;

    /// <summary>September 2026, the middle of it — far enough from both month ends for a test to step across one.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The address every request seems to come from. The test server leaves it unset, as no proxy is trusted.</summary>
    public IPAddress? ClientAddress { get; set; }

    /// <summary>Every line the service logs.</summary>
    public LogCapture Log { get; } = new();

    /// <summary>A web root to give the service, as a published folder with a <c>wwwroot</c> would give it one.</summary>
    public string? WebRoot { get; init; }

    public Api(Dictionary<string, string?>? settings = null)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "ceilings"));
        System.IO.File.WriteAllText(Path.Combine(_folder, "ceilings", "BS3D-v9.9.9-ceilings.json"), $$"""
            { "format": "bs3d-ceilings", "version": 1, "rulesVersion": 1, "hashLength": 16, "levels": [
              { "file": "{{File}}", "name": "One", "hash": "{{Hash}}", "rulesVersion": {{Rules}}, "shots": {{Budget}},
                "ceiling": {{Ceiling}}, "minShots": {{MinShots}} } ] }
            """);

        _settings = new()
        {
            ["Scores:Database"] = Path.Combine(_folder, "scores.db"),
            ["Scores:CeilingsDirectory"] = Path.Combine(_folder, "ceilings"),
            ["Scores:AddressSalt"] = "test-salt",
            ["Scores:SubmissionsPerMinutePerAddress"] = "1000",
            ["Scores:SubmissionsPerMinutePerPlayer"] = "1000",
        };
        foreach (var (key, value) in settings ?? new()) _settings[key] = value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        if (WebRoot != null) builder.UseSetting(WebHostDefaults.WebRootKey, WebRoot);
        builder.ConfigureLogging(logging => logging.AddProvider(Log));
        builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
            services.AddTransient<IStartupFilter>(_ => new ClientAddressFilter(this));
        });
    }

    /// <summary>Puts <see cref="ClientAddress"/> on every request before the service's own middleware sees it.</summary>
    private sealed class ClientAddressFilter(Api api) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, rest) =>
            {
                if (api.ClientAddress != null) context.Connection.RemoteIpAddress = api.ClientAddress;
                return rest(context);
            });
            next(app);
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // This fixture's pool only: clearing every pool reached into the fixtures running beside it, whose flusher was
        // writing (an ObjectDisposedException at their stop, 2 runs in 12)
        new ScoreStore(_settings["Scores:Database"]!).ClearPool();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    public ScoreStore Store => Services.GetRequiredService<ScoreStore>();

    /// <summary>A player as the game makes one: a fresh id and 32 random bytes of token.</summary>
    public static (Guid Id, string Token) NewPlayer() =>
        (Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));

    public HttpClient ClientFor(string? token)
    {
        HttpClient client = CreateClient();
        if (token != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static SubmissionRequest Clear(Guid player, int score, string name = "Tester", int stars = 3, int shots = 10,
        double seconds = 60, string file = File, string hash = Hash, int rules = Rules, string version = "v0.2.0", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), player, name, new LevelKey(file, hash), rules, score, stars, shots, seconds, version);

    public async Task<HttpResponseMessage> Post((Guid Id, string Token) player, SubmissionRequest body) =>
        await ClientFor(player.Token).PostAsJsonAsync("/v1/scores", body);

    public async Task<SubmissionAnswer> Accepted((Guid Id, string Token) player, SubmissionRequest body)
    {
        HttpResponseMessage response = await Post(player, body);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubmissionAnswer>())!;
    }

    public async Task<BoardPage> Board(string period = "month", string? month = null, Guid? player = null, int? limit = null, int? offset = null)
    {
        string query = $"/v1/boards/{File}?hash={Hash}&rules={Rules}&period={period}"
            + (month != null ? $"&month={month}" : "") + (player != null ? $"&player={player}" : "")
            + (limit != null ? $"&limit={limit}" : "") + (offset != null ? $"&offset={offset}" : "");
        return (await CreateClient().GetFromJsonAsync<BoardPage>(query))!;
    }

    public static async Task<string> ReasonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Refusal>())!.Reason;
}
