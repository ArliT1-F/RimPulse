# RimWorld 1.6 vs 1.7 — runtime differences

RimPulse must never assume the two supported game versions behave the same way. This document records
what is **actually known** about each version, where that knowledge comes from, and what still has to be
verified against a real installation before milestones 4–6 are implemented.

Legend for every claim below:

- **Verified (documentation)** — stated by Ludeon or by maintained community documentation, with a source.
- **Requires a real installation** — must be confirmed by inspecting an installed copy before code is written.
- **Unknown** — no reliable information; do not build on it.

---

## 1. Summary

| | RimWorld 1.6 (stable branch) | RimWorld 1.7 (Waylith) |
| --- | --- | --- |
| Engine | Unity **2022.3.35** | Ludeon's in-house **Waylith** engine |
| Rendering | Unity (DirectX/Vulkan/OpenGL backends) | Vulkan |
| Scripting runtime | Bundled **Mono**, .NET Framework **4.7.2** profile | **.NET 10** |
| Game assemblies | `Assembly-CSharp.dll` + `UnityEngine*.dll` in `RimWorld*_Data/Managed` | `Assembly-CSharp.dll`, `Waylith.dll`, `UnityStubs.dll` (and `Waylith.UnityCompat.dll`) in the **installation root** |
| Mod C# target | SDK-style or legacy csproj targeting `net472` | SDK-style csproj targeting `net10.0` (the `.0` matters) |
| Unity API | Real `UnityEngine` assemblies | `UnityStubs.dll` shim — `using UnityEngine;` still compiles |
| Harmony | Harmony 2.x | **Harmony 3.0** (shipped as a mod dependency, not bundled) |
| Save compatibility | — | 1.6 saves load in 1.7 and vice versa |
| Mod compatibility | — | “99% of 1.6 mods should work out of the box”; Prepatcher-dependent mods break |
| Release channel | Steam main branch | Steam **unstable beta** branch (since the 12 Oct 2026 announcement) |

Sources:

- RimWorld Wiki, *Modding Tutorials* — “RimWorld is a multi-platform game written on Unity 2022.3.35 …
  RimWorld 1.7 uses an entirely new bespoke engine called Waylith, running on .NET 10 and Vulkan.”
  <https://rimworldwiki.com/wiki/Modding_Tutorials>
- RimWorld Wiki, *RimWorld 1.7 Mod Updates* — engine is “a completely new engine called Waylith”, “custom
  made shim to translate former Unity calls”, mods must target `net10.0`, must reference
  `Assembly-CSharp.dll`, `UnityStubs.dll`, `Waylith.dll`, must use Harmony 3.0, and should use
  `LoadFolders.xml` to separate 1.6 and 1.7 content.
  <https://rimworldwiki.com/wiki/Modding_Tutorials/RimWorld_1.7_Mod_Updates>
- Ludeon’s 1.7 announcement coverage (engine switch, save compatibility in both directions, unstable beta
  branch) — <https://www.notebookcheck.net/Popular-Steam-game-abandons-Unity-for-new-engine-promising-up-to-4x-better-performance.1422995.0.html>

## 2. Installation layout

### 1.6 and earlier — Unity player layout (implemented in `InstallationProbe`)

```text
RimWorld/
├── Version.txt                       ← authoritative version, e.g. "1.6.4851 rev1248"
├── RimWorldLinux_Data/Managed/       ← Assembly-CSharp.dll, UnityEngine.CoreModule.dll, mscorlib, …
├── Mods/
└── RimWorldLinux (executable)
```

Windows uses `RimWorldWin64_Data/Managed`; the macOS bundle keeps the same tree under
`RimWorldMac.app/Contents/Resources/Data/Managed`.

### 1.7+ — Waylith layout (implemented in `InstallationProbe`)

```text
RimWorld/
├── Version.txt
├── Assembly-CSharp.dll
├── Waylith.dll
├── UnityStubs.dll
├── Waylith.UnityCompat.dll
├── Mods/
└── RimWorld (executable)
```

