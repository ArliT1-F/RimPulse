namespace RimPulse.Core.Tests.TestSupport;

/// <summary>
/// Builds directory trees that mimic the two real RimWorld layouts: the Unity/Mono layout used by 1.6
/// and the Waylith layout used by 1.7. These are structural stand-ins for tests only — RimPulse itself
/// always talks to a real installation.
/// </summary>
internal static class FakeInstallation
{
    /// <summary>
    /// RimWorld 1.6 style: <c>Version.txt</c> in the root and managed assemblies in
    /// <c>RimWorldLinux_Data/Managed</c>.
    /// </summary>
    public static string CreateUnity(string root, string? version = "1.6.4851 rev1248", bool includeAllAssemblies = true)
    {
        Directory.CreateDirectory(root);

        if (version is not null)
        {
            File.WriteAllText(Path.Combine(root, "Version.txt"), version);
        }

        var managed = Path.Combine(root, "RimWorldLinux_Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "Assembly-CSharp.dll"), string.Empty);

        if (includeAllAssemblies)
        {
            File.WriteAllText(Path.Combine(managed, "UnityEngine.CoreModule.dll"), string.Empty);
        }

        return root;
    }

    /// <summary>RimWorld 1.7 style: assemblies directly in the installation root.</summary>
    public static string CreateWaylith(string root, string? version = "1.7.0", bool includeAllAssemblies = true)
    {
        Directory.CreateDirectory(root);

        if (version is not null)
        {
            File.WriteAllText(Path.Combine(root, "Version.txt"), version);
        }

        File.WriteAllText(Path.Combine(root, "Assembly-CSharp.dll"), string.Empty);

        if (includeAllAssemblies)
        {
            File.WriteAllText(Path.Combine(root, "Waylith.dll"), string.Empty);
            File.WriteAllText(Path.Combine(root, "UnityStubs.dll"), string.Empty);
        }

        return root;
    }
}
