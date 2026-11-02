# Development guide

Exact commands for building, running, testing and troubleshooting RimPulse. Linux (Linux Mint 22.x class
systems) is the primary target; the same commands work on Windows and macOS unless stated otherwise.

---

## 1. Prerequisites

| Requirement | Why | Check |
| --- | --- | --- |
| **.NET SDK 10.0.100+** | Builds the desktop app, Core library and tests (`net10.0`) | `dotnet --info` |
| Git | Source control | `git --version` |
| A desktop session (X11 or Wayland) | Opening the Avalonia window | — |
| RimWorld 1.6 and/or 1.7 | Only needed to make detection find something | — |

### Installing the .NET 10 SDK on Linux Mint / Ubuntu

Either use Microsoft's package feed:

```bash
# Recommended: Microsoft's install script, per-user, no root required
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"        # add to ~/.bashrc to persist
dotnet --version                          # expect 10.0.x
```

…or the distribution/repository package if your system already provides .NET 10 (`sudo apt install
dotnet-sdk-10.0`). RimPulse pins the SDK through `global.json` with `rollForward: latestFeature`, so any
10.0.x SDK works; an older SDK produces a clear "SDK not found" error instead of a confusing compile
failure.

No other development dependencies are required: there is no Node.js, Python, Mono or game-assembly
download step. `dotnet build` restores the three NuGet packages the app needs.

## 2. Build, run, test

```bash
# restore + build everything (app, core, tests)
dotnet build RimPulse.slnx

# Release build
dotnet build RimPulse.slnx -c Release

# run the desktop application
dotnet run --project src/RimPulse.App
```

```bash
# run the test suite
dotnet test RimPulse.slnx

# run the test suite without the SDK's test orchestrator (xunit v3 is a Microsoft.Testing.Platform runner)
dotnet run --project tests/RimPulse.Core.Tests

# filter to one test class
dotnet run --project tests/RimPulse.Core.Tests -- --filter-class "RimPulse.Core.Tests.InstallationProbeTests"
```

Building only the parts you changed is faster during development:

```bash
dotnet build src/RimPulse.Core
dotnet test tests/RimPulse.Core.Tests
```

## 3. Pointing RimPulse at a specific installation

RimPulse finds RimWorld through Steam libraries, `~/GOG Games/*`, the `RIMWORLD_DIR` environment variable
and any path you add in the UI (persisted in the configuration file).

```bash
# Explicit override, no UI interaction required
RIMWORLD_DIR="$HOME/games/RimWorld" dotnet run --project src/RimPulse.App

# Or inspect a specific directory without the UI
dotnet run --project src/RimPulse.App     # then paste the path into "Add an installation path"
```

`RIMWORLD_DIR` is used by many RimWorld mod build scripts, so pointing it at the installation you are
debugging keeps RimPulse and your editor in sync.

If the path points at a `Managed` directory rather than the installation root, RimPulse walks up to the
root automatically.

## 4. Where state lives

| What | Linux | Windows | macOS |
| --- | --- | --- | --- |
| Configuration | `${XDG_CONFIG_HOME:-~/.config}/RimPulse/config.json` | `%APPDATA%\RimPulse\config.json` | `~/Library/Application Support/RimPulse/config.json` |
| Log file | `${XDG_STATE_HOME:-~/.local/state}/RimPulse/logs/rimpulse.log` | `%LOCALAPPDATA%\RimPulse\logs\rimpulse.log` | `~/Library/Logs/RimPulse/rimpulse.log` |

The log rotates at 2 MB and keeps up to three archives. A corrupt configuration file is renamed to
`config.json.corrupt-<timestamp>` (never deleted) and RimPulse starts from defaults.

Useful reset commands while developing:

```bash
rm -rf "${XDG_CONFIG_HOME:-$HOME/.config}/RimPulse"          # forget selected installation & paths
rm -rf "${XDG_STATE_HOME:-$HOME/.local/state}/RimPulse"      # delete logs
```

