namespace RimPulse.Core.Configuration;

/// <summary>
/// Everything RimPulse persists between sessions. Kept intentionally small and forward-compatible:
/// unknown JSON properties are ignored on load and <see cref="SchemaVersion"/> records the shape.
/// </summary>
public sealed class RimPulseConfiguration
{
    public const int CurrentSchemaVersion = 2;

    private const int MaxCustomInstallationPaths = 50;

    /// <summary>Upper bound on remembered projects, applied when the file is loaded or saved.</summary>
    public const int MaxProjectPaths = 30;

    /// <summary>Shape version of this file. Bumped when a migration is required.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Installation root the user last selected in the UI, if any.</summary>
    public string? SelectedInstallationPath { get; set; }

    /// <summary>Installation roots the user added by hand (e.g. a GOG copy outside Steam).</summary>
    public List<string> CustomInstallationPaths { get; set; } = [];

    /// <summary>Mod project directories the user has opened, newest first.</summary>
    public List<string> ProjectPaths { get; set; } = [];

    /// <summary>Last selected mod project directory, if any. Always part of <see cref="ProjectPaths"/>.</summary>
    public string? SelectedProjectPath { get; set; }

    /// <summary>Whether RimPulse scans the well-known locations on startup.</summary>
    public bool AutoDetectOnStartup { get; set; } = true;

    /// <summary>Trims, de-duplicates and caps the user-managed lists so a bad file cannot hurt us.</summary>
    public void Normalize()
    {
        SelectedInstallationPath = string.IsNullOrWhiteSpace(SelectedInstallationPath)
            ? null
            : SelectedInstallationPath.Trim();

        SelectedProjectPath = string.IsNullOrWhiteSpace(SelectedProjectPath) ? null : SelectedProjectPath.Trim();

        // Schema 1 stored a single project path; keeping it in the list makes an upgrade lose nothing.
        var projects = new List<string>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ProjectPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var trimmed = path.Trim();
            if (!seenProjects.Add(trimmed))
            {
                continue;
            }

            projects.Add(trimmed);
            if (projects.Count >= MaxProjectPaths)
            {
                break;
            }
        }

        if (SelectedProjectPath is not null && !seenProjects.Contains(SelectedProjectPath))
        {
            projects.Insert(0, SelectedProjectPath);
            if (projects.Count > MaxProjectPaths)
            {
                projects.RemoveAt(projects.Count - 1);
            }
        }

        ProjectPaths = projects;

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