RimPulse detects the layout structurally (`Assembly-CSharp.dll` in the root ⇒ Waylith) and never infers
the version from it. `Version.txt` is the only version source.

### Requires a real installation

- Whether 1.7 still ships `Version.txt` at the root, and its exact contents.
- Whether 1.7 keeps any `*_Data` folder for assets (textures, audio) that asset reload (milestone 6) needs.
- The macOS bundle layout for 1.7.
- Whether a single installation directory can hold both a 1.6 and a 1.7 build (dual installs via Steam
  betas usually live in the same folder and are switched, not kept side by side).

## 3. Consequences for RimPulse

### Build and reference resolution (milestone 3)

| | 1.6 | 1.7 |
| --- | --- | --- |
| Reference set | `Assembly-CSharp.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.IMGUIModule.dll`, `UnityEngine.TextRenderingModule.dll`, … from `Managed/` | `Assembly-CSharp.dll`, `UnityStubs.dll`, `Waylith.dll`, `Waylith.UnityCompat.dll` from the install root, plus `0Harmony.dll` from the Harmony mod |
| Target framework | `net472` | `net10.0` |
| Framework reference pack | Not installed on modern Linux machines — build against the assemblies shipped with the game (`FrameworkPathOverride` style) | Provided by the .NET 10 targeting pack |
| Project style | Legacy csproj may exist and must still be supported | Legacy csproj **does not work**; an SDK-style project is required |

This is why the build layer of RimPulse must be a **version-specific backend** rather than one generic
compiler invocation.

**What is implemented today** (`RimPulse.Core.Build`):

- The reference set is every `*.dll` in the installation's managed directory (the `Managed/` folder for 1.6,
  the installation root for 1.7), de-duplicated by assembly name with the game's copy winning.
- For the Waylith layout the assemblies of the runtime RimPulse itself runs on
  (`TRUSTED_PLATFORM_ASSEMBLIES`) are added, because a `net10.0` mod needs `System.*` and the installation does
  not ship the whole framework. **This is an assumption to verify against a real 1.7 installation**: if the
  game ships a specific reference set, RimPulse should use that instead.
- For the Unity/Mono layout nothing from the host runtime is added: a 1.6 installation ships `mscorlib` and
  `System` itself, and mixing a `net10.0` framework into a `net472` compile would be wrong.
- A `HintPath` is expanded before it is used: MSBuild properties from the project, the nearest
  `Directory.Build.props`, the props files it imports and the environment are evaluated first, and the
  `Condition`s on properties, imports and items are evaluated too. The selected installation is also supplied
  as immutable global properties (`RIMWORLD_DIR` / `RimWorldDir` and `RIMWORLD_MANAGED_DIR` /
  `RimWorldManagedDir`), so a path copied from another machine still points at the game selected in RimPulse.
  This is what makes the usual `<HintPath>$(RimWorldManagedDir)/Assembly-CSharp.dll</HintPath>` work.
- SDK-style projects retain their implicit `.cs` source set even when they explicitly include linked sources;
  `Compile` globs support `*`, `?`, recursive `**` path segments, relative directories, and semicolon-separated
  specifications, along with `Exclude` and `Remove`.
- If a declared hint path still points at a file that is not on this machine (another computer's install, a
  Windows path on Linux), the same file name is taken from the selected installation's managed folder and the
  substitution is reported, so one missing path does not become a wall of `CS0246` errors.
- Harmony is **not** referenced automatically. A mod that needs `0Harmony.dll` must declare a `HintPath` to it
  (its own csproj normally already does) — or declare it as a `PackageReference`, which RimPulse resolves from
  the **already restored** NuGet global packages folder (`NUGET_PACKAGES`, else `~/.nuget/packages`), choosing
  `ref/` before `lib/` and the nearest compatible target framework. RimPulse never runs NuGet restore: a package
  that is not restored, uses a floating version or a version range, or has no framework compatible with the
  project is reported as a single problem instead of being guessed at.
