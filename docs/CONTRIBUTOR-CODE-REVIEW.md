# Contributor project review

Reviewed 2026-11-02 against these upstream GitHub snapshots (short commit IDs below). This is an engineering
and license triage for RimPulse's current design, not a claim that every file in every upstream repository
has received a line-by-line legal or security audit. Before copying a specific file, inspect its current
copyright/license headers and the full license at that revision, since repositories can change.

## Findings and recommendations

### Runtime bridge and IPC

- **RimPulse's present protocol is intentionally narrower than RimBridgeServer's.** The current companion
  sends a bounded startup snapshot over a per-launch loopback connection with a fresh secret, mutual
  authentication, expected-PID/version checks, reconnection handling, and no commands. RimBridgeServer
  provides a broad live-game tool surface (actions, UI, state, screenshots, tools/capabilities, diagnostics).
  Its modular capabilities, operation outcomes, readiness waits, and fault-isolated tool discovery are
  valuable design references for a future authorized command protocol. Do not transplant that server stack:
  it is much larger than the current problem and RimPulse's existing handshake is a better security boundary
  for a minimal status channel. If commands are added, define least-privilege operations, cancellation,
  acknowledgement/result IDs, state/readiness semantics, and game-thread dispatch before implementing them.
- **RimWorldHotReload** exposes an HTTP trigger and watches assemblies/assets/defs, but its implementation
  does not meet RimPulse's per-launch authentication model, and reload success is not equivalent to a
  confirmed game-side result. Its feature breakdown is a reference, not code suitable for direct reuse.
- **Reloader** is useful evidence about runtime limits: its README explicitly describes loading successive
  assemblies and detouring methods rather than unloading old assemblies. That design can accumulate memory
  and patch state. Do not copy its reload mechanism into RimPulse; continue to research unloadability and
  use confirmed restart fallback where safe reload cannot be proven.

### Testing and validation

- **Rimworld-Pickle** provides the clearest improvement opportunity: real in-game tests that exercise UI,
  wait on live game state/ticks, and assert outcomes rather than treating a process launch as success. Adopt
  this as a future verification strategy (or interoperable optional test runner), not as a hard dependency
  for RimPulse users. It is MIT licensed in the reviewed snapshot.
- **RimTest Redux** is dual-licensed `Apache-2.0 OR MIT` in the reviewed snapshot (not merely a custom
  license). The catalog now records that accurately. It is a possible reference for unit-level in-game test
  discovery/execution; any future code reuse must select one offered license and preserve its notices.
- **steam-game-image-action** suggests a route to repeatable integration tests with actual game assemblies.
  Its use is gated by game distribution, image privacy, and CI terms; investigate before making game assets
  part of any automated build.
- **RimworldXsd** offers schema-based XML diagnostics, but the reviewed repo has no declared license. Use
  schema concepts or independently authored rules only; do not copy its implementation without a license grant.

### Logging, project management, and release

- **rimworld-logging-framework** demonstrates structured channels, levels, multiple sinks, and throttling.
  RimPulse already has categorized levels and bounded rotating logs; the useful incremental idea is structured
  context and filtering, but the framework is designed for in-game RimWorld mods, not a direct dependency for
  the desktop app. No code was copied.
- **RimSort** contains mature mod metadata/load-order functionality, but is GPL-3.0. Its behavior can inform
  requirements; code reuse would require an explicit compatibility/distribution review. RimPulse has its own
  tested dependency-closure/load-order implementation and should compare edge cases rather than replace it
  wholesale.
- **pickle-template** is useful as an example project layout and CI setup, not a shared library to pull into
  RimPulse.
- **semantic-release-steam** and **RimWorks (organisation)** are release/engineering references; they do not
  justify adding Steam publishing automation to a desktop developer tool without a product requirement.

### Code-review change made

`ProjectMonitor` flushes watcher events from a worker-thread timer. Previously, an exception thrown by any
`ChangesDetected` subscriber could escape the timer callback, potentially terminating the app and preventing
later subscribers from receiving the batch. It now isolates each subscriber, reports consumer failures through
`MonitoringError`, and makes error subscribers best-effort as well. A regression test covers this failure path.
This follows the general fault-isolation principle used by mature tool/plugin systems; it is an original,
small fix, not copied source code.

## Reviewed upstream snapshots

| Repository | Snapshot | License noted in reviewed tree | Disposition |
| --- | --- | --- | --- |
| [Reloader](https://github.com/pardeike/Reloader) | `87db92f` | MIT | Read reload constraints; no code reused |
| [RimWorldHotReload](https://github.com/xiao-e-yun/RimWorldHotReload) | `9aa16bd` | BSD-3-Clause | Feature/architecture reference; no code reused |
| [RimBridgeServer](https://github.com/pardeike/RimBridgeServer) | `1dcd044` | MIT | Architecture reference; no code reused |
| [RimWorldDevMCP](https://github.com/Jeffrharr/RimWorldDevMCP) | `2a650b7` | MIT | External automation reference; no code reused |
| [RimTest Redux](https://github.com/ilyvion/rimtest-redux) | `d3ba773` | Apache-2.0 OR MIT | License corrected in credits; no code reused |
| [Rimworld-Pickle](https://github.com/RimWorks/Rimworld-Pickle) | `525b556` | MIT | Live-test strategy reference; no code reused |
| [pickle-template](https://github.com/RimWorks/pickle-template) | `3c0dd2a` | MIT | Project-template reference; no code reused |
| [rimworld-logging-framework](https://github.com/RimWorks/rimworld-logging-framework) | `2315523` | MIT | Logging design reference; no code reused |
| [steam-game-image-action](https://github.com/RimWorks/steam-game-image-action) | `a72e50e` | MIT | CI feasibility reference; no code reused |
| [semantic-release-steam](https://github.com/RimWorks/semantic-release-steam) | `177feb7` | MIT | Release reference; no code reused |
| [RimSort](https://github.com/RimSort/RimSort) | `bbe8aac` | GPL-3.0 | Requirements reference only; no code reused |
| [RimworldXsd](https://github.com/Ragonfana/RimworldXsd) | `954e6f9` | No license declared | No code reused |
| [RimLoc](https://github.com/0-danielviktorovich-0/RimLoc) | `019755b` | GPL-3.0 | Research reference only; no code reused |
| [AI in RimWorld](https://github.com/ryseymour/ai-in-rimworld) | `d0fccfa` | No license declared | Research reference only; no code reused |

Other credits entries include organizations, search-result collections, the proprietary game decompilation,
and shipped dependencies (Avalonia, Roslyn, xUnit); these are not contributor repositories whose source was
copied into RimPulse. Exact source-level reuse decisions must be recorded here and in the in-app Credits if
that changes.

## Verification limits

The development environment for this review does not have `dotnet`, so the C# suite could not be executed.
`git diff --check` is available; the code/test change needs CI or a .NET SDK run before merge. Nothing in this
review establishes real-game behavior on RimWorld 1.6 or 1.7.
