using RimPulse.Core.Platform;

namespace RimPulse.Core.Configuration;

/// <summary>
/// Resolves the XDG/Windows/macOS locations RimPulse writes to. Nothing is ever written into the
/// user's RimWorld installation or mod directories by this class.
/// </summary>
public sealed class AppPaths
{
    private const string ApplicationFolderName = "RimPulse";

    public AppPaths(string configurationDirectory, string stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(configurationDirectory))
        {
            throw new ArgumentException("A configuration directory is required.", nameof(configurationDirectory));
        }

        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            throw new ArgumentException("A state directory is required.", nameof(stateDirectory));
        }

        ConfigurationDirectory = configurationDirectory;
        StateDirectory = stateDirectory;
    }

    public string ConfigurationDirectory { get; }

    public string StateDirectory { get; }

    public string ConfigurationFilePath => Path.Combine(ConfigurationDirectory, "config.json");

    public string LogDirectory => Path.Combine(StateDirectory, "logs");

    public string LogFilePath => Path.Combine(LogDirectory, "rimpulse.log");

    /// <summary>
    /// Resolves the platform-appropriate directories:
    /// Linux uses <c>$XDG_CONFIG_HOME</c> / <c>$XDG_STATE_HOME</c> (defaults: <c>~/.config</c>,
    /// <c>~/.local/state</c>), Windows uses <c>%APPDATA%</c> / <c>%LOCALAPPDATA%</c> and macOS uses
    /// <c>~/Library/Application Support</c> / <c>~/Library/Logs</c>.
    /// </summary>
    public static AppPaths Resolve(HostPlatform platform, string homeDirectory, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            throw new ArgumentException("A home directory is required.", nameof(homeDirectory));
        }

        string baseConfigDirectory;
        string baseStateDirectory;

        switch (platform)
        {
            case HostPlatform.Windows:
                baseConfigDirectory = FirstNonEmpty(
                    getEnvironmentVariable("APPDATA"),
                    Path.Combine(homeDirectory, "AppData", "Roaming"));
                baseStateDirectory = FirstNonEmpty(
                    getEnvironmentVariable("LOCALAPPDATA"),
                    Path.Combine(homeDirectory, "AppData", "Local"));
                break;

            case HostPlatform.MacOS:
                baseConfigDirectory = Path.Combine(homeDirectory, "Library", "Application Support");
                baseStateDirectory = Path.Combine(homeDirectory, "Library", "Logs");
                break;

            default:
                baseConfigDirectory = FirstNonEmpty(
                    getEnvironmentVariable("XDG_CONFIG_HOME"),
                    Path.Combine(homeDirectory, ".config"));
                baseStateDirectory = FirstNonEmpty(
                    getEnvironmentVariable("XDG_STATE_HOME"),
                    Path.Combine(homeDirectory, ".local", "state"));
                break;
        }

        return new AppPaths(
            Path.Combine(baseConfigDirectory, ApplicationFolderName),
            Path.Combine(baseStateDirectory, ApplicationFolderName));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigurationDirectory);
        Directory.CreateDirectory(StateDirectory);
    }

    private static string FirstNonEmpty(string? preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
}
