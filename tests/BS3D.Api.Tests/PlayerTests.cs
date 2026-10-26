using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BS3D.Api.Tests;

/// <summary>A player's own two requests: a new nickname, and the removal of everything they sent (BS3D#548's rows).</summary>
public sealed class PlayerTests
{
    [Fact]
    public async Task A_rename_is_answered_in_normalized_form_and_shows_on_the_board()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100, name: "Old"));

        HttpResponseMessage response = await api.ClientFor(player.Token).PutAsJsonAsync($"/v1/players/{player.Id}", new NameBody("  New   Name "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("New Name", (await response.Content.ReadFromJsonAsync<NameBody>())!.Name);
        Assert.Equal("New Name", Assert.Single((await api.Board("all")).Entries).Name);
    }

    [Fact]
    public async Task A_rename_needs_the_player_their_token_and_a_nickname()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        var stranger = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));

        HttpResponseMessage unknown = await api.ClientFor(stranger.Token).PutAsJsonAsync($"/v1/players/{stranger.Id}", new NameBody("Whoever"));
        HttpResponseMessage wrong = await api.ClientFor(stranger.Token).PutAsJsonAsync($"/v1/players/{player.Id}", new NameBody("Whoever"));
        HttpResponseMessage bad = await api.ClientFor(player.Token).PutAsJsonAsync($"/v1/players/{player.Id}", new NameBody("x"));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(Reasons.BadName, await Api.ReasonOf(bad));
    }

    [Fact]
    public async Task A_removal_takes_the_player_and_every_submission_and_answers_404_the_second_time()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        var other = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));
        await api.Accepted(player, Api.Clear(player.Id, 200));
        await api.Accepted(other, Api.Clear(other.Id, 150, name: "Other"));

        HttpClient client = api.ClientFor(player.Token);
        HttpResponseMessage removed = await client.DeleteAsync($"/v1/players/{player.Id}");
        HttpResponseMessage again = await client.DeleteAsync($"/v1/players/{player.Id}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("Other", Assert.Single((await api.Board("all")).Entries).Name);
        Assert.Single(api.Store.Export(api.Store.Open()));
    }

    [Fact]
    public async Task A_removal_takes_the_refusal_log_lines_that_carry_the_players_id()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        var other = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));
        await api.Accepted(other, Api.Clear(other.Id, 150, name: "Other"));
        await api.ClientFor(other.Token).DeleteAsync($"/v1/players/{player.Id}");
        await api.ClientFor(player.Token).DeleteAsync($"/v1/players/{other.Id}");
        Flush(api);

        await api.ClientFor(player.Token).DeleteAsync($"/v1/players/{player.Id}");

        Assert.Equal([$"/v1/players/{other.Id} {other.Id}"], RefusalLines(api));
    }

    [Fact]
    public async Task A_removal_needs_the_players_token()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));

        HttpResponseMessage response = await api.ClientFor(Api.NewPlayer().Token).DeleteAsync($"/v1/players/{player.Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single((await api.Board("all")).Entries);
    }

    [Fact]
    public async Task A_name_changed_in_the_game_follows_with_the_next_clear()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100, name: "Before"));

        SubmissionAnswer answer = await api.Accepted(player, Api.Clear(player.Id, 50, name: "After"));

        Assert.Equal("After", answer.Name);
        Assert.Equal("After", Assert.Single((await api.Board("all")).Entries).Name);
    }

    [Fact]
    public async Task The_admin_cli_hides_renames_and_exports()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));

        StringWriter output = new();
        Assert.Equal(0, AdminCli.Run(["hide-player", player.Id.ToString()], api.Store, new ScoresOptions(), output));
        Assert.Equal(0, AdminCli.Run(["rename", player.Id.ToString(), "Renamed"], api.Store, new ScoresOptions(), output));
        Assert.Equal(1, AdminCli.Run(["hide-player", Guid.NewGuid().ToString()], api.Store, new ScoresOptions(), output));
        Assert.Equal(2, AdminCli.Run(["nonsense"], api.Store, new ScoresOptions(), output));

        StringWriter export = new();
        Assert.Equal(0, AdminCli.Run(["export"], api.Store, new ScoresOptions(), export));
        Assert.Contains("\"name\":\"Renamed\"", export.ToString());
        Assert.Contains("\"hidden\":1", export.ToString());
    }

    [Fact]
    public async Task The_admin_cli_deletes_a_player_and_every_row_that_names_them_only_with_their_nickname_typed_back()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        var other = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100, name: "Gone Soon"));
        await api.Accepted(player, Api.Clear(player.Id, 200, name: "Gone Soon"));
        await api.Accepted(other, Api.Clear(other.Id, 150, name: "Other"));
        // The id in a refusal's detail and its path (a wrong token on a rename), in the detail alone (on a score), and in
        // the path alone, written as the route also takes it (a bad name)
        await api.ClientFor(other.Token).PutAsJsonAsync($"/v1/players/{player.Id}", new NameBody("Whoever"));
        await api.Post((player.Id, other.Token), Api.Clear(player.Id, 300, name: "Gone Soon"));
        await api.ClientFor(player.Token).PutAsJsonAsync($"/v1/players/{player.Id.ToString("N").ToUpperInvariant()}", new NameBody("x"));
        await api.ClientFor(player.Token).PutAsJsonAsync($"/v1/players/{other.Id}", new NameBody("Whoever"));
        await api.Post(other, Api.Clear(other.Id, Api.Ceiling + 1, name: "Other"));
        Flush(api);
        List<string> refusals = RefusalLines(api);
        Assert.Equal(5, refusals.Count);

        StringWriter refused = new();
        Assert.Equal(1, AdminCli.Run(["delete-player", player.Id.ToString(), "Other"], api.Store, new ScoresOptions(), refused));
        Assert.Equal(1, AdminCli.Run(["delete-player", player.Id.ToString(), "gone soon"], api.Store, new ScoresOptions(), refused));
        Assert.Equal(2, AdminCli.Run(["delete-player", player.Id.ToString()], api.Store, new ScoresOptions(), refused));
        Assert.Equal(1, AdminCli.Run(["delete-player", Guid.NewGuid().ToString(), "Gone Soon"], api.Store, new ScoresOptions(), refused));
        Assert.Contains($"'Other' is not the nickname of {player.Id}: nothing deleted", refused.ToString());
        Assert.Contains("no such player", refused.ToString());
        Assert.Equal(3, api.Store.Export(api.Store.Open()).Count());
        Assert.Equal(refusals, RefusalLines(api));

        StringWriter output = new();
        Assert.Equal(0, AdminCli.Run(["delete-player", player.Id.ToString(), "Gone Soon"], api.Store, new ScoresOptions(), output));

        Assert.Equal($"deleted player {player.Id} 'Gone Soon': 2 submission(s), 0 note(s) with their pictures, 3 refusal-log line(s)",
            output.ToString().Trim());
        using (SqliteConnection c = api.Store.Open()) Assert.Null(api.Store.FindPlayer(c, player.Id));
        Assert.Equal("Other", Assert.Single((await api.Board("all")).Entries).Name);
        Assert.Single(api.Store.Export(api.Store.Open()));
        Assert.Equal(refusals.Where(line => !line.Contains(player.Id.ToString()) && !line.Contains(player.Id.ToString("N").ToUpperInvariant())),
            RefusalLines(api));
        Assert.Equal(2, RefusalLines(api).Count);
    }

    [Fact]
    public async Task A_backup_taken_while_the_service_runs_serves_the_same_rows()
    {
        using Api api = new();
        var player = Api.NewPlayer();
        await api.Accepted(player, Api.Clear(player.Id, 100));
        await api.Accepted(player, Api.Clear(player.Id, 250));

        string backup = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"), "backup.db");
        StringWriter output = new();
        Assert.Equal(0, AdminCli.Run(["backup", backup], api.Store, new ScoresOptions(), output));

        ScoreStore restored = new(backup);
        StringWriter count = new();
        Assert.Equal(0, AdminCli.Run(["count"], restored, new ScoresOptions(), count));
        Assert.Equal("players 1 submissions 2", count.ToString().Trim());

        using var c = restored.Open();
        Assert.Equal(250, restored.Page(c, new BoardKey(Api.File, Api.Hash, Api.Rules), null, 10, 0).Single().Score);
        restored.ClearPool();
    }

    private static void Flush(Api api) => api.Services.GetRequiredService<RefusalFlusher>().Flush();

    /// <summary>The refusal log's lines, as "path detail", oldest first.</summary>
    private static List<string> RefusalLines(Api api)
    {
        using SqliteConnection c = api.Store.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, detail FROM refusal_log ORDER BY id";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<string> lines = new();
        while (r.Read()) lines.Add($"{r.GetString(0)} {r.GetString(1)}");
        return lines;
    }
}
