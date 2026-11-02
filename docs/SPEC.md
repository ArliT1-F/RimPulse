<!--
  This file is the verbatim project brief for RimPulse, preserved for reference.

  It was the initial contents of README.md (commit cbe316f). It is kept unedited on purpose: it is the
  specification the implementation is measured against. Do not "fix" its contents here — update the
  top-level README.md and the other documents in docs/ instead.
-->

Project: RimPulse — Live RimWorld Mod Development & Testing Environment

Tagline: Build. Reload. Test. Repeat.

1. Project Vision

Build a cross-platform desktop application called RimPulse that provides a dedicated, persistent development and testing environment for RimWorld mod developers.

RimPulse is not an IDE. Developers should continue using their preferred code editor, such as VS Code, Rider, or Visual Studio. RimPulse operates alongside their editor and manages the RimWorld development runtime.

The primary objective is to eliminate the repetitive development workflow of:

1. Editing mod files.
2. Building the mod.
3. Launching RimWorld.
4. Waiting for the game to load.
5. Testing the change.
6. Closing and restarting the game for the next change.

Instead, RimPulse should provide a persistent RimWorld process that monitors development directories, detects changes, validates and builds the mod, and reloads modified components whenever technically safe.

The application must use the actual RimWorld engine, game assemblies, definitions, APIs, and runtime behavior. Do not create a simulator, mock game engine, fake definition database, or substitute renderer.

The application should initially support:

* RimWorld 1.6
* RimWorld 1.7

Linux is the initial development and testing priority. Windows support should be designed into the architecture from the beginning.

⸻

2. Core Development Workflow

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

⸻

3. Technology Stack

Use a maintainable, cross-platform architecture.

Preferred stack:

* Language: C#
* Desktop UI: Avalonia UI
* Architecture: MVVM
* Compilation and diagnostics: Roslyn
* XML parsing and validation: System.Xml.Linq
* Assembly inspection: Mono.Cecil and/or reflection
* File monitoring: FileSystemWatcher with debouncing and batching
* IPC: JSON-based communication using named pipes, Unix domain sockets, or another suitable cross-platform mechanism
* Configuration: JSON
* Testing: xUnit or an equivalent .NET testing framework

Use a modern .NET version compatible with the selected desktop stack. Do not assume that the RimWorld runtime uses the same .NET version.

The RimWorld integration layer must accommodate the actual runtime requirements of each supported game version.

Avoid unnecessary external dependencies. Every dependency should have a clear technical justification.

⸻

4. Repository Inspection and Existing Code

Before making changes:

1. Inspect the entire existing GitHub repository.
2. Identify its current structure, architecture, dependencies, and build system.
3. Identify any existing functionality that can be reused.
4. Determine whether an existing application or prototype needs to be extended.
5. Avoid overwriting existing functionality without understanding its purpose.
6. Identify architectural limitations and technical blockers.

Do not blindly generate an entirely new project if a suitable foundation already exists.

Keep the repository buildable throughout development.

Do not leave placeholder implementations, fake functionality, or unfinished methods presented as working features.

If a feature cannot yet be implemented, clearly document its status and technical reason.

⸻

5. Application Architecture

Use a modular architecture with clear separation between the desktop application and RimWorld-specific integrations.

Suggested structure:

RimPulse/
├── src/
│   ├── RimPulse.App/
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Controls/
│   │   └── Assets/
│   │
│   ├── RimPulse.Core/
│   │   ├── Projects/
│   │   ├── Monitoring/
│   │   ├── Build/
│   │   ├── Validation/
│   │   ├── Diagnostics/
│   │   ├── Runtime/
│   │   ├── Configuration/
│   │   └── IPC/
│   │
│   ├── RimPulse.Backends/
│   │   ├── RimWorld16/
│   │   └── RimWorld17/
│   │
│   └── RimPulse.Companion/
│       ├── Shared/
│       ├── RimWorld16/
│       └── RimWorld17/
│
├── tests/
│   ├── RimPulse.Core.Tests/
│   ├── RimPulse.Build.Tests/
│   └── RimPulse.Integration.Tests/
│
├── packaging/
│   ├── linux/
│   └── windows/
│
├── docs/
│
├── DEVELOPMENT.md
└── README.md

