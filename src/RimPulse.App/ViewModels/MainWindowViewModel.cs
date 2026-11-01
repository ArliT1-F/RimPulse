using System.Collections.ObjectModel;
using Avalonia.Threading;
using RimPulse.Core.Configuration;
using RimPulse.Core.Diagnostics;
using RimPulse.Core.Installations;

namespace RimPulse.App.ViewModels;

/// <summary>
/// Milestone 1 view model: installation discovery, selection and diagnostics. Everything shown in the UI
/// is produced by real work — no placeholder panels for features that do not exist yet.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    private const int MaxActivityEntries = 250;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly IRimWorldInstallationDetector _detector;
    private readonly ConfigurationStore _configurationStore;
    private readonly IDiagnosticLog _log;
    private readonly AppPaths _paths;
    private readonly string? _environmentRimWorldDirectory;

    private RimPulseConfiguration _configuration = new();
    private InstallationViewModel? _selectedInstallation;
    private string _statusMessage = "Starting…";
    private string _customPathInput = string.Empty;
    private bool _isBusy;
    private bool _initialized;
    private bool _suppressSelectionPersistence;

    public MainWindowViewModel(
        IRimWorldInstallationDetector detector,
        ConfigurationStore configurationStore,
        IDiagnosticLog log,
        AppPaths paths,
        string? environmentRimWorldDirectory = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _log = log ?? NullDiagnosticLog.Instance;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _environmentRimWorldDirectory = environmentRimWorldDirectory;

        Installations = new ObservableCollection<InstallationViewModel>();
        SelectedDetails = new ObservableCollection<DetailRow>();
        SelectedProblems = new ObservableCollection<ProblemRow>();
        ActivityLog = new ObservableCollection<LogEntry>();

        RescanCommand = new AsyncRelayCommand(() => RescanAsync(), () => !IsBusy, HandleCommandException);
        AddCustomPathCommand = new AsyncRelayCommand(
            AddCustomPathAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(CustomPathInput),
            HandleCommandException);
        RemoveCustomPathCommand = new AsyncRelayCommand(
            RemoveSelectedCustomPathAsync,
            () => !IsBusy && CanRemoveSelectedCustomPath,
            HandleCommandException);

        _log.EntryWritten += OnLogEntry;
    }

    public ObservableCollection<InstallationViewModel> Installations { get; }

    public ObservableCollection<DetailRow> SelectedDetails { get; }

    public ObservableCollection<ProblemRow> SelectedProblems { get; }

    public ObservableCollection<LogEntry> ActivityLog { get; }

    public AsyncRelayCommand RescanCommand { get; }

    public AsyncRelayCommand AddCustomPathCommand { get; }

    public AsyncRelayCommand RemoveCustomPathCommand { get; }

    public string ConfigurationFilePath => _paths.ConfigurationFilePath;

    public string LogFilePath => _paths.LogFilePath;

    public bool HasEnvironmentOverride => !string.IsNullOrWhiteSpace(_environmentRimWorldDirectory);

    public string EnvironmentOverrideText => _environmentRimWorldDirectory ?? string.Empty;

    public string EnvironmentOverrideDisplay => HasEnvironmentOverride
        ? $"RIMWORLD_DIR override active: {_environmentRimWorldDirectory}"
        : string.Empty;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string CustomPathInput
    {
        get => _customPathInput;
        set
        {
            // A TextBox can hand us null when it is cleared; keep the property non-null.
            if (SetProperty(ref _customPathInput, value ?? string.Empty))
            {
                AddCustomPathCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public InstallationViewModel? SelectedInstallation
    {
        get => _selectedInstallation;
        set
        {
            if (!SetProperty(ref _selectedInstallation, value))
            {
                return;
            }

            RebuildSelectedDetails();

            if (!_suppressSelectionPersistence)
            {
                PersistSelection(value);
            }
        }
    }

    public bool HasInstallations => Installations.Count > 0;

    public bool HasNoInstallations => Installations.Count == 0;

    public string InstallationCountText => Installations.Count switch
    {
        0 => "none found",
        1 => "1 installation",
        var count => $"{count} installations",
    };

    public bool HasSelection => SelectedInstallation is not null;

    public bool HasNoSelection => SelectedInstallation is null;

    public bool SelectedIsReady => SelectedInstallation?.IsReady ?? false;

    public bool SelectedIsUnsupported => SelectedInstallation?.IsUnsupported ?? false;

    public bool SelectedIsIncomplete => SelectedInstallation?.IsIncomplete ?? false;

    public bool SelectedHasProblems => SelectedProblems.Count > 0;

    public string SelectedTitle => SelectedInstallation?.Model.DisplayName ?? "No installation selected";

    public string SelectedSubtitle => SelectedInstallation is null
        ? "Select a detected installation on the left, or add one by path."
        : SelectedInstallation.Summary;

    public string SelectionNote
    {
        get
        {
            var model = SelectedInstallation?.Model;

            if (model is null)
            {
                return string.Empty;
            }

            if (!model.IsUsable)
            {
                return "This path cannot be used yet — the problems detected during inspection are listed above.";
            }

            if (!model.IsSupportedVersion)
            {
                return $"RimPulse currently supports RimWorld 1.6 and 1.7. This installation reports {model.VersionDisplay}.";
            }

            return "Installation verified. Project management arrives in milestone 2, runtime integration in milestone 4.";
        }
    }

    public bool CanRemoveSelectedCustomPath
    {
        get
        {
            var model = SelectedInstallation?.Model;
            if (model is null)
            {
                return false;
            }

            return _configuration.CustomInstallationPaths.Exists(
                path => PathComparer.Equals(InstallationProbe.NormalizePath(path), model.RootPath));
        }
    }

    /// <summary>Loads the persisted configuration and runs the first installation scan.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            var loadResult = await _configurationStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            _configuration = loadResult.Configuration;

            if (loadResult.Status is ConfigurationLoadStatus.Loaded or ConfigurationLoadStatus.DefaultsCreated)
            {
                _log.Info("Configuration", loadResult.Message);
            }
            else
            {
                _log.Warning("Configuration", loadResult.Message);
            }

            StatusMessage = "Ready.";

            if (_configuration.AutoDetectOnStartup)
            {
                await RescanAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
            }
            else
            {
                _log.Info("Detection", "Automatic installation detection is disabled in the configuration.");
                StatusMessage = "Automatic installation detection is disabled. Use Re-scan to search anyway.";
            }
        }
        catch (Exception ex)
        {
            _log.Error("Startup", "RimPulse failed to initialise.", ex);
            StatusMessage = "Initialisation failed: " + ex.Message;
        }
    }

    private async Task RescanAsync(string? preferredRootPath = null, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Scanning for RimWorld installations…";
        _log.Info("Detection", "Installation scan started.");

        try
        {
            var previousPath = preferredRootPath
                               ?? SelectedInstallation?.Model.RootPath
                               ?? _configuration.SelectedInstallationPath;

            var additionalPaths = _configuration.CustomInstallationPaths.ToArray();
            var found = await Task.Run(() => _detector.DetectAsync(additionalPaths, cancellationToken), cancellationToken)
                .ConfigureAwait(true);

            _suppressSelectionPersistence = true;

            try
            {
                Installations.Clear();
                foreach (var installation in found)
                {
                    Installations.Add(new InstallationViewModel(installation));
                }

                OnPropertiesChanged(nameof(HasInstallations), nameof(HasNoInstallations), nameof(InstallationCountText));

                var target = FindInstallationOrDefault(previousPath);
                SelectedInstallation = target;
            }
            finally
            {
                _suppressSelectionPersistence = false;
            }

            var selectedPath = SelectedInstallation?.Model.RootPath;
            if (!string.Equals(_configuration.SelectedInstallationPath, selectedPath, StringComparison.Ordinal))
            {
                _configuration.SelectedInstallationPath = selectedPath;
                await SaveConfigurationAsync(cancellationToken).ConfigureAwait(true);
            }

            StatusMessage = Installations.Count == 0
                ? "No RimWorld installation was found. Add a path manually, or start RimPulse with RIMWORLD_DIR set."
                : $"Detected {InstallationCountText}. Last scan: {DateTimeOffset.Now:HH:mm:ss}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Installation scan cancelled.";
            _log.Info("Detection", "Installation scan cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error("Detection", "Installation scan failed.", ex);
            StatusMessage = "Installation scan failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddCustomPathAsync()
    {
        var rawPath = CustomPathInput.Trim();

        if (rawPath.Length == 0)
        {
            return;
        }

        var normalizedPath = InstallationProbe.NormalizePath(rawPath);
        _log.Info("Detection", $"Inspecting user-supplied path '{normalizedPath}'.");

        var installation = InstallationProbe.Probe(normalizedPath, InstallationSource.Manual);

        if (!installation.RootExists)
        {
            StatusMessage = $"'{normalizedPath}' does not exist or is not accessible.";
            _log.Warning("Detection", StatusMessage);
            return;
        }

        var alreadyKnown = _configuration.CustomInstallationPaths.Exists(
            path => PathComparer.Equals(InstallationProbe.NormalizePath(path), installation.RootPath));

        if (!alreadyKnown)
        {
            _configuration.CustomInstallationPaths.Add(installation.RootPath);
            await SaveConfigurationAsync(CancellationToken.None).ConfigureAwait(true);
        }

        CustomPathInput = string.Empty;
        await RescanAsync(installation.RootPath).ConfigureAwait(true);

        StatusMessage = installation.IsUsable
            ? $"Added {installation.DisplayName}."
            : $"Path added, but it is not a usable RimWorld installation: {installation.Problems.FirstOrDefault() ?? "unknown reason"}";
    }

    private async Task RemoveSelectedCustomPathAsync()
    {
        var installation = SelectedInstallation;

        if (installation is null)
        {
            return;
        }

        var removed = _configuration.CustomInstallationPaths.RemoveAll(
            path => PathComparer.Equals(InstallationProbe.NormalizePath(path), installation.Model.RootPath));

        if (removed == 0)
        {
            return;
        }

        _log.Info("Detection", $"Removed '{installation.Model.RootPath}' from the custom installation list.");
        await SaveConfigurationAsync(CancellationToken.None).ConfigureAwait(true);
        await RescanAsync().ConfigureAwait(true);

        StatusMessage = "Removed the path from the custom installation list.";
    }

    private InstallationViewModel? FindInstallationOrDefault(string? rootPath)
    {
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            var match = Installations.FirstOrDefault(
                candidate => PathComparer.Equals(candidate.Model.RootPath, rootPath));

            if (match is not null)
            {
                return match;
            }
        }

        return Installations.FirstOrDefault(candidate => candidate.IsReady)
               ?? Installations.FirstOrDefault();
    }

    private void RebuildSelectedDetails()
    {
        SelectedDetails.Clear();
        SelectedProblems.Clear();

        var model = SelectedInstallation?.Model;

        if (model is not null)
        {
            SelectedDetails.Add(new DetailRow("Game version", model.VersionDisplay));
            SelectedDetails.Add(new DetailRow(
                "Version source",
                model.VersionSource == GameVersionSource.VersionFile
                    ? $"{VersionFileReader.VersionFileName} (authoritative)"
                    : "unconfirmed"));
            SelectedDetails.Add(new DetailRow("Runtime layout", model.LayoutDisplay));
            SelectedDetails.Add(new DetailRow("Assembly directory", model.ManagedAssemblyDirectory ?? "not found"));
            SelectedDetails.Add(new DetailRow(
                "Assembly check",
                model.MissingAssemblies.Count == 0 && model.PresentAssemblies.Count > 0
                    ? $"all {model.PresentAssemblies.Count} required assemblies present"
                    : $"{model.PresentAssemblies.Count} present, {model.MissingAssemblies.Count} missing"));
            SelectedDetails.Add(new DetailRow(
                "Assemblies found",
                model.PresentAssemblies.Count == 0 ? "none" : string.Join(", ", model.PresentAssemblies)));
            SelectedDetails.Add(new DetailRow("Discovered via", model.Source.ToString()));
            SelectedDetails.Add(new DetailRow("Installation root", model.RootPath));

            foreach (var problem in model.Problems)
            {
                SelectedProblems.Add(new ProblemRow(problem));
            }
        }

        OnPropertiesChanged(
            nameof(HasSelection),
            nameof(HasNoSelection),
            nameof(SelectedIsReady),
            nameof(SelectedIsUnsupported),
            nameof(SelectedIsIncomplete),
            nameof(SelectedHasProblems),
            nameof(SelectedTitle),
            nameof(SelectedSubtitle),
            nameof(SelectionNote),
            nameof(CanRemoveSelectedCustomPath));

        RaiseCommandStates();
    }

    private void PersistSelection(InstallationViewModel? installation)
    {
        var path = installation?.Model.RootPath;

        if (string.Equals(_configuration.SelectedInstallationPath, path, StringComparison.Ordinal))
        {
            return;
        }

        _configuration.SelectedInstallationPath = path;
        _ = SaveConfigurationAsync(CancellationToken.None);
    }

    private async Task SaveConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _configurationStore.SaveAsync(_configuration, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("Configuration", "Could not save the RimPulse configuration.", ex);
            StatusMessage = "Could not save configuration: " + ex.Message;
        }
    }

    private void RaiseCommandStates()
    {
        RescanCommand.RaiseCanExecuteChanged();
        AddCustomPathCommand.RaiseCanExecuteChanged();
        RemoveCustomPathCommand.RaiseCanExecuteChanged();
    }

    private void HandleCommandException(Exception exception)
    {
        _log.Error("UI", "An interface command failed.", exception);
        StatusMessage = "Operation failed: " + exception.Message;
    }

    private void OnLogEntry(object? sender, LogEntry entry)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AppendLogEntry(entry);
            return;
        }

        Dispatcher.UIThread.Post(() => AppendLogEntry(entry));
    }

    private void AppendLogEntry(LogEntry entry)
    {
        ActivityLog.Add(entry);

        while (ActivityLog.Count > MaxActivityEntries)
        {
            ActivityLog.RemoveAt(0);
        }
    }
}
