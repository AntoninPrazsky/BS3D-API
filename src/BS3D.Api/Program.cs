// The BS3D score service (issue #1): the online per-level boards the game submits every cleared level to.
// Contract v1 is BS3D#542's. See CLAUDE.md for what lives where.

using BS3D.Api;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

bool admin = args.Length > 0 && args[0] == "admin";
WebApplicationBuilder builder = WebApplication.CreateBuilder(admin ? [] : args);

builder.Services.Configure<ScoresOptions>(builder.Configuration.GetSection(ScoresOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new ScoreStore(sp.GetRequiredService<IOptions<ScoresOptions>>().Value.Database));
builder.Services.AddSingleton(sp => Ceilings.Load(
    sp.GetRequiredService<IOptions<ScoresOptions>>().Value.CeilingsDirectory, sp.GetRequiredService<ILogger<Ceilings>>()));
builder.Services.AddSingleton<RateLimits>();
builder.Services.AddSingleton(sp => new AddressHasher(sp.GetRequiredService<IOptions<ScoresOptions>>().Value.AddressSalt));
builder.Services.AddOpenApi();

// Behind Cloudflare Tunnel every request arrives from loopback (issue #2): the client's own address is in
// CF-Connecting-IP, trusted only from the loopback proxies the defaults already name, or every player is one address.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    o.ForwardedForHeaderName = "CF-Connecting-IP";
});

// A submission is a few hundred bytes; nothing this service takes is bigger than 4 KB (issue #2)
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 4096);

WebApplication app = builder.Build();
ScoresOptions options = app.Services.GetRequiredService<IOptions<ScoresOptions>>().Value;
ScoreStore store = app.Services.GetRequiredService<ScoreStore>();

if (admin) return AdminCli.Run(args[1..], store, options, Console.Out);

// An unsalted hash of an address is a register of addresses in all but name; only a developer's machine may run so
if (string.IsNullOrEmpty(options.AddressSalt) && !app.Environment.IsDevelopment())
    throw new InvalidOperationException("Scores:AddressSalt is not set; the service refuses to hash addresses without a salt");

store.EnsureSchema();
Ceilings ceilings = app.Services.GetRequiredService<Ceilings>();
if (ceilings.Count == 0 && options.RequireKnownBoard)
    app.Logger.LogWarning("No ceiling table is loaded and boards must be known: every submission will be refused");

app.UseForwardedHeaders();
app.MapOpenApi();

// What a deployment checks after an update (issue #3) and what the tunnel is smoke-tested with (issue #2)
app.MapGet("/v1/health", () => Results.Ok(new HealthAnswer("ok", Contract: 1, Schema: ScoreStore.SchemaVersion, Boards: ceilings.Count)));
app.MapScoreEndpoints();

app.Run();
return 0;

/// <summary>Visible to the tests' <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
