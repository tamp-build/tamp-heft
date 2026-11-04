# Tamp.Heft.V1

> Tamp wrapper for [Rushstack Heft](https://heft.rushstack.io/) 1.x, the build orchestrator behind SharePoint Framework 1.22+ and Rush monorepos. Typed `build` / `test` / `clean` / `run` verbs, plus any phase action a rig defines (`package-solution` for SPFx). Runs the project's own pinned Heft, never a downloaded one.

| Package | Status |
|---|---|
| `Tamp.Heft.V1` | 0.3.0 |

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
- **Reporting.** On the event stream the server is a service: `tool.invoked`, `service.ready` (kind `heft-dev-server`, with URL, port, PID, manifests URL as `readinessProbe`, and the state-file path), `service.stopped` with the reason it stopped, and `tool.exited`, all on one span. Once ready it also prints the URL, port, manifests URL, PID and state-file path, and writes a `HeftDevServerState` JSON file atomically, announced with `artifact.produced` (kind `heft-dev-server`; kept for one more minor version, prefer `service.ready`). The file holds only the URL, manifests URL, port, PID, process start time, ready timestamp and versions: no cookies, credentials or debug query strings. The default path is `.tamp/temp/heft-dev-server/<port>.json`, in Tamp's ignored work area; `SetStatePath` changes it.
- **Shutdown.** Disposing the handle, Ctrl+C / SIGTERM / SIGHUP and the end of the build all kill the whole process tree (Node and its webpack children) and delete the state file. `service.stopped` says why: `requested` (disposed, or still running when the build ended), `signal`, `exited` (Heft exited on its own) or `timeout` (never ready). Read that, not `tool.exited`: a server stopped on purpose is killed, so its exit code is non-zero (137 on Linux). Inside a `tamp` run, Ctrl+C ends a `RunUntilCancelled` target as `cancelled` (exit 130). A server can serve later targets, but never outlives the build. The process handling is Tamp.Core's [`ManagedProcess`](https://github.com/tamp-build/tamp/blob/main/docs/agent-first.md#long-running-services), so this needs Tamp.Core 1.18.0 or later.
- **Never deploys.** Only `start` or a `*-watch` action can run (`SetAction("build-watch").AddArgument("--serve")` for a rig without the alias), and any action or argument containing `deploy` (`deploy-azure-storage`, `dev-deploy`) is refused.

**Verified against a real SPFx project.** `tests/spfx-fixture` is a stock SPFx 1.24.1 solution with a Form Customizer (Heft 1.3.2). The `SPFx integration` workflow (Linux; nightly, on demand, and on PRs that touch the dev server) runs `npm ci` there and checks that `heft start` becomes ready, serves the component's manifests over HTTPS with Heft's own dev certificate, and leaves nothing listening after disposal. A cold `--clean` start was ready in about 6 seconds on a 4-core Linux machine, well inside the 5-minute default `ReadyTimeout`. To run it locally: `npm ci` in the fixture, then `TAMP_HEFT_SPFX_FIXTURE=<fixture path> dotnet test --filter "FullyQualifiedName~RealSpfx"`. On Windows it isn't run in CI: Heft requires its dev CA in the current user's trusted root store, and Windows only allows that after an interactive confirmation, which a hosted runner can't give ([#7](https://github.com/tamp-build/tamp-heft/issues/7)). Locally, run `heft trust-dev-cert` (or `Spfx.TrustDevCertificate`) once and accept the prompt; after that the test, and `heft start`, run unattended.

Out of scope for now: a detached mode (start in one `dotnet tamp` command, stop in a later one).

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
