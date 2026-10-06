using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BS3D.Api.Tests;

/// <summary>
/// <c>POST /v1/notes</c> (#10, BS3D#813): anyone may send a note, with a picture, so everything it takes is checked and
/// every limit holds; and what the owner reads it with, the admin CLI and the Notes tab.
/// </summary>
public sealed class NoteTests
{
    /// <summary>
    /// A file shaped like the game's JPEG: SOI, a JFIF header, a frame header with its size, a scan header, entropy-coded
    /// bytes (none of them 0xFF) and EOI. Nothing decodes it, and nothing here needs to.
    /// </summary>
    public static byte[] Jpeg(int width = 1280, int height = 533, int body = 1000, bool sofFirst = true)
    {
        List<byte> b = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0];
        byte[] sof = [0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1];
        byte[] sos = [0xFF, 0xDA, 0x00, 0x0C, 0x03, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0];
        if (sofFirst) { b.AddRange(sof); b.AddRange(sos); }
        else { b.AddRange(sos); b.AddRange(sof); }
        for (int i = 0; i < body; i++) b.Add((byte)(i % 251));
        b.AddRange([0xFF, 0xD9]);
        return b.ToArray();
    }

    private static readonly JsonElement GameContext = JsonDocument.Parse("""
        {"where":"pause","level":"One.json","scene":"Sea","quality":"High","resolution":"1920x1080","shots":12,"seconds":84.5}
        """).RootElement;

    public static NoteRequest Note(string text = "The second chip does nothing I can see.", Guid? id = null, byte[]? picture = null,
        string version = "v0.3.5", JsonElement? context = null, Guid? player = null, string? name = null, string? screenshot = null) =>
        new(id ?? Guid.NewGuid(), text, version, context ?? GameContext, screenshot ?? (picture != null ? Convert.ToBase64String(picture) : null), player, name);

    private static Task<HttpResponseMessage> Send(Api api, NoteRequest note, string? token = null) =>
        api.ClientFor(token).PostAsJsonAsync("/v1/notes", note);

    private static List<ScoreStore.StoredNote> Stored(Api api)
    {
        using SqliteConnection c = api.Store.Open();
        return api.Store.Notes(c, 0);
    }

    [Fact]
    public async Task An_anonymous_note_is_stored_with_its_context_and_answered()
    {
        using Api api = new();
        NoteRequest note = Note(picture: Jpeg());

        HttpResponseMessage response = await Send(api, note);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(new NoteAnswer(note.NoteId, ScreenshotStored: true), await response.Content.ReadFromJsonAsync<NoteAnswer>());
        ScoreStore.StoredNote stored = Assert.Single(Stored(api));
        Assert.Equal(note.Text, stored.Text);
        Assert.Null(stored.PlayerId);
        Assert.Null(stored.ClaimedName);
        Assert.Equal("v0.3.5", stored.GameVersion);
        Assert.Equal("Sea", JsonDocument.Parse(stored.Context).RootElement.GetProperty("scene").GetString());
        Assert.Equal(Jpeg().Length, stored.PictureBytes);
        // A file beside the database, not a row: the nightly backup copies the database
        Assert.Equal(Jpeg(), File.ReadAllBytes(api.Store.PicturePath(stored.Id)));
        Assert.Equal(Jpeg(), api.Store.NotePicture(stored.Id));
    }

    [Fact]
    public async Task A_note_without_a_picture_says_none_was_stored()
    {
        using Api api = new();

        HttpResponseMessage response = await Send(api, Note());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<NoteAnswer>())!.ScreenshotStored);
        Assert.Equal(0, Assert.Single(Stored(api)).PictureBytes);
    }

    [Fact]
    public async Task A_retried_note_is_answered_as_the_first_time_and_stored_once()
    {
        // The day's last note: a retry of it is still answered, never counted against the day again
        using Api api = new(new() { ["Scores:NotesPerDay"] = "1" });
        NoteRequest note = Note(picture: Jpeg());

        HttpResponseMessage first = await Send(api, note);
        HttpResponseMessage again = await Send(api, note with { Text = "something else" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        Assert.Equal(note.Text, Assert.Single(Stored(api)).Text);
    }

    [Fact]
    public async Task A_known_player_with_their_token_is_linked_and_a_stranger_keeps_a_claim()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100, name: "Ann"));
        var newcomer = Api.NewPlayer();

        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note("linked", player: player.Id, name: "Ann"), player.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note("claimed", player: newcomer.Id, name: "Bob"), newcomer.Token)).StatusCode);

        List<ScoreStore.StoredNote> notes = Stored(api);
        Assert.Equal(player.Id, notes[0].PlayerId);
        Assert.Equal("Ann", notes[0].PlayerName);
        Assert.Null(notes[1].PlayerId);
        Assert.Equal("Bob", notes[1].ClaimedName);
        // A note makes no player: only a score does
        using SqliteConnection c = api.Store.Open();
        Assert.Null(api.Store.FindPlayer(c, newcomer.Id));
    }

    [Fact]
    public async Task A_known_player_needs_their_own_token()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));
        var stranger = Api.NewPlayer();

        HttpResponseMessage wrong = await Send(api, Note(player: player.Id), stranger.Token);
        HttpResponseMessage none = await Send(api, Note(player: player.Id));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(Reasons.WrongToken, await Api.ReasonOf(wrong));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(Reasons.NoToken, await Api.ReasonOf(none));
        Assert.Empty(Stored(api));
    }

    [Fact]
    public async Task A_removed_player_takes_their_linked_notes_with_them()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));
        await Send(api, Note("mine", player: player.Id), player.Token);
        await Send(api, Note("someone else's"));

        HttpResponseMessage removed = await api.ClientFor(player.Token).DeleteAsync($"/v1/players/{player.Id}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal("someone else's", Assert.Single(Stored(api)).Text);
    }

    [Fact]
    public async Task The_text_is_composed_its_lines_made_plain_and_trimmed()
    {
        using Api api = new();

        await Send(api, Note("  Café\r\nline two\rline three\tend  "));

        Assert.Equal("Café\nline two\nline three end", Assert.Single(Stored(api)).Text);
    }

    public static TheoryData<string, NoteRequest> Refused() => new()
    {
        { Reasons.EmptyNote, Note("   \n  ") },
        { Reasons.NoteTooLong, Note(new string('a', 1001)) },
        { Reasons.BadText, Note("a\u001b[31m red") },
        { Reasons.BadText, Note("a\u0000b") },
        { Reasons.BadVersion, Note(version: "1.0") },
        { Reasons.BadName, Note(name: "x") },
        { Reasons.BadContext, Note(context: JsonDocument.Parse("[1,2]").RootElement) },
        { Reasons.ContextTooLarge, Note(context: JsonDocument.Parse($$"""{"pad":"{{new string('x', 4100)}}"}""").RootElement) },
        { Reasons.BadPicture, Note(screenshot: "not base64!") },
        { Reasons.BadPicture, Note(picture: [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]) },
        { Reasons.BadPicture, Note(picture: Jpeg()[..^2]) },
        { Reasons.BadPicture, Note(picture: Jpeg(sofFirst: false)) },
        { Reasons.BadPicture, Note(picture: Jpeg(width: 0)) },
        { Reasons.PictureTooLarge, Note(picture: Jpeg(width: 4096, height: 1700)) },
        { Reasons.PictureTooLarge, Note(picture: Jpeg(body: 400_001)) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_note_that_breaks_a_rule_is_refused_with_its_reason(string reason, NoteRequest note)
    {
        using Api api = new();

        HttpResponseMessage response = await Send(api, note);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(reason, await Api.ReasonOf(response));
        Assert.Empty(Stored(api));
    }

    [Fact]
    public async Task A_lone_surrogate_is_refused_before_the_note_is_read()
    {
        // A serializer replaces one before it is sent, so only a hand-written body carries it, and the JSON reader refuses
        // that with a bare 400 before the endpoint runs (NoteText's own refusal is the second line)
        using Api api = new();
        StringContent body = new($$"""{"noteId":"{{Guid.NewGuid()}}","text":"lone \ud800 surrogate","gameVersion":"v0.3.5"}""",
            System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await api.CreateClient().PostAsync("/v1/notes", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Stored(api));
    }

    [Fact]
    public async Task A_refused_note_text_never_reaches_the_log()
    {
        using Api api = new();

        await Send(api, Note("SECRET-WORDS " + new string('a', 1000)));

        Assert.Contains(api.Log.Lines, line => line.Contains(Reasons.NoteTooLong));
        Assert.DoesNotContain(api.Log.Lines, line => line.Contains("SECRET-WORDS"));
    }

    [Fact]
    public async Task One_address_may_send_three_a_minute()
    {
        using Api api = new(new() { ["Scores:NotesPerMinutePerAddress"] = "3" });

        for (int i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
        HttpResponseMessage fourth = await Send(api, Note());

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.NotNull(fourth.Headers.RetryAfter);
        api.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
    }

    [Fact]
    public async Task One_address_may_send_only_so_many_a_day()
    {
        using Api api = new(new() { ["Scores:NotesPerMinutePerAddress"] = "100", ["Scores:NotesPerDayPerAddress"] = "4" });

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
            api.Clock.Advance(TimeSpan.FromHours(1));
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(api, Note())).StatusCode);

        // The first of the four leaves the window 24 hours after it came
        api.Clock.Advance(TimeSpan.FromHours(20));
        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
    }

    [Fact]
    public async Task Everyone_together_may_send_only_so_many_a_UTC_day()
    {
        using Api api = new(new() { ["Scores:NotesPerDay"] = "2" });

        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
        HttpResponseMessage third = await Send(api, Note());

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        // Until the next UTC midnight: the clock stands at noon
        Assert.Equal(TimeSpan.FromHours(12), third.Headers.RetryAfter!.Delta);
        api.Clock.Advance(TimeSpan.FromHours(12));
        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
    }

    [Fact]
    public async Task Past_the_pictures_cap_a_note_is_kept_without_its_picture()
    {
        int one = Jpeg().Length;
        using Api api = new(new() { ["Scores:NotesMaxStoredPictureBytes"] = (one * 2).ToString() });

        for (int i = 0; i < 2; i++)
            Assert.True((await (await Send(api, Note(picture: Jpeg()))).Content.ReadFromJsonAsync<NoteAnswer>())!.ScreenshotStored);
        HttpResponseMessage third = await Send(api, Note("the third", picture: Jpeg()));

        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        Assert.False((await third.Content.ReadFromJsonAsync<NoteAnswer>())!.ScreenshotStored);
        ScoreStore.StoredNote kept = Stored(api)[2];
        Assert.Equal("the third", kept.Text);
        Assert.Equal(0, kept.PictureBytes);
    }

    [Fact]
    public async Task The_admin_cli_lists_the_notes_writes_their_pictures_and_deletes_one()
    {
        using Api api = new();
        await Send(api, Note("first", picture: Jpeg()));
        await Send(api, Note("second, ěščř"));
        string folder = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"));
        try
        {
            StringWriter output = new();
            Assert.Equal(0, AdminCli.Run(["notes", "--out", folder], api.Store, new ScoresOptions(), output));

            string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            using JsonDocument first = JsonDocument.Parse(lines[0]);
            Assert.Equal("first", first.RootElement.GetProperty("text").GetString());
            Assert.Equal("Sea", first.RootElement.GetProperty("context").GetProperty("scene").GetString());
            Assert.Equal(Jpeg(), File.ReadAllBytes(first.RootElement.GetProperty("picture").GetString()!));
            Assert.Contains("ěščř", lines[1]);
            Assert.False(first.RootElement.TryGetProperty("ipHash", out _));

            StringWriter after = new();
            AdminCli.Run(["notes", "--after", first.RootElement.GetProperty("id").GetInt64().ToString()], api.Store, new ScoresOptions(), after);
            Assert.Single(after.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));

            long firstId = first.RootElement.GetProperty("id").GetInt64();
            Assert.True(File.Exists(api.Store.PicturePath(firstId)));
            Assert.Equal(0, AdminCli.Run(["delete-note", firstId.ToString()], api.Store, new ScoresOptions(), new StringWriter()));
            Assert.Equal("second, ěščř", Assert.Single(Stored(api)).Text);
            Assert.False(File.Exists(api.Store.PicturePath(firstId)));
            Assert.Equal(1, AdminCli.Run(["delete-note", "999"], api.Store, new ScoresOptions(), new StringWriter()));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static long AddNote(ScoreStore store, SqliteConnection c, DateTimeOffset now, string text, Guid? player = null,
        string? claimed = null, byte[]? picture = null, string ipHash = "1PHASH0000000000", string? context = null) =>
        store.InsertNote(c, new ScoreStore.NewNote(Guid.NewGuid(), now, player, null, null, claimed, text, "v0.3.5",
            context ?? GameContext.GetRawText(), ipHash, picture, 1280, 533, "{}"));

    [Fact]
    public async Task The_notes_tab_lists_who_wrote_what_and_a_note_page_shows_it_encoded()
    {
        long anonymous = 0, linked = 0, claimed = 0;
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            Guid ann = AdminPage.AddPlayer(store, c, now, "Ann");
            anonymous = AddNote(store, c, now, "<script>alert(1)</script>\nsecond line", picture: Jpeg());
            linked = AddNote(store, c, now, "from Ann", player: ann);
            claimed = AddNote(store, c, now, "from Bob", claimed: "Bob");
        });
        await page.LogInAsync();

        string list = await page.Client.GetStringAsync("/notes");
        Assert.Contains("anonymous", list);
        Assert.Contains("<bdi>Ann</bdi>", list);
        Assert.Contains("<bdi>Bob</bdi>", list);
        Assert.Contains("unverified", list);
        Assert.Contains("One.json", list);
        Assert.DoesNotContain("<script", list);

        string note = await page.Client.GetStringAsync($"/note?id={anonymous}");
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", note);
        Assert.DoesNotContain("<script", note);
        Assert.Contains($"<img class=\"shot\" src=\"/note.jpg?id={anonymous}\"", note);
        Assert.Contains("<bdi>Sea</bdi>", note);

        HttpResponseMessage picture = await page.Client.GetAsync($"/note.jpg?id={anonymous}");
        Assert.Equal("image/jpeg", picture.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Jpeg(), await picture.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await page.Client.GetAsync($"/note.jpg?id={linked}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await page.Client.GetAsync("/note?id=999")).StatusCode);
        Assert.DoesNotContain("<img", await page.Client.GetStringAsync($"/note?id={claimed}"));
    }

    [Fact]
    public async Task The_notes_tab_needs_the_session_like_every_page()
    {
        long id = 0;
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) => id = AddNote(store, c, now, "x", picture: Jpeg()));

        foreach (string path in new[] { "/notes", $"/note?id={id}", $"/note.jpg?id={id}" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await page.Client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("""{"where":"\ud800"}""")]
    [InlineData("""{"\udc00":1}""")]
    [InlineData("""{"deep":[{"x":"\ud800 lone"}]}""")]
    public async Task A_context_string_that_is_not_text_is_refused(string context)
    {
        // Stored as it came, a lone surrogate's escape would be fine until a reader decoded it: the admin page and the CLI
        using Api api = new();
        StringContent body = new($$"""{"noteId":"{{Guid.NewGuid()}}","text":"hello","gameVersion":"v0.3.5","context":{{context}}}""",
            System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await api.CreateClient().PostAsync("/v1/notes", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(Reasons.BadContext, await Api.ReasonOf(response));
        Assert.Empty(Stored(api));
    }

    [Fact]
    public async Task Past_the_notes_cap_a_note_is_refused_and_a_retry_still_answered()
    {
        using Api api = new(new() { ["Scores:NotesMaxStored"] = "2" });
        NoteRequest first = Note("first");

        await Send(api, first);
        await Send(api, Note("second"));
        HttpResponseMessage third = await Send(api, Note("third"));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(Reasons.NotesFull, await Api.ReasonOf(third));
        api.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await Send(api, first)).StatusCode);
        Assert.Equal(2, Stored(api).Count);
    }

    [Fact]
    public async Task An_IPv6_host_is_limited_by_its_64_and_not_by_each_address()
    {
        using Api api = new(new() { ["Scores:NotesPerMinutePerAddress"] = "2" }) { ClientAddress = IPAddress.Parse("2001:db8:0:1::1") };

        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
        api.ClientAddress = IPAddress.Parse("2001:db8:0:1::2");
        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
        api.ClientAddress = IPAddress.Parse("2001:db8:0:1:ffff::3");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(api, Note())).StatusCode);

        api.ClientAddress = IPAddress.Parse("2001:db8:0:2::1");
        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
        api.ClientAddress = IPAddress.Parse("192.0.2.1");
        Assert.Equal(HttpStatusCode.Created, (await Send(api, Note())).StatusCode);
    }

    [Fact]
    public async Task A_newcomer_note_goes_with_them_when_they_remove_themselves_and_only_theirs()
    {
        using Api api = new();
        var newcomer = Api.NewPlayer();
        var impostor = Api.NewPlayer();
        await Send(api, Note("mine, before my first score", player: newcomer.Id, picture: Jpeg()), newcomer.Token);
        // Someone else's note claiming the newcomer's id, with a token that is not theirs
        await Send(api, Note("not mine", player: newcomer.Id), impostor.Token);
        long mine = Stored(api)[0].Id;

        await api.Accepted(newcomer, Api.Clear(newcomer.Id, 100));
        Assert.Equal(HttpStatusCode.NoContent, (await api.ClientFor(newcomer.Token).DeleteAsync($"/v1/players/{newcomer.Id}")).StatusCode);

        Assert.Equal("not mine", Assert.Single(Stored(api)).Text);
        Assert.False(File.Exists(api.Store.PicturePath(mine)));
    }

    [Fact]
    public async Task A_refused_detail_is_cut_before_it_is_logged()
    {
        using Api api = new();

        await Send(api, Note(version: new string('9', 5000)));

        string line = Assert.Single(api.Log.Lines, l => l.Contains(Reasons.BadVersion));
        Assert.True(line.Length < 400, $"{line.Length} characters");
    }

    [Fact]
    public async Task One_unreadable_context_neither_stops_the_cli_nor_breaks_the_note_page()
    {
        long bad = 0;
        await using AdminPage page = await AdminPage.StartAsync((store, c, now) =>
        {
            bad = AddNote(store, c, now, "bad", context: """{"where":"\ud800"}""");
            AddNote(store, c, now, "good, with \u202E an override");
        });
        await page.LogInAsync();

        HttpResponseMessage response = await page.Client.GetAsync($"/note?id={bad}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("(unreadable)", await response.Content.ReadAsStringAsync());

        StringWriter output = new();
        Assert.Equal(0, AdminCli.Run(["notes"], page.Store, new ScoresOptions(), output));
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("good", lines[1]);
        // Letters as letters, a format character escaped
        Assert.DoesNotContain("\u202E", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_admin_count_line_is_the_one_update_sh_compares()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));
        await Send(api, Note());

        StringWriter output = new();
        AdminCli.Run(["count"], api.Store, new ScoresOptions(), output);

        // update.sh compares this line before and after an update, and the line before is the old release's
        Assert.Equal("players 1 submissions 1", output.ToString().Trim());
    }
}
