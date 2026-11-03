using System.Diagnostics;
using Xunit;

namespace Tamp.Heft.V1.Tests;

public sealed class HeftDevServerSettingsTests
{
    private static readonly HeftBinaryResolution Node = new()
    {
        Executable = "/usr/bin/node",
        PrefixArguments = new[] { "/w/node_modules/@rushstack/heft/bin/heft" },
        Source = HeftResolutionSource.Explicit,
    };

    private static string[] HeftArgs(HeftDevServerSettings s) => s.ToCommandPlan().Arguments.Skip(1).ToArray();

    [Fact]
    public void Default_Action_Is_Start()
    {
        var s = new HeftDevServerSettings().SetBinary(Node).SetWorkingDirectory("/w");
        var plan = s.ToCommandPlan();
        Assert.Equal("/usr/bin/node", plan.Executable);
        Assert.Equal(new[] { "start" }, HeftArgs(s));
        Assert.Equal("/w", plan.WorkingDirectory);
    }

    [Fact]
    public void Global_Flags_Verbose_Clean_And_Extra_Arguments()
    {
        var s = new HeftDevServerSettings().SetBinary(Node).SetDebug().SetVerbose().SetClean().AddArgument("--emit-stats");
        Assert.Equal(new[] { "--debug", "start", "-v", "--clean", "--emit-stats" }, HeftArgs(s));
    }

