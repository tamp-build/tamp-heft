# Tamp.Heft.V1

> Tamp wrapper for [Rushstack Heft](https://heft.rushstack.io/) 1.x, the build orchestrator behind SharePoint Framework 1.22+ and Rush monorepos. Typed `build` / `test` / `clean` / `run` verbs, plus any phase action a rig defines (`package-solution` for SPFx). Runs the project's own pinned Heft, never a downloaded one.

| Package | Status |
|---|---|
| `Tamp.Heft.V1` | 0.1.0 (initial) |

```bash
dotnet add package Tamp.Heft.V1
```

Multi-targets net8 / net9 / net10. For SharePoint Framework projects, use [`Tamp.Spfx`](https://github.com/tamp-build/tamp-spfx), which builds on this package and adds the SPFx project model, version stamping and `.sppkg` inspection.

## Quick start

```csharp
using Tamp;
using Tamp.Heft.V1;

AbsolutePath Web => RootDirectory / "web";

Target Test => _ => _
    .DependsOn(nameof(NpmCi))
    .Executes(() => Heft.Test(s => s
        .SetWorkingDirectory(Web)
        .SetProduction()
        .SetClean()
        .SetMaxWorkers("50%")));

// A phase action the rig defines, by name:
Target Package => _ => _
    .DependsOn(nameof(Test))
    .Executes(() => Heft.Phase("package-solution", s => s.SetWorkingDirectory(Web).SetProduction()));
```

## Verbs

| Facade | Command line | Notes |
|---|---|---|
| `Heft.Build` | `heft build` | `SetProduction`, `SetClean`, `SetVerbose`, `AddLocale` |
| `Heft.Test` | `heft test` | Plus the Jest plugin flags: `SetJestConfig`, `SetMaxWorkers`, `SetTestPathPattern`, `AddTestPathIgnorePattern`, `SetTestNamePattern`, `SetTestTimeoutMs`, `SetSilent`, `SetDisableCodeCoverage`, `SetDetectOpenHandles`, `AddFindRelatedTests`, `SetLogHeapUsage`, `SetUpdateSnapshots` |
| `Heft.Clean` | `heft clean` | `AddTo` / `AddToExcept` / `AddOnly` scoping |
| `Heft.Run` | `heft run --to … -- <scoped>` | Needs a phase selection; `AddScopedArgument("--production")` goes after the `--` |
| `Heft.Phase(name, …)` | `heft <name>` | Any rig-defined phase action |
| `Heft.Raw(dir, args…)` | `heft <args…>` | Escape hatch: same resolution, no validation |

Every settings type also has `SetWorkingDirectory`, `SetEnvironmentVariable`, `SetNodeOptions` (e.g. `--max-old-space-size=8192`), the global `SetDebug` / `SetUnmanaged`, and `AddArgument` for plugin-defined parameters such as `--emit-stats`. There are object-init overloads for every verb.

**Watch modes are refused.** `start` and the `*-watch` actions never exit, so a build step that ran one would hang the build machine. `Heft.Raw` is there if you really mean it.

**Exit codes.** Heft exits non-zero when any task fails (compile, lint, test, bundle), so the default `ProcessRunner` behaviour is right; there's no "findings found" code to tolerate.

## Binary resolution

`HeftBinaryResolver` looks, in order, for:

1. **The installed `@rushstack/heft` package** in `node_modules` at the working directory *or any parent* (so a hoisted monorepo install is found), run as `node <package>/bin/heft`.
2. **The `node_modules/.bin/heft` shim** (`heft.cmd` on Windows), only if `node` isn't on `PATH`.
3. **A global `heft`** on `PATH`.

Two choices here are deliberate:

- **There is no `npm exec` / `npx` fallback.** Either can download a package that isn't in the lockfile. On a build machine a missing install should fail the build, not fetch whatever the registry serves today. Run your package install first (`Npm.Ci` from [`Tamp.Npm.V10`](https://github.com/tamp-build/tamp-npm)), or pre-flight with `HeftBinaryResolver.IsAvailable(dir)`.
- **`node <bin script>` rather than the Windows `.cmd` shim.** It keeps the argument list out of `cmd.exe`, whose quoting rules differ from every other process launch.

The package's `bin` entry is read from its `package.json` and must stay inside the package directory. `SetBinary(...)` overrides resolution entirely.

## Releasing

Releases follow the Tamp dogfood pattern: bump `<Version>` in `Directory.Build.props`, tag `v<X.Y.Z>`, and the release workflow runs `dotnet tamp Ci` then `dotnet tamp Push`.

## License

MIT.
