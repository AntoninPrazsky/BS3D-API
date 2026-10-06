using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BS3D.Api.Tests;

/// <summary>
/// The service the tunnel reaches has no admin surface, and never will (issue #5): the admin page is a separate
/// process on the Pi's loopback. Written before that page exists, so the day someone maps an admin route into this
/// process, serves a static admin file from it or wires the antiforgery an admin form needs, a test says so.
/// </summary>
public sealed class AdminSeparationTests
{
    [Fact]
    public void The_public_route_table_is_exactly_the_contract()
    {
        using Api api = new();
        _ = api.CreateClient();

        HashSet<string> routes = api.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => $"{method} {e.RoutePattern.RawText}"))
            .ToHashSet();

        Assert.Equal(new HashSet<string>
        {
            "POST /v1/scores",
            "GET /v1/boards/{file}",
            "GET /v1/boards",
            "PUT /v1/players/{id:guid}",
            "DELETE /v1/players/{id:guid}",
            "POST /v1/notes",
            "GET /v1/health",
            "GET /openapi/{documentName}.json",
        }, routes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("127.0.0.1:5001")]
    public async Task What_an_admin_page_would_answer_is_not_found_here(string? host)
    {
        using Api api = new();
        using HttpClient client = api.CreateClient();
        if (host != null) client.DefaultRequestHeaders.Host = host;

        foreach (string path in new[] { "/", "/admin", "/admin/", "/admin.html", "/index.html", "/login?key=x", "/live" })
        {
            HttpResponseMessage response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} answered {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Files_in_a_web_root_are_not_served()
    {
        // A published folder carries a project's wwwroot beside the binary; here a web root with an admin page in it
        string root = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "admin.html"), "<p>admin</p>");
        File.WriteAllText(Path.Combine(root, "index.html"), "<p>index</p>");
        try
        {
            using Api api = new() { WebRoot = root };
            using HttpClient client = api.CreateClient();

            foreach (string path in new[] { "/admin.html", "/index.html", "/" })
            {
                HttpResponseMessage response = await client.GetAsync(path);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} answered {(int)response.StatusCode}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_public_service_has_no_antiforgery_an_admin_form_would_need()
    {
        using Api api = new();
        _ = api.CreateClient();

        Assert.Null(api.Services.GetService<IAntiforgery>());
    }
}