This is a suggested structure, not an instruction to force an unsuitable architecture onto an existing repository.

Architectural principles

* Keep game-version-specific code isolated.
* Avoid coupling the desktop UI directly to RimWorld internals.
* Use a shared runtime abstraction.
* Use companion mods to communicate with the actual game.
* Keep IPC versioned and documented.
* Handle runtime failures without crashing the desktop application.
* Keep all expensive operations asynchronous.

⸻

6. RimWorld Version Compatibility

RimWorld 1.6 and 1.7 must have separate compatibility implementations where their runtime or API differences require them.

Do not assume that the two versions have identical runtime architectures.

Investigate the actual installed game assemblies, runtime, target frameworks, loading mechanisms, and available APIs before implementing integration.

RimWorld 1.6

Investigate:

* Actual Mono runtime behavior.
* Assembly loading and dependency resolution.
* Whether an isolated development assembly-loading approach is possible.
* Whether unloading or replacing a development assembly is technically safe.
* How to manage Harmony patches, static references, and game objects.

Do not assume that an assembly can be unloaded simply because another assembly with the same name can be loaded.

If safe assembly replacement is not possible, provide a controlled runtime restart.

RimWorld 1.7

Investigate:

* Actual .NET runtime and assembly-loading architecture.
* Whether collectible AssemblyLoadContext can be used.
* How to isolate development mod assemblies while sharing game assemblies.
* How dependencies are resolved.
* How to unload old assemblies.
* How to release static references, event subscriptions, threads, Harmony patches, and other references preventing unloading.

Do not assume collectible AssemblyLoadContext automatically makes RimWorld mod hot reload possible.

Verify the actual runtime implementation before selecting an approach.

Compatibility rule

Do not force a shared implementation onto both versions when their runtime behavior differs.

Implement version-specific backends behind a common interface.

⸻

7. Persistent Runtime and Hot Reload

This is one of the most important parts of RimPulse.

The application should maintain a persistent RimWorld process whenever possible.

The objective is to avoid repeatedly reloading:

* RimWorld core assemblies.
* Vanilla dependencies.
* Unchanged mods.
* Game initialization systems.
* The testing map.

When a development mod changes, the preferred behavior is to update only that development mod and the components that depend on it.

Reload architecture

Investigate and implement different reload strategies depending on what changed.

A. Asset reload

For supported textures, images, and other assets:

* Detect the modified asset.
* Reload only the affected resource.
* Release outdated resource references where necessary.
* Update the relevant game presentation.

B. Definition reload

For XML definitions:

* Detect changed XML files.
* Parse and validate them.
* Identify affected definitions.
* Determine whether the game safely supports updating those definitions at runtime.
* Update only what can be safely updated.

Do not blindly reload all definitions.

Some definitions may be referenced by existing game objects and cannot safely be replaced.

C. Assembly reload

For C# changes:

* Detect source changes.
* Compile the development assembly.
* Validate references.
* Identify affected assemblies and dependencies.
* Attempt safe replacement through the appropriate version-specific mechanism.
* Release old references and state when possible.
* Notify the game when the new assembly is ready.

D. Full runtime restart

Use a full restart when:

* Assembly unloading is unsafe.
* Existing objects cannot be migrated.
* Harmony patches cannot safely be replaced.
* Static state cannot be released.
* Core initialization must be repeated.
* The change fundamentally requires a fresh game process.

Always explain why a restart is required.

Reload profiles

Expose these categories in the interface:

* Asset reload.
* Definition reload.
* Assembly reload.
* Full runtime restart.

Indicate whether a feature is stable, experimental, or unsupported for each game version.

Critical technical consideration

Existing game objects may hold references to old mod assemblies.

For example:

* ThingComp instances.
* Components attached to pawns or buildings.
* Static fields.
* Harmony patches.
* Event subscriptions.
* Delegates.
* Background threads.
* Cached definition references.

Do not attempt unsafe assembly unloading while these references remain active.

If object migration is necessary, investigate whether it is practical. Do not compromise runtime stability to avoid a restart.

⸻

8. Mod Project Management

