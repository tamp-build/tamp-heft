using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Tamp.Heft.V1.Tests;

/// <summary>
/// <see cref="HeftDevServer"/> against a real SharePoint Framework <c>heft start</c>, using the checked-in
/// <c>tests/spfx-fixture</c> solution (one Form Customizer). Runs only when <c>TAMP_HEFT_SPFX_FIXTURE</c> points at
/// that fixture after <c>npm ci</c>; the <c>spfx-integration</c> workflow does that nightly and on demand. Without
/// the variable the test returns immediately, so the normal test run needs neither npm nor a gigabyte of
/// <c>node_modules</c>.
/// </summary>
public sealed class RealSpfxDevServerTests
{
    private const string FixtureVariable = "TAMP_HEFT_SPFX_FIXTURE";
    private readonly ITestOutputHelper _log;

    public RealSpfxDevServerTests(ITestOutputHelper log) => _log = log;

    [Fact]
    public async Task Real_Heft_Start_Becomes_Ready_Serves_Manifests_And_Cleans_Up()
    {
        var fixture = Environment.GetEnvironmentVariable(FixtureVariable);
        if (string.IsNullOrEmpty(fixture)) return;
        Assert.True(File.Exists(Path.Combine(fixture, "node_modules", "@rushstack", "heft", "package.json")),
            $"{FixtureVariable} is set to '{fixture}', but Heft isn't installed there; run `npm ci` in the fixture first.");

        var componentId = ReadComponentId(fixture);
        var installedHeft = ReadVersion(Path.Combine(fixture, "node_modules", "@rushstack", "heft", "package.json"));
        var statePath = Path.Combine(Path.GetTempPath(), "tamp-heft-it", Guid.NewGuid().ToString("N"), "state.json");
        var output = new StringWriter();
        var log = TextWriter.Synchronized(output);

        var clock = Stopwatch.StartNew();
        HeftDevServer server;
        try
        {
            // --clean, as the template's `npm start` does, so this is a cold first compile.
            server = await HeftDevServer.StartAsync(s => s
                .SetWorkingDirectory(fixture)
                .SetClean()
                .SetStatePath(statePath)
                .SetOutput(log)
                .SetReadyTimeout(TimeSpan.FromMinutes(10)));
        }
        catch
        {
            _log.WriteLine(Text(output, log));
            throw;
        }
        var readyAfter = clock.Elapsed;
        _log.WriteLine($"Cold heft start ready after {readyAfter.TotalSeconds:F1}s (default ReadyTimeout is 5 min).");

        int pid;
        try
        {
            pid = server.ProcessId;
            Assert.Equal(new Uri("https://localhost:4321/"), server.Url);
            Assert.Equal(new Uri("https://localhost:4321/temp/build/manifests.js"), server.ManifestsUrl);
            Assert.Equal(installedHeft, server.HeftVersion);

            // The served manifests carry the Form Customizer, over HTTPS with Heft's own dev certificate.
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                    request.RequestUri is { } u && HeftServeConfig.IsLoopback(u.Host),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var manifests = await client.GetStringAsync(server.ManifestsUrl);
            Assert.Contains(componentId, manifests, StringComparison.OrdinalIgnoreCase);

            var state = HeftDevServerState.TryRead(statePath);
            Assert.NotNull(state);
            Assert.Equal(pid, state!.Pid);
            Assert.True(state.IsLive());
            Assert.Equal(installedHeft, state.HeftVersion);
        }
        finally
        {
            await server.DisposeAsync();
            _log.WriteLine(Text(output, log));
        }

        Assert.False(File.Exists(statePath), "the state file must be removed on shutdown");
        Assert.False(IsRunning(pid), "the Heft process must be gone");
        Assert.False(await HeftDevServer.IsPortInUseAsync("localhost", 4321, CancellationToken.None),
            "nothing may still listen on 4321: webpack's dev server is a child of Heft and must be killed with it");
    }

    private static string ReadComponentId(string fixture)
    {
        var manifest = Directory.GetFiles(Path.Combine(fixture, "src"), "*.manifest.json", SearchOption.AllDirectories).Single();
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static string ReadVersion(string packageJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
        return doc.RootElement.GetProperty("version").GetString()!;
    }

    private static string Text(StringWriter output, TextWriter sync)
    {
        lock (sync) return output.ToString();
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
