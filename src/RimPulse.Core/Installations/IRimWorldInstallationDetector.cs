namespace RimPulse.Core.Installations;

/// <summary>Discovers RimWorld installations on this machine.</summary>
public interface IRimWorldInstallationDetector
{
    /// <summary>
    /// Scans the well-known locations for the current platform plus any extra roots the user configured.
    /// Never throws for a path that merely is not a RimWorld installation.
    /// </summary>
    Task<IReadOnlyList<RimWorldInstallation>> DetectAsync(
        IEnumerable<string>? additionalRootPaths = null,
        CancellationToken cancellationToken = default,
        bool scanWellKnownLocations = true);
}
