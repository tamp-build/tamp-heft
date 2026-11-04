using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace Tamp.Heft.V1;

/// <summary>
/// A managed Heft development server: runs the project's pinned Heft as <c>heft start</c>, waits until the
/// server answers a real request for the manifests file, reports where it is, and kills the whole process
/// tree when disposed. Opt-in and separate from the one-shot verbs on <see cref="Heft"/>, which still refuse
/// watch modes. Built on Tamp.Core's <see cref="ManagedProcess"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two ways to use it.</strong> <see cref="StartAsync(Action{HeftDevServerSettings}, CancellationToken)"/>
/// returns once the server is ready; dispose the handle to stop it (use it inside one target that also runs the
/// browser automation). <see cref="RunUntilCancelled(Action{HeftDevServerSettings})"/> starts it, reports it and
/// blocks until Ctrl+C.
/// </para>
/// <para>
/// <strong>Ready means served.</strong> Readiness is an HTTP(S) GET of <c>temp/build/manifests.js</c> (configurable)
/// that returns a success status, not just an open port. Startup refuses to begin if the port is already in use
/// or a live server from an earlier run still owns the state file, so a probe can't be answered by a stale server.
/// The certificate check is relaxed only for loopback hosts, to accept the self-signed SPFx dev certificate.
/// </para>
/// <para>
/// <strong>Reporting.</strong> On the event stream: <c>tool.invoked</c>, <c>service.ready</c> (kind
/// <c>heft-dev-server</c>, with URL, port, PID and state file), <c>service.stopped</c> with its reason
/// (<c>requested</c>, <c>signal</c>, <c>exited</c>, <c>timeout</c>) and <c>tool.exited</c>, on one span. Once ready it
/// also prints the URL, port and manifests URL, and writes a <see cref="HeftDevServerState"/> file atomically (default
/// <c>.tamp/temp/heft-dev-server/&lt;port&gt;.json</c>), announced with <c>artifact.produced</c> (kind
/// <c>heft-dev-server</c>; kept for one more minor version, prefer <c>service.ready</c>). The state file is deleted
/// when the server stops, however it stops.
/// </para>
/// <para>
/// <strong>Stopping.</strong> Dispose, Ctrl+C / SIGTERM / SIGHUP (handled by the <c>tamp</c> run), and the end of the
/// build all stop it and kill the tree. Stopping is idempotent and safe from several threads.
/// </para>
/// <para>
/// <strong>Never deploys.</strong> Only <c>start</c> or a <c>*-watch</c> action runs, and anything mentioning
/// <c>deploy</c> is refused.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// Target BrowserEvidence => _ => _
///     .DependsOn(nameof(NpmCi))
///     .Executes(async () =&gt;
///     {
///         await using var server = await HeftDevServer.StartAsync(s =&gt; s.SetWorkingDirectory(Web));
///         RunPlaywright(server.Url, server.ManifestsUrl);
///     });
///
/// Target Serve => _ => _.Executes(() =&gt; HeftDevServer.RunUntilCancelled(s =&gt; s.SetWorkingDirectory(Web)));
/// </code>
/// </example>
public sealed class HeftDevServer : IAsyncDisposable, IDisposable
{
    /// <summary>The service <c>kind</c> on <c>service.ready</c> / <c>service.stopped</c>, and on the state file's <c>artifact.produced</c>.</summary>
    public const string ArtifactKind = "heft-dev-server";

    private readonly ManagedProcess _process;
    private readonly TextWriter _output;
    private bool _stateWritten;
    private int _finished;

