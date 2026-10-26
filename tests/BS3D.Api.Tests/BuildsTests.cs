using System.Net.Http.Json;

namespace BS3D.Api.Tests;

/// <summary>
/// A release of the game never shows a score a dev build sent, and a dev build shows both (#17, the owner's rule of
/// 2026-10-26). A board's GET reads the build from the <c>User-Agent</c> the game sends, <c>BS3D/&lt;version&gt;</c>; a
/// submission's answer from its own <c>gameVersion</c>.
/// </summary>
public sealed class BuildsTests
{
    private const string Release = "v0.5.0";
    private const string Dev = "dev-1234abc";
    private const string Two = "Two.json";
    private const string TwoHash = "fedcba9876543210";

    private static async Task<BoardPage> Board(Api api, string? agent, string period = "all")
    {
        HttpClient client = api.CreateClient();
        if (agent != null) client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", agent);
        return (await client.GetFromJsonAsync<BoardPage>($"/v1/boards/{Api.File}?hash={Api.Hash}&rules={Api.Rules}&period={period}"))!;
    }

    private static async Task<BoardsSummary> Summary(Api api, string? agent, Guid? player = null)
    {
        HttpClient client = api.CreateClient();
        if (agent != null) client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", agent);
        return (await client.GetFromJsonAsync<BoardsSummary>("/v1/boards?period=all" + (player != null ? $"&player={player}" : "")))!;
    }

    private static string[] Names(BoardPage page) => page.Entries.Select(e => e.Name).ToArray();

    [Theory]
    [InlineData("BS3D/v0.5.0", new[] { "Released" })]
    [InlineData("BS3D/v0.2.0-beta", new[] { "Released" })]
    [InlineData("BS3D/dev-1234abc", new[] { "Developer", "Released" })]
    [InlineData("BS3D/dev", new[] { "Developer", "Released" })]
    [InlineData(null, new[] { "Released" })]
    [InlineData("Mozilla/5.0 (X11; Linux aarch64)", new[] { "Released" })]
    [InlineData("BS3D-Local-Score-Demo/1.0", new[] { "Released" })]
    [InlineData("BS3D/dev-NOTHEX", new[] { "Released" })]
    [InlineData("BS3D/devil", new[] { "Released" })]
    public async Task A_board_shows_dev_scores_to_a_dev_build_only(string? agent, string[] names)
    {
        using Api api = new();
        var released = Api.NewPlayer();
        var developer = Api.NewPlayer();
        await api.Accepted(released, Api.Clear(released.Id, 100, name: "Released", version: Release));
        await api.Accepted(developer, Api.Clear(developer.Id, 200, name: "Developer", version: Dev));

        BoardPage page = await Board(api, agent);

        Assert.Equal(names, Names(page));
        Assert.Equal(names.Length, page.Total);
    }

    [Fact]
    public async Task A_players_place_on_a_board_is_among_what_the_asking_build_shows()
    {
        using Api api = new();
        var released = Api.NewPlayer();
        var developer = Api.NewPlayer();
        await api.Accepted(developer, Api.Clear(developer.Id, 200, name: "Developer", version: Dev));
        await api.Accepted(released, Api.Clear(released.Id, 100, name: "Released", version: Release));

        HttpClient asRelease = api.CreateClient();
        asRelease.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BS3D/" + Release);
        HttpClient asDev = api.CreateClient();
        asDev.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BS3D/" + Dev);
        string query = $"/v1/boards/{Api.File}?hash={Api.Hash}&rules={Api.Rules}&period=all";

        Assert.Equal(1, (await asRelease.GetFromJsonAsync<BoardPage>($"{query}&player={released.Id}"))!.Me?.Rank);
        Assert.Null((await asRelease.GetFromJsonAsync<BoardPage>($"{query}&player={developer.Id}"))!.Me);
        Assert.Equal(2, (await asDev.GetFromJsonAsync<BoardPage>($"{query}&player={released.Id}"))!.Me?.Rank);
    }

