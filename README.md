# RimPulse

**Build. Reload. Test. Repeat.**

RimPulse is a cross-platform desktop development environment for RimWorld mod authors. It runs
alongside your normal editor (VS Code, Rider, Visual Studio), manages the RimWorld runtime, and aims to
replace the edit → build → launch → wait → test → quit loop with a persistent, live development session.

RimPulse is **not** an IDE and never pretends to be a game engine: it drives the real RimWorld
installation on your machine, using the real game assemblies, definitions and APIs.

> The full product specification this repository is being built against is preserved verbatim in
> [`docs/SPEC.md`](docs/SPEC.md).

---

## Intended development workflow

The intended user experience is:

1. Launch RimPulse.
2. Select an installed RimWorld version (1.6 or 1.7).
3. Select an existing mod development directory.
4. RimPulse detects the mod structure and reads its metadata.
5. RimPulse monitors the directory for changes.
6. The developer starts the RimPulse testing environment.
7. RimPulse launches the actual RimWorld runtime in a managed development configuration.
8. The developer edits files in an external editor.
9. Saving a file triggers change detection.
10. RimPulse classifies the modification.
11. Relevant validation and compilation are performed.
12. RimPulse reloads the affected components if safe.
13. The developer continues testing without restarting the entire game whenever possible.

When a change cannot safely be applied through hot reload, RimPulse must explain why and offer the appropriate fallback.

Never claim that a reload succeeded if the game has not confirmed it.


This is the planned workflow; see the status below for what is implemented today.

---

## Project status

This repository is being built incrementally, one milestone at a time. Features below describe implemented behavior; new changes still need a fresh build and test run.

| Milestone | Scope | State |
| --- | --- | --- |
| **1 — Foundation** | Avalonia desktop shell, configuration persistence, RimWorld installation detection (1.6 / 1.7), installation selection UI, Linux-first build | **Implemented** |
| 2 — Project management | Mod project directories, `About.xml` parsing, structure detection, dependency detection, file monitoring | **In progress** — single-project inspection and monitoring implemented; multiple projects remain |
| 3 — Build & validation | `.csproj` detection, game reference resolution, Roslyn compilation, XML validation, assembly inspection | **In progress** — basic changed-file XML syntax checks only |
| 4 — Runtime integration | Companion mods (1.6 / 1.7), IPC, process management, runtime status | Not started |
| 5 — Testing environment | Minimal world/map generation, configurable world features, Developer Mode integration, entity spawning, tick controls | Not started |
| 6 — Live reload | Asset reload, definition reload, assembly reload research per version, restart fallback | Not started |
| 7 — Packaging | AppImage, `.deb`, desktop entry, Windows packaging | Not started |

### What actually works today

- Detects RimWorld installations from **Steam** (all library folders, read from `libraryfolders.vdf`),
  **GOG-style** install folders, the **`RIMWORLD_DIR`** environment variable and **paths you add by hand**.
- Distinguishes the two real installation layouts:
  - RimWorld **1.6 and earlier**: Unity 2022.3 player, Mono/.NET Framework 4.7.2, assemblies in
    `RimWorldLinux_Data/Managed` (or the Windows/macOS equivalent).
  - RimWorld **1.7 and later**: Waylith engine on .NET 10, assemblies in the installation root.
    See [`docs/GAME-VERSIONS.md`](docs/GAME-VERSIONS.md).
- Reads the game version from `Version.txt` and **reports it as unconfirmed** when that file is missing
  rather than guessing from folder names.
- Verifies that the assemblies that layout requires actually exist, and explains exactly what is wrong
  when a directory is not a usable installation.
- Persists the selected installation and any custom paths (`config.json`) across sessions, tolerating a
  missing or corrupt configuration file.
- Keeps a list of mod directories, selects and remembers several of them, reads each `About/About.xml` metadata
  and declared dependencies, and reports common content folders.
- Interprets `LoadFolders.xml`: version sections (`<v1.6>`, `<v1.7>`), `IfModActive` / `IfModActiveAny` /
  `IfModActiveAll` / `IfModNotActive` conditions and the legacy `_steam` suffix, projected onto the selected
  installation's version. Folders that a condition excludes are listed with that condition, never dropped
  silently; RimPulse only knows the mod itself is active, so it says so instead of guessing a mod list.
