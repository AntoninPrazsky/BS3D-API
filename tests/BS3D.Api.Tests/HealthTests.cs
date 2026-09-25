using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BS3D.Api.Tests;

public sealed class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_answers_ok_with_the_contract_version()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HealthAnswer? answer = await response.Content.ReadFromJsonAsync<HealthAnswer>();
        Assert.Equal(new HealthAnswer("ok", 1), answer);
    }

    [Fact]
    public async Task An_unknown_path_is_404()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/v1/nothing-here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