- The same simple assembly name is only referenced once. When a project and the installation both provide an
  assembly, a platform assembly (`Assembly-CSharp`, `UnityEngine*`, `System*`, `mscorlib`, …) comes from the
  installation and any other assembly (Harmony, a shared library) comes from the project. The choice is written
  to the log, because a silent second copy makes Roslyn report `CS1703` and every shared type ambiguous.
- A failed build records its inputs, reference set, first diagnostics and an error-code histogram in the log
  (`BuildLogReport`); the summary line the app used to log alone only contained counts.
- Mod assemblies are emitted to the mod's `Assemblies/` folder, which is where both versions load a mod's
  DLLs. When the mod keeps version folders, the folder of the selected installation's `major.minor` (`1.6`)
  replaces the mod root, so the output goes to `1.6/Assemblies/`; the resolution is
  `ModOutputDirectoryResolver` in Core. Whether that folder is the only lookup path in 1.7 is part of open
  question 3 below.

### Companion and reusable development profile

| | 1.6 | 1.7 |
| --- | --- | --- |
| Companion input | Separate adapter compiled against the selected installation's Unity/Mono managed assemblies | Separate adapter compiled against the selected Waylith assemblies plus the desktop's .NET runtime references |
| API used | `LongEventHandler.ExecuteWhenFinished`, `VersionControl.CurrentVersionStringWithRev`, `LoadedModManager.RunningModsListForReading` and each mod's name, package ID and root directory | Same surface is assumed to exist; compatibility and startup timing are **not verified** against a real 1.7 game |
| Save/config profile | `-savedatafolder=<absolute path>` researched in 1.6 game startup; paths containing `=` are rejected by RimPulse because of the 1.6 argument parser | The same option is used experimentally; real Linux behavior and mod/config loading need verification |
| Installation `Mods/` | Discovered from the game installation, not the separate save-data directory | Treated as shared; verify on the real game |
| Companion verification | Synthetic adapter compilation uses .NET 10 stand-ins, **not Mono**. User reports linking/launch working, but exact version and IPC readiness are unconfirmed | Synthetic fake-game build and local IPC exchange only; no confirmed real 1.7 run |

