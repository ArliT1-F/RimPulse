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

What each module contains today:

```text
RimPulse.Core/Projects      About.xml metadata, content folders, LoadFolders.xml interpretation
RimPulse.Core/Monitoring    FileSystemWatcher with debouncing and change classification
RimPulse.Core/Build         .csproj discovery, game reference resolution, Roslyn compilation
RimPulse.Core/Validation    changed-file XML syntax and conservative Defs structure checks
RimPulse.Core/Runtime       Linux launch, opt-in links, mod catalog and reusable save-data profiles
RimPulse.Core/IPC           bounded, authenticated loopback status listener
RimPulse.Companion/         shared client/protocol plus separate 1.6 and 1.7 adapters
```

Planned capabilities (the listed capabilities do not exist yet):

```text
RimPulse.Core/Validation    RimWorld schema and definition-level diagnostics
RimPulse.Core/Runtime       minimal maps, world configuration, command dispatch and stronger readiness
RimPulse.Backends/          version-specific reload and testing implementations
```

## 3. Current components

| Area | Type | Responsibility |
| --- | --- | --- |
| Platform | `HostPlatform`, `HostPlatformExtensions` | Platform enum, detection, case-sensitivity rules. Passed explicitly instead of calling `OperatingSystem` deep in the logic, so every branch is testable from any machine |
| Paths | `AppPaths` | XDG (Linux), `%APPDATA%`/`%LOCALAPPDATA%` (Windows), `~/Library` (macOS) resolution |
| Configuration | `RimPulseConfiguration` | Schema-3 settings: paths, reusable profiles, build optimization/symbols, logging and UI preferences |
| Configuration | `ConfigurationStore` | Async JSON load/save, atomic write (temp file + move), corrupt-file quarantine, schema version |
| Diagnostics | `IDiagnosticLog`, `DiagnosticLog`, `RotatingFileLogSink`, `NullDiagnosticLog` | Bounded in-memory ring of log entries, event for the UI, rotating file sink capped in size and archive count |
| Game | `GameVersion` | `major.minor` + build text, parsing of `Version.txt`, comparison, supported-version check |
| Installations | `RimWorldInstallation` | Immutable inspection result, including *why* an installation is unusable |
| Installations | `InstallationProbe` | Layout detection (Unity vs Waylith), assembly verification, version reading, path normalisation |
| Installations | `VersionFileReader` | Reads `Version.txt` — the only authoritative version source |
| Installations | `RimWorldInstallationDetector` | Collects candidates (manual, `RIMWORLD_DIR`, Steam, GOG), probes, de-duplicates, orders |
| Installations | `ValveKeyValues`, `SteamLibraryReader` | Minimal VDF/ACF reader and Steam library discovery |
| Credits | `CreditEntry`, `CreditsCatalog` | The compiled-in list of every referenced project and creator, with license, usage classification and milestone relevance. Asserted by tests, rendered by the app |
| App | `RimPulseServiceGraph` | Composition root: builds paths, log, configuration store and detector; owns their lifetime |
| App | `ILinkLauncher`, `SystemLinkLauncher` | Opens https links through the OS shell; refuses anything that is not an absolute http(s) URL |
| App | `IClipboardService`, `SystemClipboardService` | Copies text to the system clipboard for the build-error button, keeping clipboard access out of view models. A refused copy is reported, never thrown |
| App | `CreditEntryViewModel` | Owns the per-row link commands so the credits list needs no ancestor bindings |
| Projects | `LoadFoldersReader`, `LoadFoldersDocument`, `LoadFolderPlan` | Parses `LoadFolders.xml` (version sections, the four condition attributes, `_steam` suffix) and projects it onto one game version for a given set of active package IDs |
| Build | `BuildInputDiscovery`, `MsBuildPropertySet`, `ModCompileUnit` | Finds `.csproj` files and evaluates the documented MSBuild subset: properties (from the project, the nearest `Directory.Build.props`, imported props files, environment and caller-supplied global properties), conditions, first-match `Choose` branches, SDK default source items and `Compile` globs, `HintPath` references, `ProjectReference` and `PackageReference`. Orders projects so dependencies come first |
| Build | `GameReferenceResolver`, `GameReferenceSet` | Collects the installation's assemblies and, for the Waylith layout, the host runtime's framework assemblies. Reference assemblies only; nothing is loaded or executed |
| Build | `ModCompiler`, `CompileRequest`, `CompilationResult`, `BuildDiagnostic` | In-process Roslyn compilation into memory; the assembly is written only after success |
| Build | `ModBuilder`, `ModBuildPlan`, `ModBuildResult` | Compiles the units in order, feeding each output to the projects that reference it |
| Build | `ModOutputDirectory`, `ModOutputDirectoryResolver` | Decides where the DLLs go: the version folder's `Assemblies` folder when the mod has one for the selected installation's version (`1.6/Assemblies`), else the mod root's |
| Build | `AssemblyInspector`, `AssemblyInspection`, `AssemblyTypeInfo` | Reads a produced assembly's name, version, references and types with `System.Reflection.Metadata`; the assembly is never loaded into RimPulse, so inspecting a mod cannot run its code |
| Runtime | `GameProcessLauncher` | Validates and starts the selected Linux executable, optionally passing a separate data path and child-only environment; returns a process handle |
| Runtime | `CompanionInstaller`, `DevelopmentProjectLink` | Explicit Linux setup; builds embedded companion source against selected game references and creates no-overwrite links in shared `Mods/` |
| Runtime | `ModCatalogReader`, `ModLoadOrder` | Reads official/local/downloaded Workshop content; resolves dependency closure, hard/advisory order and incompatibilities without executing mod code |
| Runtime | `PersistentDevelopmentProfile` | Reuses a profile's data directory, writes managed mod configuration and exposed preferences atomically, preserves unrelated preferences/saves and rejects installation/version/path mismatches |
| Runtime | `DevelopmentProfile` | Legacy fresh per-launch profile creator retained for compatibility; no longer used by the GUI development-launch flow |
| Runtime | `RuntimeProjectMatch` | Matches the game startup snapshot by package ID **and** canonical project directory |
| IPC | `CompanionSession`, `WireProtocol` | Per-launch loopback listener, mutual HMAC challenge, bounded versioned JSON status, PID/version checks and reconnect handling; see [IPC.md](IPC.md) |
| Companion | `Shared/Companion.cs`, `RimWorld16/GameAdapter.cs`, `RimWorld17/GameAdapter.cs` | Game-thread startup snapshot and background heartbeat; separate source adapters compiled for the selected game's references. Neither is verified against a real game yet |
| App | `MainWindowViewModel` | Installation/project selection, monitoring, build, launch orchestration and activity log |
| App | `ProfileEditorViewModel`, `AppSettingsViewModel` | Explicit saved-vs-draft profile editing, catalog/order presentation and application/build preferences |
| App | `ProjectRow`, `BuildDiagnosticRow` | Presentation projections of the project list and build output |
| App | `InstallationViewModel`, `DetailRow`, `ProblemRow` | Presentation projections of installation results |

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
- Companion connection callbacks marshal to the UI dispatcher and are scoped to a launch sequence; obsolete
  callbacks are ignored. Closing RimPulse cancels the listener and flushes the log without killing the game.
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
| `LoadFolders.xml` malformed or unsafe | Problems reported on the project; sections that did parse are still shown |
| Load-folder entry outside the mod, or with an unknown condition attribute | Entry ignored and reported; the rest of the section still applies |
| `.csproj` malformed, or an unsupported wildcard/Condition | Reported as a build problem; other projects are still read |
| Compilation error | Diagnostics with file/line/column; **no assembly is written**, and dependent projects are skipped |
| Build without a usable installation | Refused before compiling, with the reason in the status bar and the log |
| Setup link already occupied by an unrelated target | Refused; no pre-existing path is overwritten |
| Companion frame invalid, incompatible or disconnected | Rejected or cleared, visible as waiting/disconnected; the listener can accept a later connection |
| Runtime process exits | Listener is canceled and the exit code is reported; process liveness is not companion or game readiness |

