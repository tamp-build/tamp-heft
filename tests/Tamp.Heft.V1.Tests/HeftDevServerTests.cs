using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Tamp.Heft.V1.Tests;

/// <summary>
/// End-to-end lifecycle tests against a fake <c>@rushstack/heft</c> whose bin script is a small Node dev
/// server. They need <c>node</c> on PATH (as any Heft project does) and are skipped silently without it.
/// </summary>
public sealed class HeftDevServerTests
{
    // A stand-in for `heft start`: serves temp/build/manifests.js (404 until FAKE_READY_DELAY_MS has passed),
    // over HTTPS when given a cert, and optionally fails, never listens, spawns a child, or exits later.
    private const string FakeHeft = """
        #!/usr/bin/env node
        const fs = require('fs'), http = require('http'), https = require('https'), cp = require('child_process');
        const env = process.env;
        if (env.FAKE_ARGS_FILE) fs.writeFileSync(env.FAKE_ARGS_FILE, JSON.stringify(process.argv.slice(2)));
        console.log('fake heft ' + process.argv.slice(2).join(' '));
        if (env.FAKE_EXIT_CODE) { console.error('fake compile failure'); process.exit(+env.FAKE_EXIT_CODE); }
        if (env.FAKE_CHILD_PID_FILE) {
          const child = cp.spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
          fs.writeFileSync(env.FAKE_CHILD_PID_FILE, String(child.pid));
        }
        if (env.FAKE_EXIT_AFTER_MS) setTimeout(() => { console.error('fake crash'); process.exit(7); }, +env.FAKE_EXIT_AFTER_MS);
        const started = Date.now(), delay = +(env.FAKE_READY_DELAY_MS || 0);
        const handler = (req, res) => {
          if (req.url === '/temp/build/manifests.js' && Date.now() - started >= delay) {
            res.writeHead(200, { 'content-type': 'text/javascript' }); res.end('// manifests');
          } else { res.writeHead(404); res.end(); }
        };
        const server = env.FAKE_CERT
          ? https.createServer({ cert: fs.readFileSync(env.FAKE_CERT), key: fs.readFileSync(env.FAKE_KEY) }, handler)
          : http.createServer(handler);
        if (env.FAKE_NEVER_LISTEN) setInterval(() => {}, 1000);
        else server.listen(+env.FAKE_PORT, () => console.log('listening on ' + env.FAKE_PORT));
        """;

    private static readonly bool HaveNode = HeftBinaryResolver.FindOnPath("node") is not null;

    private sealed class Fixture : IDisposable
    {
        public TempProject Project { get; } = new();
        public string Web { get; }
        public int Port { get; } = FreePort();
        public string StatePath { get; }
        public StringWriter Output { get; } = new();

        public Fixture(bool https = true)
        {
            Web = Project.Dir("web");
            var script = Project.InstallHeft(Web);
            File.WriteAllText(script, FakeHeft.Replace("\r\n", "\n", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(Project.Dir("web", "config"), "serve.json"),
                $$"""{ "port": {{Port}}, "https": {{(https ? "true" : "false")}}, "hostname": "localhost" }""");
            StatePath = Path.Combine(Project.Root, ".tamp", "temp", "heft-dev-server", $"{Port}.json");
        }

        public HeftDevServerSettings Settings(bool https = true)
        {
            var s = new HeftDevServerSettings()
                .SetWorkingDirectory(Web)
                .SetStatePath(StatePath)
                .SetOutput(Output)
                .SetPollInterval(TimeSpan.FromMilliseconds(100))
                .SetReadyTimeout(TimeSpan.FromSeconds(30))
                .SetEnvironmentVariable("FAKE_PORT", Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (https)
            {
                var (cert, key) = WriteSelfSignedCert(Project.Root);
                s.SetEnvironmentVariable("FAKE_CERT", cert).SetEnvironmentVariable("FAKE_KEY", key);
            }
            return s;
        }

        public void Dispose()
        {
            Output.Dispose();
            Project.Dispose();
        }
    }

    [Fact]
    public async Task Starts_Over_Https_Waits_For_Manifests_Reports_And_Cleans_Up()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        var argsFile = Path.Combine(f.Project.Root, "args.json");
        var childPidFile = Path.Combine(f.Project.Root, "child.pid");
        var settings = f.Settings()
            .SetEnvironmentVariable("FAKE_READY_DELAY_MS", "1500")
            .SetEnvironmentVariable("FAKE_ARGS_FILE", argsFile)
            .SetEnvironmentVariable("FAKE_CHILD_PID_FILE", childPidFile);

        var clock = Stopwatch.StartNew();
        var server = await HeftDevServer.StartAsync(settings);
        int childPid;
        try
        {
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1.4), "ready must wait for the manifests file, not just the open port");
            Assert.Equal(new Uri($"https://localhost:{f.Port}/"), server.Url);
            Assert.Equal(f.Port, server.Port);
            Assert.Equal(new Uri($"https://localhost:{f.Port}/temp/build/manifests.js"), server.ManifestsUrl);
            Assert.Equal("1.3.2", server.HeftVersion);
            Assert.False(server.HasExited);
            Assert.Equal("[\"start\"]", File.ReadAllText(argsFile));