- Monitors the selected directory, batches and classifies changes, refreshes metadata when `About.xml` changes, and checks changed XML files for well-formedness (not RimWorld semantics).
- Finds the mod's `.csproj` projects, resolves the game assemblies of the selected installation and **compiles
  the mod in-process with Roslyn**, reporting every diagnostic with file, line and column. A build writes the
  assembly into the mod's `Assemblies` folder only when you press **Build** and only after compilation
  succeeded. The produced assembly is then read back from its metadata (name, version, references, types), so
  the build panel shows what the game would actually load.
- Streams diagnostics to a bounded in-app activity log and a rotating log file.
- Ships an in-app **Credits** panel that features every referenced project and creator with its license and
  why it matters (see [Credits and references](#credits-and-references)).
- Foundation tests previously passed in CI; the new project, monitoring and XML tests require a fresh CI run.

### Deliberately not there yet

No dependency resolution against installed mods, no XML schema/semantic validation, no game launching, hot
reload or testing environment. Those are later milestones; nothing in the UI pretends otherwise.

---

## Requirements

- **.NET 10 SDK** (10.0.100 or newer 10.0.x). The desktop app targets `net10.0`.
- A RimWorld installation to point RimPulse at — **1.6** (Unity/Mono) or **1.7** (Waylith/.NET 10).
  RimPulse itself runs without one (it will just report that nothing was detected).
- Linux is the primary platform (developed against Linux Mint 22.x / Debian 12 class systems).
  The architecture is platform-agnostic and path handling is unit tested for Windows and macOS, but only
  Linux has been exercised so far.

## Quick start

```bash
git clone <this-repository> RimPulse
cd RimPulse

dotnet build RimPulse.slnx            # build everything
dotnet run --project src/RimPulse.App # start the desktop application
dotnet test RimPulse.slnx             # run the test suite
```

If your game is not in a Steam or GOG library, point RimPulse at it explicitly:

```bash
RIMWORLD_DIR="$HOME/games/RimWorld" dotnet run --project src/RimPulse.App
```

Full build, test, publish and troubleshooting instructions live in [`DEVELOPMENT.md`](DEVELOPMENT.md).

## Inspecting a mod project

Open **Mod project**, browse for a mod root or enter its path, then choose **Add**. Projects you add are
listed on the left, remembered across sessions and inspected again at startup; selecting one re-inspects it,
and **Remove from list** forgets it without touching the directory. The display includes the name, package ID,
author(s), supported versions and declared dependencies. Missing or malformed metadata is reported, and no
files are changed.

Dependency declarations are displayed, not resolved against installed mods. `LoadFolders.xml` is interpreted
per game version as described below. Only the selected project is watched while RimPulse is open; detected
changes are debounced and listed by path and category. About.xml changes refresh the metadata; changed XML is
checked for syntax only, not against the RimWorld schema. `bin`, `obj`, VCS/editor directories and common
temporary files are ignored. File-system notifications are best-effort; monitoring errors are reported, and
**Inspect / refresh** re-reads the project and restarts monitoring. Changes during that refresh may be missed.

### Interpreting `LoadFolders.xml`

For the selected installation's version, RimPulse reads the matching `<v1.6>`/`<v1.7>` section and lists the
folders that version would load, together with the entries whose conditions exclude them and the condition
that did it. `IfModActive`, `IfModActiveAny` and `IfModNotActive` are treated as "or" lists,
`IfModActiveAll` (RimWorld 1.6+) as "and", and several attributes on one entry must all pass — the semantics
published by the RimWorld wiki. Only the mod's own `packageId` is treated as active, because RimPulse does not
read the game's active mod list; conditional folders are therefore reported, not assumed. A missing section
is reported as such: `LoadFolders.xml` supersedes RimWorld's default versioned-folder rules, and RimPulse will
not invent a folder list for that version.

### Building the mod

**Build mod** compiles the mod's C# projects against the selected installation and writes the resulting
assemblies into the mod's `Assemblies` folder — the only place RimPulse ever writes inside a mod directory, and
only as a direct result of pressing the button. Projects are compiled in dependency order, so a referenced
project is built first and the projects that reference it are linked against that output. Diagnostics are
listed with file, line and column. `DEBUG` and `TRACE` are defined, matching a development build.

RimPulse reads a documented subset of MSBuild: `Compile` items (literal paths, `*.cs` and `**/*.cs`), `Reference`
with `HintPath`, `ProjectReference`, and the unconditional `AssemblyName`, `TargetFramework`, `LangVersion`,
`DefineConstants`, `AllowUnsafeBlocks` and `Nullable` properties. Conditional properties and items, other
wildcards and MSBuild targets are not evaluated, and anything ignored is reported rather than guessed.

## How installation detection works

1. **Explicit paths** — anything you added in the UI or that `RIMWORLD_DIR` points at.
2. **Steam** — every library listed in `steamapps/libraryfolders.vdf` under the well-known Steam roots
   (`~/.steam/steam`, `~/.local/share/Steam`, `~/.steam/debian-installation`, the Flatpak root, …), using
   `appmanifest_294100.acf` to find the install directory.
3. **GOG** — `~/GOG Games/*`, `~/Games/*`, `~/gog-games/*`.

Each candidate is inspected, not trusted:

| Check | What RimPulse looks at |
| --- | --- |
| Layout | `<root>/Assembly-CSharp.dll` + `Waylith.dll` (1.7+) — or `RimWorldLinux_Data/Managed/Assembly-CSharp.dll` (≤ 1.6, plus Windows/macOS path variants) |
| Version | `<root>/Version.txt` only. No inference from folder names or engine layout |
| Assemblies | `Assembly-CSharp.dll`, `UnityEngine.CoreModule.dll` for the Unity layout; `Assembly-CSharp.dll`, `Waylith.dll`, `UnityStubs.dll` for Waylith |
| Support | 1.6 and 1.7 are supported; other versions are detected, labelled unsupported, and never silently dropped |

Directories discovered automatically that are not usable installations are ignored. Paths **you** add are
always reported, together with the reason they cannot be used.

## Where RimPulse stores data

RimPulse never writes into your RimWorld installation or your mod directories.

| Platform | Configuration | State / logs |
| --- | --- | --- |
| Linux | `$XDG_CONFIG_HOME/RimPulse/config.json` (`~/.config/RimPulse`) | `$XDG_STATE_HOME/RimPulse/logs/rimpulse.log` (`~/.local/state/RimPulse`) |
| Windows | `%APPDATA%\RimPulse\config.json` | `%LOCALAPPDATA%\RimPulse\logs\rimpulse.log` |
| macOS | `~/Library/Application Support/RimPulse/config.json` | `~/Library/Logs/RimPulse/rimpulse.log` |

A corrupt `config.json` is moved aside (never deleted) and RimPulse starts from defaults.

## Repository layout

```text
src/
  RimPulse.Core/          UI-independent logic
    Configuration/        XDG paths, JSON persistence (atomic save, corruption recovery)
    Diagnostics/          Bounded log, rotating file sink
    Game/                 GameVersion model (1.6 / 1.7, future versions tolerated)
    Installations/        Layout probing, version file reading, Steam VDF parsing, detection
    Platform/             Host platform abstraction (Linux / Windows / macOS)
    Projects/             Mod metadata, content folders and LoadFolders.xml interpretation
    Monitoring/           Batched file notifications and classification
    Build/                Project discovery, game reference resolution, Roslyn compilation
    Validation/           Changed XML well-formedness checks
  RimPulse.App/           Avalonia desktop application (MVVM, hand-written, no MVVM framework)
    Views/ ViewModels/ Styles/ Converters/ Services/
tests/
  RimPulse.Core.Tests/    xUnit v3 tests over real temporary directories
docs/
  SPEC.md                 The verbatim project brief
  ARCHITECTURE.md         Layers, decisions, threading, dependency justification
  GAME-VERSIONS.md        Verified RimWorld 1.6 vs 1.7 runtime differences and open questions
DEVELOPMENT.md            Exact build / run / test / publish / troubleshooting commands
```

## Credits and references

RimPulse is built in conversation with the RimWorld modding community. The projects below were studied as
reference for specific milestones; **every creator and repository is also featured inside the application**
(header → **Credits**), which is the authoritative list. RimPulse adapts no RimWorld tooling code
— those projects were studied as reference, and the license column exists so that any future adoption decision
is informed. The three dependencies that RimPulse actually builds on are listed as well.

| Project | Creator | License | Relevant to |
| --- | --- | --- | --- |
| [Reloader](https://github.com/pardeike/Reloader) | Andreas Pardeike | MIT | Hot reload prior art (milestone 6) |
| [RimWorldHotReload](https://github.com/xiao-e-yun/RimWorldHotReload) | xiao-e-yun | BSD-3-Clause | Hot reload prior art (milestone 6) |
| [RimBridgeServer](https://github.com/pardeike/RimBridgeServer) | Andreas Pardeike | MIT | In-game IPC / remote control (milestones 4–5) |
| [RimWorldDevMCP](https://github.com/search?q=RimWorldDevMCP&type=repositories) | Jeffrharr | MIT | Driving native dev actions from outside the game (milestone 5) |
| [RimTest Redux](https://github.com/search?q=RimTest+Redux&type=repositories) | ilyvion | Custom (see repo) | In-game automated checks that a reload really happened |
| [Rimworld-Pickle](https://github.com/RimWorks/Rimworld-Pickle) | RimWorks | MIT | Live-session testing (milestones 5–6) |
| [pickle-template](https://github.com/RimWorks/pickle-template) | RimWorks | MIT | Well-structured mod projects to inspect (milestones 2–3) |
| [rimworld-logging-framework](https://github.com/RimWorks/rimworld-logging-framework) | RimWorks | MIT | Structured diagnostics for the companion mod |
| [steam-game-image-action](https://github.com/RimWorks/steam-game-image-action) | RimWorks | MIT | Testing against a real install in CI |
| [semantic-release-steam](https://github.com/RimWorks/semantic-release-steam) | RimWorks | MIT | Workshop release automation (milestone 7) |
| [RimWorks (organisation)](https://github.com/RimWorks) | RimWorks | Mixed, mostly MIT | Engineering-first mod tooling |
| [RimSort](https://github.com/RimSort/RimSort) | RimSort contributors | GPL-3.0 | Mod metadata and load-order handling (reference only) |
| [RimworldXsd](https://github.com/Ragonfana/RimworldXsd) | Ragonfana | No license declared | XML validation (reference only) |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | Avalonia Team | MIT | **Shipped dependency** — the desktop UI framework |
| [Roslyn](https://github.com/dotnet/roslyn) | Roslyn contributors (Microsoft and the .NET community) | MIT | **Shipped dependency** — compiles mod C# in-process (milestone 3) |
| [xUnit.net](https://github.com/xunit/xunit) | xUnit.net contributors | Apache-2.0 | **Development dependency** — runs the test suite, not shipped |

**Attribution policy.** If code, data or assets from any of these projects are ever adapted, the credit entry
must be marked as adapted rather than reference, the original author must remain credited in the application,
and the original license must be honoured. Copyleft (GPL-3.0) and unlicensed projects must not be copied into
RimPulse without an explicit licensing decision.

## Documentation

- [`docs/SPEC.md`](docs/SPEC.md) — the product brief (verbatim).
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — module boundaries, design decisions, extension points.
- [`docs/GAME-VERSIONS.md`](docs/GAME-VERSIONS.md) — what is verified about RimWorld 1.6 vs 1.7, what is
  unverified, and what must be confirmed against a real installation before milestones 4–6.
- [`DEVELOPMENT.md`](DEVELOPMENT.md) — commands and troubleshooting.

## Verification status

Honesty matters more than a green checkmark. Current state, on Ubuntu with the .NET 10 SDK (GitHub Actions):

| Check | Result |
| --- | --- |
| `dotnet build RimPulse.slnx -c Release` | **Passes in CI** (GitHub Actions, Ubuntu, .NET 10) with warnings as the compiler sees them; no warnings in the last run |
| `dotnet test RimPulse.slnx -c Release` | **Passes in CI** — 177 tests, including the project, `LoadFolders`, build-input, reference-resolution, Roslyn compiler and assembly-inspection tests |
| The desktop window on a real desktop | **Verified locally for the previous milestone** — the app started, detected an installation and inspected a mod directory on Linux. The new project list and build panel have not been run on a desktop yet |
| Detection against a real RimWorld installation | **Partially verified** — a scan on the author's machine reported one installation; whether its version was confirmed was not recorded |
| Building a real mod against a real installation | **Not verified** — the compiler is exercised in CI against the test runtime's assemblies, not against game assemblies from a real install |
| Windows / macOS runtime behaviour | **Not verified** — path handling is unit tested for all three platforms |

CI has earned its keep twice: it found a real defect (the log sink left no active log file after a rotation,
which would break `tail -f`) and several wrong test expectations. Only green-that-is-true counts here.

Everything except installation detection and the compiler's use against real game assemblies runs without
RimWorld installed, so the suite works on any development machine; see
[`DEVELOPMENT.md`](DEVELOPMENT.md#verification-status).
