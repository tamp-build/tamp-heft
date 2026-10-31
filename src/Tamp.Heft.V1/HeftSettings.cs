using System.Globalization;
using System.Text.RegularExpressions;

namespace Tamp.Heft.V1;

/// <summary>
/// Shared base for every Heft invocation: working directory, environment, binary resolution, the
/// two global flags that precede the command (<c>--debug</c>, <c>--unmanaged</c>), and pass-through
/// arguments for plugin-defined parameters.
/// </summary>
public abstract class HeftSettingsBase
{
    /// <summary>Working directory for the process, and the starting point for binary resolution.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Per-invocation environment variables.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Explicit Heft resolution. When null, <see cref="ToCommandPlan"/> resolves via
    /// <see cref="HeftBinaryResolver.TryResolve(string)"/> against <see cref="WorkingDirectory"/>.
    /// </summary>
    public HeftBinaryResolution? Binary { get; set; }

    /// <summary>Global <c>--debug</c>: show full error stacks and Heft's own diagnostics.</summary>
    public bool Debug { get; set; }

    /// <summary>
    /// Global <c>--unmanaged</c>: skip Heft's check that the project pins its own Heft. Rarely right on a
    /// build machine, where the pinned version is the point.
    /// </summary>
    public bool Unmanaged { get; set; }

    /// <summary>
    /// Arguments appended after the typed flags, for parameters that plugins define (for example SPFx's
    /// <c>--emit-stats</c> or <c>--pretty-print-xml</c>). Each entry is one argv token.
    /// </summary>
    public List<string> ExtraArguments { get; } = new();

    /// <summary>The Heft command (<c>build</c>, <c>test</c>, <c>clean</c>, <c>run</c>, or a rig-defined phase action).</summary>
    protected abstract string Command { get; }

    /// <summary>Append the command-specific flags.</summary>
    protected abstract void AppendArguments(List<string> args);

    /// <summary>Throw when the settings can't produce a valid command line.</summary>
    protected virtual void Validate()
    {
        foreach (var a in ExtraArguments)
            if (a is null) throw new InvalidOperationException("ExtraArguments must not contain null.");
    }

    /// <summary>Build the <see cref="CommandPlan"/>.</summary>
    public CommandPlan ToCommandPlan()
    {
        Validate();

        var resolution = Binary
            ?? HeftBinaryResolver.TryResolve(WorkingDirectory ?? Directory.GetCurrentDirectory())
            ?? throw new InvalidOperationException(
                "Heft was not found: no @rushstack/heft package or node_modules/.bin/heft at the working directory or " +
                "any parent, and no heft on PATH. Run `npm ci` (or your package manager's equivalent) first, or check " +
                "HeftBinaryResolver.IsAvailable(workingDirectory) and skip the target.");

        var args = new List<string>(resolution.PrefixArguments);
        if (Debug) args.Add("--debug");
        if (Unmanaged) args.Add("--unmanaged");
        args.Add(Command);
        AppendArguments(args);
        args.AddRange(ExtraArguments);

        return new CommandPlan
        {
            Executable = resolution.Executable,
            Arguments = args,
            Environment = new Dictionary<string, string>(EnvironmentVariables, StringComparer.Ordinal),
            WorkingDirectory = WorkingDirectory,
        };
    }

