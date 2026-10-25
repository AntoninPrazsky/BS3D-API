using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BS3D.Api.Tests;

/// <summary>
/// <c>POST /v1/benchmarks</c> (BS3D#988): a finished benchmark stored as it came, once per id, held to bounds because anyone
/// can send one, and kept with nothing that names a player or an address; the admin CLI's lines and the Benchmarks tab.
/// </summary>
public sealed class BenchmarkTests
{
    /// <summary>A result as the game writes <c>Benchmark.json</c>, cut to a few fields of each part.</summary>
    public static string ResultJson(string gpu = "AMD Radeon RX 6900 XT", string extra = "") => $$$"""
        {"when":"2026-10-25T16:40:00Z","budgetMs":13.333,"recommended":"Ultra",
         "system":{"build":"v0.5.0","os":"Microsoft Windows 10.0.26300","cpu":"AMD Ryzen 9 5900X 12-Core Processor","cpuThreads":24,
                   "gpu":{{{JsonSerializer.Serialize(gpu)}}},"backBuffer":"3840x1600","refreshHz":75,"fullscreen":true,"quality":"High",
                   "supersample":2,"potato":false,"scale":1.25{{{extra}}}},
         "segments":[{"scene":"Cavern","frames":5952,"medianMs":3.06},{"scene":"Neon City","frames":4434,"medianMs":4.89}],
         "physics":{"level":"Girandole","balls":1204,"medianMs":1.325}}
        """;

    public static BenchmarkRequest Request(string? result = null, Guid? id = null, string? version = "v0.5.0") =>
        new(id ?? Guid.NewGuid(), version, JsonDocument.Parse(result ?? ResultJson()).RootElement.Clone());

    private static async Task<HttpResponseMessage> Post(Api api, BenchmarkRequest body, string? token = null) =>
        await api.ClientFor(token).PostAsJsonAsync("/v1/benchmarks", body);

    private static async Task<HttpResponseMessage> PostRaw(Api api, string json) =>
        await api.CreateClient().PostAsync("/v1/benchmarks", new StringContent(json, Encoding.UTF8, "application/json"));

    private static List<ScoreStore.StoredBenchmark> Stored(Api api)
    {
        using SqliteConnection c = api.Store.Open();
        return api.Store.Benchmarks(c, 0);
    }

    [Fact]
    public async Task A_result_is_stored_as_it_came_and_a_resend_is_stored_once()
    {
        using Api api = new();
        BenchmarkRequest body = Request();

        HttpResponseMessage first = await Post(api, body);
        HttpResponseMessage again = await Post(api, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(new BenchmarkAnswer(body.BenchmarkId, false), await first.Content.ReadFromJsonAsync<BenchmarkAnswer>());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(new BenchmarkAnswer(body.BenchmarkId, true), await again.Content.ReadFromJsonAsync<BenchmarkAnswer>());

        ScoreStore.StoredBenchmark stored = Assert.Single(Stored(api));
        Assert.Equal(body.BenchmarkId, stored.BenchmarkId);
        Assert.Equal("v0.5.0", stored.GameVersion);
        Assert.Equal(api.Clock.GetUtcNow(), stored.ReceivedAt);
        // As the request carried it: the client writes the element compactly, and the service keeps those bytes
        Assert.Equal(JsonSerializer.Serialize(body.Result!.Value), stored.Result);
    }

    [Fact]
    public async Task No_token_is_needed_and_one_sent_links_nothing()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        Assert.Equal(HttpStatusCode.Created, (await Post(api, Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Request(), player.Token)).StatusCode);

        using SqliteConnection c = api.Store.Open();
        Assert.Null(api.Store.FindPlayer(c, player.Id));
        Assert.Empty(api.Store.Export(c));
        Assert.Equal(2, Stored(api).Count);
    }