Users should be able to add one or multiple mod development projects.

Mod detection

Detect valid RimWorld mod structures.

Read and display:

* About/About.xml
* Package ID.
* Mod name.
* Author.
* Description.
* Supported versions.
* Dependencies.
* Load order information, where available.

Handle incomplete or malformed mod structures gracefully.

Allow a user to select a directory before it is a fully valid mod, so they can develop a new project from scratch.

Project features

* Add and remove development projects.
* Select the active development mod.
* Monitor multiple project directories.
* Display dependencies.
* Detect missing dependencies.
* Detect incompatible game versions.
* Display project paths.
* Configure build settings.
* Configure target RimWorld versions.
* Remember project selections between sessions.

Do not modify the user’s original mod directory unless explicitly required by a user action.

⸻

9. File Monitoring and Change Classification

Implement continuous monitoring of selected mod directories.

Use debouncing and batching to avoid repeated builds while files are still being written.

Ignore irrelevant files, temporary files, generated output, and build artifacts.

Classify changes into categories such as:

File type	Expected action
C# source	Recompile development assembly
XML definitions	Parse, validate, assess reload safety
Textures	Asset reload if supported
UI textures	Asset reload if supported
Audio assets	Reload if supported
About.xml	Refresh metadata and validate
Languages	Refresh supported localization resources
Other files	Classify and report

Do not rebuild C# assemblies when only an unrelated texture has changed.

Do not reload the game for files that do not affect the runtime.

Show the developer:

* Modified file.
* Change category.
* Detection time.
* Validation result.
* Build result.
* Reload result.
* Whether a restart is necessary.

⸻

10. C# Build System

RimPulse must support the C# build workflow commonly used by RimWorld mod developers.

Requirements

* Detect existing .csproj files.
* Detect source files even when no project file exists.
* Support configurable build settings.
* Resolve references to the correct RimWorld version.
* Resolve required Unity and game assemblies.
* Resolve mod dependency assemblies where available.
* Build the development assembly.
* Capture compiler output.
* Display diagnostics with file paths and line numbers.
* Avoid unnecessary full rebuilds where incremental compilation is practical.

Use Roslyn where appropriate for compilation and diagnostics.

Do not assume that every mod follows the same project structure.

Assembly inspection

Where practical, inspect assemblies to identify:

* Assembly name.
* References.
* Dependencies.
* Target framework.
* Potential version conflicts.
* Missing references.

Provide useful diagnostics rather than generic build failure messages.

Never silently overwrite the original mod DLL without an explicit and documented reason.

⸻

11. XML and Mod Validation

XML validation should be a first-class feature.

Implement:

* XML syntax validation.
* Malformed XML detection.
* Missing required fields.
* Basic About.xml validation.
* Dependency validation.
* Version compatibility warnings.
* Definition structure diagnostics where practical.
* Duplicate or conflicting identifiers where detectable.

Where possible, use actual RimWorld definitions and APIs to validate definitions rather than inventing a separate definition database.

Clearly distinguish between:

* XML syntax errors.
* Structural warnings.
* Missing references.
* Runtime loading errors.
* Potential compatibility problems.

Do not claim that XML is fully valid for RimWorld merely because it is well-formed XML.

⸻

12. Companion Mod and Runtime Communication

Create a small companion mod for each supported RimWorld version, sharing common functionality where possible.

The companion is responsible for communication between RimPulse and the actual RimWorld process.

Responsibilities

* Establish IPC connection.
* Report runtime status.
* Report active game version.
* Report loaded development mod.
* Receive authorized development commands.
* Trigger supported reload operations.
* Report reload success or failure.
* Expose testing environment controls.
* Expose available developer tools.
* Report relevant runtime diagnostics.

Use a documented, versioned JSON message protocol.

The companion must not become a large, unrelated mod framework.

Keep its scope focused on RimPulse integration.

Communication failures

Handle:

* Lost connections.
* Game process crashes.
* Companion mod loading failures.
* Invalid messages.
* Timeouts.
* Version mismatches.

The desktop application must remain usable if the game process exits unexpectedly.

⸻

13. RimPulse Desktop Interface

The application must look and feel like a polished developer tool.