    internal static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Settings for a Heft <em>phase action</em>: a command that runs the rig's phases up to and including one
/// phase. <c>build</c> and <c>test</c> are built in; rigs add more (SPFx adds <c>package-solution</c>,
/// <c>deploy-azure-storage</c>, …), which <see cref="Heft.Phase(string, Action{HeftActionSettings})"/> runs.
/// </summary>
public class HeftActionSettings : HeftSettingsBase
{
    private static readonly Regex ActionName = new("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);

    /// <summary>Create settings for the phase action <paramref name="action"/>.</summary>
    public HeftActionSettings(string action) { Action = action; }

    /// <summary>The phase action to run, as Heft spells it on the command line.</summary>
    public string Action { get; }

    /// <inheritdoc />
    protected override string Command => Action;

    /// <summary><c>-v</c>: log information useful for debugging.</summary>
    public bool Verbose { get; set; }

    /// <summary><c>--production</c>: run in production mode (minified bundles, no dev-only output).</summary>
    public bool Production { get; set; }

    /// <summary><c>--clean</c>: clean outputs before the lifecycle and before each phase.</summary>
    public bool Clean { get; set; }

    /// <summary><c>--locales</c>, one per entry.</summary>
    public List<string> Locales { get; } = new();

    /// <inheritdoc />
    protected override void Validate()
    {
        base.Validate();
        if (string.IsNullOrEmpty(Action) || !ActionName.IsMatch(Action))
            throw new InvalidOperationException($"'{Action}' is not a Heft action name (lowercase letters, digits and '-').");
        if (Action == "start" || Action.EndsWith("-watch", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{Action}' runs in watch mode and never exits, so it can't be a build step. Use Heft.Raw if you really mean it.");
    }

    /// <inheritdoc />
    protected override void AppendArguments(List<string> args)
    {
        if (Verbose) args.Add("-v");
        if (Production) args.Add("--production");
        if (Clean) args.Add("--clean");
        foreach (var l in Locales) { args.Add("--locales"); args.Add(l); }
    }
}

/// <summary>Settings for <c>heft build</c>: compile, lint and bundle.</summary>
public sealed class HeftBuildSettings : HeftActionSettings
{
    /// <summary>Settings for <c>heft build</c>.</summary>
    public HeftBuildSettings() : base("build") { }
}

/// <summary>
/// Settings for <c>heft test</c>: <c>build</c> plus the Jest phase. The Jest flags are the ones the Heft Jest
/// plugin exposes; anything it adds later can go in <see cref="HeftSettingsBase.ExtraArguments"/>.
/// </summary>
public sealed class HeftTestSettings : HeftActionSettings
{
    /// <summary>Settings for <c>heft test</c>.</summary>
    public HeftTestSettings() : base("test") { }

    /// <summary><c>--config</c>: Jest config file, relative to the project (Heft's default is <c>config/jest.config.json</c>).</summary>
    public string? JestConfig { get; set; }

    /// <summary><c>--disable-code-coverage</c>.</summary>
    public bool DisableCodeCoverage { get; set; }

    /// <summary><c>--detect-open-handles</c> (slow; for diagnosing a run that won't exit).</summary>
    public bool DetectOpenHandles { get; set; }

    /// <summary><c>--find-related-tests</c>, one per source file.</summary>
    public List<string> FindRelatedTests { get; } = new();

    /// <summary><c>--log-heap-usage</c>.</summary>
    public bool LogHeapUsage { get; set; }

    /// <summary><c>--max-workers</c>: a count (<c>"3"</c>) or a percentage of CPUs (<c>"50%"</c>).</summary>
    public string? MaxWorkers { get; set; }

    /// <summary><c>--silent</c>: suppress test console output.</summary>
    public bool Silent { get; set; }

    /// <summary><c>--test-name-pattern</c>: run only tests whose full name matches this regex.</summary>
    public string? TestNamePattern { get; set; }

    /// <summary><c>--test-path-pattern</c>: run only test files whose path matches this regex (use <c>/</c>, even on Windows).</summary>
    public string? TestPathPattern { get; set; }

    /// <summary><c>--test-path-ignore-patterns</c>, one per regex.</summary>
    public List<string> TestPathIgnorePatterns { get; } = new();

    /// <summary><c>--test-timeout-ms</c>: default per-test timeout.</summary>
    public int? TestTimeoutMs { get; set; }

    /// <summary><c>--update-snapshots</c>. Rewrites snapshot files, so leave it off on a build machine.</summary>
    public bool UpdateSnapshots { get; set; }

    /// <inheritdoc />
    protected override void Validate()
    {
        base.Validate();
        if (TestTimeoutMs is <= 0) throw new InvalidOperationException("TestTimeoutMs must be positive.");
    }

    /// <inheritdoc />
    protected override void AppendArguments(List<string> args)
    {
        base.AppendArguments(args);
        if (!string.IsNullOrEmpty(JestConfig)) { args.Add("--config"); args.Add(JestConfig!); }
        if (DisableCodeCoverage) args.Add("--disable-code-coverage");
        if (DetectOpenHandles) args.Add("--detect-open-handles");
        foreach (var f in FindRelatedTests) { args.Add("--find-related-tests"); args.Add(f); }
        if (LogHeapUsage) args.Add("--log-heap-usage");
        if (!string.IsNullOrEmpty(MaxWorkers)) { args.Add("--max-workers"); args.Add(MaxWorkers!); }
        if (Silent) args.Add("--silent");
        if (!string.IsNullOrEmpty(TestNamePattern)) { args.Add("--test-name-pattern"); args.Add(TestNamePattern!); }
        if (!string.IsNullOrEmpty(TestPathPattern)) { args.Add("--test-path-pattern"); args.Add(TestPathPattern!); }
        foreach (var p in TestPathIgnorePatterns) { args.Add("--test-path-ignore-patterns"); args.Add(p); }
        if (TestTimeoutMs is int t) { args.Add("--test-timeout-ms"); args.Add(Invariant(t)); }
        if (UpdateSnapshots) args.Add("--update-snapshots");
    }
}

/// <summary>Phase selection shared by <c>heft clean</c> and <c>heft run</c>.</summary>
public abstract class HeftScopedSettingsBase : HeftSettingsBase
{
    /// <summary><c>--to</c>: the phase to run to, with its dependencies. One per entry.</summary>
    public List<string> To { get; } = new();

    /// <summary><c>--to-except</c>: run up to, but not including, the phase. One per entry.</summary>
    public List<string> ToExcept { get; } = new();

    /// <summary><c>--only</c>: run just the phase. One per entry.</summary>
    public List<string> Only { get; } = new();

    /// <summary>Append the scoping flags.</summary>
    protected void AppendScoping(List<string> args)
    {
        foreach (var p in To) { args.Add("--to"); args.Add(p); }
        foreach (var p in ToExcept) { args.Add("--to-except"); args.Add(p); }
        foreach (var p in Only) { args.Add("--only"); args.Add(p); }
    }
}

/// <summary>Settings for <c>heft clean</c>: remove the outputs of the selected phases (all phases by default).</summary>
public sealed class HeftCleanSettings : HeftScopedSettingsBase
{
    /// <inheritdoc />
    protected override string Command => "clean";

    /// <summary><c>-v</c>.</summary>
    public bool Verbose { get; set; }

    /// <inheritdoc />
    protected override void AppendArguments(List<string> args)
    {
        AppendScoping(args);
        if (Verbose) args.Add("-v");
    }
}

/// <summary>
/// Settings for <c>heft run</c>: run an explicit selection of phases. Action parameters such as
/// <c>--production</c> are <em>scoped</em> here and go after a <c>--</c> separator, which
/// <see cref="ScopedArguments"/> handles.
/// </summary>
public sealed class HeftRunSettings : HeftScopedSettingsBase
{
    /// <inheritdoc />
    protected override string Command => "run";

    /// <summary>Parameters for the selected phases' tasks, emitted after <c>--</c> (for example <c>--production</c>).</summary>
    public List<string> ScopedArguments { get; } = new();

    /// <inheritdoc />
    protected override void Validate()
    {
        base.Validate();
        if (To.Count + ToExcept.Count + Only.Count == 0)
            throw new InvalidOperationException("heft run needs a phase selection: AddTo, AddToExcept or AddOnly.");
    }

    /// <inheritdoc />
    protected override void AppendArguments(List<string> args)
    {
        AppendScoping(args);
        if (ScopedArguments.Count == 0) return;
        args.Add("--");
        args.AddRange(ScopedArguments);
    }
}

/// <summary>Fluent setters shared by every Heft settings type.</summary>
public static class HeftSettingsBaseExtensions
{
    /// <summary>Set the working directory (also where resolution starts).</summary>
    public static T SetWorkingDirectory<T>(this T s, string? cwd) where T : HeftSettingsBase { s.WorkingDirectory = cwd; return s; }
    /// <summary>Set an environment variable for the process.</summary>
    public static T SetEnvironmentVariable<T>(this T s, string name, string value) where T : HeftSettingsBase { s.EnvironmentVariables[name] = value; return s; }
    /// <summary>Set <c>NODE_OPTIONS</c>, e.g. <c>--max-old-space-size=8192</c> for a large webpack bundle.</summary>
    public static T SetNodeOptions<T>(this T s, string options) where T : HeftSettingsBase { s.EnvironmentVariables["NODE_OPTIONS"] = options; return s; }
    /// <summary>Use an explicit resolution instead of searching.</summary>
    public static T SetBinary<T>(this T s, HeftBinaryResolution binary) where T : HeftSettingsBase { s.Binary = binary; return s; }
    /// <summary>Global <c>--debug</c>.</summary>
    public static T SetDebug<T>(this T s, bool v = true) where T : HeftSettingsBase { s.Debug = v; return s; }
    /// <summary>Global <c>--unmanaged</c>.</summary>
    public static T SetUnmanaged<T>(this T s, bool v = true) where T : HeftSettingsBase { s.Unmanaged = v; return s; }
    /// <summary>Append one pass-through argument (a plugin-defined parameter).</summary>
    public static T AddArgument<T>(this T s, string argument) where T : HeftSettingsBase { s.ExtraArguments.Add(argument); return s; }
}

/// <summary>Fluent setters for phase actions (<c>build</c>, <c>test</c>, rig-defined actions).</summary>
public static class HeftActionSettingsExtensions
{
    /// <summary><c>-v</c>.</summary>
    public static T SetVerbose<T>(this T s, bool v = true) where T : HeftActionSettings { s.Verbose = v; return s; }
    /// <summary><c>--production</c>.</summary>
    public static T SetProduction<T>(this T s, bool v = true) where T : HeftActionSettings { s.Production = v; return s; }
    /// <summary><c>--clean</c>.</summary>
    public static T SetClean<T>(this T s, bool v = true) where T : HeftActionSettings { s.Clean = v; return s; }
    /// <summary>Add a <c>--locales</c> value.</summary>
    public static T AddLocale<T>(this T s, string locale) where T : HeftActionSettings { s.Locales.Add(locale); return s; }
}

/// <summary>Fluent setters for <c>heft test</c>.</summary>
public static class HeftTestSettingsExtensions
{
    /// <summary><c>--config</c>.</summary>
    public static HeftTestSettings SetJestConfig(this HeftTestSettings s, string? path) { s.JestConfig = path; return s; }
    /// <summary><c>--disable-code-coverage</c>.</summary>
    public static HeftTestSettings SetDisableCodeCoverage(this HeftTestSettings s, bool v = true) { s.DisableCodeCoverage = v; return s; }
    /// <summary><c>--detect-open-handles</c>.</summary>
    public static HeftTestSettings SetDetectOpenHandles(this HeftTestSettings s, bool v = true) { s.DetectOpenHandles = v; return s; }
    /// <summary>Add a <c>--find-related-tests</c> source file.</summary>
    public static HeftTestSettings AddFindRelatedTests(this HeftTestSettings s, string sourceFile) { s.FindRelatedTests.Add(sourceFile); return s; }
    /// <summary><c>--log-heap-usage</c>.</summary>
    public static HeftTestSettings SetLogHeapUsage(this HeftTestSettings s, bool v = true) { s.LogHeapUsage = v; return s; }
    /// <summary><c>--max-workers</c>.</summary>
    public static HeftTestSettings SetMaxWorkers(this HeftTestSettings s, string? countOrPercent) { s.MaxWorkers = countOrPercent; return s; }
    /// <summary><c>--silent</c>.</summary>
    public static HeftTestSettings SetSilent(this HeftTestSettings s, bool v = true) { s.Silent = v; return s; }
    /// <summary><c>--test-name-pattern</c>.</summary>
    public static HeftTestSettings SetTestNamePattern(this HeftTestSettings s, string? regex) { s.TestNamePattern = regex; return s; }
    /// <summary><c>--test-path-pattern</c>.</summary>
    public static HeftTestSettings SetTestPathPattern(this HeftTestSettings s, string? regex) { s.TestPathPattern = regex; return s; }
    /// <summary>Add a <c>--test-path-ignore-patterns</c> regex.</summary>
    public static HeftTestSettings AddTestPathIgnorePattern(this HeftTestSettings s, string regex) { s.TestPathIgnorePatterns.Add(regex); return s; }
    /// <summary><c>--test-timeout-ms</c>.</summary>
    public static HeftTestSettings SetTestTimeoutMs(this HeftTestSettings s, int? ms) { s.TestTimeoutMs = ms; return s; }
    /// <summary><c>--update-snapshots</c>.</summary>
    public static HeftTestSettings SetUpdateSnapshots(this HeftTestSettings s, bool v = true) { s.UpdateSnapshots = v; return s; }
}

/// <summary>Fluent setters for <c>heft clean</c> and <c>heft run</c>.</summary>
public static class HeftScopedSettingsExtensions
{
    /// <summary>Add a <c>--to</c> phase.</summary>
    public static T AddTo<T>(this T s, string phase) where T : HeftScopedSettingsBase { s.To.Add(phase); return s; }
    /// <summary>Add a <c>--to-except</c> phase.</summary>
    public static T AddToExcept<T>(this T s, string phase) where T : HeftScopedSettingsBase { s.ToExcept.Add(phase); return s; }
    /// <summary>Add an <c>--only</c> phase.</summary>
    public static T AddOnly<T>(this T s, string phase) where T : HeftScopedSettingsBase { s.Only.Add(phase); return s; }
    /// <summary><c>-v</c> for <c>heft clean</c>.</summary>
    public static HeftCleanSettings SetVerbose(this HeftCleanSettings s, bool v = true) { s.Verbose = v; return s; }
    /// <summary>Add a scoped parameter for <c>heft run</c> (emitted after <c>--</c>).</summary>
    public static HeftRunSettings AddScopedArgument(this HeftRunSettings s, string argument) { s.ScopedArguments.Add(argument); return s; }
}
