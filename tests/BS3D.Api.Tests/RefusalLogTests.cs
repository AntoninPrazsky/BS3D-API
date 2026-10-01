using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BS3D.Api.Tests;

/// <summary>
/// Refusals in the database for the admin page (issue #5): every one counted per UTC day and reason in
/// <c>refusal_days</c>, the recent ones in <c>refusal_log</c>, both written by <see cref="RefusalFlusher"/> off the
/// request path, and never an address.
/// </summary>
public sealed class RefusalLogTests
{
    /// <summary>A month of seconds: the flusher's own timer stays out of a test that moves the clock by days.</summary>
    private const string NoTimer = "2592000";

    private static async Task<HttpResponseMessage> RefuseOne(Api api, (Guid Id, string Token) player) =>
        await api.Post(player, Api.Clear(player.Id, Api.Ceiling + 1));

    private static List<string[]> Rows(Api api, string sql)
    {
        using SqliteConnection c = api.Store.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using SqliteDataReader r = cmd.ExecuteReader();
        List<string[]> rows = new();
        while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => Convert.ToString(r.GetValue(i))!).ToArray());
        return rows;
    }

    private static RefusalFlusher Flusher(Api api) => api.Services.GetRequiredService<RefusalFlusher>();

    [Fact]
    public async Task A_refusal_reaches_the_database_at_the_next_flush_not_on_the_request()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        HttpResponseMessage refused = await RefuseOne(api, player);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Empty(Rows(api, "SELECT * FROM refusal_log"));
        Assert.Empty(Rows(api, "SELECT * FROM refusal_days"));

        Flusher(api).Flush();

        string[] row = Assert.Single(Rows(api, "SELECT status, reason, method, path, detail FROM refusal_log"));
        Assert.Equal(["422", Reasons.OverCeiling, "POST", "/v1/scores"], row[..4]);
        Assert.Contains($"{Api.File}#{Api.Hash}", row[4]);
        Assert.Equal(["2026-09-15", Reasons.OverCeiling, "1"], Assert.Single(Rows(api, "SELECT day, reason, count FROM refusal_days")));
    }

    [Fact]
    public async Task Day_counts_add_up_across_flushes()
    {
        using Api api = new();
        var player = Api.NewPlayer();

        await RefuseOne(api, player);
        await RefuseOne(api, player);
        Flusher(api).Flush();
        await RefuseOne(api, player);
        Flusher(api).Flush();

        Assert.Equal(["2026-09-15", Reasons.OverCeiling, "3"], Assert.Single(Rows(api, "SELECT day, reason, count FROM refusal_days")));
        Assert.Equal(3, Rows(api, "SELECT id FROM refusal_log").Count);
    }

    [Fact]
    public async Task A_flood_past_the_queue_is_counted_in_full_and_written_as_one_dropped_row()
    {
        using Api api = new(new() { ["Scores:RefusalQueueCapacity"] = "3" });
        var player = Api.NewPlayer();

        for (int i = 0; i < 5; i++) await RefuseOne(api, player);
        Flusher(api).Flush();

        List<string[]> log = Rows(api, "SELECT reason, detail FROM refusal_log ORDER BY id");
        Assert.Equal([Reasons.OverCeiling, Reasons.OverCeiling, Reasons.OverCeiling, "dropped"], log.Select(r => r[0]));
        Assert.StartsWith("2 refusal(s)", log[3][1]);
        Assert.Equal("5", Assert.Single(Rows(api, "SELECT count FROM refusal_days"))[0]);
    }

    [Fact]
    public async Task The_log_drops_rows_older_than_its_days_and_the_day_counts_stay()
    {
        using Api api = new(new() { ["Scores:RefusalFlushSeconds"] = NoTimer, ["Scores:RefusalLogDays"] = "7" });
        var player = Api.NewPlayer();

        await RefuseOne(api, player);
        Flusher(api).Flush();
        api.Clock.Advance(TimeSpan.FromDays(8));
        await RefuseOne(api, player);
        Flusher(api).Flush();

        Assert.StartsWith("2026-09-23", Assert.Single(Rows(api, "SELECT at FROM refusal_log"))[0]);
        Assert.Equal([["2026-09-15"], ["2026-09-23"]], Rows(api, "SELECT day FROM refusal_days ORDER BY day"));
    }

    [Fact]
    public async Task The_log_keeps_its_newest_rows_up_to_its_size()
    {
        using Api api = new(new() { ["Scores:RefusalLogMaxRows"] = "3" });
        var player = Api.NewPlayer();

        for (int i = 0; i < 5; i++) await RefuseOne(api, player);
        Flusher(api).Flush();

        Assert.Equal([["3"], ["4"], ["5"]], Rows(api, "SELECT id FROM refusal_log ORDER BY id"));
        Assert.Equal("5", Assert.Single(Rows(api, "SELECT count FROM refusal_days"))[0]);
    }

    [Fact]
    public async Task No_row_holds_the_client_address_and_a_limited_address_is_named_by_its_hash()
    {
        IPAddress client = IPAddress.Parse("203.0.113.80");
        using Api api = new(new() { ["Scores:SubmissionsPerMinutePerAddress"] = "2" }) { ClientAddress = client };
        var player = Api.NewPlayer();

        await RefuseOne(api, player);
        await RefuseOne(api, player);
        HttpResponseMessage limited = await RefuseOne(api, player);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Flusher(api).Flush();

        List<string[]> log = Rows(api, "SELECT at, status, reason, method, path, detail FROM refusal_log");
        Assert.Equal(3, log.Count);
        Assert.DoesNotContain(log, row => row.Any(value => value.Contains(client.ToString())));
        string hash = new AddressHasher("test-salt").Hash(new DefaultHttpContext { Connection = { RemoteIpAddress = client } });
        Assert.Contains(log, row => row[2] == Reasons.RateLimited && row[5] == $"address {hash}");
    }

    [Fact]
    public async Task The_timer_flushes_without_being_asked()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await RefuseOne(api, player);

        api.Clock.Advance(TimeSpan.FromSeconds(5));

        for (int i = 0; i < 100 && Rows(api, "SELECT id FROM refusal_log").Count == 0; i++) await Task.Delay(50);
        Assert.Single(Rows(api, "SELECT id FROM refusal_log"));
    }

    [Fact]
    public async Task Stopping_the_service_writes_what_was_gathered()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await RefuseOne(api, player);

        await Flusher(api).StopAsync(CancellationToken.None);

        Assert.Single(Rows(api, "SELECT id FROM refusal_log"));
    }
}
