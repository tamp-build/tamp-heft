using System.Text.RegularExpressions;

namespace Tamp.Heft.V1;

/// <summary>
/// Settings for <see cref="HeftDevServer"/>: a managed <c>heft start</c> (the SPFx rig's alias for
/// <c>build-watch --serve</c>). Shares the working directory, environment, binary resolution, global flags and
/// pass-through arguments of every other Heft verb.
/// </summary>
/// <remarks>
/// Only serve/watch actions are allowed, and nothing that deploys: any action or argument containing
/// <c>deploy</c> (the SPFx rig's <c>deploy-azure-storage</c> and <c>dev-deploy</c>) is refused, so a dev-server
/// target can't change a remote environment.
/// </remarks>
public sealed class HeftDevServerSettings : HeftSettingsBase
{
    private static readonly Regex ActionName = new("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The Heft action that starts the server. Default <c>start</c>; a rig without that alias can use
    /// <c>build-watch</c> plus <c>AddArgument("--serve")</c>.
    /// </summary>
    public string Action { get; set; } = "start";

    /// <inheritdoc />
    protected override string Command => Action;

    /// <summary><c>-v</c>.</summary>
    public bool Verbose { get; set; }

    /// <summary><c>--clean</c>: clean outputs before the first build.</summary>
    public bool Clean { get; set; }

    /// <summary>
    /// The <c>serve.json</c> to read the URL from. Default: <c>config/serve.json</c> under the working
    /// directory. Ignored when <see cref="ServerUrl"/> is set.
    /// </summary>
    public string? ServeConfigPath { get; set; }

    /// <summary>Explicit server root (e.g. <c>https://localhost:4321/</c>), instead of reading <c>serve.json</c>.</summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>
    /// Path under the server root that must answer a real HTTP(S) request before the server counts as ready.
    /// Default <see cref="HeftServeConfig.DefaultManifestsPath"/>, the file the SharePoint debug loader loads,
    /// which only exists once the first webpack compilation has finished.
    /// </summary>
    public string ReadinessPath { get; set; } = HeftServeConfig.DefaultManifestsPath;

    /// <summary>How long to wait for readiness before failing. Default 5 minutes (a cold SPFx build is slow).</summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay between readiness probes. Default 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait for the process tree to exit after it is killed. Default 10 seconds.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Where the ready-state file is written. Default:
    /// <c>&lt;repo&gt;/.tamp/temp/heft-dev-server/&lt;port&gt;.json</c> (Tamp's ignored work area).
    /// </summary>
    public string? StatePath { get; set; }

    /// <summary>Where Heft's output and Tamp's own status lines go. Default: the console.</summary>
    public TextWriter? Output { get; set; }

    /// <inheritdoc />
    protected override void Validate()
    {
        base.Validate();
        if (string.IsNullOrEmpty(Action) || !ActionName.IsMatch(Action))
            throw new InvalidOperationException($"'{Action}' is not a Heft action name (lowercase letters, digits and '-').");
        if (Action != "start" && !Action.EndsWith("-watch", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{Action}' is not a serve action. The dev server runs 'start' or a '*-watch' action with '--serve'; " +
                "use Heft.Phase for one-shot actions.");
        foreach (var token in ExtraArguments.Prepend(Action))
            if (token.Contains("deploy", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{token}' deploys. The dev server never deploys packages or changes remote environments.");
        if (string.IsNullOrWhiteSpace(ReadinessPath) || ReadinessPath.Contains("://", StringComparison.Ordinal))
            throw new InvalidOperationException("ReadinessPath must be a path under the server root.");
        if (ReadyTimeout <= TimeSpan.Zero) throw new InvalidOperationException("ReadyTimeout must be positive.");
        if (PollInterval <= TimeSpan.Zero) throw new InvalidOperationException("PollInterval must be positive.");
        if (ShutdownTimeout <= TimeSpan.Zero) throw new InvalidOperationException("ShutdownTimeout must be positive.");
    }

    /// <inheritdoc />
    protected override void AppendArguments(List<string> args)
    {
        if (Verbose) args.Add("-v");
        if (Clean) args.Add("--clean");
    }

    /// <summary>Where the server will listen: <see cref="ServerUrl"/>, else <c>serve.json</c>.</summary>
    public HeftServeConfig ResolveServeConfig()
    {
        if (ServerUrl is not null) return HeftServeConfig.FromUrl(ServerUrl);
        var dir = WorkingDirectory ?? Directory.GetCurrentDirectory();
        return HeftServeConfig.Load(ServeConfigPath ?? Path.Combine(dir, "config", "serve.json"));
    }
}

/// <summary>Fluent setters for <see cref="HeftDevServerSettings"/>.</summary>
public static class HeftDevServerSettingsExtensions
{
    /// <summary>The Heft action to run (default <c>start</c>).</summary>
    public static HeftDevServerSettings SetAction(this HeftDevServerSettings s, string action) { s.Action = action; return s; }
    /// <summary><c>-v</c>.</summary>
    public static HeftDevServerSettings SetVerbose(this HeftDevServerSettings s, bool v = true) { s.Verbose = v; return s; }
    /// <summary><c>--clean</c>.</summary>
    public static HeftDevServerSettings SetClean(this HeftDevServerSettings s, bool v = true) { s.Clean = v; return s; }
    /// <summary>Read the URL from this <c>serve.json</c>.</summary>
    public static HeftDevServerSettings SetServeConfigPath(this HeftDevServerSettings s, string? path) { s.ServeConfigPath = path; return s; }
    /// <summary>Use an explicit server root instead of <c>serve.json</c>.</summary>
    public static HeftDevServerSettings SetServerUrl(this HeftDevServerSettings s, Uri? url) { s.ServerUrl = url; return s; }
    /// <summary>Use an explicit server root instead of <c>serve.json</c>.</summary>
    public static HeftDevServerSettings SetServerUrl(this HeftDevServerSettings s, string url) { s.ServerUrl = new Uri(url, UriKind.Absolute); return s; }
    /// <summary>Path probed for readiness (default <c>temp/build/manifests.js</c>).</summary>
    public static HeftDevServerSettings SetReadinessPath(this HeftDevServerSettings s, string path) { s.ReadinessPath = path; return s; }
    /// <summary>How long to wait for readiness.</summary>
    public static HeftDevServerSettings SetReadyTimeout(this HeftDevServerSettings s, TimeSpan timeout) { s.ReadyTimeout = timeout; return s; }
    /// <summary>Delay between readiness probes.</summary>
    public static HeftDevServerSettings SetPollInterval(this HeftDevServerSettings s, TimeSpan interval) { s.PollInterval = interval; return s; }
    /// <summary>How long to wait for the process tree to exit after it is killed.</summary>
    public static HeftDevServerSettings SetShutdownTimeout(this HeftDevServerSettings s, TimeSpan timeout) { s.ShutdownTimeout = timeout; return s; }
    /// <summary>Where to write the ready-state file.</summary>
    public static HeftDevServerSettings SetStatePath(this HeftDevServerSettings s, string? path) { s.StatePath = path; return s; }
    /// <summary>Where Heft's output and status lines go.</summary>
    public static HeftDevServerSettings SetOutput(this HeftDevServerSettings s, TextWriter? output) { s.Output = output; return s; }
}
