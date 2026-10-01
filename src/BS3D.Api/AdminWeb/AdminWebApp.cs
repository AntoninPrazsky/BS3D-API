using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Console;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// The admin page (issue #5): a separate process of the same binary, <c>BS3D.Api admin web [--port N]</c>, started by
/// hand on the Pi, listening on 127.0.0.1 only, read-only, for as long as the terminal that started it. The service the
/// tunnel reaches has no part of it (<c>AdminSeparationTests</c>).
/// </summary>
public static class AdminWebApp
{
    /// <summary>Headers a proxy adds. Over loopback the page never sees one, unless something routes the world to it.</summary>
    public static readonly string[] ProxyHeaders = ["CF-Ray", "CDN-Loop", "CF-Connecting-IP", "X-Forwarded-For", "Forwarded"];

    private static readonly (string Name, string Value)[] SecurityHeaders =
    [
        ("Content-Security-Policy", "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'"),
        ("X-Frame-Options", "DENY"),
        ("X-Content-Type-Options", "nosniff"),
        ("Referrer-Policy", "same-origin"),
        ("Cache-Control", "no-store"),
        ("Cross-Origin-Opener-Policy", "same-origin"),
        ("Cross-Origin-Resource-Policy", "same-origin"),
    ];

    /// <summary>
    /// The page's application. An empty builder reads no configuration, so neither <c>ASPNETCORE_URLS</c> nor
    /// <c>Kestrel:Endpoints</c> can add an address to the one loopback listener; <paramref name="configure"/> is for
    /// tests, which put a test server in Kestrel's place.
    /// </summary>
    public static WebApplication Build(AdminWebOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // No colours: they are escape sequences, which the terminal's filter would print as '?'
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.ColorBehavior = LoggerColorBehavior.Disabled;
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<AdminSession>();
        builder.Services.AddSingleton<AdminData>();
        builder.Services.AddHostedService<IdleWatch>();
        configure?.Invoke(builder);

        WebApplication app = builder.Build();
        AdminSession session = app.Services.GetRequiredService<AdminSession>();

        // The guard, before anything else the page does with a request
        app.Use(async (http, next) =>
        {
            http.Response.OnStarting(() =>
            {
                foreach ((string name, string value) in SecurityHeaders) http.Response.Headers[name] = value;
                return Task.CompletedTask;
            });

            string[] proxied = ProxyHeaders.Where(h => http.Request.Headers.ContainsKey(h)).ToArray();
            if (proxied.Length > 0)
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                options.Output.WriteLine($"A request reached the admin page carrying {string.Join(", ", proxied)}. A tunnel route may " +
                    $"point a public hostname at 127.0.0.1:{options.Port} (check the Cloudflare dashboard), or something on this machine sent them. Stopping.");
                app.Lifetime.StopApplication();
                return;
            }
            if (!IsOwnHost(http.Request.Host, options.Port))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            string site = http.Request.Headers["Sec-Fetch-Site"].ToString();
            if (site.Length > 0 && site is not ("same-origin" or "none"))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
            {
                http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }
            // Everything but the login wants the session; the live view's own reloads do not count as the owner's activity
            bool reload = http.Request.Path == "/live" && http.Request.Query["auto"] == "1";
            if (http.Request.Path != "/login" && !session.IsSession(http.Request.Cookies[AdminSession.Cookie], activity: !reload))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await http.Response.WriteAsync("Not logged in. Start the admin page again for a new link.\n");
                return;
            }
            await next(http);
        });

        app.MapGet("/login", (HttpContext http) =>
        {
            string? cookie = session.TryLogin(http.Request.Query["key"]);
            if (cookie == null)
                return Results.Text("This link is used, expired or wrong. Start the admin page again for a new link.\n", statusCode: StatusCodes.Status403Forbidden);
            http.Response.Cookies.Append(AdminSession.Cookie, cookie, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
            });
            http.Response.Headers.Location = "/";
            return Results.StatusCode(StatusCodes.Status303SeeOther);
        });
        app.MapGet("/", (AdminData data) => Results.Content(AdminPages.Overview(data.ReadOverview(), options.Clock.GetUtcNow()), "text/html; charset=utf-8"));
        app.MapGet("/live", (AdminData data) => Results.Content(AdminPages.Live(data.ReadLive(100)), "text/html; charset=utf-8"));
        app.MapGet("/boards", (AdminData data) =>
        {
            Ceilings ceilings = data.LoadCeilings();
            return Page(AdminPages.Boards(data.ReadBoards(ceilings), ScoreStore.MonthOf(options.Clock.GetUtcNow()),
                ceilings.Count == 0 ? Path.GetFullPath(options.CeilingsDirectory) : null));
        });
        app.MapGet("/board", (AdminData data, string? file, string? hash, int? rules) =>
            file is { Length: > 0 } && hash is { Length: > 0 } && rules is int r && data.ReadBoard(new BoardKey(file, hash, r), data.LoadCeilings()) is { } board
                ? Page(AdminPages.Board(board))
                : Results.NotFound());
        app.MapGet("/players", (AdminData data, string? sort) =>
        {
            string order = sort is not null && AdminData.PlayerOrders.ContainsKey(sort) ? sort : "name";
            return Page(AdminPages.Players(data.ReadPlayers(order), order));
        });
        app.MapGet("/player", (AdminData data, string? id) =>
            Guid.TryParse(id, out Guid player) && data.ReadPlayer(player) is { } view ? Page(AdminPages.Player(view)) : Results.NotFound());
        app.MapGet("/style.css", () => Results.Content(AdminPages.Css, "text/css; charset=utf-8"));
        return app;
    }

    /// <summary><c>BS3D.Api admin web [--port N]</c>: checks, starts, proves the address, prints the link, waits.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        // The page's lines and the console logger's through one filter, set before anything is printed or built: the
        // logger takes Console.Out when it is made
        Console.SetOut(new TerminalWriter(Console.Out));
        TextWriter output = Console.Out;

        int port = 5001;
        bool valid = args switch
        {
            [] => true,
            ["--port", var p] => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1024 and <= 65535,
            _ => false,
        };
        if (!valid)
        {
            output.WriteLine("usage: BS3D.Api admin web [--port <1024-65535>]");
            return 2;
        }
        if (RouteLocalnet() is { } iface)
        {
            output.WriteLine($"net.ipv4.conf.{iface}.route_localnet is 1: the LAN could reach 127.0.0.1. Not starting.");
            return 1;
        }
        if (OperatingSystem.IsLinux()) _ = prctl(PrSetDumpable, 0, 0, 0, 0);

        ScoresOptions scores = new();
        new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection(ScoresOptions.Section).Bind(scores);
        if (!File.Exists(scores.Database))
        {
            output.WriteLine($"No database at {Path.GetFullPath(scores.Database)} (Scores__Database).");
            return 1;
        }
        // Kestrel would say the same in a stack trace
        if (PortTaken(port))
        {
            output.WriteLine($"127.0.0.1:{port} is already in use, most likely by another admin page: one runs per port. " +
                $"Close it, or start this one with --port {(port < 65535 ? port + 1 : port - 1)}.");
            return 1;
        }
        AdminWebOptions options = new()
        {
            Database = scores.Database,
            CeilingsDirectory = scores.CeilingsDirectory,
            Port = port,
            Output = output,
        };

        WebApplication app = Build(options);
        using PosixSignalRegistration hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop);
        using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        await app.StartAsync();

        string expected = $"http://127.0.0.1:{port}";
        ICollection<string> addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        if (addresses.Count != 1 || addresses.Single() != expected)
        {
            output.WriteLine($"Listening on {string.Join(", ", addresses)} instead of {expected} only. Stopping.");
            await app.StopAsync();
            return 1;
        }

        string key = app.Services.GetRequiredService<AdminSession>().IssueKey();
        output.WriteLine($"BS3D admin page {AdminPages.Version}, read-only, on this machine only.");
        output.WriteLine($"Open within {options.LinkLifetime.TotalMinutes:0} minutes, once: {expected}/login?key={key}");
        output.WriteLine($"It stops after {options.IdleTimeout.TotalMinutes:0} minutes without use, when this window closes, or with Ctrl+C.");
        await app.WaitForShutdownAsync();
        output.WriteLine("The admin page has stopped.");
        return 0;

        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            app.Lifetime.StopApplication();
        }
    }

    private static IResult Page(string html) => Results.Content(html, "text/html; charset=utf-8");

    private static bool IsOwnHost(HostString host, int port) =>
        host.Port == port && host.Host is { } name
        && (name.Equals("127.0.0.1", StringComparison.Ordinal) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether something holds 127.0.0.1:<paramref name="port"/>: Linux refuses a bind where a socket listens, whatever SO_REUSEADDR says.</summary>
    private static bool PortTaken(int port)
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return false;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
    }

    /// <summary>The first interface on which Linux routes packets for 127.0.0.0/8 from outside, or null.</summary>
    private static string? RouteLocalnet()
    {
        const string conf = "/proc/sys/net/ipv4/conf";
        if (!Directory.Exists(conf)) return null;
        foreach (string iface in Directory.EnumerateDirectories(conf))
        {
            string setting = Path.Combine(iface, "route_localnet");
            if (File.Exists(setting) && File.ReadAllText(setting).Trim() == "1") return Path.GetFileName(iface);
        }
        return null;
    }

    // Both processes run as bs3d-api and the kernel has no Yama: without this the public service could ptrace the page
    private const int PrSetDumpable = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    /// <summary>
    /// Stops the page after <see cref="AdminWebOptions.IdleTimeout"/> without the owner's activity, and when the link has
    /// expired with nobody logged in, since nothing can log in after that.
    /// </summary>
    private sealed class IdleWatch(AdminSession session, AdminWebOptions options, IHostApplicationLifetime lifetime) : BackgroundService
    {
        private readonly DateTimeOffset _started = options.Clock.GetUtcNow();

        // Made here, not in ExecuteAsync, which .NET 10 starts on the thread pool: a timer made there could start after
        // the clock has already moved, and would then wait from a later time than the page started at
        private readonly PeriodicTimer _timer = new(TimeSpan.FromMinutes(1), options.Clock);

        public override void Dispose()
        {
            _timer.Dispose();
            base.Dispose();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(stoppingToken))
                {
                    TimeSpan? idle = session.Idle();
                    if (idle == null && options.Clock.GetUtcNow() - _started > options.LinkLifetime)
                    {
                        options.Output.WriteLine("The link expired unused. Stopping.");
                        lifetime.StopApplication();
                        return;
                    }
                    if (idle > options.IdleTimeout)
                    {
                        options.Output.WriteLine($"{options.IdleTimeout.TotalMinutes:0} minutes without use. Stopping.");
                        lifetime.StopApplication();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }
}
