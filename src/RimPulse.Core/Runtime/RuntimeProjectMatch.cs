using RimPulse.Core.IPC;

namespace RimPulse.Core.Runtime;

/// <summary>Matches both the package ID and the reported mod directory; an ID alone can name another copy.</summary>
public static class RuntimeProjectMatch
{
    public static string Describe(RuntimeSnapshot snapshot, string? packageId, string? projectRoot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(projectRoot))
            return "No development project with a package ID was selected when the game started.";

        var candidates = snapshot.Mods.Where(mod =>
            string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) return $"Development project {packageId}: not in the startup mod snapshot.";

        var expected = Resolve(projectRoot);
        foreach (var candidate in candidates)
        {
            if (expected is not null && string.Equals(Resolve(candidate.RootDir), expected,
                    OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return $"Development project {packageId}: present in the startup mod snapshot ({candidate.RootDir}).";
        }
        return $"Package ID {packageId} is present, but its reported directory does not match the selected project: {candidates[0].RootDir}";
    }

    private static string? Resolve(string path)
    {
        try
        {
            return CanonicalDirectory.Resolve(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