## 5. Publishing

RimPulse packages (AppImage, `.deb`, Windows installer) are milestone 7. Publishing a runnable build by
hand already works:

```bash
# framework-dependent: small, requires the .NET 10 runtime
dotnet publish src/RimPulse.App -c Release -o artifacts/linux-framework-dependent

# self-contained: includes the runtime, runs on machines without .NET installed
dotnet publish src/RimPulse.App -c Release -r linux-x64 --self-contained true \
    -o artifacts/linux-x64

./artifacts/linux-x64/RimPulse
```

Keep published output out of Git (`artifacts/` is ignored).

## 6. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `No RimWorld installation was found` | Game is not in a Steam/GOG location RimPulse scans | Set `RIMWORLD_DIR`, or add the installation root in the UI |
| Installation shows **INCOMPLETE** | Layout not recognised or required assemblies missing — the problems list says which | Point RimPulse at the installation root (the folder containing `Version.txt`), not at `Mods/` |
| Version shows `unconfirmed` | `Version.txt` is missing or unreadable | Verify the file exists in the installation root; RimPulse will not guess |
| Installation shows **UNSUPPORTED** | Game version is outside 1.6 / 1.7 | Expected. Future versions are detected but not yet targeted |
| `dotnet: command not found` / SDK not found | .NET 10 SDK missing or not on `PATH` | Follow §1, then `dotnet --info` |
| Window does not open on Wayland | Compositor/backend issue | Try `AVALONIA_X11=1` in a X11 session, or run under XWayland |
| Configuration not saved | Config directory not writable | Check the paths in §4 and the log file for the `Error` entry |
| Build fails with hundreds of `CS0246` / `CS0103` errors | A dependency is not in the reference set — usually a `PackageReference` that has not been restored, or an assembly the project only declares behind a condition | Read the `Build` entry in the log: it lists the projects, the reference set, the first diagnostics and an error-code histogram, and names any package it could not resolve |
| Build fails with hundreds of `CS0436`, `CS0104` and `CS0121` errors, all in one project | That project compiles its own stand-ins for the game's API (`Verse.*`, `RimWorld.*`, `UnityEngine.*`) while the installation's real assemblies are in the reference set too, so every type it duplicates is ambiguous | RimPulse names such a project in one line and leaves test projects out of the build (`IsTestProject`, a test-framework `PackageReference`, or a `*.Tests` project name). For any other project, exclude the stand-in sources from the compilation or stop referencing the game in it |
| Build error count is in the log but not the errors | Older logs only recorded the summary line | Every build now writes its detail block below the summary; check that the app you run is current |
| Unexpected behaviour | — | Read the activity log first, then the log file; both record detection, configuration and UI errors |

Logs are the first place to look. Every startup writes the platform, configuration path and log path:

```bash
tail -f "${XDG_STATE_HOME:-$HOME/.local/state}/RimPulse/logs/rimpulse.log"
```

A `Build` entry is written for every build. The summary line carries counts (`Build failed: 1051 error(s)…`) and
the indented block below it carries what is needed to diagnose it: the discovered projects and their options, the
reference assemblies the compilation used, the problems found before compiling (unresolved hint paths, unrestored
`PackageReference` items), the first 40 diagnostics of each severity and an error-code histogram. The in-app
diagnostics list is capped at 500 rows for the same reason; the log is the complete record of the first rows.

## 7. Verification status

Honest accounting of how the current code has been verified. Update this section whenever a claim changes.

