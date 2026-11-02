using System.Text.Json.Serialization;

namespace RimPulse.Core.Configuration;

/// <summary>A user-owned, reusable development profile. Never points at the ordinary RimWorld save directory.</summary>
public sealed class DevelopmentProfileSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Development";
    public string InstallationPath { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string? ProjectPath { get; set; }
    /// <summary>Ordered package IDs, including Core and the companion. Unselected DLC is deliberately absent.</summary>
    public List<string> ActiveMods { get; set; } = ["ludeon.rimworld", "rimpulse.companion"];
    public DevelopmentGameSettings Game { get; set; } = new();

    public DevelopmentProfileSettings Clone() => new()
    {
        Id = Id, Name = Name, InstallationPath = InstallationPath, GameVersion = GameVersion,
        ProjectPath = ProjectPath, ActiveMods = [.. ActiveMods], Game = Game with { },
    };

    public void Normalize()
    {
        Id = Id?.Trim().ToLowerInvariant() ?? string.Empty;
        if (Id.Length != 32 || !Id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            Id = Guid.NewGuid().ToString("N");
        Name = string.IsNullOrWhiteSpace(Name) ? "Development" : Name.Trim();
        if (Name.Length > 80) Name = Name[..80];
        InstallationPath = InstallationPath?.Trim() ?? string.Empty;
        GameVersion = GameVersion?.Trim() ?? string.Empty;
        ProjectPath = string.IsNullOrWhiteSpace(ProjectPath) ? null : ProjectPath.Trim();
        ActiveMods = (ActiveMods ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 256)
            .Select(id => id.Trim().ToLowerInvariant()).Distinct().Take(2048).ToList();
        foreach (var required in new[] { "ludeon.rimworld", "rimpulse.companion" })
            if (!ActiveMods.Contains(required)) ActiveMods.Add(required);
        Game = Game.Normalize();
    }
}

/// <summary>Preferences written only to this profile's Config/Prefs.xml, and applied at the next launch.</summary>
public sealed record DevelopmentGameSettings
{
    public bool DeveloperMode { get; init; } = true;
    public bool PauseOnLoad { get; init; } = true;
    public bool RunInBackground { get; init; } = true;
    public bool VerboseLogging { get; init; }
    public bool ResetModsOnCrash { get; init; }
    public bool OpenDeveloperPalette { get; init; }
    /// <summary>The game's native 1.6 quick test: normal world generation and a 250×250 map, not a sandbox.</summary>
    public bool NativeQuickStart { get; init; }
    public bool Fullscreen { get; init; }
    public int ScreenWidth { get; init; } = 1280;
    public int ScreenHeight { get; init; } = 720;
    public double UIScale { get; init; } = 1;

    [JsonIgnore] public bool HasValidDisplay => ScreenWidth is >= 640 and <= 7680 && ScreenHeight is >= 480 and <= 4320 && UIScale is >= 0.5 and <= 3;

    public DevelopmentGameSettings Normalize()
    {
        var result = this with
        {
            ScreenWidth = Math.Clamp(ScreenWidth, 640, 7680),
            ScreenHeight = Math.Clamp(ScreenHeight, 480, 4320),
            UIScale = Math.Clamp(double.IsFinite(UIScale) ? UIScale : 1, 0.5, 3),
        };
        return result with { OpenDeveloperPalette = result.DeveloperMode && result.OpenDeveloperPalette };
    }
}
