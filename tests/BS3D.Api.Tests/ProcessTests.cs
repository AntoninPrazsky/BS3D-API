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

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
