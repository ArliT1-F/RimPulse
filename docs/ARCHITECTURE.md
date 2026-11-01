# RimPulse architecture

This document describes what exists today and the boundaries future milestones must respect. Anything
marked **planned** is design intent, not implemented behaviour.

## 1. Principles

1. **The game is the source of truth.** RimPulse inspects and drives a real RimWorld installation. No
   simulated game engine, no invented definition database, no fabricated capability.
2. **Version-specific code stays isolated.** 1.6 (Unity/Mono) and 1.7 (Waylith/.NET 10) differ in
   runtime, assembly layout and mod build inputs; see [`GAME-VERSIONS.md`](GAME-VERSIONS.md).
3. **The UI never blocks.** Filesystem scans, builds and runtime work run off the UI thread and report
   progress through immutable results and a diagnostic log.
4. **Fail visibly, never silently.** Every failure ends up in the activity log with the reason, and in the
   rotating log file for later troubleshooting.
5. **No placeholders.** A feature that is not implemented is absent from the UI and documented as such.
6. **Minimal dependencies.** Each package must justify itself (see §7).

## 2. Layers

```text
┌──────────────────────────────────────────────┐
│  RimPulse.App  (Avalonia 12 / net10.0)       │
│  Views → ViewModels → Services (composition) │
└───────────────────┬──────────────────────────┘
                    │ project reference (one direction only)
┌───────────────────▼──────────────────────────┐
│  RimPulse.Core  (net10.0, UI-independent)    │
│  Configuration · Diagnostics · Game ·        │
│  Installations · Platform                    │
└──────────────────────────────────────────────┘
```

`RimPulse.Core` must never reference Avalonia, and must be testable without a display server or a RimWorld
installation. `RimPulse.App` may not contain filesystem or game logic that belongs in Core.

Planned for later milestones (none of this exists yet):

```text
RimPulse.Core/Projects      mod project model, About.xml parsing, dependency graph
RimPulse.Core/Monitoring    FileSystemWatcher with debouncing and change classification
RimPulse.Core/Build         Roslyn compilation, reference resolution
RimPulse.Core/Validation    XML validation, definition diagnostics
RimPulse.Core/Runtime       process launch/management, runtime state machine
RimPulse.Core/IPC           versioned JSON protocol shared with the companion mods
RimPulse.Backends/          RimWorld16 · RimWorld17 (version-specific implementations of the above)
RimPulse.Companion/         companion mods: Shared · RimWorld16 · RimWorld17
```

## 3. Current components

| Area | Type | Responsibility |
| --- | --- | --- |
| Platform | `HostPlatform`, `HostPlatformExtensions` | Platform enum, detection, case-sensitivity rules. Passed explicitly instead of calling `OperatingSystem` deep in the logic, so every branch is testable from any machine |
| Paths | `AppPaths` | XDG (Linux), `%APPDATA%`/`%LOCALAPPDATA%` (Windows), `~/Library` (macOS) resolution |
| Configuration | `RimPulseConfiguration` | The persisted settings model (selected installation, custom paths, auto-detect) |
| Configuration | `ConfigurationStore` | Async JSON load/save, atomic write (temp file + move), corrupt-file quarantine, schema version |
| Diagnostics | `IDiagnosticLog`, `DiagnosticLog`, `RotatingFileLogSink`, `NullDiagnosticLog` | Bounded in-memory ring of log entries, event for the UI, rotating file sink capped in size and archive count |
| Game | `GameVersion` | `major.minor` + build text, parsing of `Version.txt`, comparison, supported-version check |
| Installations | `RimWorldInstallation` | Immutable inspection result, including *why* an installation is unusable |
| Installations | `InstallationProbe` | Layout detection (Unity vs Waylith), assembly verification, version reading, path normalisation |
| Installations | `VersionFileReader` | Reads `Version.txt` — the only authoritative version source |
| Installations | `RimWorldInstallationDetector` | Collects candidates (manual, `RIMWORLD_DIR`, Steam, GOG), probes, de-duplicates, orders |
| Installations | `ValveKeyValues`, `SteamLibraryReader` | Minimal VDF/ACF reader and Steam library discovery |
| App | `RimPulseServiceGraph` | Composition root: builds paths, log, configuration store and detector; owns their lifetime |
| App | `MainWindowViewModel` | Installaion list, selection, custom path entry, activity log mirror, status text |
| App | `InstallationViewModel`, `DetailRow`, `ProblemRow` | Presentation projections of Core results |

## 4. Detection pipeline

```text
candidate paths ──► InstallationProbe ──► RimWorldInstallation ──► de-duplicate ──► order ──► UI
(manual, RIMWORLD_DIR,                 (layout, version,          (symlink-aware,   (usable first,
 Steam libraries, GOG)                  assemblies, problems)      platform case)    supported first)
```

Rules the pipeline enforces:

- **Automatic candidates are filtered, manual candidates are not.** A Steam library that contains an empty
  `RimWorld` folder is silently skipped; a path the user typed is always listed with the reason it cannot be
  used.
- **Manual wins over automatic** when the same path is discovered twice, because the user's label is more
  useful than "Steam".
