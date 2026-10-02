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

        foreach (string path in new[] { "/", "/live", "/live?auto=1", "/charts", "/style.css" })
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

    /// <summary>
    /// The off-box copy (#6) as deploy/backup.sh records it in backups/last-offbox, read on the overview at 2026-09-15 12:00:
    /// its stat, and a warning when none is set up, the last one failed, or the newest good one is over two days old.
    /// </summary>
    [Theory]
    [InlineData(null, "<strong>no record</strong>", null)]
    [InlineData("attempt=2026-09-15T03:31:00Z\nresult=none\ndetail=no target\nok=\ncopy=\n", "<strong>none</strong><small>not set up", "Every backup is on this Pi's own disk")]
    [InlineData("attempt=2026-09-15T03:31:00Z\nresult=ok\ndetail=copied\nok=2026-09-15T03:31:00Z\ncopy=scores-20260915-033100.db\n", "<strong>8 h ago</strong><small>scores-20260915-033100.db</small>", null)]
    [InlineData("attempt=2026-09-12T03:31:00Z\nresult=ok\ndetail=copied\nok=2026-09-12T03:31:00Z\ncopy=scores-20260912-033100.db\n", "<strong>3 d ago</strong>", "The newest copy off the box is from 3 d ago")]
    [InlineData("attempt=2026-09-15T03:31:00Z\nresult=failed\ndetail=no drive is mounted at /mnt/bs3d-backup\nok=2026-09-14T03:31:00Z\ncopy=scores-20260914-033100.db\n",
        "<strong>failed</strong><small>8 h ago: no drive is mounted at /mnt/bs3d-backup</small>", "The last copy off the box failed 8 h ago: no drive is mounted at /mnt/bs3d-backup. The newest good copy is from 1 d ago.")]
    [InlineData("attempt=2026-09-15T03:31:00Z\nresult=failed\ndetail=<b>rsync</b> failed\nok=\ncopy=\n", "<small>8 h ago: &lt;b&gt;rsync&lt;/b&gt; failed</small>", "There is no good copy yet.")]
    public async Task The_overview_shows_the_copy_off_the_box_and_warns_when_there_is_none(string? record, string stat, string? warning)
    {
        await using AdminPage page = await AdminPage.StartAsync();
        if (record != null)
        {
            string backups = Path.Combine(Path.GetDirectoryName(page.Options.Database)!, "backups");
            Directory.CreateDirectory(backups);
            File.WriteAllText(Path.Combine(backups, "last-offbox"), record);
        }
        await page.LogInAsync();

        string overview = await page.Client.GetStringAsync("/");

        Assert.Contains(stat, overview);
        if (warning == null) Assert.DoesNotContain("<p class=\"warn\">", overview);
        else Assert.Contains(warning, overview);
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
        Assert.Contains("<strong>2</strong><small>1 hidden</small>", overview);
        Assert.Contains("<strong>2</strong><small>0 unfinished · 0 by hidden players</small>", overview);
        Assert.Contains("<tr><td>2026-09-15</td><td class=\"n\">2</td><td class=\"n\">0</td><td class=\"n\">2</td></tr>", overview);
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
        Guid scripted = Guid.Empty;
        BoardKey hostile = new("<script>x.json", "<img src=y>", 1);
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            scripted = AdminPage.AddPlayer(store, c, now, script);
            AdminPage.AddClear(store, c, now, scripted, 100);
            AdminPage.AddClear(store, c, now, scripted, 100, board: hostile);
            AdminPage.AddClear(store, c, now, AdminPage.AddPlayer(store, c, now, reversed), 100);
            store.WriteRefusals(c, new Refusals.Batch(new Dictionary<(string, string), int> { [("2026-09-15", Reasons.BadName)] = 1 },
                [new Refusals.Entry(now, 422, Reasons.BadName, "POST", "/v1/scores", image)], 0), now, TimeSpan.FromDays(7), 100);
        });
        await page.LogInAsync();

        foreach (string path in new[] { "/", "/live", "/charts?range=all", "/boards", "/players", $"/player?id={scripted}",
                     $"/board?file={Uri.EscapeDataString(hostile.File)}&hash={Uri.EscapeDataString(hostile.Hash)}&rules=1" })
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
        Guid ann = Guid.Empty;
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            ann = AdminPage.AddPlayer(store, c, now, "Ann", token);
            AdminPage.AddClear(store, c, now, ann, 100, address);
            // A rate-limited address is named by its hash in the refusal log, as in the journal
            store.WriteRefusals(c, new Refusals.Batch(new Dictionary<(string, string), int> { [(ScoreStore.DayOf(now), Reasons.RateLimited)] = 1 },
                [new Refusals.Entry(now, 429, Reasons.RateLimited, "POST", "/v1/scores", $"address {address}")], 0), now, TimeSpan.FromDays(7), 100);
        });
        page.WriteCeilingTable();
        await page.LogInAsync();

        foreach (string path in new[] { "/", "/live", "/charts?range=all", "/boards", "/players", $"/player?id={ann}", $"/board?file={Api.File}&hash={Api.Hash}&rules={Api.Rules}" })
        {
            string html = await page.Client.GetStringAsync(path);
            Assert.DoesNotContain(token[..8], html);
            Assert.DoesNotContain(address[..8], html);
        }
        Assert.Contains("an address (its hash is not shown)", await page.Client.GetStringAsync("/live"));
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

    [Fact]
    public void The_terminal_gets_no_control_character_but_the_line_break()
    {
        StringWriter terminal = new() { NewLine = "\n" };
        TerminalWriter writer = new(terminal);

        // ESC and BEL (C0), CSI (C1, which some terminals take on its own), a carriage return to write over a line, DEL
        writer.Write("a\u001b]0;owned\u0007b");
        writer.Write('\u009b');
        writer.Write("2J\rc\td\u007f".ToCharArray(), 0, 7);
        writer.WriteLine("e\u001b[31m");
        writer.Write("f\n");
        writer.Write("g\r\nh\r");   // a Windows line break kept, a carriage return on its own not

        Assert.Equal("a?]0;owned?b?2J?c?d?e?[31m\nf\ng\r\nh?", terminal.ToString());
    }
}