Avoid a generic admin dashboard appearance.

Use a modern, cohesive dark interface with clear visual hierarchy, good typography, carefully designed spacing, and meaningful status indicators.

The UI should be responsive and must not freeze during compilation, file monitoring, or runtime operations.

Dashboard

Display:

* Selected RimWorld installation.
* Detected game version.
* Active development project.
* Runtime status.
* Companion connection status.
* Last build result.
* Last reload result.
* Recent development activity.

Projects

Display:

* Mod name.
* Package ID.
* Project directory.
* Target RimWorld versions.
* Dependencies.
* Build status.
* Monitoring status.

Runtime

Display:

* Running/stopped state.
* Selected game version.
* Loaded mods.
* Active development mod.
* Companion connection.
* Current testing map status.
* Runtime controls.
* Restart requirements.

Live Changes

Display:

* Modified files.
* Change classification.
* Validation progress.
* Build progress.
* Reload progress.
* Reload history.
* Restart requirements.

Diagnostics

Provide:

* XML errors.
* C# compiler errors.
* Missing references.
* Runtime errors.
* Harmony-related errors where detectable.
* Companion communication errors.
* Clickable file paths and line numbers where possible.

Settings

Provide configuration for:

* Game installations.
* Project directories.
* Build configuration.
* Runtime behavior.
* Testing environment.
* Reload strategy.
* Logging.
* UI preferences.

⸻

14. Minimal and Configurable Testing Environment

This is a core feature of RimPulse.

RimPulse must provide a dedicated testing environment that uses the actual RimWorld engine but avoids unnecessary normal gameplay activity.

The objective is to let mod developers test their changes quickly without having to establish and maintain a normal colony.

The environment should be configurable rather than permanently restricted.

A. Default minimal sandbox

The default preset should generate:

World map

* The smallest practical world map supported by the selected RimWorld version.
* No NPC settlements.
* No enemy bases.
* No faction outposts.
* No other colonies.
* No unnecessary world objects.
* No unnecessary world activity.
* No random world encounters.
* No random caravan generation.

Testing map

* Approximately 50×50 tiles initially.
* Flat and simple terrain.
* A consistent grey floor appearance across the map where technically possible.
* No unnecessary mountains.
* No caves.
* No water.
* No structures.
* No settlements.
* No decorative terrain variation.
* No unnecessary environmental clutter.

The purpose is to provide a clean, neutral development environment.

Do not compromise required game systems simply to achieve a completely empty map.

If the engine requires certain world, map, faction, or game components, retain them.

B. Disable unnecessary normal gameplay

In the default minimal preset, disable or suppress unnecessary gameplay activity wherever safely possible:

* Random animal spawning.
* Wild plant generation where not needed.
* Visitors.
* Raids.
* Vanilla storyteller events.
* Random incidents.
* Quests.
* Weather progression.
* Seasonal progression.
* Unnecessary faction activity.
* Random pawn generation.
* Normal colony progression.
* Unnecessary world events.

Investigate how these systems actually work in RimWorld 1.6 and 1.7.

Do not assume disabling the storyteller automatically disables all random activity.

Do not remove engine systems indiscriminately. Preserve systems required by the game and the mod being tested.

C. No automatic colony requirement

RimPulse must not force the developer to establish a normal colony.

Where the selected game version requires a player faction, map, colony object, or other game structure for initialization, investigate the minimum valid configuration.

The default environment should remain as close to an isolated testing sandbox as possible.

D. Configurable world generation

Users must be able to enable additional world features when their mods require them.

For example:

* A faction mod may need multiple factions.
* A settlement mod may need settlements.
* A world-generation mod may need a more representative world.
* An animal mod may need animal populations.
* A caravan mod may need caravan functionality.

Provide individual configuration controls rather than forcing users to switch to a completely different environment.

At minimum, provide these presets:

Preset	Configuration
Minimal Sandbox	Tiny world, no settlements, no random activity, isolated testing map
Mod Testing	Minimal world, native developer tools, manual spawning enabled
Extended Testing	Configurable factions, settlements, incidents, animals, and world activity

Users must also be able to customize individual settings.

E. World map controls

Allow users to configure:

* World map size.
* Settlement generation.
* Faction generation.
* World activity.
* Random encounters.
* Caravan activity.
* Other relevant world-generation features.

The default should be the smallest practical world with no other settlements or colonies.

When a feature is disabled, prevent its generation or underlying activity wherever technically possible. Do not merely hide visual icons.

If a particular feature cannot be safely disabled without breaking required game functionality, explain the limitation.

World and activity controls must distinguish **generation** from **runtime activity**. A control that prevents a faction or settlement from being generated is not equivalent to suppressing activity from one that already exists. The UI must state which effect a setting has and whether changing it requires a new world. Never imply that hiding an icon disables the underlying behavior.

Faction controls must be expressed in terms users can understand and test: faction presence/generation, settlement generation, and faction-specific behavior are separate options. “Disable tribes” must not silently remove every faction or the player faction; it should mean excluding tribal factions from generated worlds, with a separate runtime policy only if the game API supports it safely. Likewise, “disable caravans” must distinguish preventing random caravan incidents/visitors from disabling player-created caravan mechanics. Do not globally block caravan code or mod-initiated behavior as a side effect of a storyteller setting.

For each world/activity option, record the supported game versions, the mechanism used, and known bypasses. If a version has no verified safe mechanism, show the option as unavailable rather than exposing a nonfunctional toggle. Verify settings against both vanilla and a test mod that exercises the relevant game system before marking them supported.

F. Testing map persistence

The testing map should remain persistent between supported development reloads.

Do not regenerate the world or map every time the developer saves a file.

Only regenerate the map when:

* The developer explicitly requests it.
* The map becomes invalid.
* A particular change requires a fresh world or map.
* The selected testing configuration changes in a way that requires regeneration.

Provide controls for resetting the map and clearing spawned entities.

G. Developer-controlled entities

The developer must be able to manually create and spawn entities.

Support, where available:

* Pawns.
* Specific PawnKinds.
* Animals.
* Buildings.
* Items.
* Weapons.
* Apparel.
* Other ThingDefs.
* Other supported game objects.

This must use the actual RimWorld definition system.

Do not create a fake list of spawnable entities.

H. Native RimWorld Developer Mode integration

Retain and expose RimWorld’s existing Developer Mode and Debug Actions wherever possible.

Do not unnecessarily reinvent functionality that already exists in the game.

Investigate the native developer tools available in RimWorld 1.6 and 1.7.

Expose the useful functionality through RimPulse or provide a convenient way to access the original in-game tools.

Important capabilities include:

* Spawn Thing.
* Spawn Pawn.
* Spawn Animal.
* Spawn specific PawnKinds.
* Spawn buildings.
* Spawn items.
* Trigger incidents.
* Inspect definitions.
* Inspect game state.
* Test interactions.
* Access relevant debugging actions.

For example, someone developing an animal retexture mod should be able to spawn that animal immediately.

Someone developing animal behavior changes should be able to spawn the relevant animal and observe its actual behavior.

Someone developing a faction mod should be able to enable factions and test the relevant functionality.

The testing environment must not prevent users from testing functionality simply because the default preset is minimal.

I. Searchable definition browser

Provide a searchable browser of actual game definitions.

Where practical, expose:

* ThingDefs.
* PawnKindDefs.
* Animal definitions.
* Building definitions.
* Item definitions.
* Other relevant definitions.

Allow users to search by name or definition identifier and spawn supported entities directly.

Use the actual definitions loaded by the selected game runtime.

Do not create a hardcoded list that becomes outdated when mods add new definitions.

J. Testing environment controls

Provide controls for:

* Start testing runtime.
* Stop testing runtime.
* Pause.
* Resume.
* Advance one tick.
* Advance a configurable number of ticks.
* Change game speed.
* Spawn selected entity.
* Clear spawned entities.
* Reset testing map.
* Regenerate world.
* Reload development mod.
* Access native Developer Mode tools.

Where possible, allow developers to configure the number of ticks advanced by a single action.

K. Implementation strategy

Investigate the actual game initialization and world-generation APIs separately for RimWorld 1.6 and 1.7.

Do not implement the environment by simply creating an ordinary world and hiding everything unwanted.

