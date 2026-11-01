using RimPulse.Core.Installations;

namespace RimPulse.App.ViewModels;

/// <summary>Presentation wrapper around one detected installation.</summary>
public sealed class InstallationViewModel
{
    public InstallationViewModel(RimWorldInstallation model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public RimWorldInstallation Model { get; }

    public string Title => Model.Version.IsKnown ? $"RimWorld {Model.Version.ShortVersion}" : "RimWorld (version unconfirmed)";

    public string PathText => Model.RootPath;

    public string Summary => Model.RootExists
        ? $"{Model.LayoutDisplay} · version {Model.VersionDisplay}"
        : "Path not accessible";

    /// <summary>Layout recognised, required assemblies present and the version is supported.</summary>
    public bool IsReady => Model.IsUsable && Model.IsSupportedVersion;

    /// <summary>Usable, but the game version is outside the supported 1.6 / 1.7 range.</summary>
    public bool IsUnsupported => Model.IsUsable && !Model.IsSupportedVersion;

    /// <summary>Not usable at all: wrong folder, missing assemblies, or an unreadable version.</summary>
    public bool IsIncomplete => !Model.IsUsable;

    public string StatusText => IsReady ? "Ready" : IsUnsupported ? "Unsupported" : "Incomplete";

    public override string ToString() => $"{Title} — {PathText}";
}
