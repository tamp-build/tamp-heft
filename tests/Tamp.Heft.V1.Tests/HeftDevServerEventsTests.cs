using System.Diagnostics;
using System.Runtime.InteropServices;
using Tamp;
using Xunit;

namespace Tamp.Heft.V1.Tests;

/// <summary>
/// #3 / tamp-build/tamp#125 part 3: inside a real <c>tamp</c> run (<see cref="TampBuild.Execute{T}"/> with
/// <c>--events</c>), <see cref="HeftDevServer"/> emits <c>tool.invoked</c>, <c>service.ready</c>,
/// <c>service.stopped</c> with its reason and <c>tool.exited</c> on one span, plus <c>artifact.produced</c> for the
/// state file; and the state file is removed however the server stops. Uses the fake Node Heft from
/// <see cref="HeftDevServerTests"/>; returns early without <c>node</c>.
/// </summary>
[Collection(nameof(HeftDevServerEventsTests))]
public sealed class HeftDevServerEventsTests
{
    public sealed class ServeBuild : TampBuild
    {
        public static Func<CancellationToken, Task> Body = _ => Task.CompletedTask;
        public static Func<CancellationToken, Task> After = _ => Task.CompletedTask;
        public Target Serve => _ => _.Executes(async ct => await Body(ct));
        public Target Later => _ => _.DependsOn(nameof(Serve)).Executes(async ct => await After(ct));
    }

    private static (int Exit, List<BuildEvent> Events) Run(Func<CancellationToken, Task> body, Func<CancellationToken, Task>? after = null)
    {
        ServeBuild.Body = body;
        ServeBuild.After = after ?? (_ => Task.CompletedTask);
        var eventsFile = Path.Combine(Path.GetTempPath(), $"tamp-heft-events-{Guid.NewGuid():N}.ndjson");
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try
        {
            var exit = TampBuild.Execute<ServeBuild>(new[] { nameof(ServeBuild.Later), "--events", eventsFile });
            var events = File.ReadAllLines(eventsFile).Select(l => BuildEventJson.Deserialize(l)!).ToList();
            return (exit, events);
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
            File.Delete(eventsFile);
        }
    }

    private static List<BuildEvent> Of(List<BuildEvent> events, string type) => events.Where(e => e.Type == type).ToList();

    [Fact]
    public void A_Dev_Server_Reports_Ready_And_Stopped_On_One_Span()
    {
        if (!HeftDevServerTests.HaveNode) return;
        using var f = new HeftDevServerTests.Fixture();
        int pid = 0;
        var (exit, events) = Run(async ct =>
        {
            await using var server = await HeftDevServer.StartAsync(f.Settings(), ct);
            pid = server.ProcessId;
        });
        Assert.Equal(0, exit);

        var sequence = events.Where(e => e.Type is BuildEventTypes.ToolInvoked or BuildEventTypes.ServiceReady
            or BuildEventTypes.ArtifactProduced or BuildEventTypes.ServiceStopped or BuildEventTypes.ToolExited).ToList();
        Assert.Equal(new[]
        {
            BuildEventTypes.ToolInvoked, BuildEventTypes.ServiceReady, BuildEventTypes.ArtifactProduced,
            BuildEventTypes.ServiceStopped, BuildEventTypes.ToolExited,
        }, sequence.Select(e => e.Type));
        var serviceSpan = sequence[0].SpanId;
        Assert.All(sequence.Where(e => e.Type != BuildEventTypes.ArtifactProduced), e => Assert.Equal(serviceSpan, e.SpanId));

        var ready = (ServiceReadyPayload)Of(events, BuildEventTypes.ServiceReady).Single().Payload;
        Assert.Equal(HeftDevServer.ArtifactKind, ready.Kind);
        Assert.Equal($"https://localhost:{f.Port}/", ready.Url);
        Assert.Equal(f.Port, ready.Port);
        Assert.Equal(pid, ready.Pid);
        Assert.Equal($"https://localhost:{f.Port}/temp/build/manifests.js", ready.ReadinessProbe);
        Assert.Equal(f.StatePath, ready.StatePath);

        var stopped = (ServiceStoppedPayload)Of(events, BuildEventTypes.ServiceStopped).Single().Payload;
        Assert.Equal(ServiceStopReason.Requested, stopped.Reason);
        Assert.Equal(HeftDevServer.ArtifactKind, ((ArtifactProducedPayload)Of(events, BuildEventTypes.ArtifactProduced).Single().Payload).Kind);
        Assert.False(File.Exists(f.StatePath));
    }

