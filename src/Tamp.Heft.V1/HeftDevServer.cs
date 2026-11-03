using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Tamp.Heft.V1;

/// <summary>
/// A managed Heft development server: runs the project's pinned Heft as <c>heft start</c>, waits until the
/// server answers a real request for the manifests file, reports where it is, and kills the whole process
/// tree when disposed. Opt-in and separate from the one-shot verbs on <see cref="Heft"/>, which still refuse
/// watch modes.
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
/// <strong>Reporting.</strong> Once ready it prints the URL, port and manifests URL, writes a
/// <see cref="HeftDevServerState"/> file atomically (default <c>.tamp/temp/heft-dev-server/&lt;port&gt;.json</c>)
/// and emits <c>artifact.produced</c> for it with kind <c>heft-dev-server</c>, alongside the usual
/// <c>tool.invoked</c> / <c>tool.exited</c> pair. The state file is deleted on disposal.
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
    /// <summary>The <c>kind</c> on the <c>artifact.produced</c> event for the state file.</summary>
    public const string ArtifactKind = "heft-dev-server";

    private const int TailLines = 40;

    private readonly HeftDevServerSettings _settings;
    private readonly CommandPlan _plan;
    private readonly TextWriter _output;
    private readonly object _ioLock = new();
    private readonly Queue<string> _tail = new();
    private readonly Process _process;
    private readonly long _startTimestamp;
    private readonly ManualResetEventSlim _stopped = new();
    private readonly List<PosixSignalRegistration> _signals = new();
    private string? _cmdSpanId;
    private string? _lastProbeError;
    private int _exitCode = -1;
    private double _exitDurationMs;
    private bool _stateWritten;
    private int _stopping;

    private HeftDevServer(HeftDevServerSettings settings, CommandPlan plan, HeftServeConfig serve, string statePath, TextWriter output)
    {
        _settings = settings;
        _plan = plan;
        _output = output;
        Url = serve.BaseUrl;
        ManifestsUrl = new Uri(serve.BaseUrl, settings.ReadinessPath.TrimStart('/'));
        StatePath = statePath;
        HeftVersion = (settings.Binary ?? HeftBinaryResolver.TryResolve(settings.WorkingDirectory ?? Directory.GetCurrentDirectory()))?.Version;

        var psi = new ProcessStartInfo
        {
            FileName = plan.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (plan.WorkingDirectory is not null) psi.WorkingDirectory = plan.WorkingDirectory;
        foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in plan.Environment) psi.Environment[k] = v;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };

        _cmdSpanId = BuildEvents.ToolInvoked(plan);
        _startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            _process.Start();
        }
        catch
        {
            BuildEvents.ToolExited(_cmdSpanId, plan.Executable, -1, 0);
            _process.Dispose();
            throw;
        }
        ProcessId = _process.Id;
        ProcessStartTime = new DateTimeOffset(_process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Don't leave a server behind if the build is interrupted or exits without disposing the handle.
        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        // A console app gets no ProcessExit on SIGTERM (kill, a CI cancel) or SIGHUP (terminal closed): the default
        // action just ends the process. Stop the server first, then let the default action continue.
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        {
            try { _signals.Add(PosixSignalRegistration.Create(signal, _ => Stop())); }
            catch (PlatformNotSupportedException) { }
        }
    }

    /// <summary>The server root, e.g. <c>https://localhost:4321/</c>.</summary>
    public Uri Url { get; }

    /// <summary>The listening port.</summary>
    public int Port => Url.Port;

    /// <summary>The URL probed for readiness; for SPFx the manifests file to pass as <c>debugManifestsFile</c>.</summary>
    public Uri ManifestsUrl { get; }

    /// <summary>PID of the Heft process (the root of the server's process tree).</summary>
    public int ProcessId { get; }

    /// <summary>When the Heft process started (UTC).</summary>
    public DateTimeOffset ProcessStartTime { get; }

    /// <summary>The installed <c>@rushstack/heft</c> version, when known.</summary>
    public string? HeftVersion { get; }

    /// <summary>Where the ready-state file is written.</summary>
    public string StatePath { get; }

    /// <summary>True once the Heft process has exited.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>True once <see cref="Dispose"/> (or an interrupt) has begun stopping the server.</summary>
    public bool IsStopping => Volatile.Read(ref _stopping) == 1;

    /// <summary>Completes when the Heft process exits.</summary>
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

        var plan = settings.ToCommandPlan(); // validates and resolves the pinned Heft
        var serve = settings.ResolveServeConfig();
        var statePath = Path.GetFullPath(settings.StatePath ?? DefaultStatePath(serve.Port));
        var output = settings.Output ?? Console.Out;

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
        var server = new HeftDevServer(settings, plan, serve, statePath, output);
        try
        {
            await server.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            server.WriteState();
        }
        catch
        {
            server.Dispose();
            throw;
        }
        return server;
    }

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

    /// <summary>Start the server from pre-built settings and wait until Ctrl+C or <paramref name="cancellationToken"/>.</summary>
    public static async Task RunUntilCancelledAsync(HeftDevServerSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            HeftDevServer server;
            try
            {
                server = await StartAsync(settings, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return; // Ctrl+C before the server was ready.
            }

            using (server)
            {
                (settings.Output ?? Console.Out).WriteLine("Press Ctrl+C to stop the Heft dev server.");
                try
                {
                    await server.WaitForExitAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                if (server.IsStopping) return;
                throw new InvalidOperationException(
                    $"The Heft dev server exited unexpectedly with code {server.ExitCodeText()}.{server.Tail()}");
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Kill the process tree, wait for it to exit and delete the state file. Safe to call more than once.</summary>
    public void Dispose() => Stop();

    /// <inheritdoc cref="Dispose" />
    public ValueTask DisposeAsync()
    {
        Stop();
        return default;
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1)
        {
            // Another thread (typically the Ctrl+C handler) is already stopping the server. Wait for it: returning
            // early would let the build exit, and end that thread, before the process tree has been killed.
            _stopped.Wait(_settings.ShutdownTimeout + TimeSpan.FromSeconds(5));
            EmitToolExited();
            return;
        }

        try
        {
            StopCore();
        }
        finally
        {
            _stopped.Set();
        }
    }

    private void StopCore()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        foreach (var registration in _signals) registration.Dispose();

        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to kill any more.
        }

        var exited = _process.WaitForExit((int)Math.Min(int.MaxValue, _settings.ShutdownTimeout.TotalMilliseconds));
        if (exited) _process.WaitForExit(); // flush the async output readers

        if (_stateWritten && HeftDevServerState.TryRead(StatePath)?.Pid == ProcessId)
        {
            try { File.Delete(StatePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        _exitDurationMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        _exitCode = exited ? _process.ExitCode : -1;
        EmitToolExited();
        WriteLine(exited
            ? "Heft dev server stopped."
            : $"Heft dev server (pid {ProcessId.ToString(CultureInfo.InvariantCulture)}) did not exit within {_settings.ShutdownTimeout.TotalSeconds:F0}s of being killed.");
        _process.Dispose();
    }

    // The event scope flows with the build's async context, so a stop on a signal thread can't emit. Leave the
    // event for the owner's Dispose, which waits for that stop and then calls this on the build's own flow.
    private void EmitToolExited()
    {
        if (!BuildEvents.IsActive) return;
        var span = Interlocked.Exchange(ref _cmdSpanId, null);
        if (span is not null) BuildEvents.ToolExited(span, _plan.Executable, _exitCode, _exitDurationMs);
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) => Stop();

    private void OnProcessExit(object? sender, EventArgs e) => Stop();

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            // The SPFx dev certificate is self-signed; accept it, but only from this machine.
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == SslPolicyErrors.None || (request.RequestUri is { } u && HeftServeConfig.IsLoopback(u.Host)),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var ready = await Polling.Until(async ct =>
        {
            if (_process.HasExited) throw ExitedBeforeReady();
            try
            {
                using var response = await client.GetAsync(ManifestsUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
                _lastProbeError = $"HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}";
                return false;
            }
            catch (HttpRequestException ex)
            {
                _lastProbeError = ex.Message;
                return false;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _lastProbeError = "request timed out";
                return false;
            }
        }, _settings.ReadyTimeout, Backoff.Constant(_settings.PollInterval), logger: null, cancellationToken).ConfigureAwait(false);

        if (_process.HasExited) throw ExitedBeforeReady();
        if (!ready)
            throw new TimeoutException(
                $"The Heft dev server was not ready within {_settings.ReadyTimeout.TotalSeconds:F0}s: {ManifestsUrl} " +
                $"never returned a success status (last result: {_lastProbeError ?? "none"}).{Tail()}");
    }

    private InvalidOperationException ExitedBeforeReady()
    {
        _process.WaitForExit();
        return new InvalidOperationException(
            $"Heft exited with code {ExitCodeText()} before the dev server was ready.{Tail()}");
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

    private void Log(string line)
    {
        lock (_ioLock)
        {
            _tail.Enqueue(line);
            while (_tail.Count > TailLines) _tail.Dequeue();
        }
        WriteLine(line);
    }

    private void WriteLine(string line)
    {
        lock (_ioLock)
        {
            try { _output.WriteLine(line); }
            catch (ObjectDisposedException) { }
        }
    }

    private string ExitCodeText()
    {
        try { return _process.ExitCode.ToString(CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "unknown"; }
    }

    private string Tail()
    {
        lock (_ioLock)
        {
            return _tail.Count == 0 ? "" : Environment.NewLine + "Last Heft output:" + Environment.NewLine + string.Join(Environment.NewLine, _tail);
        }
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // nothing answered (a filtered port); treat as free
        }
    }
}