Use actual game systems and supported APIs to produce the minimal environment.

If a feature requires a version-specific implementation, isolate it in the relevant backend or companion integration.

L. Visual presentation

The testing environment should look intentionally designed for development.

Use:

* A consistent grey or neutral floor.
* Minimal visual clutter.
* A clean map.
* No unnecessary decorations.
* Clear spawn and testing controls.

Do not sacrifice actual game rendering or engine behavior for visual simplicity.

⸻

15. Logging and Diagnostics

Maintain useful development logs.

Record:

* Application startup.
* Game installation detection.
* Game version detection.
* Project loading.
* File changes.
* Compilation.
* XML validation.
* Reload attempts.
* Reload results.
* Runtime errors.
* IPC errors.
* Companion status.
* Testing environment initialization.
* World and map generation.
* Spawn actions and relevant failures.

Provide useful diagnostic information without flooding the user with irrelevant output.

Allow logs to be exported for troubleshooting.

Use bounded diagnostic history to avoid unbounded memory or disk consumption.

⸻

16. Linux-First Development and Packaging

Linux is the primary development and testing platform.

The initial implementation must be practical to build and run on Linux Mint 22.x x64 with Cinnamon/X11.

Avoid Snap packaging.

Linux requirements

* Buildable using documented .NET commands.
* Self-contained publishing where practical.
* AppImage preferred.
* .deb package as an additional option.
* Desktop entry.
* Application icon.
* Correct XDG configuration paths.
* No unnecessary root privileges.
* Correct process launching and termination.
* Correct Unix domain socket or other IPC handling.
* Correct filesystem path handling.
* Reliable FileSystemWatcher behavior.

The application should be testable without requiring the developer to install an extensive collection of additional dependencies.

Provide exact build, run, test, and packaging commands.

Windows requirements

Design the architecture for Windows 10/11 x64.

Later packaging should support:

* Self-contained builds.
* Portable ZIP.
* Installer.

Do not let Windows-specific assumptions compromise Linux support.

⸻

17. Performance and Resource Usage

The application should be lightweight.

Avoid:

* Continuous full-directory rescans.
* Unnecessary rebuilds.
* Repeated game restarts.
* Blocking the UI thread.
* Excessive logging.
* Unbounded memory usage.
* Unnecessary background processes.
* Unnecessary game initialization.

Use incremental processing where practical.

The goal is to make the development cycle substantially faster than manually restarting RimWorld.

⸻

18. Testing Strategy

Create automated tests for functionality that does not require a running RimWorld installation.

At minimum, test:

* Mod directory detection.
* About.xml parsing.
* Package ID extraction.
* Game version detection.
* Project configuration.
* File classification.
* File monitoring and debouncing.
* Build orchestration.
* XML parsing and validation.
* Dependency resolution.
* IPC message serialization.
* Configuration persistence.
* Platform-specific path handling.

For game-dependent functionality, distinguish between:

* Verified working.
* Tested and failed.
* Not yet tested.
* Requires manual verification.
* Unsupported by the selected game version.

Do not fabricate test results.

Where a game installation is available, test against the actual installed game.

⸻

19. Development Roadmap

Implement the project incrementally.

Do not attempt to build every feature in one enormous implementation.

Milestone 1 — Foundation

* Inspect repository.
* Establish or improve application architecture.
* Create Avalonia desktop application.
* Implement configuration persistence.
* Implement Linux-first build.
* Detect RimWorld installations.
* Detect RimWorld 1.6 and 1.7.
* Provide basic installation selection.

Milestone 2 — Project Management

* Add mod project directories.
* Read About.xml.
* Detect mod structure.
* Support multiple projects.
* Display project metadata.
* Detect dependencies.
* Implement file monitoring.

Milestone 3 — Build and Validation

* Detect C# projects.
* Resolve game references.
* Implement compilation.
* Add Roslyn diagnostics.
* Implement XML validation.
* Inspect assemblies.
* Display useful errors.

Milestone 4 — Runtime Integration

* Create version-specific companion mods.
* Implement IPC.
* Launch and manage the actual RimWorld process.
* Detect runtime status.
* Report loaded mods.
* Establish persistent testing runtime.