| Area | Status | How it was verified |
| --- | --- | --- |
| C# syntax of all sources | Verified | `dotnet build` passes in CI on every push |
| XAML / MSBuild / solution files | Verified | The app, including the project list and build panel AXAML, compiles in CI (compiled bindings turn binding typos into build errors) |
| `dotnet build` of the solution | Verified | GitHub Actions, Release configuration |
| `dotnet test` of the suite | Verified | GitHub Actions, Release configuration; includes the `LoadFolders`, build-input, test-project detection, reference-resolution, Roslyn compiler and assembly-inspection tests |
| Credits catalog completeness | Verified | Encoded as tests (`CreditsCatalogTests`) and re-checked on every push |
| Opening the desktop window | Verified for the previous milestone | Run on Linux with a real desktop session; the new project list and build panel need the same treatment |
| Building a mod against a real installation | **Partially verified** | One real 1.6.4871 (`rev598`) run compiled the mod's own assembly against the installation (3 `CS0618` deprecation warnings), while its `*.Tests` project produced 712 errors: hand-written game stand-ins compiled next to the real assemblies. Test projects are now left out of a build and the duplicate-type cause is reported, both covered by tests in this change; that real-installation run has not been repeated yet |
| Installation detection against a real RimWorld 1.6 install | **Not verified** | Requires a machine with the game installed |
| Installation detection against a real RimWorld 1.7 install | **Not verified** | Requires a machine with the game installed |
| Runtime behaviour (launching the game, IPC, reload) | Not implemented | Milestones 4–6 |

The first CI run found a real defect (the rotating log sink left no active log file after a rotation) and a
wrong test expectation (`shortVersion` was asserted as `1.6.4851` instead of `1.6`). Both are fixed.

If a build fails on your machine but passes in CI, report your SDK version (`dotnet --info`) and the
compiler output.

## 8. Credits and attribution

Referenced projects, their creators and RimPulse's own dependencies are listed in
`RimPulse.Core.Credits.CreditsCatalog` and shown in the application's **Credits** panel. Adding a reference means adding a catalog entry — the panel is the
authoritative list, and `CreditsCatalogTests` fails if an entry loses its creator, link, summary or license.

When you consult a new project, add it there (and to the table in the README). When you actually adopt code
from a project, change its `Usage` from `Reference` to `Adapted` in the same change, keep the author credited,
and honour the license; GPL-3.0 and unlicensed code must not be copied in without a licensing decision.

## 9. Code conventions

- `RimPulse.Core` has no UI dependencies and no `static` mutable state; everything is injected.
- Public types and non-obvious methods carry XML documentation; comments explain *why*, not *what*.
- All I/O paths are absolute and resolved through `AppPaths` or `InstallationProbe.NormalizePath`.
- Nothing is written outside RimPulse's own configuration/state directories unless a user action requires it.
- No placeholder implementations, no `TODO`-backed features exposed in the UI. If something is not
  implemented, it is absent and documented in [`docs/SPEC.md`](docs/SPEC.md)-driven milestone status.
- New logic in Core requires tests. Tests use real temporary directories rather than a mocked filesystem.

## 10. Continuous integration

`.github/workflows/build.yml` restores, builds and tests the solution on every push and pull request
(Ubuntu, .NET 10 SDK). The .NET 10 SDK requires opting into the new test experience, which `global.json` does:

```json
"test": { "runner": "Microsoft.Testing.Platform" }
```

Failing runs attach the tail of the build/test output as a commit comment, because raw job logs are not
reachable from every environment that needs to read them. Remove that step if your team does not want the
comment.

## 11. Next steps (milestone 3 and beyond)

1. **XML validation** — changed XML is checked for well-formedness only. Definition-level checks (root
   elements, `defName` uniqueness, `Abstract`/`ParentName` references) and RimWorld schema validation remain.
2. **Build fidelity** — the MSBuild subset RimPulse understands is documented in the README. Properties,
   `Directory.Build.props`, imported props files, conditions, SDK default sources and common `Compile` globs
   (`*`, `?`, `**`, relative directories, `Exclude` and `Remove`) are evaluated. `Choose`, MSBuild targets,
   item/string functions, wildcard imports and metadata-dependent item expressions are not.
3. **Dependency resolution** — declared dependencies are displayed, not resolved against installed mods; the
   load-folder projection currently treats only the mod itself as active.
4. **Runtime integration (milestone 4)** — companion mod, IPC and process management. Compilation is the last
   prerequisite that is now in place.
