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
| 2 — Project management | Mod project directories, `About.xml` parsing, structure detection, dependency detection, file monitoring | **In progress** — multiple-project inspection and monitoring implemented; dependency resolution remains |
| 3 — Build & validation | `.csproj` detection, game reference resolution, Roslyn compilation, XML validation, assembly inspection | **In progress** — changed-file XML syntax checks plus conservative per-file `Defs` structure checks |
| 4 — Runtime integration | Companion mods (1.6 / 1.7), IPC, process management, runtime status | **In progress / experimental** — opt-in 1.6/1.7 companions, local IPC and separate profiles implemented; real-game verification pending |
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
- Monitors the selected directory, batches and classifies changes, refreshes metadata when `About.xml` changes, and checks changed XML for well-formedness. Files under `Defs/` also get conservative checks for a `<Defs>` or `<Patch>` root and duplicate explicit `defName` values within the same definition type in that file; this is not game-schema validation and does not resolve cross-file or vanilla-game inheritance.
- Finds the mod's `.csproj` projects, resolves the game assemblies of the selected installation and **compiles
  the mod in-process with Roslyn**, reporting every diagnostic with file, line and column. A build writes the
  assembly into the mod's `Assemblies` folder — or `1.6/Assemblies` when the mod keeps a folder for the selected
  installation's version — only when you press **Build** and only after compilation succeeded. The produced
  assembly is then read back from its metadata (name, version, references, types), so the build panel shows
  what the game would actually load.
- Leaves out a project that is a test harness rather than part of the mod — it declares `IsTestProject`,
  references xunit/NUnit/MSTest, or is named `*.Tests` — and says so in one line. The game never loads such an
  assembly, and those projects usually compile their own stand-ins for the game's API, which cannot be
  compiled side by side with the installation's real assemblies (that combination produces hundreds of
  `CS0436`/`CS0104`/`CS0121` errors). A build that fails that way anyway gets one explanatory line next to the
  diagnostics.
- Resolves `<PackageReference>` items from packages that are **already restored** in the NuGet global packages
  folder, so a Harmony dependency declared as a package compiles; a package that is missing, uses a floating
  version or is not compatible with the project's target framework is reported as one clear line instead of
  hundreds of `CS0246` errors. A reference the project and the installation both provide is resolved to one
  copy and reported, never silently duplicated.
- Writes a build's **inputs, reference set, first diagnostics and error-code histogram** to the log, so a
  failed build can be diagnosed from the log file alone (the summary line only carries counts).
- Launches the selected 1.6 or 1.7 Linux executable directly and displays its process ID and exit code. Only
  one launch can be monitored at a time. A normal launch uses the normal configuration; an optional experimental
  launch uses a separate save-data profile. Closing RimPulse leaves the game running.
- Opt-in setup builds a companion against the selected installation and links it into the game's `Mods/` folder.
  A launch-specific, authenticated loopback connection reports the game's version and a **startup-only snapshot**
  of running mods, with separate indicators for process, companion and selected-project match. A live process or
  even a companion snapshot is not proof that the main menu loaded or that every mod initialized successfully.
  Synthetic tests exercise both build paths and the 1.7 protocol; neither companion has been verified in a real game.
