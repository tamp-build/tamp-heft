# Changelog

All notable changes to `Tamp.Heft.V1` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.0] — 2026-11-04

Requires Tamp.Core 1.18.0.

### Changed

- **`HeftDevServer` is built on Tamp.Core's `ManagedProcess`** ([tamp-build/tamp#125](https://github.com/tamp-build/tamp/issues/125)). Process start, readiness, process-tree kill, the stop race and signal handling now come from the core, which owns Ctrl+C / SIGTERM / SIGHUP for the whole run. The public API and the state file are unchanged.
- **Inside a `tamp` run, Ctrl+C ends a `RunUntilCancelled` target as `cancelled`** (exit 130), as Tamp.Core 1.18.0 reports interrupts, instead of returning normally. Cancelling its `cancellationToken` still just returns.
- A server still running when the build ends is now stopped then (Tamp.Core's backstop), so one started in one target can serve later ones, but never outlives the build. Its state file is removed however it stops.

### Added

- **`service.ready` / `service.stopped` events** (#3): `HeftDevServer` reports itself as a service (kind `heft-dev-server`, URL, port, PID, manifests URL, state-file path) with the reason it stopped (`requested`, `signal`, `exited`, `timeout`), on the same span as `tool.invoked` / `tool.exited`. `artifact.produced` for the state file stays for one more minor version.
- `HeftDevServer.ExitCode` and `HeftDevServer.StopReason`.
- Integration test of `HeftDevServer` against a real SPFx `heft start` (#2): `tests/spfx-fixture` (a stock SPFx 1.24.1 Form Customizer solution with its lockfile), `RealSpfxDevServerTests` (runs only when `TAMP_HEFT_SPFX_FIXTURE` is set), and the `SPFx integration` workflow (Linux; nightly, on demand, and on PRs that touch the dev server). Windows isn't covered in CI: Heft needs its dev CA in the user root store, which Windows only allows interactively.

## [0.2.0] — 2026-11-03

### Added

- `HeftDevServer`: a managed `heft start` for local browser work (#1). Verified against the SPFx 1.24.1 rig (Heft 1.3.2).
  - `StartAsync` returns once an HTTP(S) request for `temp/build/manifests.js` succeeds; disposing it kills the whole process tree. `RunUntilCancelled` serves until Ctrl+C.
  - The URL and port come from `config/serve.json` (`hostname` / `ipAddress`, `port`, `https`), or from `SetServerUrl`.
  - Refuses to start when the port is already in use, or when the state file belongs to a live server from an earlier run (PID plus process start time).
  - Writes a `HeftDevServerState` file atomically once ready (default `.tamp/temp/heft-dev-server/<port>.json`) and emits `artifact.produced` for it with kind `heft-dev-server`. The file is deleted on shutdown.
  - Ctrl+C, SIGTERM, SIGHUP and process exit all stop the server and remove the state file.
  - Only `start` / `*-watch` actions run; anything mentioning `deploy` is refused.
- `HeftDevServerSettings`, `HeftServeConfig`, `HeftDevServerState`.

## [0.1.0] — 2026-10-31

### Added

- Initial release: a Tamp wrapper for Rushstack Heft 1.x, verified against Heft 1.3.2 as used by SharePoint Framework 1.24.0.
  - `Heft.Build`, `Heft.Test` (with the Jest plugin's flags), `Heft.Clean`, `Heft.Run` (phase scoping, plus scoped parameters after `--`), `Heft.Phase(name, …)` for rig-defined phase actions, and `Heft.Raw`. Fluent and object-init forms.
  - Global `--debug` / `--unmanaged`, `NODE_OPTIONS`, environment variables and pass-through plugin arguments.
  - Watch-mode actions (`start`, `*-watch`) are refused, because they never exit.
- `HeftBinaryResolver`: the installed `@rushstack/heft` package at the working directory or any parent, run through `node`; then the `node_modules/.bin` shim; then a global `heft`. No `npm exec` / `npx` fallback, so nothing outside the lockfile is ever downloaded.
- Multi-targets `net8.0;net9.0;net10.0`.
