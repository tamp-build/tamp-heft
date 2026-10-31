namespace Tamp.Heft.V1;

/// <summary>
/// Tamp wrapper for the Rushstack Heft 1.x CLI. Builds <see cref="CommandPlan"/>s for the built-in
/// <c>build</c>, <c>test</c>, <c>clean</c> and <c>run</c> commands, and for any phase action a rig defines
/// (<see cref="Phase(string, Action{HeftActionSettings})"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Which Heft runs.</strong> The project's own installed <c>@rushstack/heft</c>, run as
/// <c>node &lt;heft bin&gt;</c>; see <see cref="HeftBinaryResolver"/>. Nothing is ever downloaded.
/// </para>
/// <para>
/// <strong>Exit codes.</strong> Heft exits <c>0</c> on success and non-zero when any task fails (a
/// compile error, a lint error, a failing test, a webpack error). There is no "findings" exit code to
/// treat as success, so the default <c>ProcessRunner.Execute</c> behaviour is right.
/// </para>
/// <para>
/// <strong>Watch modes are refused.</strong> <c>start</c> and the <c>*-watch</c> actions never exit, so a
/// build step that ran one would hang the build machine. <see cref="Raw"/> remains for a deliberate use.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// Target Test => _ => _.Executes(() =&gt; Heft.Test(s =&gt; s
///     .SetWorkingDirectory(WebRoot)
///     .SetProduction()
///     .SetClean()
///     .SetMaxWorkers("50%")));
/// </code>
/// </example>
public static class Heft
{
    /// <summary><c>heft build</c>.</summary>
    public static CommandPlan Build(Action<HeftBuildSettings> configure) => Configure(new HeftBuildSettings(), configure);

    /// <summary><c>heft build</c> from pre-built settings.</summary>
    public static CommandPlan Build(HeftBuildSettings settings) => Plan(settings);

    /// <summary><c>heft test</c> (build, then Jest).</summary>
    public static CommandPlan Test(Action<HeftTestSettings> configure) => Configure(new HeftTestSettings(), configure);

    /// <summary><c>heft test</c> from pre-built settings.</summary>
    public static CommandPlan Test(HeftTestSettings settings) => Plan(settings);

    /// <summary><c>heft clean</c>.</summary>
    public static CommandPlan Clean(Action<HeftCleanSettings> configure) => Configure(new HeftCleanSettings(), configure);

    /// <summary><c>heft clean</c> from pre-built settings.</summary>
    public static CommandPlan Clean(HeftCleanSettings settings) => Plan(settings);

    /// <summary><c>heft run</c> with an explicit phase selection.</summary>
    public static CommandPlan Run(Action<HeftRunSettings> configure) => Configure(new HeftRunSettings(), configure);

    /// <summary><c>heft run</c> from pre-built settings.</summary>
    public static CommandPlan Run(HeftRunSettings settings) => Plan(settings);

    /// <summary>
    /// A rig-defined phase action, e.g. <c>Heft.Phase("package-solution", s =&gt; s.SetProduction())</c>
    /// for SharePoint Framework. The action name is passed as Heft spells it.
    /// </summary>
    public static CommandPlan Phase(string action, Action<HeftActionSettings> configure) =>
        Configure(new HeftActionSettings(action), configure);

    /// <summary>
    /// Escape hatch: <c>heft &lt;arguments&gt;</c> exactly as given, with the same binary resolution. No validation.
    /// </summary>
    public static CommandPlan Raw(string? workingDirectory, params string[] arguments)
    {
        if (arguments is null) throw new ArgumentNullException(nameof(arguments));
        var resolution = HeftBinaryResolver.TryResolve(workingDirectory ?? Directory.GetCurrentDirectory())
            ?? throw new InvalidOperationException("Heft was not found for the working directory; run your package install first.");
        var args = new List<string>(resolution.PrefixArguments);
        args.AddRange(arguments);
        return new CommandPlan { Executable = resolution.Executable, Arguments = args, WorkingDirectory = workingDirectory };
    }

    private static CommandPlan Configure<T>(T settings, Action<T> configure) where T : HeftSettingsBase
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        configure(settings);
        return settings.ToCommandPlan();
    }

    private static CommandPlan Plan(HeftSettingsBase settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        return settings.ToCommandPlan();
    }
}