    [Fact]
    public void Build_Watch_With_Serve_Is_Allowed()
    {
        var s = new HeftDevServerSettings().SetBinary(Node).SetAction("build-watch").AddArgument("--serve");
        Assert.Equal(new[] { "build-watch", "--serve" }, HeftArgs(s));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("package-solution")]
    [InlineData("test")]
    public void One_Shot_Actions_Are_Refused(string action)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new HeftDevServerSettings().SetBinary(Node).SetAction(action).ToCommandPlan());
        Assert.Contains("not a serve action", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev-deploy-watch", null)]
    [InlineData("start", "deploy-azure-storage")]
    [InlineData("start", "--DEPLOY")]
    public void Anything_That_Deploys_Is_Refused(string action, string? argument)
    {
        var s = new HeftDevServerSettings().SetBinary(Node).SetAction(action);
        if (argument is not null) s.AddArgument(argument);
        var ex = Assert.Throws<InvalidOperationException>(() => s.ToCommandPlan());
        Assert.Contains("never deploys", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_Timings_And_Readiness_Path_Are_Refused()
    {
        Assert.Throws<InvalidOperationException>(() => new HeftDevServerSettings().SetBinary(Node).SetReadyTimeout(TimeSpan.Zero).ToCommandPlan());
        Assert.Throws<InvalidOperationException>(() => new HeftDevServerSettings().SetBinary(Node).SetPollInterval(TimeSpan.Zero).ToCommandPlan());
        Assert.Throws<InvalidOperationException>(() => new HeftDevServerSettings().SetBinary(Node).SetShutdownTimeout(TimeSpan.Zero).ToCommandPlan());
        Assert.Throws<InvalidOperationException>(() => new HeftDevServerSettings().SetBinary(Node).SetReadinessPath("https://evil/").ToCommandPlan());
    }

    [Fact]
    public void Phase_Still_Refuses_Start()
    {
        Assert.Throws<InvalidOperationException>(() => Tamp.Heft.V1.Heft.Phase("start", s => s.SetBinary(Node)));
    }

    [Fact]
    public void ServeConfig_Missing_File_Gives_Rig_Defaults()
    {
        using var p = new TempProject();
        var c = HeftServeConfig.Load(Path.Combine(p.Root, "config", "serve.json"));
        Assert.Equal(new Uri("http://localhost:4321/"), c.BaseUrl);
        Assert.Null(c.SourcePath);
    }

    [Fact]
    public void ServeConfig_Reads_Port_Https_And_Hostname_With_Comments()
    {
        using var p = new TempProject();
        var path = Path.Combine(p.Dir("config"), "serve.json");
        File.WriteAllText(path, """
            {
              // SPFx writes comments in its config files
              "$schema": "https://developer.microsoft.com/json-schemas/spfx-build/spfx-serve.schema.json",
              "port": 4500,
              "https": true,
              "hostname": "dev.localhost",
              "initialPage": "https://{tenantDomain}/_layouts/workbench.aspx",
            }
            """);
        var c = HeftServeConfig.Load(path);
        Assert.Equal(new Uri("https://dev.localhost:4500/"), c.BaseUrl);
        Assert.Equal(4500, c.Port);
        Assert.True(c.Https);
        Assert.Equal(Path.GetFullPath(path), c.SourcePath);
    }

    [Fact]
    public void ServeConfig_Wildcard_IpAddress_Browses_Localhost()
    {
        using var p = new TempProject();
        var path = Path.Combine(p.Dir("config"), "serve.json");
        File.WriteAllText(path, """{ "ipAddress": "0.0.0.0", "https": true }""");
        Assert.Equal(new Uri("https://localhost:4321/"), HeftServeConfig.Load(path).BaseUrl);
    }

    [Theory]
    [InlineData("""{ "hostname": "{tenantDomain}" }""")]
    [InlineData("""{ "port": "4321" }""")]
    [InlineData("""{ "port": 70000 }""")]
    [InlineData("""{ "https": "yes" }""")]
    [InlineData("""{ not json""")]
    public void ServeConfig_Rejects_Unusable_Files(string json)
    {
        using var p = new TempProject();
        var path = Path.Combine(p.Dir("config"), "serve.json");
        File.WriteAllText(path, json);
        Assert.Throws<InvalidOperationException>(() => HeftServeConfig.Load(path));
    }

    [Fact]
    public void Explicit_Url_Wins_And_Gets_A_Trailing_Slash()
    {
        var s = new HeftDevServerSettings().SetWorkingDirectory("/nonexistent").SetServerUrl("https://localhost:5000/app");
        Assert.Equal(new Uri("https://localhost:5000/app/"), s.ResolveServeConfig().BaseUrl);
        Assert.Throws<ArgumentException>(() => HeftServeConfig.FromUrl(new Uri("ftp://localhost/")));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("dev.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.10.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("contoso.sharepoint.com", false)]
    [InlineData("localhost.contoso.com", false)]
    [InlineData("", false)]
    public void Loopback_Detection(string host, bool expected) => Assert.Equal(expected, HeftServeConfig.IsLoopback(host));

    [Fact]
    public void State_Round_Trips_And_Detects_Liveness()
    {
        using var p = new TempProject();
        using var me = Process.GetCurrentProcess();
        var started = new DateTimeOffset(me.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var path = Path.Combine(p.Root, "state", "4321.json");
        var state = new HeftDevServerState
        {
            Url = "https://localhost:4321/",
            ManifestsUrl = "https://localhost:4321/temp/build/manifests.js",
            Port = 4321,
            Pid = me.Id,
            ProcessStartTime = started,
            ReadyAt = DateTimeOffset.UtcNow,
            HeftVersion = "1.3.2",
        };
        state.WriteAtomically(path);

        var read = HeftDevServerState.TryRead(path);
        Assert.NotNull(read);
        Assert.Equal(state, read);
        Assert.True(read!.IsLive());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // no temp file left behind

        // Same PID, different start time: a recycled PID is not the recorded server.
        Assert.False((read with { ProcessStartTime = started.AddMinutes(-5) }).IsLive());
        Assert.False((read with { Pid = int.MaxValue }).IsLive());
    }

    [Fact]
    public void State_TryRead_Ignores_Missing_Garbage_And_Unknown_Schema()
    {
        using var p = new TempProject();
        var path = Path.Combine(p.Root, "s.json");
        Assert.Null(HeftDevServerState.TryRead(path));
        File.WriteAllText(path, "not json");
        Assert.Null(HeftDevServerState.TryRead(path));
        File.WriteAllText(path, """{ "schemaVersion": 99, "url": "x", "manifestsUrl": "x", "port": 1, "pid": 1, "processStartTime": "2026-01-01T00:00:00Z", "readyAt": "2026-01-01T00:00:00Z" }""");
        Assert.Null(HeftDevServerState.TryRead(path));
    }

    [Fact]
    public void Default_State_Path_Is_Per_Port_Under_Tamp_Temp()
    {
        var path = HeftDevServer.DefaultStatePath(4321);
        Assert.EndsWith(Path.Combine("heft-dev-server", "4321.json"), path, StringComparison.Ordinal);
    }
}
