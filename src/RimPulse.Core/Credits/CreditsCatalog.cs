namespace RimPulse.Core.Credits;

/// <summary>
/// The projects RimPulse is built in conversation with. Every creator and repository whose work informs
/// RimPulse must appear here, because this catalog is what the application's Credits panel renders: if a
/// project is not in this list, it is not featured in the app.
/// </summary>
/// <remarks>
/// Ordering is by relevance to RimPulse's milestones (reload research first), not by popularity.
/// </remarks>
public static class CreditsCatalog
{
    public const string AttributionPolicy =
        "Every project RimPulse references or depends on is listed here together with its creator and license. " +
        "RimPulse currently adapts no RimWorld tooling code; these projects have been studied as references. " +
        "Code, data or assets may be reused when it is better suited than a clean implementation and the actual " +
        "license permits the intended distribution. Before inclusion, review the source license and notices, record " +
        "the repository revision and adapted files, preserve required notices, satisfy license obligations, and " +
        "assess compatibility with RimPulse's distribution. Update this entry to identify adaptations. Copyleft " +
        "obligations must be met; do not copy from unlicensed sources without an explicit license grant.";

    /// <summary>Every referenced project and organisation, in milestone order.</summary>
    public static IReadOnlyList<CreditEntry> Entries { get; } =
    [
        new CreditEntry(
            ProjectName: "Reloader",
            Creator: "Andreas Pardeike",
            Url: "https://github.com/pardeike/Reloader",
            Summary: "A mod helper for RimWorld that allows you to develop and patch code while the game is running.",
            Relevance: "Closest prior art for milestone 6 (assembly reload). Study how far in-place patch reloading can be pushed before RimPulse must fall back to a controlled restart.",
            License: "MIT"),

        new CreditEntry(
            ProjectName: "RimWorldHotReload",
            Creator: "xiao-e-yun",
            Url: "https://github.com/xiao-e-yun/RimWorldHotReload",
            Summary: "A simple mod for hot reloading in RimWorld.",
            Relevance: "A second, independent take on mod-level hot reload. Compare its reload granularity with Reloader before choosing RimPulse's reload strategy.",
            License: "BSD-3-Clause"),

        new CreditEntry(
            ProjectName: "RimBridgeServer",
            Creator: "Andreas Pardeike",
            Url: "https://github.com/pardeike/RimBridgeServer",
            Summary: "Runs an MCP server inside RimWorld so AI agents and external tools can remotely control and observe a running game.",
            Relevance: "Reference for authenticated local IPC and separating mod construction from runtime readiness; no RimBridgeServer code is copied.",
            License: "MIT"),

        new CreditEntry(
            ProjectName: "RimWorldDevMCP ecosystem",
            Creator: "Jeffrharr",
            Url: "https://github.com/search?q=RimWorldDevMCP&type=repositories",
            Summary: "Community search that surfaces Jeffrharr/RimWorldDevMCP: an MCP server plus CLI that drives RimWorld developer actions against a live, running game.",
            Relevance: "Reference for milestone 5: driving native dev actions (spawn, incidents, inspection) from outside the game instead of inventing a parallel API.",
            License: "MIT (related project)",
            RelatedUrl: "https://github.com/Jeffrharr/RimWorldDevMCP",
            RelatedUrlLabel: "Open Jeffrharr/RimWorldDevMCP"),

        new CreditEntry(
            ProjectName: "RimTest Redux ecosystem",
            Creator: "ilyvion",
            Url: "https://github.com/search?q=RimTest+Redux&type=repositories",
            Summary: "Community search that surfaces ilyvion/rimtest-redux: a lightweight automated testing framework for other RimWorld mods.",
            Relevance: "Reference for in-game automated checks, which RimPulse needs to confirm a reload actually succeeded rather than reporting success blindly.",
            License: "Apache-2.0 OR MIT (dual-licensed; verify selected license terms)",
            RelatedUrl: "https://github.com/ilyvion/rimtest-redux",
            RelatedUrlLabel: "Open ilyvion/rimtest-redux"),

        new CreditEntry(
            ProjectName: "Rimworld-Pickle",
            Creator: "RimWorks",
            Url: "https://github.com/RimWorks/Rimworld-Pickle",
            Summary: "RimWorld mod testing framework: run Gherkin scenarios against a live game session, click the real UI, wait on real ticks and assert on the real colony.",
            Relevance: "Reference for the game-owned running-mod list and startup lifecycle, and for verifying future testing or reload actions.",
            License: "MIT"),

        new CreditEntry(
            ProjectName: "pickle-template",
            Creator: "RimWorks",
            Url: "https://github.com/RimWorks/pickle-template",
            Summary: "A minimal RimWorld mod with a Pickle test suite, build and release pipeline already wired up.",
            Relevance: "Shows the shape of a well-engineered mod repository; useful when RimPulse inspects and validates project structure in milestones 2 and 3.",
            License: "MIT"),

        new CreditEntry(
            ProjectName: "rimworld-logging-framework",
            Creator: "RimWorks",
            Url: "https://github.com/RimWorks/rimworld-logging-framework",
            Summary: "RimWorld log viewer and bug report sharing with structured logging (channels, levels, a filter DSL) that replaces Verse.Log for 1.6+ mods.",
            Relevance: "Reference for the companion mod's structured diagnostics (milestone 4) and for log filtering in RimPulse's own diagnostics.",
            License: "MIT"),

        new CreditEntry(
            ProjectName: "steam-game-image-action",
            Creator: "RimWorks",
            Url: "https://github.com/RimWorks/steam-game-image-action",
            Summary: "Builds a private OCI image of a Steam game (RimWorld by default) for CI, so builds can reference the real assemblies and the game can be launched headless.",
            Relevance: "Directly addresses RimPulse's largest verification gap: automated testing against a real installation in CI (milestones 3-6).",
            License: "MIT"),

        new CreditEntry(
            ProjectName: "semantic-release-steam",
            Creator: "RimWorks",
            Url: "https://github.com/RimWorks/semantic-release-steam",
            Summary: "A semantic-release plugin that publishes a Steam Workshop item through SteamCMD.",
            Relevance: "Reference for milestone 7 release automation, if RimPulse grows a packaging/publish helper.",
            License: "MIT"),

        new CreditEntry(
            ProjectName: "RimWorks (organisation)",
            Creator: "RimWorks",
            Url: "https://github.com/RimWorks",
            Summary: "Tools for treating a RimWorld mod like real software: testing, logging, telemetry and release automation.",
            Relevance: "The closest existing body of work to RimPulse's engineering-first approach; review its repositories before designing testing and release features.",
            License: "Mixed (mostly MIT)"),

        new CreditEntry(
            ProjectName: "RimSort",
            Creator: "RimSort contributors",
            Url: "https://github.com/RimSort/RimSort",
            Summary: "An open-source, community-managed RimWorld mod manager for Linux, macOS and Windows.",
            Relevance: "Reference for mod metadata parsing, About.xml/load-order handling and cross-platform packaging (milestone 2). GPL-3.0: reference only, do not copy code into RimPulse without a licensing decision.",
            License: "GPL-3.0"),

        new CreditEntry(
            ProjectName: "RimworldXsd",
            Creator: "Ragonfana",
            Url: "https://github.com/Ragonfana/RimworldXsd",
            Summary: "XSD schema generator for RimWorld XML mod development, with validation and editor autocompletion support.",
            Relevance: "Reference for milestone 3 XML validation: structural diagnostics instead of merely checking that XML is well-formed. No license declared: reference only, do not copy code.",
            License: "No license declared"),

        new CreditEntry(
            ProjectName: "RimWorld 1.6 decompilation",
            Creator: "32bitx64bit (decompilation); game code by Ludeon Studios",
            Url: "https://github.com/32bitx64bit/RW-Decompiled-1.6",
            Summary: "A decompiled view of RimWorld 1.6 intended to help mod authors inspect game APIs.",
            Relevance: "Read-only research into game startup, mod discovery and savedatafolder parsing. Game code is proprietary and was not copied.",
            License: "No license declared; proprietary game code, reference only"),

        new CreditEntry(
            ProjectName: "RimWorld auto test",
            Creator: "xtzhyhydwl",
            Url: "https://github.com/xtzhyhydwl/RimWorld_auto_test",
            Summary: "Automation tooling with a documented RimWorld 1.7 Windows profile launch and bridge investigation.",
            Relevance: "Reference for the savedatafolder option and version-specific launch observations; Linux behavior remains to be verified.",
            License: "No license declared; reference only"),

        new CreditEntry(
            ProjectName: "RimLoc",
            Creator: "0-danielviktorovich-0",
            Url: "https://github.com/0-danielviktorovich-0/RimLoc",
            Summary: "RimWorld localization tooling with research into separate save-data profiles on macOS.",
            Relevance: "Reference only for the limits of savedatafolder and mod discovery; no GPL code or data is copied.",
            License: "GPL-3.0; reference only"),

        new CreditEntry(
            ProjectName: "AI in RimWorld",
            Creator: "ryseymour",
            Url: "https://github.com/ryseymour/ai-in-rimworld",
            Summary: "RimWorld automation research including notes about Linux game startup and profile paths.",
            Relevance: "Read-only reference for Linux savedatafolder and ModsConfig conventions; no project code is copied.",
            License: "No license declared; reference only"),

        // Actual dependencies of this repository, so the list covers what RimPulse runs on as well as what it studies.
        new CreditEntry(
            ProjectName: "Avalonia",
            Creator: "Avalonia Team and contributors",
            Url: "https://github.com/AvaloniaUI/Avalonia",
            Summary: "Cross-platform .NET UI framework that renders the RimPulse desktop interface.",
            Relevance: "Shipped dependency: RimPulse.App uses Avalonia 12.1.4 (MIT), including the Fluent theme and compiled bindings.",
            License: "MIT",
            Usage: CreditUsage.Bundled),

        new CreditEntry(
            ProjectName: "Roslyn (.NET Compiler Platform)",
            Creator: "Roslyn contributors (Microsoft and the .NET community)",
            Url: "https://github.com/dotnet/roslyn",
            Summary: "The open-source C# compiler and code-analysis API that also ships with the .NET SDK.",
            Relevance: "Shipped dependency: RimPulse.Core compiles mod sources in-process with Microsoft.CodeAnalysis.CSharp 4.14.0 (MIT), so a project targeting .NET Framework 4.7.2 can be built on Linux and its diagnostics reach RimPulse structured, with file, line and column.",
            License: "MIT",
            Usage: CreditUsage.Bundled),

        new CreditEntry(
            ProjectName: "xUnit.net",
            Creator: "xUnit.net contributors",
            Url: "https://github.com/xunit/xunit",
            Summary: "Unit testing framework for .NET, used here through its Microsoft.Testing.Platform-based v3 runner.",
            Relevance: "Development dependency only: xunit.v3 4.0.2 runs the RimPulse.Core test suite and is never shipped to users.",
            License: "Apache-2.0",
            Usage: CreditUsage.DevelopmentDependency),
    ];
}
