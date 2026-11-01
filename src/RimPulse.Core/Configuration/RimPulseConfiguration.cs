namespace RimPulse.Core.Configuration;

/// <summary>
/// Everything RimPulse persists between sessions. Kept intentionally small and forward-compatible:
/// unknown JSON properties are ignored on load and <see cref="SchemaVersion"/> records the shape.
/// </summary>
public sealed class RimPulseConfiguration
{
    public const int CurrentSchemaVersion = 1;

    private const int MaxCustomInstallationPaths = 50;

    /// <summary>Shape version of this file. Bumped when a migration is required.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Installation root the user last selected in the UI, if any.</summary>
    public string? SelectedInstallationPath { get; set; }

    /// <summary>Installation roots the user added by hand (e.g. a GOG copy outside Steam).</summary>
    public List<string> CustomInstallationPaths { get; set; } = [];

    /// <summary>Whether RimPulse scans the well-known locations on startup.</summary>
    public bool AutoDetectOnStartup { get; set; } = true;

    /// <summary>Trims, de-duplicates and caps the user-managed lists so a bad file cannot hurt us.</summary>
    public void Normalize()
    {
        SelectedInstallationPath = string.IsNullOrWhiteSpace(SelectedInstallationPath)
            ? null
            : SelectedInstallationPath.Trim();

        var cleaned = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in CustomInstallationPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var trimmed = path.Trim();
            if (!seen.Add(trimmed))
            {
                continue;
            }

            cleaned.Add(trimmed);
            if (cleaned.Count >= MaxCustomInstallationPaths)
            {
                break;
            }
        }

        CustomInstallationPaths = cleaned;
    }
}
