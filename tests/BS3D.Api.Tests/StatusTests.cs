using System.Text.Json;
using BS3D.Api.AdminWeb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BS3D.Api.Tests;

/// <summary>The status snapshot for the server's front panel (#13): what is in it, what never is, and how it is written.</summary>
public sealed class StatusTests : IDisposable
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly BoardKey One = new(Api.File, Api.Hash, Api.Rules);
    private static readonly BoardKey Two = new("Two.json", "fedcba9876543210", 1);
    private const string TokenHash = "T0KENHASH0000000000000000000000000000000000000000000000000000000";
    private const string IpHash = "1PHASH0000000000";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"));
    private readonly ScoreStore _store;
    private readonly SqliteConnection _c;

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 15, 12, 30, 0, TimeSpan.Zero));

    public StatusTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "ceilings"));
        File.WriteAllText(Path.Combine(_folder, "ceilings", "BS3D-v9.9.9-ceilings.json"), $$"""
            { "format": "bs3d-ceilings", "version": 1, "rulesVersion": 1, "hashLength": 16, "levels": [
              { "file": "{{Api.File}}", "name": "One", "hash": "{{Api.Hash}}", "rulesVersion": 1, "shots": 30, "ceiling": 50000, "minShots": 3 },
              { "file": "Two.json", "name": "Two", "hash": "fedcba9876543210", "rulesVersion": 1, "shots": 20, "ceiling": 40000, "minShots": 2 } ] }
            """);
        _store = new ScoreStore(Path.Combine(_folder, "scores.db"));
        _store.EnsureSchema();
        _c = _store.Open();
    }

    public void Dispose()
    {
        _c.Dispose();
        _store.ClearPool();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private AdminData Data => new(new AdminWebOptions
    {
        Database = Path.Combine(_folder, "scores.db"),
        CeilingsDirectory = Path.Combine(_folder, "ceilings"),
        Clock = Clock,
        TimeZone = Prague,
    });

    private Ceilings Ceilings => Ceilings.Load(Path.Combine(_folder, "ceilings"), NullLogger.Instance);

    private AdminData.Status Read(bool requireKnownBoard = true) => Data.ReadStatus(Ceilings, requireKnownBoard);

    private string Json() => JsonSerializer.Serialize(Read(), AdminData.StatusJson);

    private Guid Player(string name, DateTimeOffset? at = null) => AdminPage.AddPlayer(_store, _c, at ?? Clock.GetUtcNow(), name, TokenHash);

    private void Clear(Guid player, int score, DateTimeOffset? at = null, BoardKey? board = null, int stars = 3) =>
        AdminPage.AddClear(_store, _c, at ?? Clock.GetUtcNow(), player, score, IpHash, board ?? One, stars);

    /// <summary>A note linked to <paramref name="player"/>, or a newcomer's claiming <paramref name="claimed"/>'s id and a name.</summary>
    private void Note(DateTimeOffset at, Guid? player = null, Guid? claimed = null, string? name = null) =>
        _store.InsertNote(_c, new ScoreStore.NewNote(Guid.NewGuid(), at, player, claimed, claimed == null ? null : TokenHash, name,
            "SECRET NOTE TEXT", "v0.5.0", """{"where":"menu"}""", IpHash, null, 0, 0, "{}"));

    /// <summary>A small server: two players, a hidden one, clears and an unfinished attempt, a refusal, a note, a backup record.</summary>
    private void Fill()
    {
        DateTimeOffset now = Clock.GetUtcNow();
        Guid ann = Player("Ann");
        Clear(ann, 300, now.AddHours(-2));
        Clear(ann, 200, now.AddMinutes(-5), Two);
        Guid bob = Player("Bob");
        Clear(bob, 250, now.AddMinutes(-3));
        Clear(bob, 100, now.AddMinutes(-2), Two, stars: 0);
        Guid hid = Player("Hidden Hilda");
        Clear(hid, 999, now.AddMinutes(-1));
        _store.SetHidden(_c, hid, true);
        _store.WriteRefusals(_c, new Refusals.Batch(new Dictionary<(string, string), int> { [("2026-09-15", "rate-limited")] = 1 },
            [new Refusals.Entry(now.AddMinutes(-20), 429, "rate-limited", "POST", "/v1/scores", "address " + IpHash)], 0),
            now, TimeSpan.FromDays(30), 1000);
        Directory.CreateDirectory(Path.Combine(_folder, "backups"));
        File.WriteAllText(Path.Combine(_folder, "backups", "last-offbox"),
            "attempt=2026-09-15T01:30:00Z\nresult=ok\ndetail=\nok=2026-09-15T01:30:00Z\ncopy=/mnt/x/scores.db\nevery=1\n");
        Note(now.AddMinutes(-10), ann);
    }

    /// <summary>Every key path in the snapshot: an array's items as <c>[]</c>; the refusal counts, keyed by reason, as one leaf.</summary>
    private static SortedSet<string> Keys(JsonElement element, string path = "")
    {
        SortedSet<string> keys = new(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Object && !path.EndsWith("refusals", StringComparison.Ordinal) && path != "refusals_last_hour")
            foreach (JsonProperty p in element.EnumerateObject())
            {
                string child = path.Length == 0 ? p.Name : $"{path}.{p.Name}";
                keys.Add(child);
                keys.UnionWith(Keys(p.Value, child));
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray())
                keys.UnionWith(Keys(item, path + "[]"));
        return keys;
    }

    [Fact]
    public void The_snapshot_has_exactly_the_documented_keys()
    {
        Fill();
        using JsonDocument document = JsonDocument.Parse(Json());

        // A new key is a decision (#13): add it to the issue's list and here, and the panel's reader knows what to expect
        Assert.Equal(new SortedSet<string>(StringComparer.Ordinal)
        {
            "format", "written_at",
            "service", "service.version", "service.schema", "service.expected_schema", "service.db_bytes",
            "totals", "totals.players", "totals.clears", "totals.attempts", "totals.boards", "totals.boards_with_clear", "totals.notes",
            "today", "today.day", "today.zone", "today.clears", "today.attempts", "today.new_players", "today.active_players",
            "today.seconds_played", "today.refusals",
            "refusals_last_hour", "hours", "last_submission_at",
            "latest", "latest[].level", "latest[].player", "latest[].stars", "latest[].score", "latest[].at",
            "top", "top.rules", "top.month", "top.this_month", "top.all_time",
            "top.this_month[].rank", "top.this_month[].name", "top.this_month[].levels", "top.this_month[].stars", "top.this_month[].score",
            "top.all_time[].rank", "top.all_time[].name", "top.all_time[].levels", "top.all_time[].stars", "top.all_time[].score",
            "backups", "backups.offbox", "backups.offbox.result", "backups.offbox.attempt", "backups.offbox.ok", "backups.offbox.every",
            "backups.offsite",
            "play", "play.starts", "play.clears", "play.seconds",
            "recent", "recent[].level", "recent[].player", "recent[].stars", "recent[].score", "recent[].at",
            "recent_players", "recent_players[].player", "recent_players[].last_at", "recent_players[].clears",
            "recent_players[].attempts", "recent_players[].first_day",
            "notes_recent", "notes_recent[].at", "notes_recent[].from_kind", "notes_recent[].from",
        }, Keys(document.RootElement));
    }

    [Fact]
    public void Hidden_players_hashes_and_user_agents_are_never_in_it()
    {
        Fill();
        string json = Json();

        Assert.DoesNotContain("Hilda", json);
        Assert.DoesNotContain("999", json);
        Assert.DoesNotContain(TokenHash, json);
        Assert.DoesNotContain(IpHash, json);
        Assert.DoesNotContain("BS3D/v0.2.1", json);

        AdminData.Status status = Read();
        Assert.Equal((2, 3, 1), (status.Totals.Players, status.Totals.Clears, status.Totals.Attempts));
        // Newest by arrival: Bob's clear came in last, then Ann's two
        Assert.Equal(["One", "Two", "One"], status.Latest.Select(c => c.Level));
        Assert.Equal(["Bob", "Ann", "Ann"], status.Latest.Select(c => c.Player));
        Assert.Equal(2, status.Today.ActivePlayers);
    }

    [Fact]
    public void Recent_lists_clears_and_unfinished_attempts_newest_first_and_no_hidden_player()
    {
        Fill();

        AdminData.Status status = Read();

        // In arrival order: Bob's unfinished attempt came last of the visible rows, Hilda's clear after it is hidden
        Assert.Equal([("Two", "Bob", 0, 100), ("One", "Bob", 3, 250), ("Two", "Ann", 3, 200), ("One", "Ann", 3, 300)],
            status.Recent.Select(r => (r.Level, r.Player, r.Stars, r.Score)));

        Guid carl = Player("Carl");
        for (int i = 0; i < 12; i++) Clear(carl, i, stars: i % 3);
        Assert.Equal(AdminData.StatusRecent, Read().Recent.Count);
        Assert.Equal(11, Read().Recent[0].Score);
    }

    [Fact]
    public void Recent_players_are_one_row_each_by_their_last_submission_with_the_Prague_day_s_counts()
    {
        DateTimeOffset now = Clock.GetUtcNow();    // 14:30 in Prague; its day began at 22:00 UTC on the 14th
        Guid ann = Player("Ann", now.AddDays(-2));
        Clear(ann, 100, now.AddDays(-2));
        Guid carl = Player("Carl", now.AddDays(-1));
        Clear(carl, 100, now.AddDays(-1));
        Guid bob = Player("Bob", new DateTimeOffset(2026, 9, 14, 22, 30, 0, TimeSpan.Zero));
        Clear(bob, 100, new DateTimeOffset(2026, 9, 14, 22, 30, 0, TimeSpan.Zero), Two);    // 00:30 on the 15th in Prague
        Clear(bob, 100, now.AddMinutes(-30));
        Clear(ann, 200, now.AddMinutes(-10), Two);
        Clear(ann, 50, now.AddMinutes(-5), stars: 0);
        Guid hid = Player("Hidden Hilda");
        Clear(hid, 999, now.AddMinutes(-1));
        _store.SetHidden(_c, hid, true);

        IReadOnlyList<AdminData.StatusRecentPlayer> players = Read().RecentPlayers;

        Assert.Equal([("Ann", 1, 1, false), ("Bob", 2, 0, true), ("Carl", 0, 0, false)],
            players.Select(p => (p.Player, p.Clears, p.Attempts, p.FirstDay)));
        Assert.Equal([now.AddMinutes(-5), now.AddMinutes(-30), now.AddDays(-1)], players.Select(p => p.LastAt));

        for (int i = 0; i < 12; i++) Clear(Player($"P{i:00}"), i);
        Assert.Equal(AdminData.StatusRecentPlayers, Read().RecentPlayers.Count);
        Assert.Equal("P11", Read().RecentPlayers[0].Player);
    }

    [Fact]
    public void The_newest_notes_name_their_senders_never_a_hidden_one_and_nothing_else_of_them()
    {
        DateTimeOffset now = Clock.GetUtcNow();
        Guid ann = Player("Ann");
        Guid hid = Player("Hidden Hilda");
        _store.SetHidden(_c, hid, true);
        Note(now.AddMinutes(-50));
        Note(now.AddMinutes(-40), claimed: Guid.NewGuid(), name: "Newbie");
        Note(now.AddMinutes(-30), ann);
        Note(now.AddMinutes(-20), hid);
        Note(now.AddMinutes(-10), claimed: hid, name: "Sneaky");    // a newcomer's claim, the id now a hidden player's
        Note(now.AddMinutes(-60), ann);                               // arrived last, sent earliest: listed by its time

        AdminData.Status status = Read();
        string json = Json();

        Assert.Equal([("hidden", null), ("hidden", null), ("player", "Ann"), ("claimed", "Newbie"), ("anonymous", null), ("player", "Ann")],
            status.NotesRecent.Select(n => (n.FromKind, n.From)));
        Assert.Equal([-10, -20, -30, -40, -50, -60], status.NotesRecent.Select(n => (int)(n.At - now).TotalMinutes));
        Assert.DoesNotContain("Hilda", json);
        Assert.DoesNotContain("Sneaky", json);
        Assert.DoesNotContain("SECRET NOTE TEXT", json);
        Assert.DoesNotContain("menu", json);

        for (int i = 0; i < 12; i++) Note(now.AddSeconds(i));
        Assert.Equal(AdminData.StatusRecentNotes, Read().NotesRecent.Count);
    }

    [Fact]
    public void Top_players_are_the_ranking_s_first_ten()
    {
        DateTimeOffset now = Clock.GetUtcNow();
        for (int i = 1; i <= 12; i++)
        {
            Guid p = Player($"P{i:00}");
            Clear(p, i * 100);
            Clear(p, i * 10, now.AddDays(-30), Two);    // last month: all time only
        }
        Guid hid = Player("Hidden Hilda");
        Clear(hid, 99_999);
        _store.SetHidden(_c, hid, true);

        AdminData.Status status = Read();
        BoardKey[] known = [One, Two];

        Assert.Equal((1, "2026-09"), (status.Top.Rules, status.Top.Month));
        Assert.Equal(_store.Ranking(_c, 1, "2026-09", known, 10).Select(r => (r.Rank, r.Name, r.Levels, r.Stars, r.Score)),
            status.Top.ThisMonth.Select(p => (p.Rank, p.Name, p.Levels, p.Stars, p.Score)));
        Assert.Equal(_store.Ranking(_c, 1, null, known, 10).Select(r => (r.Rank, r.Name, r.Levels, r.Stars, r.Score)),
            status.Top.AllTime.Select(p => (p.Rank, p.Name, p.Levels, p.Stars, p.Score)));
        Assert.Equal(10, status.Top.ThisMonth.Count);
        Assert.Equal(("P12", 1200L), (status.Top.ThisMonth[0].Name, status.Top.ThisMonth[0].Score));
        Assert.Equal(("P12", 1320L), (status.Top.AllTime[0].Name, status.Top.AllTime[0].Score));
    }

    [Fact]
    public void Today_is_the_Prague_day_so_23_30_UTC_counts_towards_the_next_one()
    {
        // 23:45 UTC on the 15th is 01:45 on the 16th in Prague (summer time)
        Clock.SetUtcNow(new DateTimeOffset(2026, 9, 15, 23, 45, 0, TimeSpan.Zero));
        Guid ann = Player("Ann", new DateTimeOffset(2026, 9, 15, 21, 30, 0, TimeSpan.Zero));
        Clear(ann, 100, new DateTimeOffset(2026, 9, 15, 21, 30, 0, TimeSpan.Zero));    // 23:30 on the 15th in Prague
        Clear(ann, 200, new DateTimeOffset(2026, 9, 15, 23, 30, 0, TimeSpan.Zero), Two); // 01:30 on the 16th in Prague

        AdminData.StatusToday today = Read().Today;

        Assert.Equal(("2026-09-16", "Europe/Prague"), (today.Day, today.Zone));
        Assert.Equal((1, 1, 0), (today.Clears, today.ActivePlayers, today.NewPlayers));
        Assert.Equal(60.0, today.SecondsPlayed);
    }

    [Fact]
    public void Hours_end_with_the_current_one()
    {
        DateTimeOffset now = Clock.GetUtcNow();    // 12:30
        Guid ann = Player("Ann");
        Clear(ann, 1, now.AddMinutes(-20));         // 12:10: the current hour
        Clear(ann, 2, now.AddMinutes(-91));         // 10:59
        Clear(ann, 3, now.AddHours(-23).AddMinutes(-25), stars: 0);    // 13:05 yesterday: the first hour
        Clear(ann, 4, now.AddHours(-24).AddMinutes(-20));             // 12:10 yesterday: before it

        int[] hours = Read().Hours;

        Assert.Equal(24, hours.Length);
        Assert.Equal((1, 1, 0, 1), (hours[23], hours[21], hours[22], hours[0]));
        Assert.Equal(3, hours.Sum());
    }

    [Fact]
    public void Refusals_are_counted_over_the_last_hour_and_the_Prague_day()
    {
        DateTimeOffset now = Clock.GetUtcNow();     // 14:30 in Prague
        _store.WriteRefusals(_c, new Refusals.Batch(new Dictionary<(string, string), int>(),
        [
            new Refusals.Entry(now.AddMinutes(-20), 429, "rate-limited", "POST", "/v1/scores", ""),
            new Refusals.Entry(now.AddHours(-3), 400, "bad-version", "POST", "/v1/scores", ""),
            new Refusals.Entry(now.AddHours(-16), 400, "bad-name", "POST", "/v1/scores", ""),    // 22:30 yesterday in Prague
        ], 1), now, TimeSpan.FromDays(30), 1000);

        AdminData.Status status = Read();

        Assert.Equal(new Dictionary<string, int> { ["rate-limited"] = 1 }, status.RefusalsLastHour);
        Assert.Equal(new Dictionary<string, int> { ["bad-version"] = 1, ["rate-limited"] = 1 }, status.Today.Refusals);
    }

    [Fact]
    public void Backup_records_are_read_as_the_overview_reads_them()
    {
        Fill();

        AdminData.StatusBackups backups = Read().Backups;

        Assert.Equal(("ok", 1), (backups.Offbox!.Result, backups.Offbox.Every));
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 1, 30, 0, TimeSpan.Zero), backups.Offbox.Ok);
        Assert.Null(backups.Offsite);
    }

    [Fact]
    public void A_database_at_another_schema_is_reported_and_never_migrated()
    {
        Fill();
        using (SqliteCommand cmd = _c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version = 3";
            cmd.ExecuteNonQuery();
        }

        AdminData.Status status = Read();

        Assert.Equal((3L, ScoreStore.SchemaVersion), (status.Service.Schema, status.Service.ExpectedSchema));
        Assert.Null(status.Play);
        using SqliteCommand check = _c.CreateCommand();
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, (long)check.ExecuteScalar()!);
    }

    [Fact]
    public void The_file_is_written_whole_and_readable_by_its_group_only()
    {
        string file = Path.Combine(_folder, "status.json");
        string small = JsonSerializer.Serialize(new { a = 1 });
        string large = JsonSerializer.Serialize(new { a = new string('x', 200_000) });
        int read = 0;
        using CancellationTokenSource done = new();
        StatusFeed.Write(file, small);
        Task reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(file));    // never half a file
                read++;
            }
        });
        // The writes start once the reader reads: v0.1.30's release run wrote all 300 before the reader's first read
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref read) > 0, TimeSpan.FromSeconds(10)));
        for (int i = 0; i < 300; i++) StatusFeed.Write(file, i % 2 == 0 ? large : small);
        done.Cancel();
        reader.Wait();

        Assert.True(read > 0);
        Assert.False(File.Exists(file + ".tmp"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(file));
    }

    [Fact]
    public void The_file_starts_with_its_json_and_no_byte_order_mark()
    {
        string file = Path.Combine(_folder, "status.json");

        // As the snapshot's relaxed encoder writes a nickname: its letters, not their escapes
        StatusFeed.Write(file, "{\"player\":\"Řehoř\"}");

        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("{\"player\":\"Řehoř\"}\n"), bytes);
    }

    [Fact]
    public void A_file_of_another_mode_is_written_0640_all_the_same()
    {
        string file = Path.Combine(_folder, "status.json");
        File.WriteAllText(file + ".tmp", "left by a run under another umask");
        File.SetUnixFileMode(file + ".tmp", (UnixFileMode)0b110_110_110);

        StatusFeed.Write(file, "{}");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(file));
    }

    [Theory]
    [InlineData("--every", "0")]
    [InlineData("--every", "x")]
    [InlineData("--out")]
    [InlineData("--port", "5001")]
    public async Task The_feed_takes_only_its_own_arguments(params string[] args)
    {
        Assert.Equal(2, await StatusFeed.RunAsync(args));
    }
}
