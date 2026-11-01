using RimPulse.Core.Game;

namespace RimPulse.Core.Installations;

/// <summary>
/// The result of inspecting a directory for a RimWorld installation. An instance is produced even when
/// the directory turns out to be unusable, so the UI can explain precisely what is wrong instead of
/// silently omitting the installation.
/// </summary>
public sealed class RimWorldInstallation
{
    public required string RootPath { get; init; }

    public required string DisplayName { get; init; }

    public required InstallationLayout Layout { get; init; }

    public required GameVersion Version { get; init; }

    public required GameVersionSource VersionSource { get; init; }

    public required InstallationSource Source { get; init; }

    public required bool RootExists { get; init; }

    /// <summary>Text of <c>Version.txt</c> as found on disk, for diagnostics.</summary>
    public string? VersionFileContents { get; init; }

    /// <summary>Directory that contains <c>Assembly-CSharp.dll</c> for this installation.</summary>
    public string? ManagedAssemblyDirectory { get; init; }

    public IReadOnlyList<string> PresentAssemblies { get; init; } = [];

    public IReadOnlyList<string> MissingAssemblies { get; init; } = [];

    /// <summary>Human readable explanation of anything that prevents using this installation.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>True when the layout was recognised and every required assembly exists.</summary>
    public bool IsUsable => RootExists && Layout != InstallationLayout.Unknown && MissingAssemblies.Count == 0;

    public bool IsSupportedVersion => Version.IsSupported;

    public bool HasProblems => Problems.Count > 0;

    public string VersionDisplay => VersionSource == GameVersionSource.VersionFile ? Version.ToString() : "unconfirmed";

    public string LayoutDisplay => Layout.ToDisplayName();

    public override string ToString() => $"{DisplayName} ({Layout}, version {VersionDisplay}) at '{RootPath}'";
}
