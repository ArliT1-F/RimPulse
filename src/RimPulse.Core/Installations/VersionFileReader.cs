using RimPulse.Core.Game;

namespace RimPulse.Core.Installations;

/// <summary>
/// Reads <c>Version.txt</c>, which Ludeon ships in the installation root for both the Unity-era and
/// Waylith-era builds. This is the only authoritative version source RimPulse uses: nothing is guessed
/// from folder names or engine layout.
/// </summary>
public static class VersionFileReader
{
    public const string VersionFileName = "Version.txt";

    /// <summary>Returns the trimmed <c>Version.txt</c> contents, or <c>null</c> when unavailable.</summary>
    public static string? ReadVersionText(string installationRoot)
    {
        if (string.IsNullOrWhiteSpace(installationRoot))
        {
            return null;
        }

        var versionFilePath = Path.Combine(installationRoot, VersionFileName);

        try
        {
            if (!File.Exists(versionFilePath))
            {
                return null;
            }

            var text = File.ReadAllText(versionFilePath).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Returns the parsed game version, or <see cref="GameVersion.Unknown"/>.</summary>
    public static GameVersion ReadGameVersion(string installationRoot)
        => GameVersion.TryParse(ReadVersionText(installationRoot), out var version) ? version : GameVersion.Unknown;
}
