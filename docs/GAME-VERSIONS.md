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
drives native developer actions from outside the process. Both are relevant to how RimPulse's companion mod
should expose dev tools over IPC, and both are featured in the app's Credits panel.

### Testing environment (milestone 5)

The world/map generation APIs are the same managed API in both versions (1.7 claims mod compatibility),
but the harness that drives them differs: 1.6 needs a companion mod running on Mono/.NET 4.7.2, 1.7 needs
one compiled for `net10.0`. Two companion projects, one shared protocol.

## 4. Open questions to answer against a real installation

Keep this list current; each item blocks or shapes a later milestone.

1. Exact `Version.txt` contents for a 1.7 build (does it include a `rev` suffix?).
2. Asset folder layout in 1.7 (textures/audio paths used by the engine).
3. How 1.7 loads mod assemblies (loader code, `AssemblyLoadContext` usage, dependency resolution order).
4. Whether 1.6 mod assemblies can be swapped safely at all, or whether restart is the only option.
5. Command-line arguments accepted by each build for automated launches (developer mode, skip intro,
   direct save loading) — must be verified per version.
6. Where each version keeps `ModsConfig.xml` and whether it can be prepared before launch.
7. Whether both versions can coexist in one installation directory (Steam beta switching).

## 5. How RimPulse encodes this today

Milestone 1 only needs the layout and version facts, which are implemented and tested:

- `RimPulse.Core.Installations.InstallationLayout` — `UnityMono` vs `Waylith` vs `Unknown`.
- `RimPulse.Core.Installations.InstallationProbe` — recognises both layouts, verifies the required
  assemblies per layout, and never guesses a version.
- `RimPulse.Core.Game.GameVersion` — `major.minor` plus build text, so 1.8 is detected and reported as
  unsupported instead of being ignored.

No runtime, IPC or reload code exists yet, and nothing in this document should be read as a claim that it
does.
