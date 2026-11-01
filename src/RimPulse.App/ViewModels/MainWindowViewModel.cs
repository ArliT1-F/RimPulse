using System.Collections.ObjectModel;
using Avalonia.Threading;
using RimPulse.App.Services;
using RimPulse.Core.Configuration;
using RimPulse.Core.Credits;
using RimPulse.Core.Diagnostics;
using RimPulse.Core.Installations;
using RimPulse.Core.Projects;
using RimPulse.Core.Monitoring;
using RimPulse.Core.Validation;

namespace RimPulse.App.ViewModels;

/// <summary>
/// Installation discovery, mod inspection, selection and diagnostics. Everything shown in the UI
/// is produced by real work — no placeholder panels for features that do not exist yet.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
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
    private readonly ILinkLauncher _linkLauncher;

    private RimPulseConfiguration _configuration = new();
    private InstallationViewModel? _selectedInstallation;
    private string _statusMessage = "Starting…";
    private string _customPathInput = string.Empty;
    private bool _isBusy;
    private bool _initialized;
    private bool _suppressSelectionPersistence;
    private bool _isCreditsOpen;
    private bool _isActivityLogVisible = true;
    private string _projectPathInput = string.Empty;
    private string _projectSummary = "Choose a mod directory to inspect its metadata and structure.";
    private bool _isProjectBusy;
    private ProjectMonitor? _projectMonitor;
    private string _monitorStatus = "Not monitoring a project.";
    private bool _disposed;
    private long _metadataRefreshSequence;

    public MainWindowViewModel(
        IRimWorldInstallationDetector detector,
        ConfigurationStore configurationStore,
        IDiagnosticLog log,
        AppPaths paths,
        string? environmentRimWorldDirectory = null,
        ILinkLauncher? linkLauncher = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _log = log ?? NullDiagnosticLog.Instance;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _environmentRimWorldDirectory = environmentRimWorldDirectory;
        _linkLauncher = linkLauncher ?? new SystemLinkLauncher();

        Installations = new ObservableCollection<InstallationViewModel>();
        SelectedDetails = new ObservableCollection<DetailRow>();
        SelectedProblems = new ObservableCollection<ProblemRow>();
        ActivityLog = new ObservableCollection<LogEntry>();
        ProjectDetails = new ObservableCollection<DetailRow>();
        ProjectProblems = new ObservableCollection<ProblemRow>();
        ProjectChanges = new ObservableCollection<ProjectChangeRow>();
        InspectProjectCommand = new AsyncRelayCommand(() => InspectProjectAsync(ProjectPathInput),
            () => !IsProjectBusy && !string.IsNullOrWhiteSpace(ProjectPathInput), HandleCommandException);
        Credits = new ObservableCollection<CreditEntryViewModel>();

        foreach (var entry in CreditsCatalog.Entries)
        {
            Credits.Add(new CreditEntryViewModel(entry, _linkLauncher, ReportCreditsStatus));
        }

        RescanCommand = new AsyncRelayCommand(() => RescanAsync(), () => !IsBusy, HandleCommandException);
        AddCustomPathCommand = new AsyncRelayCommand(
            AddCustomPathAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(CustomPathInput),
            HandleCommandException);
        RemoveCustomPathCommand = new AsyncRelayCommand(
            RemoveSelectedCustomPathAsync,
            () => !IsBusy && CanRemoveSelectedCustomPath,
            HandleCommandException);

        ToggleCreditsCommand = new RelayCommand(() => IsCreditsOpen = !IsCreditsOpen);
        ToggleActivityLogCommand = new RelayCommand(() => IsActivityLogVisible = !IsActivityLogVisible);

        _log.EntryWritten += OnLogEntry;
    }

    public ObservableCollection<ProjectChangeRow> ProjectChanges { get; }
    public string MonitorStatus
    {
        get => _monitorStatus;
        private set => SetProperty(ref _monitorStatus, value);
    }

    public ObservableCollection<DetailRow> ProjectDetails { get; }
    public ObservableCollection<ProblemRow> ProjectProblems { get; }
    public AsyncRelayCommand InspectProjectCommand { get; }
    public bool IsProjectBusy
    {
        get => _isProjectBusy;
        private set { if (SetProperty(ref _isProjectBusy, value)) InspectProjectCommand.RaiseCanExecuteChanged(); }
    }
    public string ProjectPathInput
    {
        get => _projectPathInput;
        set { if (SetProperty(ref _projectPathInput, value ?? string.Empty)) InspectProjectCommand.RaiseCanExecuteChanged(); }
    }
    public string ProjectSummary
    {
        get => _projectSummary;
        private set => SetProperty(ref _projectSummary, value);
    }

    /// <summary>Inspect and remember a directory selected through the path field or folder picker.</summary>
    public async Task InspectProjectAsync(string path, bool persist = true)
    {
        if (IsProjectBusy || string.IsNullOrWhiteSpace(path)) return;
        IsProjectBusy = true;
        StopProjectMonitor();
        ProjectChanges.Clear();
        MonitorStatus = "Monitoring paused during inspection.";
        ProjectSummary = "Inspecting mod directory…";
        try
        {
            var result = await Task.Run(() => ModProjectInspector.Inspect(path)).ConfigureAwait(true);
            if (_disposed) return;
            ProjectPathInput = result.RootPath;
            DisplayProjectInspection(result);
            _log.Info("Projects", $"Inspected mod directory '{result.RootPath}': {result.Problems.Count} problem(s).");
            if (Directory.Exists(result.RootPath))
            {
                StartProjectMonitor(result.RootPath);
                if (persist)
                {
                    _configuration.SelectedProjectPath = result.RootPath;
                    await SaveConfigurationAsync(CancellationToken.None).ConfigureAwait(true);
                }
            }
            else
            {
                MonitorStatus = "Not monitoring: directory is unavailable. Inspect again to retry.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ProjectDetails.Clear();
            ProjectProblems.Clear();
            ProjectSummary = "Could not inspect the mod directory: " + ex.Message;
            MonitorStatus = "Not monitoring. Inspect again to retry.";
            _log.Warning("Projects", ProjectSummary);
        }
        finally { IsProjectBusy = false; }
    }

    private void DisplayProjectInspection(ModProjectInspection result)
    {
        ProjectDetails.Clear();
        ProjectProblems.Clear();
        ProjectDetails.Add(new DetailRow("Directory", result.RootPath));
        ProjectDetails.Add(new DetailRow("Detected folders", result.Folders.Count == 0 ? "none" : string.Join(", ", result.Folders)));
        ProjectDetails.Add(new DetailRow("LoadFolders.xml", result.HasLoadFolders ? "present (not interpreted)" : "not found"));
        if (result.Metadata is { } metadata)
        {
            ProjectDetails.Add(new DetailRow("Name", metadata.Name ?? "not specified"));
            ProjectDetails.Add(new DetailRow("Package ID", metadata.PackageId ?? "not specified"));
            ProjectDetails.Add(new DetailRow("Author", metadata.Author ?? "not specified"));
            ProjectDetails.Add(new DetailRow("Supported versions", metadata.SupportedVersions.Count == 0 ? "not specified" : string.Join(", ", metadata.SupportedVersions)));
            var dependencies = metadata.Dependencies.Select(d =>
                $"{d.DisplayName ?? d.PackageId ?? "unknown"} ({d.PackageId ?? "missing package ID"}){(d.Version is null ? "" : $" — {d.Version}")}");
            ProjectDetails.Add(new DetailRow("Declared dependencies", metadata.Dependencies.Count == 0 ? "none" : string.Join("\n", dependencies)));
        }
        foreach (var problem in result.Problems) ProjectProblems.Add(new ProblemRow(problem));
        ProjectSummary = result.Metadata is null ? "Metadata unavailable; see problems below." :
            result.Problems.Count == 0 ? "Mod directory inspected." : "Mod directory inspected with warnings.";
    }

    private void StartProjectMonitor(string rootPath)
    {
        ProjectMonitor? monitor = null;
        try
        {
            monitor = new ProjectMonitor(rootPath);
            monitor.ChangesDetected += OnProjectChanges;
            monitor.MonitoringError += OnProjectMonitoringError;
            _projectMonitor = monitor;
            monitor.Start();
            MonitorStatus = $"Monitoring {rootPath}. Changes are reported, not built or reloaded.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _projectMonitor = null;
            monitor?.Dispose();
            MonitorStatus = "Could not start monitoring: " + ex.Message + ". Inspect again to retry.";
            _log.Warning("Monitoring", MonitorStatus);
        }
    }

    private void StopProjectMonitor()
    {
        var monitor = _projectMonitor;
        _projectMonitor = null;
        if (monitor is null) return;
        monitor.ChangesDetected -= OnProjectChanges;
        monitor.MonitoringError -= OnProjectMonitoringError;
        monitor.Dispose();
    }

    private void OnProjectChanges(object? sender, IReadOnlyList<ProjectChange> changes)
    {
        // The monitor raises batches on a worker thread; never parse project files on the UI thread.
        if (sender is not ProjectMonitor monitor) return;
        try
        {
            var refreshSequence = changes.Any(change => change.Category == ProjectChangeCategory.AboutMetadata)
                ? Interlocked.Increment(ref _metadataRefreshSequence) : 0;
            var rows = changes.Select(change => new ProjectChangeRow(change,
                Path.GetExtension(change.RelativePath).Equals(".xml", StringComparison.OrdinalIgnoreCase)
                    ? XmlFileValidator.Check(monitor.RootPath, change.RelativePath) : null)).ToArray();
            var refreshed = refreshSequence != 0 ? ModProjectInspector.Inspect(monitor.RootPath) : null;
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || !ReferenceEquals(sender, _projectMonitor)) return;
                foreach (var row in rows)
                {
                    ProjectChanges.Insert(0, row);
                    while (ProjectChanges.Count > 250) ProjectChanges.RemoveAt(ProjectChanges.Count - 1);
                }
                if (refreshed is not null && refreshSequence == Interlocked.Read(ref _metadataRefreshSequence))
                    DisplayProjectInspection(refreshed);
            });
        }
        catch (Exception ex)
        {
            OnProjectMonitoringError(sender, "Could not process a file-change batch: " + ex.Message);
        }
    }

    private void OnProjectMonitoringError(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _projectMonitor)) return;
            StopProjectMonitor();
            MonitorStatus = message + " Monitoring stopped; inspect again to retry.";
            _log.Warning("Monitoring", MonitorStatus);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopProjectMonitor();
        _log.EntryWritten -= OnLogEntry;
    }

    public void ReportProjectPickerError(Exception exception)
    {
        ProjectSummary = "Could not open the folder picker: " + exception.Message;
        _log.Warning("Projects", ProjectSummary);
    }

    public ObservableCollection<InstallationViewModel> Installations { get; }

    public ObservableCollection<DetailRow> SelectedDetails { get; }

    public ObservableCollection<ProblemRow> SelectedProblems { get; }

    public ObservableCollection<LogEntry> ActivityLog { get; }

    /// <summary>Every third-party project and creator featured in this application.</summary>
    public ObservableCollection<CreditEntryViewModel> Credits { get; }

    public RelayCommand ToggleCreditsCommand { get; }

    public RelayCommand ToggleActivityLogCommand { get; }

    public string AttributionPolicy => CreditsCatalog.AttributionPolicy;

    public string CreditsCountText => Credits.Count == 1
        ? "1 referenced project"
        : $"{Credits.Count} referenced projects";

    public bool IsCreditsOpen
    {
        get => _isCreditsOpen;
        set => SetProperty(ref _isCreditsOpen, value);
    }

    public bool IsActivityLogVisible
    {
        get => _isActivityLogVisible;
        set => SetProperty(ref _isActivityLogVisible, value);
    }

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

            return "Installation verified. Select a mod directory in the Mod project tab. Runtime integration is not yet implemented.";
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

            if (!string.IsNullOrWhiteSpace(_configuration.SelectedProjectPath))
            {
                await InspectProjectAsync(_configuration.SelectedProjectPath, persist: false).ConfigureAwait(true);
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

    private void ReportCreditsStatus(string message)
    {
        StatusMessage = message;

        if (message.StartsWith("Could not", StringComparison.Ordinal))
        {
            _log.Warning("Credits", message);
        }
        else
        {
            _log.Debug("Credits", message);
        }
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