            var state = HeftDevServerState.TryRead(f.StatePath);
            Assert.NotNull(state);
            Assert.Equal(server.Url.ToString(), state!.Url);
            Assert.Equal(server.ManifestsUrl.ToString(), state.ManifestsUrl);
            Assert.Equal(f.Port, state.Port);
            Assert.Equal(server.ProcessId, state.Pid);
            Assert.Equal("1.3.2", state.HeftVersion);
            Assert.True(state.IsLive());
            Assert.DoesNotContain("debugManifestsFile", File.ReadAllText(f.StatePath), StringComparison.Ordinal);

            var log = f.Output.ToString();
            Assert.Contains($"Heft dev server ready: https://localhost:{f.Port}/", log, StringComparison.Ordinal);
            Assert.Contains("fake heft start", log, StringComparison.Ordinal);

            childPid = int.Parse(File.ReadAllText(childPidFile), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(IsRunning(childPid));
        }
        finally
        {
            await server.DisposeAsync();
        }

        Assert.True(server.IsStopping);
        Assert.False(File.Exists(f.StatePath));
        Assert.False(IsRunning(server.ProcessId));
        Assert.True(WaitUntil(() => !IsRunning(childPid), TimeSpan.FromSeconds(5)), "the whole process tree must be killed");
        Assert.Contains("Heft dev server stopped.", f.Output.ToString(), StringComparison.Ordinal);
        await server.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task Concurrent_Stop_Waits_For_The_First_To_Finish()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        var childPidFile = Path.Combine(f.Project.Root, "child.pid");
        var server = await HeftDevServer.StartAsync(f.Settings().SetEnvironmentVariable("FAKE_CHILD_PID_FILE", childPidFile));
        var childPid = int.Parse(File.ReadAllText(childPidFile), System.Globalization.CultureInfo.InvariantCulture);

        // What Ctrl+C does: the signal thread stops the server while the target's own dispose runs. Whichever
        // call returns, the tree must already be dead, or the build can exit and leave the server behind.
        using var go = new ManualResetEventSlim();
        var other = Task.Run(() => { go.Wait(); server.Dispose(); });
        go.Set();
        server.Dispose();
        Assert.False(IsRunning(server.ProcessId));
        Assert.False(File.Exists(f.StatePath));
        await other;
        Assert.False(IsRunning(server.ProcessId));
        Assert.True(WaitUntil(() => !IsRunning(childPid), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Heft_Exiting_Before_Ready_Fails_With_Its_Output()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HeftDevServer.StartAsync(f.Settings().SetEnvironmentVariable("FAKE_EXIT_CODE", "3")));
        Assert.Contains("code 3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fake compile failure", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(f.StatePath));
    }

    [Fact]
    public async Task Never_Ready_Times_Out_And_Kills_The_Server()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => HeftDevServer.StartAsync(f.Settings()
            .SetEnvironmentVariable("FAKE_NEVER_LISTEN", "1")
            .SetReadyTimeout(TimeSpan.FromSeconds(2))));
        Assert.Contains("manifests.js", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Heft dev server stopped.", f.Output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(f.StatePath));
    }

