// The BS3D score service (issue #1): the online per-level boards the game submits every cleared level to.
// Contract v1 is BS3D#542's; this is the skeleton — health only, until issue #1 adds the scores, the boards and
// the player endpoints over SQLite.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();

// What a deployment checks after an update (issue #3) and what the tunnel is smoke-tested with (issue #2).
// The contract version is in the answer so a client can tell which paths it may ask for.
app.MapGet("/v1/health", () => Results.Ok(new HealthAnswer("ok", Contract: 1)));

app.Run();

/// <summary>The answer of <c>GET /v1/health</c>.</summary>
public sealed record HealthAnswer(string Status, int Contract);

/// <summary>Visible to the tests' <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
