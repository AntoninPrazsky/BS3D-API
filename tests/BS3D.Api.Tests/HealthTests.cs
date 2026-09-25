using System.Net;
using System.Net.Http.Json;

namespace BS3D.Api.Tests;

public sealed class HealthTests
{
    [Fact]
    public async Task Health_answers_ok_with_the_contract_version()
    {
        using Api api = new();
        using HttpClient client = api.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HealthAnswer? answer = await response.Content.ReadFromJsonAsync<HealthAnswer>();
        Assert.Equal(new HealthAnswer("ok", Contract: 1, Schema: ScoreStore.SchemaVersion, Boards: 1), answer);
    }

    [Fact]
    public async Task An_unknown_path_is_404()
    {
        using Api api = new();
        using HttpClient client = api.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/v1/nothing-here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