    [Fact]
    public async Task Open_Port_Without_Manifests_Is_Not_Ready()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        // Listening but always 404: a port probe alone would wrongly call this ready.
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => HeftDevServer.StartAsync(f.Settings()
            .SetEnvironmentVariable("FAKE_READY_DELAY_MS", "600000")
            .SetReadyTimeout(TimeSpan.FromSeconds(2))));
        Assert.Contains("HTTP 404", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_Http_Serve_Config_Is_Supported()
    {
        if (!HaveNode) return;
        using var f = new Fixture(https: false);
        await using var server = await HeftDevServer.StartAsync(f.Settings(https: false));
        Assert.Equal(Uri.UriSchemeHttp, server.Url.Scheme);
    }

    [Fact]
    public async Task Port_Already_In_Use_Is_Refused_Before_Starting()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        var listener = new TcpListener(IPAddress.Loopback, f.Port);
        listener.Start();
        try
        {
            var argsFile = Path.Combine(f.Project.Root, "args.json");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HeftDevServer.StartAsync(f.Settings().SetEnvironmentVariable("FAKE_ARGS_FILE", argsFile)));
            Assert.Contains("already in use", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(argsFile), "Heft must not have been started");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Live_State_From_An_Earlier_Run_Is_Refused()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        using var me = Process.GetCurrentProcess();
        LiveLikeState(f, me.Id, new DateTimeOffset(me.StartTime.ToUniversalTime(), TimeSpan.Zero)).WriteAtomically(f.StatePath);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => HeftDevServer.StartAsync(f.Settings()));
        Assert.Contains("still running", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(f.StatePath), "a live server's state file must be left alone");
    }

    [Fact]
    public async Task Stale_State_Is_Replaced()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        using var me = Process.GetCurrentProcess();
        // Our PID but a different start time: what a recycled PID looks like.
        LiveLikeState(f, me.Id, DateTimeOffset.UtcNow.AddDays(-1)).WriteAtomically(f.StatePath);

        await using var server = await HeftDevServer.StartAsync(f.Settings());
        Assert.Contains("Removing stale", f.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(server.ProcessId, HeftDevServerState.TryRead(f.StatePath)!.Pid);
    }

    [Fact]
    public async Task RunUntilCancelled_Returns_On_Cancel_And_Cleans_Up()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        var run = HeftDevServer.RunUntilCancelledAsync(f.Settings(), cts.Token);

        Assert.True(WaitUntil(() => File.Exists(f.StatePath) || run.IsCompleted, TimeSpan.FromSeconds(30)));
        Assert.False(run.IsCompleted, f.Output.ToString());
        var pid = HeftDevServerState.TryRead(f.StatePath)!.Pid;
        Assert.Contains("Press Ctrl+C", f.Output.ToString(), StringComparison.Ordinal);

        cts.Cancel();
        await run;
        Assert.False(File.Exists(f.StatePath));
        Assert.False(IsRunning(pid));
    }

    [Fact]
    public async Task RunUntilCancelled_Fails_When_Heft_Exits_On_Its_Own()
    {
        if (!HaveNode) return;
        using var f = new Fixture();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HeftDevServer.RunUntilCancelledAsync(f.Settings().SetEnvironmentVariable("FAKE_EXIT_AFTER_MS", "2000")));
        Assert.Contains("exited unexpectedly with code 7", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fake crash", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(f.StatePath));
    }

    private static HeftDevServerState LiveLikeState(Fixture f, int pid, DateTimeOffset started) => new()
    {
        Url = $"https://localhost:{f.Port}/",
        ManifestsUrl = $"https://localhost:{f.Port}/temp/build/manifests.js",
        Port = f.Port,
        Pid = pid,
        ProcessStartTime = started,
        ReadyAt = DateTimeOffset.UtcNow,
    };

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private static (string Cert, string Key) WriteSelfSignedCert(string dir)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certPath = Path.Combine(dir, "dev-cert.pem");
        var keyPath = Path.Combine(dir, "dev-key.pem");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath);
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

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }
}