    private HeftDevServer(ManagedProcess process, HeftServeConfig serve, Uri manifestsUrl, string statePath, string? heftVersion, TextWriter output)
    {
        _process = process;
        _output = output;
        Url = serve.BaseUrl;
        ManifestsUrl = manifestsUrl;
        StatePath = statePath;
        HeftVersion = heftVersion;
        // However it stops (dispose, a signal, the end of the build, a crash), clean up after it.
        _ = process.WaitForExitAsync().ContinueWith(_ => OnStopped(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The server root, e.g. <c>https://localhost:4321/</c>.</summary>
    public Uri Url { get; }

    /// <summary>The listening port.</summary>
    public int Port => Url.Port;

    /// <summary>The URL probed for readiness; for SPFx the manifests file to pass as <c>debugManifestsFile</c>.</summary>
    public Uri ManifestsUrl { get; }

    /// <summary>PID of the Heft process (the root of the server's process tree).</summary>
    public int ProcessId => _process.Pid;

    /// <summary>When the Heft process started (UTC).</summary>
    public DateTimeOffset ProcessStartTime => _process.ProcessStartTime;

    /// <summary>The installed <c>@rushstack/heft</c> version, when known.</summary>
    public string? HeftVersion { get; }

    /// <summary>Where the ready-state file is written.</summary>
    public string StatePath { get; }

    /// <summary>True once the Heft process has exited.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>True once <see cref="Dispose"/> (or an interrupt) has begun stopping the server.</summary>
    public bool IsStopping => _process.IsStopping;

    /// <summary>Heft's exit code, once it has exited.</summary>
    public int? ExitCode => _process.ExitCode;

    /// <summary>Why it stopped (<c>requested</c>, <c>signal</c>, <c>exited</c>, <c>timeout</c>), once it has.</summary>
    public string? StopReason => _process.StopReason;

    /// <summary>Completes when the Heft process has exited and its stop has been recorded.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _process.WaitForExitAsync(cancellationToken);

    /// <summary>Start the server and wait until it is ready.</summary>
    public static Task<HeftDevServer> StartAsync(Action<HeftDevServerSettings> configure, CancellationToken cancellationToken = default)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var settings = new HeftDevServerSettings();
        configure(settings);
        return StartAsync(settings, cancellationToken);
    }

    /// <summary>Start the server from pre-built settings and wait until it is ready.</summary>
    public static async Task<HeftDevServer> StartAsync(HeftDevServerSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        var plan = settings.ToCommandPlan(); // validates (no deploys) and resolves the pinned Heft
        var serve = settings.ResolveServeConfig();
        var manifestsUrl = new Uri(serve.BaseUrl, settings.ReadinessPath.TrimStart('/'));
        var statePath = Path.GetFullPath(settings.StatePath ?? DefaultStatePath(serve.Port));
        var output = TextWriter.Synchronized(settings.Output ?? Console.Out);
        var heftVersion = (settings.Binary ?? HeftBinaryResolver.TryResolve(settings.WorkingDirectory ?? Directory.GetCurrentDirectory()))?.Version;

        var existing = HeftDevServerState.TryRead(statePath);
        if (existing is not null && existing.IsLive())
            throw new InvalidOperationException(
                $"A Heft dev server from an earlier run is still running (pid {existing.Pid.ToString(CultureInfo.InvariantCulture)}, " +
                $"{existing.Url}; state file {statePath}). Stop it before starting another.");
        if (File.Exists(statePath))
        {
            output.WriteLine($"Removing stale Heft dev server state file {statePath}.");
            File.Delete(statePath);
        }

        if (await IsPortInUseAsync(serve.BaseUrl.Host, serve.Port, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"Port {serve.Port.ToString(CultureInfo.InvariantCulture)} is already in use, so a readiness check could be " +
                $"answered by another server instead of this one. Stop whatever is listening on {serve.BaseUrl} first.");

        output.WriteLine($"Starting Heft dev server for {serve}.");
        ManagedProcess process;
        try
        {
            process = await ManagedProcess.StartAsync(plan, StartOptions(settings, serve, manifestsUrl, statePath, output), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // It started but never became ready; ManagedProcess has already killed the tree.
            output.WriteLine("Heft dev server stopped.");
            throw;
        }

        var server = new HeftDevServer(process, serve, manifestsUrl, statePath, heftVersion, output);
        try
        {
            server.WriteState();
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return server;
    }

    private static ManagedProcessOptions StartOptions(HeftDevServerSettings settings, HeftServeConfig serve, Uri manifestsUrl, string statePath, TextWriter output) =>
        new()
        {
            Kind = ArtifactKind,
            Readiness = Readiness.Http(manifestsUrl, acceptLoopbackSelfSigned: true),
            ReadyTimeout = settings.ReadyTimeout,
            PollInterval = settings.PollInterval,
            ShutdownTimeout = settings.ShutdownTimeout,
            ServiceUrl = serve.BaseUrl.ToString(),
            Port = serve.Port,
            StatePath = statePath,
            Output = output,
        };

    /// <summary>Start the server, report it, and block until Ctrl+C. Throws if Heft exits on its own.</summary>
    public static void RunUntilCancelled(Action<HeftDevServerSettings> configure) =>
        RunUntilCancelledAsync(configure).GetAwaiter().GetResult();

    /// <summary>Start the server, report it, and wait until Ctrl+C or <paramref name="cancellationToken"/>.</summary>
    public static Task RunUntilCancelledAsync(Action<HeftDevServerSettings> configure, CancellationToken cancellationToken = default)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var settings = new HeftDevServerSettings();
        configure(settings);
        return RunUntilCancelledAsync(settings, cancellationToken);
    }

    /// <summary>
    /// Start the server from pre-built settings and wait until Ctrl+C or <paramref name="cancellationToken"/>. Inside a
    /// <c>tamp</c> run, Ctrl+C / SIGTERM / SIGHUP cancel the build and this throws <see cref="OperationCanceledException"/>,
    /// so the target is reported <c>cancelled</c>; <paramref name="cancellationToken"/> just returns. Throws
    /// <see cref="InvalidOperationException"/> if Heft exits on its own.
    /// </summary>
    public static async Task RunUntilCancelledAsync(HeftDevServerSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        var build = TampBuild.BuildCancellation;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, build);

        // Outside a tamp run nothing handles Ctrl+C for us.
        ConsoleCancelEventHandler? onCancel = null;
        if (!build.CanBeCanceled)
        {
            onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += onCancel;
        }
        try
        {
            HeftDevServer server;
            try
            {
                server = await StartAsync(settings, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!build.IsCancellationRequested && stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return; // Ctrl+C before the server was ready, outside a tamp run.
            }

            await using (server.ConfigureAwait(false))
            {
                (settings.Output ?? Console.Out).WriteLine("Press Ctrl+C to stop the Heft dev server.");
                try
                {
                    await server.WaitForExitAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // fall through to the checks below
                }
                build.ThrowIfCancellationRequested(); // the build was interrupted: report the target cancelled
                if (stop.IsCancellationRequested || server.StopReason is not (null or ServiceStopReason.Exited)) return;
                throw new InvalidOperationException(
                    $"The Heft dev server exited unexpectedly with code {server.ExitCodeText()}.{server.TailText()}");
            }
        }
        finally
        {
            if (onCancel is not null) Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Kill the process tree, wait for it to exit and delete the state file. Safe to call more than once.</summary>
    public void Dispose()
    {
        _process.Dispose();
        OnStopped();
    }

    /// <inheritdoc cref="Dispose" />
    public async ValueTask DisposeAsync()
    {
        await _process.DisposeAsync().ConfigureAwait(false);
        OnStopped();
    }

    // Once, however it stopped: delete our state file and say so.
    private void OnStopped()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 1) return;
        if (_stateWritten && HeftDevServerState.TryRead(StatePath)?.Pid == ProcessId)
        {
            try { File.Delete(StatePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        WriteLine(_process.ExitCode is null
            ? $"Heft dev server (pid {ProcessId.ToString(CultureInfo.InvariantCulture)}) did not exit within the shutdown timeout."
            : "Heft dev server stopped.");
    }

    private void WriteState()
    {
        var state = new HeftDevServerState
        {
            Url = Url.ToString(),
            ManifestsUrl = ManifestsUrl.ToString(),
            Port = Port,
            Pid = ProcessId,
            ProcessStartTime = ProcessStartTime,
            ReadyAt = DateTimeOffset.UtcNow,
            HeftVersion = HeftVersion,
            TampHeftVersion = typeof(HeftDevServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        };
        state.WriteAtomically(StatePath);
        _stateWritten = true;
        BuildEvents.Artifact(StatePath, kind: ArtifactKind, sizeBytes: new FileInfo(StatePath).Length);

        WriteLine($"Heft dev server ready: {Url}");
        WriteLine($"  port:      {Port.ToString(CultureInfo.InvariantCulture)}");
        WriteLine($"  manifests: {ManifestsUrl}");
        WriteLine($"  pid:       {ProcessId.ToString(CultureInfo.InvariantCulture)}");
        WriteLine($"  state:     {StatePath}");
    }

    private void WriteLine(string line)
    {
        try { _output.WriteLine(line); }
        catch (ObjectDisposedException) { }
    }

    private string ExitCodeText() => _process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private string TailText()
    {
        var tail = _process.Tail;
        return tail.Count == 0 ? "" : Environment.NewLine + "Last Heft output:" + Environment.NewLine + string.Join(Environment.NewLine, tail);
    }

    internal static string DefaultStatePath(int port)
    {
        string root;
        try { root = TampBuild.TemporaryDirectory; }
        catch (InvalidOperationException) { root = Path.Combine(Path.GetTempPath(), "tamp"); }
        return Path.Combine(root, "heft-dev-server", port.ToString(CultureInfo.InvariantCulture) + ".json");
    }

    internal static async Task<bool> IsPortInUseAsync(string host, int port, CancellationToken cancellationToken)
    {
        // Probe every address the host can mean, in parallel. "localhost" resolves to ::1 and 127.0.0.1, and a server
        // may listen on just one of them; connecting by name stops at the first address, and on Windows a refused
        // connection takes about two seconds, which used to run out the timeout before 127.0.0.1 was tried.
        var addresses = new List<IPAddress>();
        try { addresses.AddRange(await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false)); }
        catch (SocketException) { }
        if (HeftServeConfig.IsLoopback(host)) addresses.AddRange(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback });

        var probes = addresses.Distinct().Select(a => IsListeningAsync(a, port, cancellationToken)).ToArray();
        return (await Task.WhenAll(probes).ConfigureAwait(false)).Any(listening => listening);
    }

    private static async Task<bool> IsListeningAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false; // refused, or this address family isn't available here
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // nothing answered (a filtered port); treat as free
        }
    }
}
