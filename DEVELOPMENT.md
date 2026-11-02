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
| RimWorld 1.6 and/or 1.7 | Required to compile/load a real companion and verify runtime behavior | — |

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

Use **Installations → Add** for a custom game directory. RimPulse remembers it and restores it on the next
start. Automatic startup scanning covers Steam libraries and GOG-style folders; turn it off in **Settings**
to restore only remembered/custom paths and the optional legacy `RIMWORLD_DIR` hint. **Re-scan** explicitly
searches well-known locations regardless of that preference.

```bash
# Normal workflow: configure paths in the GUI
dotnet run --project src/RimPulse.App

# Optional legacy discovery hint for existing mod-development scripts
RIMWORLD_DIR="$HOME/games/RimWorld" dotnet run --project src/RimPulse.App
```

No environment variable or manual JSON edit is required. The selected installation is still supplied to
mod builds as `RIMWORLD_DIR`/`RimWorldDir` so existing project reference paths resolve against that game.

If the path points at a `Managed` directory rather than the installation root, RimPulse walks up to the
root automatically.

## 4. Where state lives

| What | Linux | Windows | macOS |
| --- | --- | --- | --- |
| Configuration | `${XDG_CONFIG_HOME:-~/.config}/RimPulse/config.json` | `%APPDATA%\RimPulse\config.json` | `~/Library/Application Support/RimPulse/config.json` |
| Log file | `${XDG_STATE_HOME:-~/.local/state}/RimPulse/logs/rimpulse.log` | `%LOCALAPPDATA%\RimPulse\logs\rimpulse.log` | `~/Library/Logs/RimPulse/rimpulse.log` |
| Generated companion / retained profiles | `<state>/companion/`, `<state>/development-profiles/<id>/data`; legacy `<state>/runtime-sessions/` is retained | Setup and launch are currently Linux-only | Setup and launch are currently Linux-only |

The log rotates at 2 MB and keeps up to three archives. A corrupt configuration file is renamed to
`config.json.corrupt-<timestamp>` (never deleted) and RimPulse starts from defaults.

Useful reset commands while developing:

```bash
rm -rf "${XDG_CONFIG_HOME:-$HOME/.config}/RimPulse"          # forget selected installation & paths
rm -rf "${XDG_STATE_HOME:-$HOME/.local/state}/RimPulse/logs" # delete logs only; preserve profiles
```

## 5. Publishing and packaging

Milestone 7 adds installable packages. Publishing a runnable build by hand still works, and is the input to
every package:

```bash
# framework-dependent: small, requires the .NET 10 runtime
dotnet publish src/RimPulse.App -c Release -o artifacts/linux-framework-dependent

# self-contained: includes the runtime, runs on machines without .NET installed
dotnet publish src/RimPulse.App -c Release -r linux-x64 --self-contained true \
    -o artifacts/publish/linux-x64

./artifacts/publish/linux-x64/RimPulse
```

Package that publish directory (never `bin/` or `obj/`) with the scripts in [`packaging/`](packaging/README.md):

```bash
# SDK-free smoke test of the packaging scripts, icons, .deb control fields and AppDir layout
python3 packaging/generate-icons.py --check
bash packaging/linux/verify.sh

# .deb and AppImage. fetch-appimagetool.sh downloads the pinned, checksum-verified appimagetool 1.9.1;
# build-appimage.sh finds it in artifacts/tools automatically (or pass --appimagetool /real/path).
bash packaging/linux/build-deb.sh --publish-dir artifacts/publish/linux-x64 --version 0.7.0 --arch amd64
bash packaging/linux/fetch-appimagetool.sh
bash packaging/linux/build-appimage.sh --publish-dir artifacts/publish/linux-x64 --version 0.7.0 \
    --arch amd64 --extract-and-run

# Windows, on a Windows host (portable ZIP; installer.iss is an Inno Setup 6 script)
pwsh packaging/windows/build-zip.ps1 -PublishDir artifacts/publish/win-x64 -Version 0.7.0
```

`v*` tags trigger `.github/workflows/release.yml`: self-contained `linux-x64` and `win-x64` publishes,
a `.deb`, an AppImage and a Windows ZIP, plus a **draft** GitHub Release. Manual dispatch builds the same
artifacts without a release. Keep published output out of Git (`artifacts/` is ignored).

Before distributing anything, read the verification table and known blockers in
[`packaging/README.md`](packaging/README.md): installation on Linux Mint/X11/Wayland and the Windows
builds are not verified here, and the repository does not publish a `LICENSE` file yet, which is why
`packaging/linux/copyright` says `UNKNOWN` and releases are drafts only.

