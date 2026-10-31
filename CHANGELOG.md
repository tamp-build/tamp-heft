# Changelog

All notable changes to `Tamp.Heft.V1` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — 2026-10-31

### Added

- Initial release: a Tamp wrapper for Rushstack Heft 1.x, verified against Heft 1.3.2 as used by SharePoint Framework 1.24.0.
  - `Heft.Build`, `Heft.Test` (with the Jest plugin's flags), `Heft.Clean`, `Heft.Run` (phase scoping, plus scoped parameters after `--`), `Heft.Phase(name, …)` for rig-defined phase actions, and `Heft.Raw`. Fluent and object-init forms.
  - Global `--debug` / `--unmanaged`, `NODE_OPTIONS`, environment variables and pass-through plugin arguments.
  - Watch-mode actions (`start`, `*-watch`) are refused, because they never exit.
- `HeftBinaryResolver`: the installed `@rushstack/heft` package at the working directory or any parent, run through `node`; then the `node_modules/.bin` shim; then a global `heft`. No `npm exec` / `npx` fallback, so nothing outside the lockfile is ever downloaded.
- Multi-targets `net8.0;net9.0;net10.0`.
