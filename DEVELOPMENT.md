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
| Unexpected behaviour | — | Read the activity log first, then the log file; both record detection, configuration and UI errors |

Logs are the first place to look. Every startup writes the platform, configuration path and log path:

```bash
tail -f "${XDG_STATE_HOME:-$HOME/.local/state}/RimPulse/logs/rimpulse.log"
```

## 7. Verification status

Honest accounting of how the current code has been verified. Update this section whenever a claim changes.

| Area | Status | How it was verified |
| --- | --- | --- |
| C# syntax of all sources | Verified | Every `.cs` file parsed with a C# grammar: 0 syntax errors |
| Credits catalog completeness | Verified by inspection | Every referenced repository, organisation and creator has a catalog entry; encoded as a test |
| XAML / MSBuild / solution files | Verified (well-formed) | All 10 XML documents parsed successfully |
| `dotnet build` of the solution | **Not run** | The environment used to write milestone 1 had no .NET SDK and no NuGet access |
| `dotnet test` of the suite | **Not run** | Same reason. The tests are written against real behaviour and must be executed by you (§2) |
| Installation detection against a real RimWorld 1.6 install | **Not verified** | Requires a machine with the game installed |
| Installation detection against a real RimWorld 1.7 install | **Not verified** | Requires a machine with the game installed |
| Runtime behaviour (launching the game, IPC, reload) | Not implemented | Milestones 4–6 |

If `dotnet build` fails on your machine, please report the exact compiler output — that is the fastest way
to close the gap above.

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

## 10. Next steps (milestone 2 and beyond)

1. **Mod project management** — `IRimWorldProjectStore`, `About.xml` parsing (`packageId`, `name`, `author`,
   `supportedVersions`, `modDependencies`), `LoadFolders.xml` awareness for 1.6/1.7 content folders,
   multiple projects persisted in the configuration.
2. **File monitoring** — `FileSystemWatcher` per project with debouncing/batching and change classification
   (C# / XML / texture / audio / About.xml / language / other).
3. Then milestone 3 (Roslyn build + XML validation) before any runtime work, because the runtime needs a
   trustworthy build result to reload.