- **De-duplication resolves symlinks** (Steam libraries are often symlinked) and compares case-insensitively
  only on Windows/macOS.
- **Ordering** is stable: usable → supported version → higher version → path.

## 5. Threading and lifetime

- `InitializeAsync` runs on the UI thread, loads configuration, then awaits
  `Task.Run(detector.DetectAsync)` so discovery never blocks the interface.
- Continuations return to the UI thread, which is where `ObservableCollection` mutations happen. A single
  `IsBusy` flag makes scans non-reentrant and disables the commands while one is running.
- Diagnostic entries can be written from any thread; `MainWindowViewModel` marshals them to the UI thread
  (`Dispatcher.UIThread.CheckAccess` / `Post`) and keeps the visible list bounded (250 entries).
- The service graph is disposed on application exit, which flushes and closes the log file.

## 6. Error handling

| Failure | Behaviour |
| --- | --- |
| Configuration file missing | Logged at info, defaults used, file created on first save |
| Configuration file corrupt | Moved to `config.json.corrupt-<timestamp>`, defaults used, warning logged |
| Configuration directory unwritable | Startup shows an explicit error window instead of crashing |
| Installation path missing/unreadable | Reported on the installation, not thrown |
| Steam VDF malformed | Warning logged, that Steam root skipped, other roots still scanned |
| Log sink failure | Swallowed by design; logging must never take the app down |
| Unexpected exception in a UI command | Logged, surfaced in the status bar, application keeps running |

## 7. Dependencies and why

| Package | Why it is justified |
| --- | --- |
| `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` (12.1.4) | Cross-platform desktop UI required by the brief. Avalonia 12 targets .NET 8+ and recommends .NET 10, which matches the rest of the stack |
| `xunit.v3` (4.0.2) | Test framework. Runs on Microsoft.Testing.Platform, so no VSTest packages are needed |
| *none* for VDF, MVVM, logging or JSON | Each is 50–150 lines of straightforward code in this repository; a dependency would add supply-chain and API-churn risk for no benefit |

Deliberately avoided: `CommunityToolkit.Mvvm` (three small base types are enough), `Avalonia.Diagnostics`
(removed in Avalonia 12 and replaced by a licensed Plus package RimPulse does not need), any VDF/Steam
library (the format subset needed is tiny).

## 8. Decisions

| # | Decision | Rationale |
| --- | --- | --- |
| D1 | Desktop app targets **net10.0** | Current LTS, recommended by Avalonia 12, and the same runtime family RimWorld 1.7 mods use. The game's own runtime is a separate concern and is never assumed |
| D2 | **Avalonia 12.1.4** | Latest stable line; compiled bindings are on by default, which turns binding typos into build errors |
| D3 | **`.slnx` solution + central package management** | No GUID churn, one place to bump package versions (`.NET 10 SDK` required, matching `global.json`) |
| D4 | `Version.txt` is the **only** version source | Guessing a version from a folder name or engine layout would violate the "never claim what you did not verify" rule |
| D5 | Layout is detected **structurally**, not from the version | Real installs have been seen with mismatched folder names; the presence of `Assembly-CSharp.dll` in the root is unambiguous for Waylith |
| D6 | Config and logs live in **XDG/AppData paths**, never inside the game or mod directory | RimPulse must not modify user data outside an explicit action |
| D7 | Camera-ready `RimWorldInstallation` includes **problems**, not just success | The UI must be able to explain failures instead of showing an empty list |
| D8 | Log buffer bounded **and** file rotation capped | Long development sessions must not grow memory or disk without limit |

## 9. Testing strategy

- Everything in `RimPulse.Core` is covered by tests that run on any machine (no game required): version
  parsing, `Version.txt` reading, both installation layouts, missing-assembly reporting, Steam VDF parsing
  (modern and legacy), library/manifest reading, detection ordering and de-duplication, configuration
  round-trip and corruption recovery, XDG/Windows/macOS path resolution, and the diagnostic log.
- Tests use real temporary directories (`TempDirectory`) and real files — no mocking framework and no
  in-memory filesystem abstraction. This keeps the tested code on the same path as production code.
- UI code is not unit tested (it is thin); the logic it depends on is in Core.
- Game-dependent behaviour (milestones 4–6) will need an integration test harness against a real
  installation; the verification status must be recorded honestly per feature.

## 10. Planned boundaries for later milestones

- **Version-specific backends** — a common interface in Core (`IRimWorldBackend`), with
  `RimWorld16Backend` and `RimWorld17Backend` implementations that are free to differ in reference
  resolution, assembly loading and reload strategy. No shared implementation will be forced where the
  runtimes differ.
- **Companion mods** — one per game version (`RimPulse.Companion.RimWorld16` targeting the 1.6 runtime,
  `RimPulse.Companion.RimWorld17` targeting `net10.0`), sharing a versioned JSON IPC contract that lives in
  Core so both sides are compiled against the same definitions.
- **Process management** — the game process is owned by RimPulse, launched with a managed development
  configuration, and its exit is treated as a normal state transition, never as a crash of the desktop app.
- **Reload policy** — asset/definition/assembly reload are separate capabilities with an explicit
  `stable / experimental / unsupported` rating per game version; a restart is always preferable to an
  unsafe unload.
