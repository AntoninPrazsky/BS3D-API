using System.Net;
using BS3D.Api.AdminWeb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BS3D.Api.Tests;

/// <summary>The admin page's guard, login and views (issue #5), on a test server; <c>ProcessTests</c> has the real process.</summary>
public sealed class AdminWebTests
{
    public static TheoryData<string> ProxyHeaders() => new(AdminWebApp.ProxyHeaders);

    [Theory]
    [MemberData(nameof(ProxyHeaders))]
    public async Task A_request_through_a_proxy_is_refused_and_stops_the_page(string header)
    {
        await using AdminPage page = await AdminPage.StartAsync();
        await page.LogInAsync();
        HttpRequestMessage request = new(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation(header, "203.0.113.9");

        HttpResponseMessage response = await page.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(page.Stopping);
        Assert.Contains(header, page.Output.ToString());
    }

    [Theory]
    [InlineData("evil.example:5001")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:5002")]
    [InlineData("192.168.0.239:5001")]
    public async Task Any_host_but_the_page_own_is_a_bad_request(string host)
    {
        await using AdminPage page = await AdminPage.StartAsync();
        await page.LogInAsync();
        HttpRequestMessage request = new(HttpMethod.Get, "/");
        request.Headers.Host = host;

        Assert.Equal(HttpStatusCode.BadRequest, (await page.Client.SendAsync(request)).StatusCode);
        Assert.False(page.Stopping);
    }

    [Theory]
    [InlineData("cross-site", HttpStatusCode.Forbidden)]
    [InlineData("same-site", HttpStatusCode.Forbidden)]
    [InlineData("same-origin", HttpStatusCode.OK)]
    [InlineData("none", HttpStatusCode.OK)]
    public async Task Only_its_own_pages_or_a_typed_address_may_ask(string site, HttpStatusCode expected)
    {
        await using AdminPage page = await AdminPage.StartAsync();
        await page.LogInAsync();
        HttpRequestMessage request = new(HttpMethod.Get, "/");
        request.Headers.Add("Sec-Fetch-Site", site);

        Assert.Equal(expected, (await page.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task It_only_reads()
    {
        await using AdminPage page = await AdminPage.StartAsync();
        await page.LogInAsync();

        foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await page.Client.SendAsync(new HttpRequestMessage(method, "/"))).StatusCode);

        using SqliteConnection c = page.App.Services.GetRequiredService<AdminData>().Open();
        using SqliteCommand update = c.CreateCommand();
        update.CommandText = "UPDATE players SET hidden = 1";
        Assert.Equal(8, Assert.Throws<SqliteException>(() => update.ExecuteNonQuery()).SqliteErrorCode); // SQLITE_READONLY
    }

    [Fact]
    public async Task Every_answer_carries_the_security_headers()
    {
        await using AdminPage page = await AdminPage.StartAsync();
        List<HttpResponseMessage> answers = [await page.Client.GetAsync("/")];                       // 401
        HttpRequestMessage foreign = new(HttpMethod.Get, "/");
        foreign.Headers.Host = "evil.example:5001";
        answers.Add(await page.Client.SendAsync(foreign));                                              // 400
        answers.Add(await page.Client.GetAsync("/login?key=wrong"));                                    // 403
        await page.LogInAsync();
        answers.Add(await page.Client.GetAsync("/"));                                                   // 200
        answers.Add(await page.Client.GetAsync("/nothing"));                                            // 404

        Assert.Equal([401, 400, 403, 200, 404], answers.Select(a => (int)a.StatusCode));
        foreach (HttpResponseMessage answer in answers)
        {
            Assert.Equal("default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'",
                Assert.Single(answer.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("DENY", Assert.Single(answer.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("nosniff", Assert.Single(answer.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-store", Assert.Single(answer.Headers.GetValues("Cache-Control")));
        }
    }

    [Fact]
    public async Task Nothing_is_shown_without_the_session()
    {
        await using AdminPage page = await AdminPage.StartAsync();

        foreach (string path in new[] { "/", "/live", "/live?auto=1", "/style.css" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await page.Client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_link_works_once_and_its_cookie_is_strict()
    {
        await using AdminPage page = await AdminPage.StartAsync();
        string key = page.Session.IssueKey();

        HttpResponseMessage first = await page.Client.GetAsync($"/login?key={key}");
        HttpResponseMessage again = await page.Client.GetAsync($"/login?key={key}");

        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal("/", first.Headers.Location?.OriginalString);
        string cookie = Assert.Single(first.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task The_link_expires()
    {
        await using AdminPage page = await AdminPage.StartAsync();
        string key = page.Session.IssueKey();

        page.Clock.Advance(page.Options.LinkLifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.Forbidden, (await page.Client.GetAsync($"/login?key={key}")).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_keys_void_the_link()
    {
        await using AdminPage page = await AdminPage.StartAsync();
        string key = page.Session.IssueKey();

        for (int i = 0; i < page.Options.MaxWrongKeys; i++)
            Assert.Equal(HttpStatusCode.Forbidden, (await page.Client.GetAsync($"/login?key=wrong{i}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await page.Client.GetAsync($"/login?key={key}")).StatusCode);
    }

    [Fact]
    public async Task The_overview_and_the_live_view_show_what_the_database_holds()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            Guid ann = AdminPage.AddPlayer(store, c, now, "Ann");
            AdminPage.AddClear(store, c, now, ann, 1234);
            AdminPage.AddClear(store, c, now, ann, 1500);
            Guid bob = AdminPage.AddPlayer(store, c, now, "Bob");
            store.SetHidden(c, bob, true);
            store.WriteRefusals(c, new Refusals.Batch(new Dictionary<(string, string), int> { [("2026-09-15", Reasons.OverCeiling)] = 2 },
                [new Refusals.Entry(now, 422, Reasons.OverCeiling, "POST", "/v1/scores", "One.json#x r1 score 999999")], 0), now, TimeSpan.FromDays(7), 100);
        });
        await page.LogInAsync();

        string overview = await page.Client.GetStringAsync("/");
        Assert.Contains("<td>2 (1 hidden)</td>", overview);
        Assert.Contains("<td>2 (0 by hidden players)</td>", overview);
        Assert.Contains("<tr><td>2026-09-15</td><td class=\"n\">2</td><td class=\"n\">2</td></tr>", overview);
        Assert.Contains($"<tr><td>2026-09-15</td><td>{Reasons.OverCeiling}</td><td class=\"n\">2</td></tr>", overview);

        string live = await page.Client.GetStringAsync("/live");
        Assert.Contains("http-equiv=\"refresh\"", live);
        Assert.Contains("1500", live);
        Assert.Contains("score 999999", live);
    }

    [Fact]
    public async Task What_came_from_a_request_is_encoded_and_isolated()
    {
        const string script = "<script>alert(1)</script>";
        const string reversed = "\u202Eevil";
        const string image = "<img src=x onerror=alert(2)>";
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, script), 100);
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, reversed), 100);
            store.WriteRefusals(c, new Refusals.Batch(new Dictionary<(string, string), int> { [("2026-09-15", Reasons.BadName)] = 1 },
                [new Refusals.Entry(now, 422, Reasons.BadName, "POST", "/v1/scores", image)], 0), now, TimeSpan.FromDays(7), 100);
        });
        await page.LogInAsync();

        foreach (string path in new[] { "/", "/live" })
        {
            string html = await page.Client.GetStringAsync(path);
            Assert.DoesNotContain("<script", html);
            Assert.DoesNotContain("<img", html);
            // Ordinal: a culture-aware comparison skips U+202E as ignorable and "finds" it anywhere
            Assert.DoesNotContain("\u202E", html, StringComparison.Ordinal);
        }
        string live = await page.Client.GetStringAsync("/live");
        Assert.Contains("<bdi>&lt;script&gt;alert(1)&lt;/script&gt;</bdi>", live);
        Assert.Contains("<bdi>&#x202E;evil</bdi>", live);
    }

    [Fact]
    public async Task No_token_hash_or_address_hash_is_ever_shown()
    {
        const string token = "AAAAtokenhashtokenhashtokenhashtokenhashtokenhashtokenhash000000";
        const string address = "BBBBaddresshash1";
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, "Ann", token), 100, address));
        await page.LogInAsync();

        foreach (string path in new[] { "/", "/live" })
        {
            string html = await page.Client.GetStringAsync(path);
            Assert.DoesNotContain(token[..8], html);
            Assert.DoesNotContain(address[..8], html);
        }
    }

    [Fact]
    public async Task A_database_at_another_schema_is_flagged_never_migrated()
    {
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 1";
            cmd.ExecuteNonQuery();
        });
        await page.LogInAsync();

        Assert.Contains("this page reads schema 2", await page.Client.GetStringAsync("/"));
        using SqliteConnection check = page.Store.Open();
        using SqliteCommand version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, version.ExecuteScalar());
    }

    [Fact]
    public async Task It_stops_after_half_an_hour_without_use()
    {
        await using AdminPage page = await AdminPage.StartAsync();
        await page.LogInAsync();

        page.Clock.Advance(TimeSpan.FromMinutes(29));
        await AdminPage.Settle();
        Assert.False(page.Stopping);
        page.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await page.StopsWithin());
    }

    [Fact]
    public async Task The_live_view_reloading_itself_does_not_keep_it_alive_but_the_owner_does()
    {
        await using AdminPage reloading = await AdminPage.StartAsync();
        await reloading.LogInAsync();
        reloading.Clock.Advance(TimeSpan.FromMinutes(20));
        await reloading.Client.GetAsync("/live?auto=1");
        reloading.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(await reloading.StopsWithin());

        await using AdminPage used = await AdminPage.StartAsync();
        await used.LogInAsync();
        used.Clock.Advance(TimeSpan.FromMinutes(20));
        await used.Client.GetAsync("/live");
        used.Clock.Advance(TimeSpan.FromMinutes(11));
        await AdminPage.Settle();
        Assert.False(used.Stopping);
    }

    [Fact]
    public async Task It_stops_when_its_link_expires_unused()
    {
        await using AdminPage page = await AdminPage.StartAsync();
        page.Session.IssueKey();

        page.Clock.Advance(page.Options.LinkLifetime + TimeSpan.FromMinutes(1));

        Assert.True(await page.StopsWithin());
    }
}
