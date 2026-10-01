using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BS3D.Api.Tests;

/// <summary>
/// The service as the Pi starts it (issue #3): its own process, Production, from a working directory that is not its
/// folder — systemd runs it in /var/lib/bs3d-api, and the admin CLI runs wherever its caller stands. The factory the
/// other tests use sets the content root itself, so only a real process shows what the service does on its own.
/// </summary>
public sealed class ProcessTests
{
    [Fact]
    public async Task Started_from_elsewhere_it_reads_its_own_settings_and_logs_no_request()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        int port = FreePort();

        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "BS3D.Api.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment.Remove("ASPNETCORE_CONTENTROOT");
        start.Environment["Scores__AddressSalt"] = "test-salt";
        start.Environment["Scores__Database"] = Path.Combine(folder, "scores.db");
        start.Environment["Scores__CeilingsDirectory"] = Path.Combine(folder, "ceilings");

        StringBuilder log = new();
        using Process process = new() { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            HttpStatusCode? status = null;
            for (int attempt = 0; attempt < 60 && status == null && !process.HasExited; attempt++)
            {
                try { status = (await client.GetAsync("/v1/health")).StatusCode; }
                catch (HttpRequestException) { await Task.Delay(500); }
            }
            Assert.True(status == HttpStatusCode.OK, $"the service did not answer /v1/health:\n{log}");

            // The console logger writes on its own thread; a request line, were there one, is out well within this
            await Task.Delay(1000);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(folder, recursive: true);
        }

        string text;
        lock (log) text = log.ToString();
        Assert.Contains($"Content root path: {AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}", text);
        Assert.DoesNotContain("Request starting", text);
    }

    /// <summary>
    /// The admin page (issue #5) as its own process, in an environment that asks for more than loopback: an
    /// <c>ASPNETCORE_URLS</c> on every address, a Kestrel endpoint from configuration, request logging turned up. It
    /// listens on 127.0.0.1 and nowhere else, prints its link once and logs it nowhere, and stops when its terminal
    /// goes away (SIGHUP).
    /// </summary>
    [Fact]
    public async Task The_admin_page_listens_on_loopback_only_whatever_the_environment_asks()
    {
        if (!OperatingSystem.IsLinux()) return;
        string folder = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        new ScoreStore(Path.Combine(folder, "scores.db")).EnsureSchema();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        int port = FreePort(), urlsPort = FreePort(), endpointPort = FreePort();

        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in new[] { Path.Combine(AppContext.BaseDirectory, "BS3D.Api.dll"), "admin", "web", "--port", port.ToString() })
            start.ArgumentList.Add(arg);
        start.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{urlsPort}";
        start.Environment["Kestrel__Endpoints__Lan__Url"] = $"http://0.0.0.0:{endpointPort}";
        start.Environment["Logging__LogLevel__Default"] = "Trace";
        start.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Information";
        start.Environment["Scores__Database"] = Path.Combine(folder, "scores.db");

        StringBuilder log = new();
        using Process process = new() { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        string Log() { lock (log) return log.ToString(); }

        try
        {
            string? link = null;
            for (int i = 0; i < 300 && link == null && !process.HasExited; i++)
            {
                link = Log().Split('\n').FirstOrDefault(l => l.Contains("/login?key="))?.Split(": ", 2).Last().Trim();
                if (link == null) await Task.Delay(100);
            }
            Assert.True(link != null, $"no link was printed:\n{Log()}");

            Assert.True(await Connects(IPAddress.Loopback, port), "the page does not answer on 127.0.0.1");
            Assert.False(await Connects(IPAddress.Loopback, urlsPort), "ASPNETCORE_URLS opened a listener");
            Assert.False(await Connects(IPAddress.Loopback, endpointPort), "Kestrel:Endpoints opened a listener");
            foreach (IPAddress lan in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
                Assert.False(await Connects(lan, port), $"the page answers on {lan}");

            using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            HttpResponseMessage login = await client.GetAsync(link);
            Assert.Equal(HttpStatusCode.SeeOther, login.StatusCode);
            HttpRequestMessage overview = new(HttpMethod.Get, $"http://127.0.0.1:{port}/");
            overview.Headers.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(overview)).StatusCode);

            using (Process hup = Process.Start("kill", ["-HUP", process.Id.ToString()])) await hup.WaitForExitAsync();
            using CancellationTokenSource exit = new(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(exit.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(folder, recursive: true);
        }

        string text = Log();
        Assert.Single(text.Split('\n'), line => line.Contains("key="));
        Assert.DoesNotContain("Request starting", text);
        Assert.Contains("The admin page has stopped.", text);
    }

    /// <summary>A second page on the same port, as when one is still open on the Pi and another is started over ssh.</summary>
    [Fact]
    public async Task The_admin_page_says_so_when_its_port_is_taken()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bs3d-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        new ScoreStore(Path.Combine(folder, "scores.db")).EnsureSchema();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using TcpListener taken = new(IPAddress.Loopback, 0);
        taken.Start();
        int port = ((IPEndPoint)taken.LocalEndpoint).Port;

        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { Path.Combine(AppContext.BaseDirectory, "BS3D.Api.dll"), "admin", "web", "--port", port.ToString() })
            start.ArgumentList.Add(arg);
        start.Environment["Scores__Database"] = Path.Combine(folder, "scores.db");

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource exit = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(exit.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(folder, recursive: true);
        }

        string text = await output + await error;
        Assert.Equal(1, process.ExitCode);
        Assert.Contains($"127.0.0.1:{port} is already in use", text);
        Assert.DoesNotContain("Exception", text);
    }

    /// <summary>What the page prints reaches the owner's terminal with no control character in it but the line break.</summary>
    [Fact]
    public async Task The_admin_page_prints_no_control_character_to_its_terminal()
    {
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { Path.Combine(AppContext.BaseDirectory, "BS3D.Api.dll"), "admin", "web" })
            start.ArgumentList.Add(arg);
        // A line the page prints that quotes what it was given: an escape sequence that would retitle the window, and a bell
        start.Environment["Scores__Database"] = Path.Combine(Path.GetTempPath(), "missing\u001b]0;owned\u0007.db");

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using (CancellationTokenSource exit = new(TimeSpan.FromSeconds(30))) await process.WaitForExitAsync(exit.Token);
        string text = await output + await error;

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("missing?]0;owned?.db", text);
        Assert.DoesNotContain(text, c => char.IsControl(c) && c != '\n');
    }

    private static async Task<bool> Connects(IPAddress address, int port)
    {
        using TcpClient tcp = new();
        try
        {
            await tcp.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception e) when (e is SocketException or TimeoutException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
