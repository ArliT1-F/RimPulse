using RimPulse.Core.Platform;

namespace RimPulse.Core.Installations;

/// <summary>
/// Inputs for installation discovery. Everything is explicit so detection is testable without touching
/// the real machine — the sandbox and CI runs use a temporary "home" directory.
/// </summary>
public sealed class InstallationDetectorOptions
{
    public HostPlatform Platform { get; init; } = HostPlatformExtensions.Detect();

    public string HomeDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Value of <c>RIMWORLD_DIR</c>, which mod build scripts already use as an override.</summary>
    public string? EnvironmentRimWorldDirectory { get; init; }

    /// <summary>Folder names under the home directory that GOG Galaxy uses for installs.</summary>
    public IReadOnlyList<string> GogParentFolderNames { get; init; } = ["GOG Games", "Games", "gog-games"];

    public bool ScanSteamLibraries { get; init; } = true;

    public bool ScanGogDirectories { get; init; } = true;
}
