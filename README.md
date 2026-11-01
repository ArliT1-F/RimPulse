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

## Project status

This repository is being built incrementally, one milestone at a time. Nothing below is claimed as
working unless it is implemented and covered by a test.

| Milestone | Scope | State |
| --- | --- | --- |
| **1 — Foundation** | Avalonia desktop shell, configuration persistence, RimWorld installation detection (1.6 / 1.7), installation selection UI, Linux-first build | **Implemented** |
| 2 — Project management | Mod project directories, `About.xml` parsing, structure detection, dependency detection, file monitoring | Not started |
| 3 — Build & validation | `.csproj` detection, game reference resolution, Roslyn compilation, XML validation, assembly inspection | Not started |
| 4 — Runtime integration | Companion mods (1.6 / 1.7), IPC, process management, runtime status | Not started |
| 5 — Testing environment | Minimal world/map generation, configurable world features, Developer Mode integration, entity spawning, tick controls | Not started |
| 6 — Live reload | Asset reload, definition reload, assembly reload research per version, restart fallback | Not started |
| 7 — Packaging | AppImage, `.deb`, desktop entry, Windows packaging | Not started |

### What actually works today (milestone 1)

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
- Streams diagnostics to a bounded in-app activity log and a rotating log file.

### Deliberately not there yet

No mod project loading, no compilation, no game launching, no hot reload, no testing environment. Those
are later milestones; nothing in the UI pretends otherwise.

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

## Documentation

- [`docs/SPEC.md`](docs/SPEC.md) — the product brief (verbatim).
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — module boundaries, design decisions, extension points.
- [`docs/GAME-VERSIONS.md`](docs/GAME-VERSIONS.md) — what is verified about RimWorld 1.6 vs 1.7, what is
  unverified, and what must be confirmed against a real installation before milestones 4–6.
- [`DEVELOPMENT.md`](DEVELOPMENT.md) — commands and troubleshooting.

## Verification status

Honesty matters more than a green checkmark:

- The C# sources in this repository were **not compiled or executed** in the environment where milestone 1
  was written: that sandbox had no .NET SDK and no NuGet access. Every `.cs` file was parsed with a C#
  grammar (no syntax errors) and the XML/AXAML files were validated as well-formed, but semantic
  verification — `dotnet build` and `dotnet test` — must be run on a machine with the .NET 10 SDK.
- The test suite is written against the real behaviour of the code (temporary directories, fake Steam
  libraries, fake Unity and Waylith installs). See [`DEVELOPMENT.md`](DEVELOPMENT.md#verification-status)
  for the current, honest status of each area.
- Nothing in milestone 1 requires RimWorld to be installed, so the suite is fully runnable on any
  development machine.