## 7. Dependencies and why

| Package | Why it is justified |
| --- | --- |
| `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` (12.1.4) | Cross-platform desktop UI required by the brief. Avalonia 12 targets .NET 8+ and recommends .NET 10, which matches the rest of the stack |
| `Microsoft.CodeAnalysis.CSharp` (4.14.0) | Roslyn is the only way to get structured, located diagnostics for a mod without its own toolchain. In-process compilation also builds `net472` projects on Linux, where `dotnet build` cannot, and keeps the build result a first-class object instead of parsed console output. Adds ~10 MB to the shipped app |
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
| D9 | Credits are a **compiled-in catalog in Core**, not documentation-only | The requirement is that every referenced creator/repository is featured *in the app*. A catalog in Core is testable (`CreditsCatalogTests` fails if an entry loses its creator, license or link) and cannot drift out of sync with the UI |
| D10 | `CreditUsage` distinguishes Reference / Adapted / Bundled | Keeps the attribution promise honest: a project may be studied freely, but adopting code must change the classification and keep the author credited |
| D11 | Link opening goes through `ILinkLauncher` | Keeps process launching out of view models and gives one place to validate URLs and report failures |
| D16 | Every label is a **`SelectableTextBlock`**, and the build panel has **Copy all errors** | An error the user cannot select has to be retyped by hand to be reported. Selection is per control, so copying a whole failed build needs one button that copies every diagnostic, not just the rows the panel is capped at |
| D12 | `LoadFolders.xml` is **interpreted, not evaluated against a real mod list** | The projection uses the edited profile's configured list when installation/version match, otherwise only the mod's own package ID. It is labelled as configuration, not runtime confirmation; excluded folders retain their condition |
| D13 | Mods are compiled **in-process with Roslyn**, not by invoking `dotnet build` | A 1.6 mod targets `net472`, which cannot be built on Linux with the .NET SDK, and invoking MSBuild would couple RimPulse to whatever toolchain the author has installed and return unstructured output |
| D14 | The compiler **emits to memory first** | A failed build must never leave a partially written DLL in `Assemblies/` for the game to load |
| D15 | Reference assemblies for the Waylith layout come from **the host runtime** (`TRUSTED_PLATFORM_ASSEMBLIES`) | The 1.7 installation ships the game's assemblies, not the whole framework, and RimPulse has no other honest source. Documented as an assumption to re-check against a real 1.7 installation |
| D17 | Build output follows the mod's **version folder** | RimWorld treats a folder named after the running version (`1.6`) as the mod root for that version, so a DLL written to the top-level `Assemblies` folder next to it is not the assembly the game loads. Writing to `1.6/Assemblies` keeps the build where the game looks, while a mod without version folders keeps the single top-level folder |
| D18 | Separate 1.6 / 1.7 companion adapters and per-installation build directories; shared wire constants | Compile against the selected game's own assemblies without overwriting another installation's build. Real game builds still need verification. |
| D19 | Per-launch loopback secret and HMAC listener proof | Avoid exposing the token or mod list to an unauthenticated listening process; same-user process access to the environment is outside this protection. |
| D20 | Explicit no-overwrite symlinks and reusable separate save-data profiles | Protect normal mod configuration from direct edits. The shared `Mods/` directory, engine preferences, logs and third-party mod behavior are not sandboxed. |
| D21 | Packages are staged from a `dotnet publish` directory by scripts in `packaging/`, never from `bin/` or a source tree | The publish directory is the only input that contains the apphost, managed assemblies and native dependencies together. A package that installs but fails to start is worse than a package that refuses to build, so the scripts validate that payload and reject escaping symlinks before invoking `dpkg-deb`/`appimagetool`. Keeping this in shell scripts instead of MSBuild targets means packaging can be smoke-tested without the SDK or a display server. |
| D22 | Icon artwork is generated by a stdlib-only Python script and verified with `--check` | No image tooling dependency, no binary assets that cannot be reproduced, and CI fails if a committed PNG/ICO drifts from its source. |
| D23 | Tag builds create a **draft** release, not a published one | The repository ships no `LICENSE` file. `packaging/linux/copyright` records `License: UNKNOWN`; publishing an installer or uploading to a package repository needs an explicit licensing decision first. |

