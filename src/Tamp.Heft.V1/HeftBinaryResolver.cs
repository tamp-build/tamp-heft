using System.Text.Json;

namespace Tamp.Heft.V1;

/// <summary>How the Heft invocation was located.</summary>
public enum HeftResolutionSource
{
    /// <summary>
    /// The project's installed <c>@rushstack/heft</c> package, run as <c>node &lt;package&gt;/bin/heft</c>.
    /// Found by walking up from the working directory, so a hoisted monorepo install is found too.
    /// Preferred: it runs the exact pinned Heft and doesn't go through a <c>.cmd</c> shim.
    /// </summary>
    ProjectPackage,

    /// <summary>
    /// The project's <c>node_modules/.bin/heft</c> shim (<c>heft.cmd</c> on Windows). Used only when
    /// the package is installed but <c>node</c> isn't on <c>PATH</c>.
    /// </summary>
    ProjectShim,

    /// <summary>A global <c>heft</c> on <c>PATH</c>.</summary>
    Global,

    /// <summary>The adopter supplied the resolution via <c>SetBinary</c>.</summary>
    Explicit,
}

/// <summary>
/// A resolved Heft invocation: the executable to spawn plus the arguments that go before the
/// Heft command line (for <see cref="HeftResolutionSource.ProjectPackage"/>, the path of the bin script).
/// </summary>
public sealed record HeftBinaryResolution
{
    /// <summary>Path of the executable to spawn (<c>node</c>, the shim, or a global <c>heft</c>).</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments emitted before the Heft command (empty unless running through <c>node</c>).</summary>
    public IReadOnlyList<string> PrefixArguments { get; init; } = Array.Empty<string>();

    /// <summary>How this resolution was produced.</summary>
    public required HeftResolutionSource Source { get; init; }

    /// <summary>The installed <c>@rushstack/heft</c> version, when it was read from the package (else null).</summary>
    public string? Version { get; init; }
}

/// <summary>
/// Locates Heft for a working directory. In priority order:
/// <list type="number">
///   <item>The installed <c>@rushstack/heft</c> package in <c>node_modules</c> at the working directory or any
///         ancestor, run through <c>node</c>.</item>
///   <item>The <c>node_modules/.bin/heft</c> shim at the working directory or any ancestor.</item>
///   <item>A global <c>heft</c> on <c>PATH</c>.</item>
///   <item><c>null</c>: nothing installed (see <see cref="IsAvailable"/>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no <c>npm exec</c> / <c>npx</c> fallback. Either can download a package that
/// isn't in the lockfile, which is the wrong failure mode for a build machine: a missing install should
/// fail the build, not fetch whatever the registry serves today.
/// </para>
/// <para>
/// Running <c>node &lt;bin script&gt;</c> rather than the Windows <c>heft.cmd</c> shim also keeps the argument
/// list out of <c>cmd.exe</c>, whose quoting rules differ from every other process launch.
/// </para>
/// </remarks>
public static class HeftBinaryResolver
{
    private const string PackageName = "@rushstack/heft";

    /// <summary>Resolve Heft for <paramref name="workingDirectory"/>, or null when it isn't installed.</summary>
    public static HeftBinaryResolution? TryResolve(string workingDirectory) =>
        TryResolve(workingDirectory, FindOnPath("node"), FindOnPath(OperatingSystem.IsWindows() ? "heft.cmd" : "heft"));

    /// <summary>True when <see cref="TryResolve(string)"/> finds Heft for <paramref name="workingDirectory"/>.</summary>
    public static bool IsAvailable(string workingDirectory) => TryResolve(workingDirectory) is not null;

    // Seam for tests: PATH lookups are passed in rather than read, so tests never mutate PATH.
    internal static HeftBinaryResolution? TryResolve(string workingDirectory, string? node, string? globalHeft)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("workingDirectory is required.", nameof(workingDirectory));

        string? shim = null;
        foreach (var dir in SelfAndAncestors(Path.GetFullPath(workingDirectory)))
        {
            var modules = Path.Combine(dir, "node_modules");
            if (!Directory.Exists(modules)) continue;

            var package = Path.Combine(modules, "@rushstack", "heft");
            var bin = ReadBinScript(package, out var version);
            if (bin is not null && node is not null)
                return new HeftBinaryResolution
                {
                    Executable = node,
                    PrefixArguments = new[] { bin },
                    Source = HeftResolutionSource.ProjectPackage,
                    Version = version,
                };

            shim ??= ShimIn(modules);
            if (bin is not null && shim is not null)
                return new HeftBinaryResolution { Executable = shim, Source = HeftResolutionSource.ProjectShim, Version = version };
        }

        if (shim is not null)
            return new HeftBinaryResolution { Executable = shim, Source = HeftResolutionSource.ProjectShim };

        return globalHeft is null ? null : new HeftBinaryResolution { Executable = globalHeft, Source = HeftResolutionSource.Global };
    }

    /// <summary>
    /// The bin script of an installed <c>@rushstack/heft</c> package, from its <c>package.json</c>
    /// <c>bin</c> field (either the string form or the <c>{"heft": "..."}</c> form).
    /// </summary>
    internal static string? ReadBinScript(string packageDirectory, out string? version)
    {
        version = null;
        var manifest = Path.Combine(packageDirectory, "package.json");
        if (!File.Exists(manifest)) return null;

        string? relative;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            if (!root.TryGetProperty("name", out var name) || name.GetString() != PackageName) return null;
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) version = v.GetString();
            relative = root.TryGetProperty("bin", out var binEl) ? binEl.ValueKind switch
            {
                JsonValueKind.String => binEl.GetString(),
                JsonValueKind.Object when binEl.TryGetProperty("heft", out var h) && h.ValueKind == JsonValueKind.String => h.GetString(),
                _ => null,
            } : null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(relative)) return null;
        var script = Path.GetFullPath(Path.Combine(packageDirectory, relative));
        // A bin entry that points outside its own package is not something to run.
        var packageRoot = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        if (!script.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(script) ? script : null;
    }

    private static string? ShimIn(string nodeModules)
    {
        var bin = Path.Combine(nodeModules, ".bin");
        var candidate = Path.Combine(bin, OperatingSystem.IsWindows() ? "heft.cmd" : "heft");
        return File.Exists(candidate) ? candidate : null;
    }

    private static IEnumerable<string> SelfAndAncestors(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            yield return dir.FullName;
    }

    internal static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        var names = OperatingSystem.IsWindows() && !Path.HasExtension(name) ? new[] { name + ".exe", name + ".cmd", name } : new[] { name };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var n in names)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim('"'), n); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
