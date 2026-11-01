namespace RimPulse.Core.Installations;

/// <summary>Where a detected installation was found. Used to decide what to show and what to persist.</summary>
public enum InstallationSource
{
    /// <summary>Added by the user (configuration or the "add path" box).</summary>
    Manual = 0,

    /// <summary>Found through a Steam library.</summary>
    Steam = 1,

    /// <summary>Found in a GOG-style install directory.</summary>
    Gog = 2,

    /// <summary>Provided by the <c>RIMWORLD_DIR</c> environment variable.</summary>
    EnvironmentVariable = 3,
}

/// <summary>How the game version was determined.</summary>
public enum GameVersionSource
{
    /// <summary>The version could not be confirmed.</summary>
    Unknown = 0,

    /// <summary>Read from <c>Version.txt</c> in the installation root.</summary>
    VersionFile = 1,
}