    /// <summary>A request's JSON by hand: an id ("" leaves it out), a version (null leaves it out) and the result's text.</summary>
    private static string Body(string result, string? id = null, string? version = "v0.5.0") =>
        "{" + (id == "" ? "" : $"\"benchmarkId\":\"{id ?? Guid.NewGuid().ToString()}\",")
        + (version != null ? $"\"gameVersion\":{JsonSerializer.Serialize(version)}," : "") + $"\"result\":{result}" + "}";

    public static TheoryData<int, string, string> Refused => new()
    {
        { 400, Reasons.BadRequest, Body(ResultJson(), id: "") },
        { 400, Reasons.BadRequest, Body(ResultJson(), id: Guid.Empty.ToString()) },
        { 400, Reasons.BadRequest, $"{{\"benchmarkId\":\"{Guid.NewGuid()}\",\"gameVersion\":\"v0.5.0\"}}" },
        { 422, Reasons.BadVersion, Body(ResultJson(), version: null) },
        { 422, Reasons.BadVersion, Body(ResultJson(), version: "v0.5.0; DROP TABLE") },
        { 422, Reasons.BadResult, Body("[1,2,3]") },
        { 422, Reasons.BadResult, Body("\"fast\"") },
        { 422, Reasons.BadResult, Body("{\"segments\":[]}") },
        { 422, Reasons.BadResult, Body("{\"system\":{}}") },
        { 422, Reasons.BadResult, Body("{\"system\":\"PC\",\"segments\":[]}") },
        { 422, Reasons.BadResult, Body("{\"system\":{},\"segments\":{}}") },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_malformed_result_is_refused_with_its_reason_and_nothing_stored(int status, string reason, string json)
    {
        using Api api = new();

        HttpResponseMessage response = await PostRaw(api, json);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(reason, await Api.ReasonOf(response));
        Assert.Empty(Stored(api));
    }

    [Theory]
    [InlineData(""","x":"\ud800" """)]
    [InlineData(""","\ud800":1""")]
    [InlineData(""","deep":[{"x":"\ud800 lone"}]""")]
    public async Task A_string_that_is_not_text_is_refused(string field)
    {
        // Stored as it came, a lone surrogate's escape would be fine until a reader decoded it: the admin page and the CLI
        using Api api = new();

        HttpResponseMessage response = await PostRaw(api, Body(ResultJson(extra: field)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(Reasons.BadResult, await Api.ReasonOf(response));
        Assert.Empty(Stored(api));
    }

    [Fact]
    public async Task A_result_over_its_size_is_refused()
    {
        using Api api = new(new() { ["Scores:BenchmarkMaxResultBytes"] = "600" });

        HttpResponseMessage fits = await Post(api, Request());
        HttpResponseMessage over = await Post(api, Request(ResultJson(extra: $$""","note":"{{new string('x', 400)}}" """)));

        Assert.Equal(HttpStatusCode.Created, fits.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal(Reasons.ResultTooLarge, await Api.ReasonOf(over));
        Assert.Single(Stored(api));
    }

    [Fact]
    public async Task One_network_is_limited_by_the_minute_and_the_refusal_names_no_address()
    {
        using Api api = new(new() { ["Scores:BenchmarksPerMinutePerAddress"] = "2" });

        Assert.Equal(HttpStatusCode.Created, (await Post(api, Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Request())).StatusCode);
        HttpResponseMessage third = await Post(api, Request());

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(Reasons.RateLimited, await Api.ReasonOf(third));
        Assert.True(third.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        api.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Request())).StatusCode);
        Assert.Equal(3, Stored(api).Count);

        string hash = new AddressHasher("test-salt").Hash(new Microsoft.AspNetCore.Http.DefaultHttpContext());
        string refused = Assert.Single(api.Log.Lines, line => line.Contains("/v1/benchmarks", StringComparison.Ordinal));
        Assert.Contains("(a network)", refused);
        Assert.DoesNotContain(hash, refused);
    }

    [Fact]
    public async Task One_network_is_limited_by_a_sliding_day()
    {
        using Api api = new(new() { ["Scores:BenchmarksPerMinutePerAddress"] = "100", ["Scores:BenchmarksPerDayPerAddress"] = "2" });

        await Post(api, Request());
        api.Clock.Advance(TimeSpan.FromHours(12));
        await Post(api, Request());
        api.Clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(api, Request())).StatusCode);

        // The first of the two leaves the window 24 hours after it came
        api.Clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Request())).StatusCode);
    }

    [Fact]
    public async Task An_ipv6_host_is_limited_by_its_64_and_another_64_is_not()
    {
        using Api api = new(new() { ["Scores:BenchmarksPerMinutePerAddress"] = "1" });

        api.ClientAddress = IPAddress.Parse("2001:db8:1:2::1");
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Request())).StatusCode);
        api.ClientAddress = IPAddress.Parse("2001:db8:1:2::ffff");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(api, Request())).StatusCode);
        api.ClientAddress = IPAddress.Parse("2001:db8:1:3::1");
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Request())).StatusCode);
    }

    [Fact]
    public async Task Every_network_together_is_held_to_the_day_and_a_retry_is_still_answered()
    {
        using Api api = new(new() { ["Scores:BenchmarksPerDay"] = "1" });
        BenchmarkRequest first = Request();

        api.ClientAddress = IPAddress.Parse("192.0.2.1");
        Assert.Equal(HttpStatusCode.Created, (await Post(api, first)).StatusCode);
        api.ClientAddress = IPAddress.Parse("192.0.2.2");
        HttpResponseMessage full = await Post(api, Request());
        HttpResponseMessage retry = await Post(api, first);

        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal(TimeSpan.FromHours(12), full.Headers.RetryAfter?.Delta);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True((await retry.Content.ReadFromJsonAsync<BenchmarkAnswer>())!.Duplicate);

        api.Clock.Advance(TimeSpan.FromHours(12));
        Assert.Equal(HttpStatusCode.Created, (await Post(api, Request())).StatusCode);
    }

    [Fact]
    public async Task Past_the_cap_a_result_is_refused_and_a_retry_still_answered()
    {
        using Api api = new(new() { ["Scores:BenchmarksMaxStored"] = "2" });
        BenchmarkRequest first = Request();

        await Post(api, first);
        await Post(api, Request());
        HttpResponseMessage third = await Post(api, Request());

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(Reasons.BenchmarksFull, await Api.ReasonOf(third));
        api.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await Post(api, first)).StatusCode);
        Assert.Equal(2, Stored(api).Count);
    }

    [Fact]
    public async Task Nothing_kept_names_a_player_or_an_address_and_nothing_accepted_is_logged()
    {
        using Api api = new();
        BenchmarkRequest body = Request();
        await Post(api, body);

        using SqliteConnection c = api.Store.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('benchmarks') ORDER BY cid";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<string> columns = new();
        while (r.Read()) columns.Add(r.GetString(0));
        Assert.Equal(["id", "benchmark_id", "received_at", "game_version", "result"], columns);

        Assert.DoesNotContain(api.Log.Lines, line => line.Contains("/v1/benchmarks", StringComparison.Ordinal)
            || line.Contains(body.BenchmarkId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_admin_cli_prints_one_line_a_result_oldest_first_after_an_id()
    {
        using Api api = new();
        await Post(api, Request(ResultJson(gpu: "First GPU")));
        api.Clock.Advance(TimeSpan.FromMinutes(1));
        await Post(api, Request(ResultJson(gpu: "Second GPU‮")));

        string Run(params string[] args)
        {
            StringWriter output = new();
            Assert.Equal(0, AdminCli.Run(["benchmarks", .. args], api.Store, new ScoresOptions(), output));
            return output.ToString();
        }

        string[] all = Run().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, all.Length);
        using (JsonDocument line = JsonDocument.Parse(all[0]))
        {
            Assert.Equal(1, line.RootElement.GetProperty("id").GetInt64());
            Assert.Equal("v0.5.0", line.RootElement.GetProperty("gameVersion").GetString());
            Assert.Equal("First GPU", line.RootElement.GetProperty("result").GetProperty("system").GetProperty("gpu").GetString());
        }
        // A right-to-left override is escaped, so it cannot reorder the terminal's line
        Assert.Contains(@"Second GPU‮", all[1]);
        Assert.DoesNotContain('‮', all[1]);

        Assert.Single(Run("--after", "1").Split('\n', StringSplitOptions.RemoveEmptyEntries));

        StringWriter usage = new();
        Assert.Equal(2, AdminCli.Run(["benchmarks", "--after", "x"], api.Store, new ScoresOptions(), usage));
        Assert.StartsWith("usage:", usage.ToString());
    }

    [Fact]
    public async Task The_benchmarks_tab_lists_the_newest_first_encoded_and_a_result_has_its_page()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            store.InsertBenchmark(c, Guid.NewGuid(), now.AddMinutes(-5), "v0.4.0", ResultJson(gpu: "Older GPU"));
            store.InsertBenchmark(c, Guid.NewGuid(), now, "v0.5.0", ResultJson(gpu: "<script>alert(1)</script>"));
            store.InsertBenchmark(c, Guid.NewGuid(), now, "v0.5.0", "{\"system\": not json");
        });
        await page.LogInAsync();

        string list = await page.Client.GetStringAsync("/benchmarks");
        Assert.Contains("aria-current=\"page\">Benchmarks</a>", list);
        Assert.DoesNotContain("<script>alert(1)</script>", list);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", list);
        Assert.True(list.IndexOf("&lt;script&gt;", StringComparison.Ordinal) < list.IndexOf("Older GPU", StringComparison.Ordinal), "newest first");
        Assert.Contains("Cavern</bdi> 327", list);          // 1000 / 3.06 ms
        Assert.Contains("1.33 ms", list);                     // the physics' median step
        Assert.Contains("3840x1600, 75 Hz, full screen, scale 1.25", list);
        Assert.Contains("High &#xD7;2", list);               // every hole is encoded, the multiplication sign too

        string one = await page.Client.GetStringAsync("/benchmark?id=1");
        Assert.Contains("Older GPU", one);
        Assert.Contains("<th><bdi>medianMs</bdi></th>", one);
        Assert.Contains("Girandole", one);
        Assert.Contains("aria-current=\"page\">Benchmarks</a>", one);

        // A result that does not read is shown as it is, never a 500
        HttpResponseMessage unreadable = await page.Client.GetAsync("/benchmark?id=3");
        Assert.Equal(HttpStatusCode.OK, unreadable.StatusCode);
        Assert.Contains("(unreadable)", await unreadable.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await page.Client.GetAsync("/benchmark?id=99")).StatusCode);
    }

    [Fact]
    public async Task The_benchmarks_tab_needs_the_session_and_reads_no_table_before_schema_5()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            using SqliteCommand drop = c.CreateCommand();
            drop.CommandText = "DROP TABLE benchmarks; PRAGMA user_version = 4;";
            drop.ExecuteNonQuery();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await page.Client.GetAsync("/benchmarks")).StatusCode);

        await page.LogInAsync();
        HttpResponseMessage old = await page.Client.GetAsync("/benchmarks");
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Contains("not at schema 5", await old.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await page.Client.GetAsync("/benchmark?id=1")).StatusCode);
    }

    [Fact]
    public async Task Count_does_not_count_the_benchmarks()
    {
        using Api api = new();
        StringWriter before = new();
        AdminCli.Run(["count"], api.Store, new ScoresOptions(), before);

        await Post(api, Request());
        StringWriter after = new();
        AdminCli.Run(["count"], api.Store, new ScoresOptions(), after);

        Assert.Equal(before.ToString(), after.ToString());
    }
}