## 6. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `No RimWorld installation was found` | Game is not in a Steam/GOG location RimPulse scans | Add the installation root in **Installations**, or use **Re-scan** |
| Installation shows **INCOMPLETE** | Layout not recognised or required assemblies missing — the problems list says which | Point RimPulse at the installation root (the folder containing `Version.txt`), not at `Mods/` |
| Version shows `unconfirmed` | `Version.txt` is missing or unreadable | Verify the file exists in the installation root; RimPulse will not guess |
| Installation shows **UNSUPPORTED** | Game version is outside 1.6 / 1.7 | Expected. Future versions are detected but not yet targeted |
| `dotnet: command not found` / SDK not found | .NET 10 SDK missing or not on `PATH` | Follow §1, then `dotnet --info` |
| Window does not open on Wayland | Compositor/backend issue | Try `AVALONIA_X11=1` in a X11 session, or run under XWayland |
| Configuration not saved | Config directory not writable | Check the paths in §4 and the log file for the `Error` entry |
| Build fails with hundreds of `CS0246` / `CS0103` errors | A dependency is not in the reference set — usually a `PackageReference` that has not been restored, or an assembly the project only declares behind a condition | Read the `Build` entry in the log: it lists the projects, the reference set, the first diagnostics and an error-code histogram, and names any package it could not resolve |
| Build fails with hundreds of `CS0436`, `CS0104` and `CS0121` errors, all in one project | That project compiles its own stand-ins for the game's API (`Verse.*`, `RimWorld.*`, `UnityEngine.*`) while the installation's real assemblies are in the reference set too, so every type it duplicates is ambiguous | RimPulse names such a project in one line and leaves test projects out of the build (`IsTestProject`, a test-framework `PackageReference`, or a `*.Tests` project name). For any other project, exclude the stand-in sources from the compilation or stop referencing the game in it |
| Build error count is in the log but not the errors | Older logs only recorded the summary line | Every build now writes its detail block below the summary; check that the app you run is current |
| Companion install fails | Game assembly missing/incompatible, unwritable `Mods/`, or an unrelated existing destination | Check the compile diagnostics and permissions. Inspect the existing path; RimPulse will not overwrite it. |
| Game running, companion waiting | Companion disabled, game not yet at snapshot phase, failed game-side compile/load, incompatible version/PID, or isolated mod config ignored | Check that the mod is enabled in the profile actually in use, inspect the game log and app log, and verify real-game compatibility. A process alone is not readiness. |
| Selected project absent or mismatched | The package ID is missing/inactive or another directory for the same ID was loaded | Inspect the startup list and the actual game-reported root path. Explicitly link the selected project if needed, avoid duplicate copies and restart. |
| Development game misses DLC or dependencies | The profile selection is incomplete or a mod was removed after catalog refresh | Refresh the catalog, enable DLC/mods and save. Missing dependencies and hard-order cycles block save/launch. Only downloaded Workshop mods are available. |
| `sudo apt install .//home/.../rimpulse.deb` fails | Older packaging output prefixed an absolute path with `./` in the install hint | The hint now prints the absolute path directly; run `sudo apt install /abs/path/rimpulse_0.7.0_amd64.deb` or `cd artifacts/packages && sudo apt install ./rimpulse_0.7.0_amd64.deb` |
| `appimagetool '/path/to/…' is not executable` | A literal example path from older docs was pasted as if it were a real file | Run `bash packaging/linux/fetch-appimagetool.sh` for a verified copy; then omit `--appimagetool`, or pass the real path the script prints. `build-appimage.sh` rejects placeholder paths explicitly |
| `fetch-appimagetool.sh` cannot download the tool | GitHub redirects release assets to `release-assets.githubusercontent.com`, which a proxy or firewall may block | Allow that host, or download `appimagetool-x86_64.AppImage` from the 1.9.1 release page manually, verify its SHA-256 against `packaging/linux/appimagetool.env` and pass `--appimagetool /real/path` |
| AppImage does not start (`dlopen(): error loading libfuse.so.2`) | The host lacks FUSE 2 | Build with `--extract-and-run`, and run with `APPIMAGE_EXTRACT_AND_RUN=1 ./RimPulse-0.7.0-x86_64.AppImage`, or install `libfuse2` |
| `Unexpected behaviour` | — | Read the activity log first, then the log file; both record detection, configuration and UI errors |

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

| Area | Status | Evidence / next action |
| --- | --- | --- |
| Full solution and official tests | **Passed in GitHub Actions** | Commit `51d50b3`: Ubuntu/.NET 10 restore, Release build (including Avalonia/XAML compiled bindings) and official xUnit v3 tests passed. Later documentation-only changes do not alter that result. |
| Local supplementary checks | **Passed, not a substitute for CI** | SDK 10.0.400's Roslyn 5.9 compiler and a temporary reflection/xUnit-compatible harness passed 295 cases. NuGet was unavailable locally; CI used the repository's pinned Roslyn 4.14 and real packages. |
| Desktop interaction | **Not verified in this environment** | XAML compiles, but no display server was available for a visual/input smoke test. |
| Real companion/link/launch | **User-reported working** | The user reports linking and opening the game. Exact game version, authenticated IPC, main-menu readiness and mod initialization errors were not supplied. |
| Reusable profiles / native quick test | **Automated file/argument checks; real-game check pending** | Tests cover persistence, preference preservation, DLC selection, dependency/order validation and the 1.6 `-quicktest` argument. Native quick test generates a normal world and 250×250 map, not a minimal sandbox. |
| 1.7 | **Unverified** | Normal startup only; native quick test is disabled. Verify save-data handling, preference fields, companion APIs and log locations on a real Waylith installation. |
| Packaging scripts and metadata (milestone 7) | **Passed locally and in CI** | `packaging/linux/verify.sh` checks script syntax, generated icon assets, desktop/control metadata, the shared `appimagetool` pin and CI wiring, `.deb` structure/fields and install hint, both dependency sets, AppDir layout, the stubbed `appimagetool` invocation, auto-detection, placeholder rejection and the refusal cases, using a fake publish payload. No .NET SDK is needed for this step. |
| Real `.deb` from published output | **User-reported build success; installation not verified** | A user ran the self-contained `linux-x64` publish and `build-deb.sh --version 0.7.0 --arch amd64` successfully on their machine. Installing or launching the package was not reported. |
| Real AppImage / Windows packages | **Built by the release workflow on tags; not run on a desktop** | `release.yml` publishes real self-contained output and packages it with a checksum-pinned `appimagetool` 1.9.1. No desktop session was available to install/launch a package on Linux Mint (X11/Wayland); the Windows ZIP and Inno Setup script were prepared but not built or run. |