## 8a. Credits and attribution

`RimPulse.Core.Credits.CreditsCatalog` is the single source of truth for third-party references. It is
rendered verbatim by the **Credits** panel in the main window, so documentation and application cannot
disagree.

Rules enforced by `CreditsCatalogTests`:

- every entry has a creator, an `https://` link, a summary, a relevance note and a license statement;
- the exact set of required references is present (removing one fails the build);
- no project marked `Adapted` or `Bundled` may be unlicensed or GPL, because that would be an unreviewed
  licensing decision (the two shipped/development dependencies, Avalonia (MIT) and xUnit.net (Apache-2.0),
  satisfy this);
- entries using GitHub search links must also link the specific repository they surface.

Attribution policy (also shown in the app):

> Every project RimPulse references or depends on is listed here with its creator and license. RimPulse adapts
> no RimWorld tooling code in this implementation. If code, data or assets from any referenced project are adapted, the
> entry must be marked as adapted, the original author credited, and the original license honoured. Copyleft
> (GPL) and unlicensed projects must not be copied into RimPulse without an explicit licensing decision.

## 8b. Packaging (milestone 7)

Packaging lives outside the C# projects, in `packaging/`. It is deliberately not part of the build graph:
`dotnet publish` produces an apphost plus managed and native files, and the scripts stage exactly that
directory into a `.deb` (`/opt/rimpulse` + `/usr/bin/rimpulse`), an AppImage (`usr/lib/rimpulse` + `AppRun`)
and a portable Windows ZIP. See [`packaging/README.md`](../packaging/README.md) for the layout and commands.

