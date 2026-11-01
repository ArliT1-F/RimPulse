using RimPulse.App.ViewModels;
using RimPulse.Core.Configuration;
using RimPulse.Core.Diagnostics;
using RimPulse.Core.Installations;
using RimPulse.Core.Platform;

namespace RimPulse.App.Services;

/// <summary>
/// Composition root: builds the Core services the desktop application needs and owns their lifetime.
/// Kept separate from <see cref="App"/> so the wiring can be reviewed in one place and reused by future
/// entry points (for example a headless CLI for diagnostics export).
/// </summary>
public sealed class RimPulseServiceGraph : IDisposable
{
    private readonly DiagnosticLog _log;

    private RimPulseServiceGraph(
        AppPaths paths,
        DiagnosticLog log,
        ConfigurationStore configurationStore,
        IRimWorldInstallationDetector detector,
        string? environmentRimWorldDirectory)
    {
        Paths = paths;
        _log = log;
        ConfigurationStore = configurationStore;
        Detector = detector;
        EnvironmentRimWorldDirectory = environmentRimWorldDirectory;
    }

    public AppPaths Paths { get; }

    public ConfigurationStore ConfigurationStore { get; }

    public IRimWorldInstallationDetector Detector { get; }

    public IDiagnosticLog Log => _log;

    public string? EnvironmentRimWorldDirectory { get; }

    public static RimPulseServiceGraph Create()
    {
        var platform = HostPlatformExtensions.Detect();
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var paths = AppPaths.Resolve(platform, homeDirectory, name => Environment.GetEnvironmentVariable(name));
        paths.EnsureCreated();

        var log = new DiagnosticLog(
            capacity: 2000,
            minimumLevel: LogLevel.Debug,
            sink: new RotatingFileLogSink(paths.LogFilePath));

        log.Info(
            "Startup",
            $"RimPulse starting on {platform.ToDisplayName()}. Configuration: '{paths.ConfigurationFilePath}'. Log: '{paths.LogFilePath}'.");

        var environmentRimWorldDirectory = Environment.GetEnvironmentVariable("RIMWORLD_DIR");

        var options = new InstallationDetectorOptions
        {
            Platform = platform,
            HomeDirectory = homeDirectory,
            EnvironmentRimWorldDirectory = environmentRimWorldDirectory,
        };

        var configurationStore = new ConfigurationStore(paths.ConfigurationFilePath, log);
        var detector = new RimWorldInstallationDetector(options, log);

        return new RimPulseServiceGraph(paths, log, configurationStore, detector, environmentRimWorldDirectory);
    }

    public MainWindowViewModel CreateMainWindowViewModel()
        => new(Detector, ConfigurationStore, _log, Paths, EnvironmentRimWorldDirectory, new SystemLinkLauncher());

    public void Dispose()
    {
        _log.Info("Shutdown", "RimPulse shutting down.");
        _log.Dispose();
    }
}
