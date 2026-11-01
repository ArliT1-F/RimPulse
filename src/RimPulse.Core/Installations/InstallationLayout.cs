namespace RimPulse.Core.Installations;

/// <summary>
/// How a RimWorld installation lays out its assemblies. The two supported game versions do not share
/// a runtime, so the layout is a first-class concept (see docs/GAME-VERSIONS.md).
/// </summary>
public enum InstallationLayout
{
    /// <summary>Nothing recognisable; the path is probably not a RimWorld installation.</summary>
    Unknown = 0,

    /// <summary>
    /// RimWorld 1.6 and earlier: a Unity 2022.3 player running the bundled Mono runtime, with managed
    /// assemblies inside <c>&lt;root&gt;/RimWorldLinux_Data/Managed</c> (or the Windows/macOS equivalent)
    /// and mods compiled against .NET Framework 4.7.2.
    /// </summary>
    UnityMono = 1,

    /// <summary>
    /// RimWorld 1.7 and later: the Waylith engine on .NET 10, with <c>Assembly-CSharp.dll</c>,
    /// <c>Waylith.dll</c> and the <c>UnityStubs.dll</c> compatibility shim in the installation root.
    /// Mods are compiled for <c>net10.0</c> against those assemblies.
    /// </summary>
    Waylith = 2,
}

public static class InstallationLayoutExtensions
{
    public static string ToDisplayName(this InstallationLayout layout) => layout switch
    {
        InstallationLayout.UnityMono => "Unity 2022.3 + Mono (.NET Framework 4.7.2)",
        InstallationLayout.Waylith => "Waylith (.NET 10)",
        _ => "Unknown layout",
    };
}