### Real-game profile checklist

1. Record the exact game version and back up normal configuration/saves. Build the small development mod and
   install/rebuild the companion against that installation. Verify link targets before following them.
2. In **Profiles**, create a named profile, refresh the catalog and link/add the project. Select required DLC
   and mods, adjust order and settings, then **Save profile**. Inspect the displayed data directory's
   `Config/ModsConfig.xml` and `Prefs.xml`; missing dependencies/cycles must prevent saving/launching.
3. Use **Save & launch development**. Confirm the actual save/config directory, enabled mods and project root
   in game. Check the game log for initialization errors. Verify normal configuration remains unchanged.
4. On 1.6, separately enable native quick test and confirm normal world generation plus a 250×250 map.
   Check Developer Mode/palette, pause-on-load and display preferences. Do not call this a minimal sandbox.
5. Close the game normally, create a save, relaunch the same profile and confirm persistence. Change a setting,
   use **Revert**, and confirm the saved value returns. Switch profiles only after saving/reverting a draft.
6. Check process/companion/project indicators independently. A startup snapshot is not map readiness. Verify
   that **Launch normally** does not apply the managed profile. Closing RimPulse leaves the game running.
7. Repeat on real 1.7 with quick test disabled. Record unsupported preference behavior or shared logs rather
   than assuming 1.6 results apply. Inspect retained state before cleanup; never delete symlink targets.

If a build fails on your machine but passes in CI, report `dotnet --info` and compiler output.

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

Failing runs attach the tail of the build/test/packaging output as a commit comment, because raw job logs
are not reachable from every environment that needs to read them. Remove that step if your team does not
want the comment. The same workflow also runs `packaging/linux/verify.sh`, an SDK-free packaging smoke
test that does not need a publish or a display server.

`.github/workflows/release.yml` builds the real `.deb`, AppImage and Windows ZIP from self-contained
publishes on `v*` tags (or manual dispatch). It verifies the downloaded `appimagetool` against a pinned
SHA-256 and creates a **draft** release, never a published one — the project has no license file yet.

## 11. Next steps (milestones 3–6)

Milestone 7 packaging is implemented; the remaining work is in the earlier, still-open milestones.

1. **XML validation** — changed XML is checked for well-formedness; files under `Defs/` also get conservative
   root (`Defs`/`Patch`) and within-file duplicate `defName` checks (grouped by definition type). Cross-file
   uniqueness, `Abstract`/`ParentName` resolution against the mod and game, and RimWorld schema validation remain.
2. **Build fidelity** — the MSBuild subset RimPulse understands is documented in the README. Properties,
   `Directory.Build.props`, imported props files, conditions, SDK default sources and common `Compile` globs
   (`*`, `?`, `**`, relative directories, `Exclude` and `Remove`) and first-match `Choose` branches are evaluated.
   MSBuild targets, item/string functions, wildcard imports and metadata-dependent item expressions are not.
3. **Profile verification** — GUI-configured reusable profiles, installed-mod dependency closure, DLC selection
   and load-order validation are implemented. Verify them on real games; downloads, live mod-list updates and
   full game-schema validation remain outside this increment.
4. **Testing environment (milestone 5)** — native Developer Mode preferences and explicit 1.6 quick test are
   the first implemented features. Minimal world/map generation, configurable world features, app-driven
   spawning/ticks and reset/cleanup still require version-specific game integration and a command channel.
   Neither process liveness nor a startup snapshot is main-menu/map readiness.
5. **Packaging verification (milestone 7)** — scripts, icons, `.deb` metadata and AppDir layout are
   implemented and smoke-tested. Still needed: choose and publish an upstream license, install and launch
   the packages on Linux Mint 22.x under X11 and Wayland, check the AppImage/FUSE path and icon cache,
   confirm the runtime dependency list, and compile/run the Windows ZIP and Inno Setup installer on a real
   Windows host. Publish a draft release only after those checks.