- Streams diagnostics to a bounded in-app activity log and a rotating log file.
- Ships an in-app **Credits** panel that features every referenced project and creator with its license and
  why it matters (see [Credits and references](#credits-and-references)).
- A previous commit passed CI. The current changes still need a full SDK build, official test run and real-game checks.

### Deliberately not there yet

No automatic mod/dependency or DLC resolution, no persistent reusable development profile, no complete XML
schema/semantic validation or cross-file `defName`/inheritance checks, no live mod-list updates, hot reload,
in-game command channel or testing environment. These are later work; a startup snapshot is not a live reload.

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
only as a direct result of pressing the button. When the mod uses version folders, the output follows the
selected installation: a folder named after its `major.minor` (e.g. `1.6`) becomes the mod root for that
version, so the assembly is written to `1.6/Assemblies` instead of the top-level `Assemblies` folder. Without
a matching version folder, the top-level `Assemblies` folder is used. `LoadFolders.xml` does not pick the
output folder — RimPulse reports the folders a version would load, but writes to the version folder (or the
root), and the build panel states the exact path before you press **Build**. Projects are compiled in
dependency order, so a referenced project is built first and the projects that reference it are linked
against that output. Diagnostics are listed with file, line and column. `DEBUG` and `TRACE` are defined,
matching a development build.

RimPulse evaluates the part of MSBuild a mod project actually uses, and properties are evaluated before the
items that read them. In MSBuild's order: the properties MSBuild itself provides (`Configuration` is `Debug`,
`Platform` is `AnyCPU`), the environment, the nearest `Directory.Build.props`, the files the project
`Import`s, and then every `PropertyGroup` in document order. For each build, the selected installation is also
passed as immutable MSBuild global properties: `RIMWORLD_DIR` / `RimWorldDir` point at the game root, and
`RIMWORLD_MANAGED_DIR` / `RimWorldManagedDir` point at the assembly directory. These selected paths take
precedence over a stale environment variable or project path written for another machine. A hint path written as
`$(RimWorldManagedDir)/Assembly-CSharp.dll` therefore resolves, including when the property lives in
`Directory.Build.props`.

`Condition`s are evaluated as well, on property groups, properties, imports and items: `==`, `!=`, `<`, `>`,
`<=`, `>=`, `and`, `or`, `!`, parentheses, and the `Exists()` and `HasTrailingSlash()` functions. `Compile`
items include SDK-style default source discovery and explicit literal paths or MSBuild-style globs (`*` and
`?` within a path segment, and `**` as a recursive path segment), including relative paths, semicolon-separated
patterns, `Exclude` and `Remove`. `Reference` with `HintPath`, `ProjectReference` and `PackageReference` are
read too. `Choose` processes the first true `When` branch and uses `Otherwise` only when no branch matches.
Item/string functions and MSBuild targets are not evaluated; anything ignored is reported rather than guessed at.

Every piece of text in the window is selectable: drag to select and press Ctrl+C, including compiler
diagnostics, paths and log lines. **Copy all errors** next to **Build mod** copies the complete diagnostic
list of the last build — not just the rows the panel is capped at — with a header naming the project, the
installation, the result and the log file, so a build failure can be pasted into a report as-is.

If a hint path still does not resolve — it points at another machine's installation, or at a property nothing
RimPulse can read defines — the same file name is taken from the selected installation's managed folder and
the substitution is reported, so a project written on Windows still compiles on Linux.

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

## Experimental companion and development profile (Linux)

This workflow is implemented but **not yet verified against actual 1.6/1.7 game installations**. Back up your
normal RimWorld configuration and saves before testing. A separate save-data profile is **not a security
sandbox**: game logs, Unity/engine preferences and arbitrary mods may still use shared paths, and 1.7 behavior
needs verification. The game's installation `Mods/` folder remains shared.

1. Select an installation and development project. Optionally **Build mod** first; this writes to the project's
   assembly output as described above.
2. In **Runtime**, explicitly choose **Install companion link**. RimPulse compiles its 1.6 or 1.7 adapter
   against that installation, writes an installation-specific generated mod under its state directory, and creates a symlink named
   `Mods/RimPulse.Companion`. It will not replace a pre-existing unrelated file, directory or link.
3. If your selected project is not already discoverable by the game, explicitly choose **Link selected project**.
   This creates a separate `Mods/RimPulse-Dev-<hash>` symlink without replacing an existing path. Avoid duplicate
   copies of the same package ID; a package ID match alone is not proof that the selected directory loaded.
4. Choose **Launch isolated development**. RimPulse requires its companion link and a build stamped for the
   selected game version; reinstall it after a game update. A fresh profile is created under RimPulse's state
   directory for **each** launch; its initial `ModsConfig.xml` selects Core, the companion and the selected project's
   package ID. RimPulse does not select other mods, dependencies or DLC; the game may enable newly discovered
   expansions itself. Add what you need in that game's mod menu and restart as required. A restart that creates
   a new process is not monitored by the old session; a later isolated launch creates another fresh profile.
5. Watch the separate process and companion statuses. Once connected, inspect the game-reported version and
   startup mod list. A project is matched only when both its package ID and directory agree. The snapshot is
   repeated as a heartbeat but **does not update during the session**. A connection confirms neither main-menu
   readiness nor successful mod initialization. If it never connects, inspect the game log and RimPulse log.

**Launch RimWorld normally** instead uses your normal configuration and only loads the companion if it is enabled there.
Installing a symlink alone does not change your normal `ModsConfig.xml`. On leaving RimPulse the game remains
running, and the companion eventually stops trying to reconnect. See [`docs/IPC.md`](docs/IPC.md) for the
wire contract and security limits.

### Cleanup

With the game and RimPulse closed, inspect `Mods/RimPulse.Companion` and `Mods/RimPulse-Dev-<hash>` and remove
**only those links that point to the directories you chose**; do not follow the links or delete the target
project. Remove no other mod files. The generated companion lives under `<RimPulse state>/companion/` and
retained per-launch save/config profiles under `<RimPulse state>/runtime-sessions/`; inspect and back up
anything you want before deleting those state subdirectories. They are not removed automatically.

## Where RimPulse stores data

RimPulse writes build output into a mod only on **Build mod**. The two explicit setup actions above may create
links under the game's shared `Mods/` directory. It does not directly rewrite the normal game configuration.

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
    Validation/           Changed XML syntax and basic Defs structure checks
    Runtime/ IPC/         Companion installation, development profiles, launch and authenticated status
  RimPulse.Companion/     Shared protocol/client and separately compiled 1.6 / 1.7 adapters
  RimPulse.App/           Avalonia desktop application (MVVM, hand-written, no MVVM framework)
    Views/ ViewModels/ Styles/ Converters/ Services/
tests/
  RimPulse.Core.Tests/    xUnit v3 tests over real temporary directories
docs/
  SPEC.md                 The verbatim project brief
  ARCHITECTURE.md         Layers, decisions, threading, dependency justification
  GAME-VERSIONS.md        RimWorld 1.6 vs 1.7 runtime differences and open questions
  IPC.md                  Local companion protocol, validation and limitations
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
| [RimWorld 1.6 decompilation](https://github.com/32bitx64bit/RW-Decompiled-1.6) | 32bitx64bit; game code by Ludeon Studios | No license declared; proprietary game code | Read-only research on startup, mod discovery and paths; no code copied |
| [RimWorld auto test](https://github.com/xtzhyhydwl/RimWorld_auto_test) | xtzhyhydwl | No license declared | Reference for separate profile and 1.7 launch observations |
| [RimLoc](https://github.com/0-danielviktorovich-0/RimLoc) | 0-danielviktorovich-0 | GPL-3.0 | Reference on profile isolation limits; no code copied |
| [AI in RimWorld](https://github.com/ryseymour/ai-in-rimworld) | ryseymour | No license declared | Linux launch and config research; no code copied |
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
- [`docs/IPC.md`](docs/IPC.md) — versioned companion protocol, threat model and readiness semantics.
- [`docs/GAME-VERSIONS.md`](docs/GAME-VERSIONS.md) — what is verified about RimWorld 1.6 vs 1.7, what is
  unverified, and what must be confirmed against a real installation before milestones 4–6.
- [`DEVELOPMENT.md`](DEVELOPMENT.md) — commands and troubleshooting.

## Verification status

| Check | Result |
| --- | --- |
| Full `dotnet build` / official `dotnet test` for these changes | **Not yet run** — the development environment lacks the .NET 10 SDK and NuGet access. An earlier commit passed GitHub Actions; that does not verify these changes. |
| Core and companion synthetic checks | **Partial** — Core compiled and 283 cases passed with a temporary .NET 10/Roslyn reflection harness and xUnit-compatible shims. Synthetic 1.6/1.7 references exercise both companion build paths; an in-process fake 1.7 game completes the authenticated snapshot exchange. These are not real game assemblies or an official SDK test run. |
| Avalonia desktop | **Not built for this change** — the view model was compiled against temporary UI stubs; real Avalonia/XAML compilation and a desktop session remain required. |
| Real installations | **Not verified for this increment** — build and load both companions, verify isolated profile behavior and check status on real Linux 1.6 and 1.7 installations. A process, connection or startup snapshot alone does not demonstrate main-menu readiness or successful mod initialization. |
| Windows / macOS runtime launch | **Not implemented** — direct launch and companion setup are currently Linux-only. |

See [`DEVELOPMENT.md`](DEVELOPMENT.md#7-verification-status) for commands and the real-game checklist.