```text
dotnet publish ──► publish directory ──► validate_publish / stage_payload ──► .deb, AppDir, ZIP
                                              │                       ──► appimagetool ──► .AppImage
                                              └─ refuses an incomplete payload or escaping symlinks
```

`packaging/linux/appimagetool.env` is the single, sourceable pin for the `appimagetool` version, URLs and
SHA-256 values; both `fetch-appimagetool.sh` (local builds) and the release workflow consume it, so a
user's AppImage and CI's AppImage are made with the same verified tool. The fetch script's default output
location is auto-detected by `build-appimage.sh`, and a pasted example path is rejected with guidance.

`packaging/linux/verify.sh` exercises this pipeline with a fake payload on every push: script syntax,
generated icon assets, desktop/control metadata, the pin and its CI wiring, `.deb` structure and control
fields (both dependency sets), the install hint, the AppDir layout, the `appimagetool` invocation,
auto-detection, placeholder rejection and the other refusal cases. The `release.yml` workflow repeats it with real published
output on `v*` tags, verifies a pinned `appimagetool` SHA-256, and uploads artifacts plus a draft release.
No packaging step needs a display server or a game installation, but none of them proves that the installed
application opens a window — that check is still outstanding (see `DEVELOPMENT.md` §7).

## 9. Testing strategy

- Everything in `RimPulse.Core` is covered by tests that run on any machine (no game required): version
  parsing, `Version.txt` reading, both installation layouts, missing-assembly reporting, Steam VDF parsing
  (modern and legacy), library/manifest reading, detection ordering and de-duplication, configuration
  round-trip and corruption recovery, XDG/Windows/macOS path resolution, the diagnostic log, and the credits
  catalog (completeness, attribution and licensing rules).
- Tests use real temporary directories (`TempDirectory`) and real files — no mocking framework and no
  in-memory filesystem abstraction. This keeps the tested code on the same path as production code.
- `LoadFolders.xml` parsing and projection are tested against the condition semantics published by the
  RimWorld wiki (or-lists, the 1.6 `IfModActiveAll` and-list, combined attributes, `_steam` suffix).
- Build discovery is tested against both project shapes seen in the wild (SDK-style with implicit sources and
  legacy with explicit `Compile`/`HintPath` items), including the paths MSBuild writes with backslashes.
- MSBuild property evaluation is tested for what a mod project depends on: expansion order, the nearest
  `Directory.Build.props`, `Import`ed props files (including a cycle), the conditions in use (`==`, `and`,
  `Exists()`, `$(OS)`), first-match `Choose` property/item branches, the conditions that are reported instead
  of guessed, and fallback to the selected installation when a hint path points somewhere this machine does not have.
- The compiler is exercised for real: tests compile source files against the test process's own runtime
  assemblies and assert on success, on located errors, and on nothing being written when compilation fails.
- Synthetic companion tests build both adapters against fake APIs, and exercise a local 1.7-stub startup
  snapshot, protocol validation, authentication, disconnects, links, reusable profile files, catalog/order planning and legacy per-launch profiles. The fake
  1.6 references are .NET 10 stand-ins, not the game's Mono assemblies.
- UI code has XAML/compiled-binding build coverage but no automated desktop-input tests; filesystem and game logic remain in Core.
- Packaging is covered by `packaging/linux/verify.sh` on every push (SDK-free, fake publish payload) and by
  the tag workflow, which stages real published output. Neither replaces installing and launching a package
  on Linux Mint or Windows.
- Game-dependent behaviour (milestones 4–6) will need an integration test harness against a real
  installation; the verification status must be recorded honestly per feature.

## 10. Planned boundaries for later milestones

- **Version-specific backends** — a common interface in Core (`IRimWorldBackend`), with
  `RimWorld16Backend` and `RimWorld17Backend` implementations that are free to differ in reference
  resolution, assembly loading and reload strategy. No shared implementation will be forced where the
  runtimes differ.
- **Runtime follow-up** — reusable profiles and dependency/order planning are implemented, but real-game
  persistence, preference compatibility and readiness need verification per version. The IPC channel remains
  read-only startup status. Minimal maps, world configuration, spawning/ticks and reset require explicit
  version-specific adapters and confirmed commands. RimPulse does not attach to existing game processes,
  and closing it leaves the launched game running.
- **Reload policy** — asset/definition/assembly reload are separate capabilities with an explicit
  `stable / experimental / unsupported` rating per game version; a restart is always preferable to an
  unsafe unload.