Milestone 5 — Testing Environment

* Implement minimal world generation.
* Implement isolated testing map.
* Implement configurable world generation.
* Implement world and map persistence.
* Integrate native Developer Mode.
* Implement entity spawning.
* Implement tick controls.
* Implement reset and cleanup operations.

Milestone 6 — Live Reload

* Implement asset reload where supported.
* Investigate and implement safe definition reload.
* Investigate 1.7 assembly isolation and unloading.
* Investigate 1.6 assembly reload possibilities.
* Implement reload diagnostics.
* Implement controlled restart fallback.

Milestone 7 — Packaging and Polish

* Complete Linux packaging.
* Provide AppImage.
* Consider .deb.
* Verify installation and execution on Linux Mint.
* Prepare Windows packaging.
* Improve UI polish.
* Complete documentation.

⸻

20. Documentation

Create and maintain:

README.md

Explain:

* What RimPulse is.
* What it does.
* Supported RimWorld versions.
* Current feature status.
* Installation.
* Screenshots when available.

DEVELOPMENT.md

Provide exact commands for:

* Installing development prerequisites.
* Restoring dependencies.
* Building.
* Running.
* Testing.
* Publishing.
* Packaging.
* Running the companion mod build.
* Troubleshooting.

Architecture documentation

Document:

* Shared application architecture.
* Version-specific backends.
* Companion mod responsibilities.
* IPC protocol.
* Runtime management.
* Reload architecture.
* Testing environment implementation.
* Known technical limitations.

Document important technical discoveries, especially differences between RimWorld 1.6 and 1.7.

⸻

21. Implementation Rules for the Coding Agent

These requirements are mandatory.

1. Inspect the repository before modifying it.
2. Verify actual RimWorld runtime behavior instead of relying on assumptions.
3. Use actual game assemblies and APIs.
4. Do not create a fake game engine or simulator.
5. Do not invent capabilities that the runtime does not support.
6. Keep version-specific implementations separate.
7. Prioritize Linux functionality.
8. Keep the project buildable throughout development.
9. Do not introduce unnecessary dependencies.
10. Do not silently discard existing project functionality.
11. Do not implement fake or placeholder functionality.
12. Do not report successful testing without actually testing.
13. Document technical blockers and workarounds.
14. Prefer a safe restart over an unsafe hot reload.
15. Preserve the testing map between reloads whenever possible.
16. Keep the default environment minimal but allow users to enable additional game systems.
17. Reuse RimWorld’s native Developer Mode and Debug Actions wherever practical.
18. Provide useful progress reports and implementation summaries.
19. Commit changes in coherent, reviewable increments when repository permissions allow.
20. Do not stop after writing an architecture proposal. Begin implementing the first working vertical slice.

⸻

22. First Response Expected from the Agent

After inspecting the repository, provide:

1. A summary of the existing repository and its architecture.
2. Existing code that can be reused.
3. Current build status.
4. Verified differences between RimWorld 1.6 and 1.7.
5. Actual runtime and assembly-loading constraints.
6. A proposed implementation architecture.
7. Technical risks and likely limitations.
8. A prioritized implementation roadmap.
9. The first vertical slice to implement.

Then begin implementation.

Do not spend the entire task producing documentation or a theoretical design.

⸻

23. Final Product Objective

The finished application should allow a RimWorld mod developer to:

* Select RimWorld 1.6 or 1.7.
* Select their mod directory.
* Start a dedicated minimal testing environment.
* Edit their mod in VS Code, Rider, or another editor.
* Save a file.
* Have RimPulse detect and classify the change.
* Automatically validate and compile when required.
* Reload the affected components whenever safe.
* Preserve the running game and unchanged dependencies.
* Use native RimWorld developer tools to spawn animals, pawns, buildings, items, and other entities.
* Enable factions, settlements, incidents, and other world features when their mod requires them.
* Advance ticks and control the testing environment.
* Avoid restarting the entire game unless technically necessary.

The central philosophy is:

RimPulse should make RimWorld mod development feel like a modern live-development workflow, while respecting the actual technical limitations of the RimWorld engine.

Build a reliable development tool, not a collection of superficial UI features.