    [Fact]
    public void A_Server_Left_Running_Serves_Later_Targets_And_Is_Stopped_At_The_End_Of_The_Build()
    {
        if (!HeftDevServerTests.HaveNode) return;
        using var f = new HeftDevServerTests.Fixture();
        HeftDevServer? server = null;
        var (exit, events) = Run(
            async ct => { server = await HeftDevServer.StartAsync(f.Settings(), ct); },
            _ => { Assert.False(server!.HasExited); Assert.True(File.Exists(f.StatePath)); return Task.CompletedTask; });
        Assert.Equal(0, exit);
        Assert.True(server!.HasExited);
        Assert.Equal(ServiceStopReason.Requested, ((ServiceStoppedPayload)Of(events, BuildEventTypes.ServiceStopped).Single().Payload).Reason);
        Assert.True(HeftDevServerTests.WaitUntil(() => !File.Exists(f.StatePath), TimeSpan.FromSeconds(5)),
            "the state file must go even when nothing disposed the server");
    }

    [Fact]
    public void A_Signal_Stops_The_Server_Cancels_The_Target_And_Removes_The_State_File()
    {
        if (!HeftDevServerTests.HaveNode || RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        using var f = new HeftDevServerTests.Fixture();
        int pid = 0;
        var (exit, events) = Run(async ct =>
        {
            await using var server = await HeftDevServer.StartAsync(f.Settings(), ct);
            pid = server.ProcessId;
            using (var kill = Process.Start("kill", $"-TERM {Environment.ProcessId}")) kill.WaitForExit();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        });
        Assert.Equal(143, exit);
        Assert.Equal(BuildEventStatus.Cancelled,
            ((TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == nameof(ServeBuild.Serve)).Payload).Status);
        Assert.Equal(ServiceStopReason.Signal, ((ServiceStoppedPayload)Of(events, BuildEventTypes.ServiceStopped).Single().Payload).Reason);
        Assert.False(HeftDevServerTests.IsRunning(pid));
        Assert.True(HeftDevServerTests.WaitUntil(() => !File.Exists(f.StatePath), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RunUntilCancelled_Is_Cancelled_By_The_Build_Interrupt()
    {
        if (!HeftDevServerTests.HaveNode || RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        using var f = new HeftDevServerTests.Fixture();
        var (exit, events) = Run(async ct =>
        {
            var serve = HeftDevServer.RunUntilCancelledAsync(f.Settings(), ct);
            Assert.True(HeftDevServerTests.WaitUntil(() => File.Exists(f.StatePath) || serve.IsCompleted, TimeSpan.FromSeconds(30)));
            using (var kill = Process.Start("kill", $"-INT {Environment.ProcessId}")) kill.WaitForExit();
            await serve;
        });
        Assert.Equal(130, exit);
        Assert.Equal(BuildEventStatus.Cancelled,
            ((TargetFinishedPayload)events.Single(e => e.Type == BuildEventTypes.TargetFinished && e.TargetId == nameof(ServeBuild.Serve)).Payload).Status);
        Assert.Equal(ServiceStopReason.Signal, ((ServiceStoppedPayload)Of(events, BuildEventTypes.ServiceStopped).Single().Payload).Reason);
    }
}

/// <summary>These tests swap the console and send real signals to the process: never in parallel.</summary>
[CollectionDefinition(nameof(HeftDevServerEventsTests), DisableParallelization = true)]
public sealed class HeftDevServerEventsCollection;
