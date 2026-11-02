using RimPulse.Core.Configuration;
using RimPulse.Core.Diagnostics;

namespace RimPulse.App.ViewModels;

/// <summary>Application-wide preferences. Saving is explicit and applies to the next build or startup as labelled.</summary>
public sealed class AppSettingsViewModel : ObservableObject
{
    private bool _autoDetect = true, _release, _symbols = true, _showLog = true;
    private LogLevel _logLevel;
    public bool AutoDetectOnStartup { get => _autoDetect; set => SetProperty(ref _autoDetect, value); }
    public bool ReleaseBuild { get => _release; set => SetProperty(ref _release, value); }
    public bool EmitDebugSymbols { get => _symbols; set => SetProperty(ref _symbols, value); }
    public bool ShowActivityLog { get => _showLog; set => SetProperty(ref _showLog, value); }
    public LogLevel MinimumLogLevel { get => _logLevel; set => SetProperty(ref _logLevel, value); }
    public IReadOnlyList<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();
    public string Status { get; private set; } = "Changes take effect after Save settings.";

    public void Load(RimPulseConfiguration configuration)
    {
        AutoDetectOnStartup = configuration.AutoDetectOnStartup;
        ReleaseBuild = configuration.BuildConfiguration == "Release";
        EmitDebugSymbols = configuration.EmitDebugSymbols;
        ShowActivityLog = configuration.ShowActivityLog;
        MinimumLogLevel = Enum.Parse<LogLevel>(configuration.MinimumLogLevel);
    }
    public void ApplyTo(RimPulseConfiguration configuration)
    {
        configuration.AutoDetectOnStartup = AutoDetectOnStartup;
        configuration.BuildConfiguration = ReleaseBuild ? "Release" : "Debug";
        configuration.EmitDebugSymbols = EmitDebugSymbols;
        configuration.ShowActivityLog = ShowActivityLog;
        configuration.MinimumLogLevel = MinimumLogLevel.ToString();
    }
    public void Report(string message) { Status = message; OnPropertyChanged(nameof(Status)); }
}