The 1.6 startup, mod discovery and argument behavior above were researched read-only in
[32bitx64bit's 1.6 decompilation](https://github.com/32bitx64bit/RW-Decompiled-1.6); game code belongs to
Ludeon Studios and none was copied. [RimLoc's isolated-run notes](https://github.com/0-danielviktorovich-0/RimLoc)
also discuss shared Mods and platform preference limitations. In the 1.6 code path, the companion queues a
snapshot after startup static constructors, but this is **not evidence that the main menu appeared or all mods
initialized successfully**. RimPulse's 1.7 timing and API assumptions require a real installation.

RimPulse now reuses a named profile's data directory. Its GUI selects installed mods/DLC, resolves declared
dependencies and validates hard load order before writing a version-stamped `ModsConfig.xml`. Installed but
unselected official expansions are recorded as known to prevent 1.6 auto-activation. Only exposed preference
fields are merged; unrelated preferences and saves are retained. Normal launch does not apply this profile.
The legacy fresh `runtime-sessions` creator remains in Core but is not used by the new GUI flow.

Native Developer Mode is a game preference, not an IPC command. The optional 1.6 `-quicktest` path was
researched in `GenCommandLine`, `QuickStarter` and `Root_Play.SetupForQuickTestPlay`: it generates a normal
world (Crashlanded, Cassandra/Rough, 30% coverage, random seed/tile) and a **250×250 map**. It is not a minimal
sandbox. The native developer palette can be requested through `quickStartDevPaletteOn`. God mode, spawning
and tick actions remain native runtime tools; RimPulse does not automate or confirm them.

On 1.7, native quick test is disabled and the checkbox explains why. The same save-data/preference approach
is experimental until checked against real Waylith behavior. No 1.7 quick-test equivalence is inferred.

Separate save data does not sandbox the game or mods. Engine preferences and logs may remain shared. In
[xtzhyhydwl/RimWorld_auto_test](https://github.com/xtzhyhydwl/RimWorld_auto_test)'s independent **Windows**
1.7 experiment, `-logFile` was reportedly ignored and the default `Player.log` remained shared; that observation
is not proof of Linux behavior. Verify both versions and actual log locations locally before relying on them.

### Assembly reload (milestone 6) — the hard problem

> Prior art to study before writing any reload code (both featured in the app's Credits panel):
> [pardeike/Reloader](https://github.com/pardeike/Reloader) — developing and patching code while the game runs,
> and [xiao-e-yun/RimWorldHotReload](https://github.com/xiao-e-yun/RimWorldHotReload) — mod-level hot reload.
> They answer, from experience, which parts of the problem are actually tractable.

**1.6 (Mono, .NET Framework 4.7.2):**

- The CLR running RimWorld 1.6 cannot unload a non-collectible assembly. Loading a rebuilt
  `MyMod.dll` next to the old one leaves two assemblies defining the same types; game objects,
  `ThingComp` instances, Harmony patches, static fields and delegates keep pointing at the old one.
- Realistic strategies, in order of preference:
  1. Reload what is genuinely reloadable at runtime (definitions, textures) — no assembly swap.
  2. Replace Harmony patches only (`Harmony.UnpatchAll`) when the changed code is patch-only and the
     patch targets are stable. Requires verification.
  3. Controlled process restart with the testing map restored (RimPulse must make this fast and
     transparent, and must say *why* a restart happened).
- Anything claiming to "unload" the old assembly in 1.6 should be treated as unsafe until proven otherwise.

**1.7 (.NET 10):**

- .NET 10 *does* have collectible `AssemblyLoadContext`, so unloading is technically possible — but only if
  every reference to the old assembly is released (static fields, Harmony patches, event handlers,
  background threads, delegates, cached defs, types held by game objects).
- RimWorld’s own mod loader decides how mod assemblies are loaded. Whether a dev assembly loaded into a
  collectible ALC that shares game assemblies is usable by the game — and whether the old ALC can be
  collected after a swap — **must be verified experimentally**, not assumed.
- Requires a real installation: inspect how 1.7 loads `Assemblies/*.dll`, whether an ALC is already used,
  and what the assembly resolution path is.

### Remote control and IPC (milestones 4–5)

[pardeike/RimBridgeServer](https://github.com/pardeike/RimBridgeServer) already runs an MCP server inside
RimWorld to control and observe a running game, and the [RimWorldDevMCP ecosystem](https://github.com/search?q=RimWorldDevMCP&type=repositories)
drives native developer actions from outside the process. Both are relevant to possible future developer
actions and are featured in the app's Credits panel. RimPulse currently implements only an authenticated,
read-only startup status and heartbeat, documented in [IPC.md](IPC.md); it does not expose game commands.

### Testing environment (milestone 5)

The world/map generation APIs are the same managed API in both versions (1.7 claims mod compatibility),
but the harness that drives them differs: 1.6 needs a companion mod running on Mono/.NET 4.7.2, 1.7 needs
one compiled against the 1.7 reference set. RimPulse keeps two companion source adapters and one shared
protocol; the exact real-game compatibility of each still needs testing.

For 1.6, read-only inspection of [`GenFilePaths`](https://github.com/32bitx64bit/RW-Decompiled-1.6/blob/main/Verse/GenFilePaths.cs)
shows `.rws` saves under the save-data `Saves` directory and `.rsc` scenarios under `Scenarios`. The profile
importer copies user-selected files there; it does not generate or validate them. The game must load them.
The 1.7 paths and actual isolated-profile behavior are not verified on a real installation.

The 1.6 scenario editor's [`ScenPart_IncidentBase`](https://github.com/32bitx64bit/RW-Decompiled-1.6/blob/main/RimWorld/ScenPart_IncidentBase.cs)
selects from loaded incident definitions. [`IncidentWorker`](https://github.com/32bitx64bit/RW-Decompiled-1.6/blob/main/RimWorld/IncidentWorker.cs)
checks matching disable-incident scenario parts when storyteller settings are not bypassed. This is not a
universal switch for all caravan movement, manually triggered incidents, or mod code. Faction and settlement
generation are separate concerns. No game code was copied, and RimPulse does not yet create or apply these
rules. The same behavior must not be assumed for 1.7 without verification.

### Design rule: custom worlds and selective activity

A future profile may configure world generation and unwanted activity, but these are not interchangeable
switches. For example, excluding tribal factions from a newly generated world is a world-generation policy;
disabling random caravan incidents is an incident policy; preventing a player from forming a caravan would
change gameplay and could break mods. RimPulse must not present one broad “disable tribes/caravans” toggle
as if it guarantees all three. Keep player-controlled and mod-triggered functionality available unless a
separate, explicitly scoped policy is verified.

Before exposing a control, verify per game version: the exact generation/incident hook, whether it applies to
already-created worlds, whether scenario settings can be bypassed (including by direct/mod-triggered calls),
and whether the minimum valid player faction/world setup still initializes. Mark unsupported controls
unavailable rather than saving preferences the companion cannot enforce. World settings that affect
creation must show a regenerate-world consequence; ordinary code reloads must not silently regenerate a
persisted test world. Real-game verification is a release gate for each supported version.

## 4. Open questions to answer against a real installation

Keep this list current; each item blocks or shapes a later milestone.

1. Exact `Version.txt` contents for a 1.7 build (does it include a `rev` suffix?).
2. Asset folder layout in 1.7 (textures/audio paths used by the engine).
3. How 1.7 loads mod assemblies (loader code, `AssemblyLoadContext` usage, dependency resolution order).
4. Whether 1.6 mod assemblies can be swapped safely at all, or whether restart is the only option.
5. Command-line arguments accepted by each build for automated launches (including `-savedatafolder`,
   developer mode, skip intro and direct save loading) — must be verified in actual games per version.
6. Whether both games honor the prepared separate `ModsConfig.xml` and which logs/preferences remain shared.
7. Whether both versions can coexist in one installation directory (Steam beta switching).
8. Whether the host runtime's .NET 10 assemblies are an adequate reference set for compiling a 1.7 mod, or
   whether the game provides its own reference/targeting assemblies (see §3, build and reference resolution).
9. Which `.NET` framework a 1.7 installation expects a mod's `TargetFramework` to declare, and whether a
   `net472`-targeted 1.6 mod is accepted by the 1.7 loader (the mod is said to work, but the loader may warn).

## 5. How RimPulse encodes this today

The layout and version facts are implemented and tested:

- `RimPulse.Core.Installations.InstallationLayout` — `UnityMono` vs `Waylith` vs `Unknown`.
- `RimPulse.Core.Installations.InstallationProbe` — recognises both layouts, verifies the required
  assemblies per layout, and never guesses a version.
- `RimPulse.Core.Game.GameVersion` — `major.minor` plus build text, so 1.8 is detected and reported as
  unsupported instead of being ignored.
- `RimPulse.Core.Build.GameReferenceResolver` — the per-layout reference set described in §3.
- `RimPulse.Core.Projects.LoadFoldersReader` — version sections and conditions, as documented by the wiki;
  used to show which content folders a version would load.
- `RimPulse.Companion/` — shared status client and separately compiled 1.6/1.7 source adapters.
- `RimPulse.Core.Runtime` and `IPC` — explicit links, reusable profiles, catalog/order planning, Linux child launch and
  authenticated startup snapshots. None is verified against an actual game for this increment.

No hot reload, testing-map or game-command implementation exists yet.