    [Fact]
    public async Task A_player_whose_only_clear_came_from_a_dev_build_shows_their_unfinished_attempt_to_a_release()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 300, name: "Both", version: Dev));
        await api.Accepted(player, Api.Clear(player.Id, 50, name: "Both", stars: 0, shots: 5, version: Release));

        BoardEntry released = Assert.Single((await Board(api, "BS3D/" + Release)).Entries);
        BoardEntry dev = Assert.Single((await Board(api, "BS3D/" + Dev)).Entries);

        Assert.Equal((50, 0), (released.Score, released.Stars));
        Assert.Equal((300, 3), (dev.Score, dev.Stars));
    }

    [Fact]
    public async Task The_summary_leaves_out_a_board_only_dev_builds_played_and_tops_the_rest_with_a_release_clear()
    {
        using Api api = new();
        api.AddTable("BS3D-v9.9.10-ceilings.json", $$"""
            { "format": "bs3d-ceilings", "version": 1, "rulesVersion": 1, "hashLength": 16, "levels": [
              { "file": "{{Two}}", "name": "Two", "hash": "{{TwoHash}}", "rulesVersion": 1, "shots": 30, "ceiling": 50000, "minShots": 3 } ] }
            """);
        var released = Api.NewPlayer();
        var developer = Api.NewPlayer();
        await api.Accepted(released, Api.Clear(released.Id, 100, name: "Released", version: Release));
        await api.Accepted(developer, Api.Clear(developer.Id, 200, name: "Developer", version: Dev));
        await api.Accepted(developer, Api.Clear(developer.Id, 300, name: "Developer", version: Dev, file: Two, hash: TwoHash));

        BoardsSummary asRelease = await Summary(api, "BS3D/" + Release, developer.Id);
        BoardsSummary asDev = await Summary(api, "BS3D/" + Dev, developer.Id);

        BoardSummary one = Assert.Single(asRelease.Boards);
        Assert.Equal((Api.File, "Released", 1), (one.File, one.Top.Name, one.Total));
        Assert.Null(one.Me);
        Assert.Equal(new[] { (Api.File, "Developer", 2), (Two, "Developer", 1) },
            asDev.Boards.Select(b => (b.File, b.Top.Name, b.Total)).ToArray());
    }

    [Fact]
    public async Task A_release_clear_is_answered_with_its_place_among_releases_and_a_dev_clear_among_all()
    {
        using Api api = new();
        var developer = Api.NewPlayer();
        var released = Api.NewPlayer();
        var other = Api.NewPlayer();
        await api.Accepted(developer, Api.Clear(developer.Id, 200, name: "Developer", version: Dev));

        SubmissionAnswer release = await api.Accepted(released, Api.Clear(released.Id, 100, name: "Released", version: Release));
        SubmissionAnswer dev = await api.Accepted(other, Api.Clear(other.Id, 150, name: "Other", version: Dev));

        Assert.Equal(new BoardRank(1, 1), release.Month);
        Assert.Equal(new BoardRank(1, 1), release.AllTime);
        Assert.Equal(new BoardRank(2, 3), dev.AllTime);
    }

    [Fact]
    public async Task A_personal_best_is_decided_among_what_the_clears_build_shows()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 300, version: Dev));

        Assert.True((await api.Accepted(player, Api.Clear(player.Id, 100, version: Release))).PersonalBest);
        Assert.False((await api.Accepted(player, Api.Clear(player.Id, 200, version: Dev))).PersonalBest);
    }

    [Fact]
    public async Task The_ranking_across_levels_counts_only_release_clears_for_a_release()
    {
        using Api api = new();
        var released = Api.NewPlayer();
        var developer = Api.NewPlayer();
        await api.Accepted(released, Api.Clear(released.Id, 100, name: "Released", version: Release));
        await api.Accepted(developer, Api.Clear(developer.Id, 200, name: "Developer", version: Dev));

        using var c = api.Store.Open();
        Assert.Equal(new[] { "Released" },
            api.Store.Ranking(c, Api.Rules, null, null, 10, builds: ScoreStore.Builds.Releases).Select(r => r.Name).ToArray());
        Assert.Equal(new[] { "Developer", "Released" }, api.Store.Ranking(c, Api.Rules, null, null, 10).Select(r => r.Name).ToArray());
    }

    [Theory]
    [InlineData("v0.5.0", ScoreStore.Builds.Releases)]
    [InlineData("v0.2.0-beta", ScoreStore.Builds.Releases)]
    [InlineData("dev", ScoreStore.Builds.All)]
    [InlineData("dev-1234abc", ScoreStore.Builds.All)]
    public void A_tag_is_a_release_and_dev_a_dev_build(string version, ScoreStore.Builds seen) =>
        Assert.Equal(seen, ScoreStore.SeenBy(version));
}
