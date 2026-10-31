namespace Tamp.Heft.V1.Tests;

/// <summary>A throwaway directory shaped like a project with (or without) an installed Heft.</summary>
internal sealed class TempProject : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tamp-heft-tests", Guid.NewGuid().ToString("N"));

    public TempProject() => Directory.CreateDirectory(Root);

    public string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { Root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Install a fake @rushstack/heft package under <paramref name="projectDir"/>/node_modules.</summary>
    public string InstallHeft(string projectDir, string version = "1.3.2", string binJson = "{ \"heft\": \"./bin/heft\" }", bool writeScript = true)
    {
        var pkg = Path.Combine(projectDir, "node_modules", "@rushstack", "heft");
        Directory.CreateDirectory(Path.Combine(pkg, "bin"));
        File.WriteAllText(Path.Combine(pkg, "package.json"),
            $$"""{ "name": "@rushstack/heft", "version": "{{version}}", "bin": {{binJson}} }""");
        var script = Path.Combine(pkg, "bin", "heft");
        if (writeScript) File.WriteAllText(script, "#!/usr/bin/env node\n");
        return script;
    }

    public string InstallShim(string projectDir)
    {
        var bin = Path.Combine(projectDir, "node_modules", ".bin");
        Directory.CreateDirectory(bin);
        var shim = Path.Combine(bin, OperatingSystem.IsWindows() ? "heft.cmd" : "heft");
        File.WriteAllText(shim, "");
        return shim;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
