# Tamp.Heft.V1

> Tamp wrapper for [Rushstack Heft](https://heft.rushstack.io/) 1.x, the build orchestrator behind SharePoint Framework 1.22+ and Rush monorepos. Typed `build` / `test` / `clean` / `run` verbs, plus any phase action a rig defines (`package-solution` for SPFx). Runs the project's own pinned Heft, never a downloaded one.

| Package | Status |
|---|---|
| `Tamp.Heft.V1` | 0.2.0 |

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

**Watch modes are refused.** `start` and the `*-watch` actions never exit, so a build step that ran one would hang the build machine. For a local dev server, use the managed [`HeftDevServer`](#dev-server) below; `Heft.Raw` is there if you really mean it.

**Exit codes.** Heft exits non-zero when any task fails (compile, lint, test, bundle), so the default `ProcessRunner` behaviour is right; there's no "findings found" code to tolerate.

## Dev server

`HeftDevServer` runs the project's pinned Heft as `heft start` (the SPFx rig's alias for `build-watch --serve`) for local browser work: SPFx debugging, Form Customizer checks, Playwright evidence. It is opt-in and separate from the `Heft.*` verbs, so keep it out of your `Ci` target's dependencies.

```csharp
// One target that serves, runs the browser automation, then stops the server.
Target BrowserEvidence => _ => _
    .DependsOn(nameof(NpmCi))
    .Executes(async () =>
    {
        await using var server = await HeftDevServer.StartAsync(s => s
            .SetWorkingDirectory(Web)
            .SetReadyTimeout(TimeSpan.FromMinutes(3)));
        RunPlaywright(server.Url, server.ManifestsUrl); // https://localhost:4321/, .../temp/build/manifests.js
    }); // disposing kills the whole process tree

// Serve until Ctrl+C.
Target Serve => _ => _.Executes(() => HeftDevServer.RunUntilCancelled(s => s.SetWorkingDirectory(Web)));
```

- **Where it listens.** Read from the project's `config/serve.json` the way the SPFx rig reads it: `hostname`, else `ipAddress`, else `localhost`; `port` (default 4321); `https` (default false). `SetServeConfigPath` picks another file; `SetServerUrl` skips `serve.json` (for other rigs, or a `{tenantDomain}` hostname).
- **Ready means served.** `StartAsync` returns only once an HTTP(S) GET of `temp/build/manifests.js` (`SetReadinessPath`) succeeds. That file only exists after the first webpack compilation, so an open port isn't enough. If Heft exits first, or `SetReadyTimeout` (default 5 minutes) runs out, it throws with the last lines of Heft's output. The certificate check is relaxed only for loopback hosts, to accept the self-signed SPFx dev certificate.
- **No stale servers.** Startup refuses if the port is already in use, so readiness can't be answered by some other server. It also refuses if the state file belongs to a live server from an earlier run (same PID *and* process start time). A state file left by a dead process is removed.
- **Reporting.** Once ready it prints the URL, port, manifests URL, PID and state-file path. It writes a `HeftDevServerState` JSON file atomically and announces it with `artifact.produced` (kind `heft-dev-server`) on the event stream, next to the usual `tool.invoked` / `tool.exited`. The file holds only the URL, manifests URL, port, PID, process start time, ready timestamp and versions: no cookies, credentials or debug query strings. The default path is `.tamp/temp/heft-dev-server/<port>.json`, in Tamp's ignored work area; `SetStatePath` changes it.
- **Shutdown.** Disposing the handle, Ctrl+C, SIGTERM, SIGHUP and normal process exit all kill the whole process tree (Node and its webpack children) and delete the state file. Because the tree is killed, `tool.exited` reports a non-zero exit code (137 on Linux) for a server that was stopped on purpose.
- **Never deploys.** Only `start` or a `*-watch` action can run (`SetAction("build-watch").AddArgument("--serve")` for a rig without the alias), and any action or argument containing `deploy` (`deploy-azure-storage`, `dev-deploy`) is refused.

Out of scope for now: a detached mode (start in one `dotnet tamp` command, stop in a later one) and a dedicated `service.ready` event type, which needs Tamp.Core.

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
