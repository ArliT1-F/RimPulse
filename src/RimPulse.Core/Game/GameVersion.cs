using System.Globalization;

namespace RimPulse.Core.Game;

/// <summary>
/// A RimWorld game version, for example <c>1.6</c> or <c>1.7</c>, optionally carrying the
/// build information found in the installation's <c>Version.txt</c> (e.g. <c>4851 rev1248</c>).
/// </summary>
/// <remarks>
/// RimPulse deliberately models versions as <c>major.minor</c> plus free-form build text instead of
/// an enum, so that a future 1.8 installation is still detected and reported (as unsupported)
/// instead of being silently ignored.
/// </remarks>
public readonly record struct GameVersion : IComparable<GameVersion>
{
    private static readonly int[] SupportedMinorVersions = [6, 7];

    /// <summary>An unconfirmed version.</summary>
    public static readonly GameVersion Unknown = new(0, 0, null);

    public GameVersion(int major, int minor, string? build = null)
    {
        if (major < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), major, "The major version cannot be negative.");
        }

        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minor), minor, "The minor version cannot be negative.");
        }

        Major = major;
        Minor = minor;
        Build = string.IsNullOrWhiteSpace(build) ? null : build.Trim();
    }

    public int Major { get; }

    public int Minor { get; }

    /// <summary>Build text that followed the version number in <c>Version.txt</c>, if any.</summary>
    public string? Build { get; }

    public bool IsKnown => Major > 0;

    /// <summary><c>1.6</c> style text, or <c>unknown</c>.</summary>
    public string ShortVersion => IsKnown ? $"{Major}.{Minor}" : "unknown";

    /// <summary>
    /// True for the versions RimPulse currently supports (1.6 and 1.7).
    /// </summary>
    public bool IsSupported => IsKnown && Major == 1 && Array.IndexOf(SupportedMinorVersions, Minor) >= 0;

    public static bool TryParse(string? text, out GameVersion version)
    {
        version = Unknown;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // Version.txt looks like "1.6.4851 rev1248": the version is the first token, the rest is
        // build/revision information that RimPulse keeps verbatim.
        var separatorIndex = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        var versionToken = separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];
        var buildToken = separatorIndex < 0 ? string.Empty : trimmed[(separatorIndex + 1)..].Trim();

        var parts = versionToken.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        if (!TryParseComponent(parts[0], out var major) || !TryParseComponent(parts[1], out var minor))
        {
            return false;
        }

        if (major <= 0 || minor <= 0)
        {
            return false;
        }

        string? build = null;
        if (parts.Length > 2)
        {
            var buildParts = new string[parts.Length - 2];
            for (var i = 0; i < buildParts.Length; i++)
            {
                var component = parts[i + 2];
                if (!TryParseComponent(component, out _))
                {
                    return false;
                }

                buildParts[i] = component.Trim();
            }

            build = string.Join('.', buildParts);
        }

        if (buildToken.Length > 0)
        {
            build = build is null ? buildToken : $"{build} {buildToken}";
        }

        version = new GameVersion(major, minor, build);
        return true;
    }

    public static GameVersion Parse(string text)
        => TryParse(text, out var version)
            ? version
            : throw new FormatException($"'{text}' is not a valid RimWorld version.");

    public int CompareTo(GameVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        return string.CompareOrdinal(Build, other.Build);
    }

    public override string ToString()
    {
        if (!IsKnown)
        {
            return "unknown";
        }

        return Build is null ? ShortVersion : $"{ShortVersion}.{Build}";
    }

    private static bool TryParseComponent(string value, out int component)
        => int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out component);
}
