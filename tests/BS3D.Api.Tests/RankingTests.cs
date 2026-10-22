using Microsoft.Data.Sqlite;

namespace BS3D.Api.Tests;

/// <summary>
/// The ranking across the campaign (#11), which the status snapshot's Top players reads (#13): per level a player's best
/// clear, added up, by the total score and then by who reached it first.
/// </summary>
public sealed class RankingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastMonth = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly BoardKey One = new("One.json", "0123456789abcdef", 1);
    private static readonly BoardKey OneOld = new("One.json", "aaaaaaaaaaaaaaaa", 1);
    private static readonly BoardKey Two = new("Two.json", "fedcba9876543210", 1);
    private static readonly BoardKey Unknown = new("Unknown.json", "0000000000000000", 1);
    private static readonly BoardKey[] Known = [One, OneOld, Two];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"));
    private readonly ScoreStore _store;
    private readonly SqliteConnection _c;

    public RankingTests()
    {
        Directory.CreateDirectory(_folder);
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

    private Guid Player(string name) => AdminPage.AddPlayer(_store, _c, Now, name);

    private void Clear(Guid player, int score, BoardKey board, int stars = 3, DateTimeOffset? at = null) =>
        AdminPage.AddClear(_store, _c, at ?? Now, player, score, board: board, stars: stars);

    private List<ScoreStore.RankingRow> Ranking(string? month = "2026-09", IEnumerable<BoardKey>? known = null, int rules = 1) =>
        _store.Ranking(_c, rules, month, known ?? Known, 100);

    [Fact]
    public void Each_level_s_best_clear_is_added_up_and_the_total_ranks()
    {
        Guid ann = Player("Ann");
        Clear(ann, 100, One, stars: 2);
        Clear(ann, 150, One, stars: 4);     // her best on One; the 100 does not add
        Clear(ann, 200, Two, stars: 3);
        Guid bob = Player("Bob");
        Clear(bob, 300, One);

        List<ScoreStore.RankingRow> rows = Ranking();

        Assert.Equal(["Ann", "Bob"], rows.Select(r => r.Name));
        Assert.Equal((1, 2, 7, 350L), (rows[0].Rank, rows[0].Levels, rows[0].Stars, rows[0].Score));
        Assert.Equal((2, 1, 3, 300L), (rows[1].Rank, rows[1].Levels, rows[1].Stars, rows[1].Score));
    }

    [Fact]
    public void The_versions_of_a_level_count_together_the_best_of_them_for_now()
    {
        Guid ann = Player("Ann");
        Clear(ann, 400, OneOld);
        Clear(ann, 100, One);

        ScoreStore.RankingRow row = Assert.Single(Ranking());

        Assert.Equal((1, 400L), (row.Levels, row.Score));
    }

    [Fact]
    public void An_unfinished_attempt_counts_for_nothing()
    {
        Guid ann = Player("Ann");
        Clear(ann, 100, One);
        Clear(ann, 9_000, Two, stars: 0);
        Guid cat = Player("Cat");
        Clear(cat, 5_000, One, stars: 0);

        ScoreStore.RankingRow row = Assert.Single(Ranking());

        Assert.Equal(("Ann", 1, 100L), (row.Name, row.Levels, row.Score));
    }

    [Fact]
    public void A_hidden_player_is_in_no_count()
    {
        Guid ann = Player("Ann");
        Clear(ann, 100, One);
        Guid hid = Player("Hid");
        Clear(hid, 900, One);
        _store.SetHidden(_c, hid, true);

        Assert.Equal(["Ann"], Ranking().Select(r => r.Name));
    }

    [Fact]
    public void A_board_no_table_names_is_left_out_before_the_sum()
    {
        Guid ann = Player("Ann");
        Clear(ann, 100, One);
        Clear(ann, 500, Unknown);
        Guid bob = Player("Bob");
        Clear(bob, 300, One);

        Assert.Equal([("Bob", 300L), ("Ann", 100L)], Ranking().Select(r => (r.Name, r.Score)));
        // Boards need not be known: every clear counts
        Assert.Equal([("Ann", 600L), ("Bob", 300L)], _store.Ranking(_c, 1, "2026-09", null, 100).Select(r => (r.Name, r.Score)));
    }

    [Fact]
    public void A_month_counts_only_its_own_clears_and_all_time_counts_every_one()
    {
        Guid ann = Player("Ann");
        Clear(ann, 900, One, at: LastMonth);
        Clear(ann, 100, One);
        Guid bob = Player("Bob");
        Clear(bob, 300, One);

        Assert.Equal([("Bob", 300L), ("Ann", 100L)], Ranking().Select(r => (r.Name, r.Score)));
        Assert.Equal([("Ann", 900L), ("Bob", 300L)], Ranking(month: null).Select(r => (r.Name, r.Score)));
    }

    [Fact]
    public void Equal_totals_go_to_the_player_who_reached_theirs_first()
    {
        Guid ann = Player("Ann");
        Guid bob = Player("Bob");
        Clear(bob, 100, One);
        Clear(ann, 100, One);
        Clear(ann, 100, Two);
        Clear(bob, 100, Two);       // Bob reaches 200 last

        Assert.Equal(["Ann", "Bob"], Ranking().Select(r => r.Name));
    }

    [Fact]
    public void A_tie_within_a_level_goes_to_the_earlier_row()
    {
        Guid ann = Player("Ann");
        Guid bob = Player("Bob");
        Clear(ann, 100, One);
        Clear(bob, 100, One);
        Clear(ann, 100, One);       // the same score again: her earlier row is her best, so she reached 100 first

        Assert.Equal(["Ann", "Bob"], Ranking().Select(r => r.Name));
    }

    [Fact]
    public void Only_the_given_rules_count()
    {
        Guid ann = Player("Ann");
        Clear(ann, 100, One);
        Clear(ann, 900, One with { Rules = 2 });

        Assert.Equal(100L, Assert.Single(Ranking()).Score);
        Assert.Equal(900L, Assert.Single(Ranking(known: [One with { Rules = 2 }], rules: 2)).Score);
    }

    [Fact]
    public void Limit_and_offset_page_through_it()
    {
        for (int i = 1; i <= 5; i++) Clear(Player($"P{i}"), i * 100, One);

        Assert.Equal(["P4", "P3"], _store.Ranking(_c, 1, "2026-09", Known, 2, 1).Select(r => r.Name));
        Assert.Equal([2, 3], _store.Ranking(_c, 1, "2026-09", Known, 2, 1).Select(r => r.Rank));
    }
}
