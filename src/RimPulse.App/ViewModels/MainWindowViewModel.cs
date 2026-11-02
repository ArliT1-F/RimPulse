using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using RimPulse.App.Services;
using RimPulse.Core.Configuration;
using RimPulse.Core.Build;
using RimPulse.Core.Credits;
using RimPulse.Core.Diagnostics;
using RimPulse.Core.Game;
using RimPulse.Core.Installations;
using RimPulse.Core.Projects;
using RimPulse.Core.Monitoring;
using RimPulse.Core.Validation;
using RimPulse.Core.Runtime;

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
    private readonly IClipboardService _clipboard;

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
    private ProjectRow? _selectedProject;
    private ModProjectInspection? _lastProjectInspection;
    private bool _suppressProjectSelection;
    private bool _isProjectBusy;
    private bool _isBuilding;
    private string _buildStatus = "No build has run yet.";
    private long _buildSequence;
    private bool _runtimeRunning;
    private string _runtimeStatus = "No game launched by RimPulse.";

    /// <summary>
    /// Upper bound on rows added to the build diagnostics list. A single failed build can report over a thousand
    /// compiler errors, which makes the list unusable and slow; the remainder is summarised and kept in the log.
    /// </summary>
    private const int MaxVisibleBuildDiagnostics = 500;

    /// <summary>
    /// Every diagnostic of the last build, in the order it was reported, without the display cap: the panel
    /// shows the first <see cref="MaxVisibleBuildDiagnostics"/> rows, but copying the build errors must copy all
    /// of them, which is the whole point of the button.
    /// </summary>
    private readonly List<string> _allBuildDiagnosticLines = new();

    /// <summary>Clears <see cref="BuildCopyNotice"/> a few seconds after a copy, so it is feedback, not a label.</summary>
    private readonly DispatcherTimer _copyNoticeTimer;
    private string _buildCopyNotice = string.Empty;
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
        ILinkLauncher? linkLauncher = null,
        IClipboardService? clipboard = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _log = log ?? NullDiagnosticLog.Instance;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _environmentRimWorldDirectory = environmentRimWorldDirectory;
        _linkLauncher = linkLauncher ?? new SystemLinkLauncher();
        _clipboard = clipboard ?? new SystemClipboardService();

        Installations = new ObservableCollection<InstallationViewModel>();
        SelectedDetails = new ObservableCollection<DetailRow>();
        SelectedProblems = new ObservableCollection<ProblemRow>();
        ActivityLog = new ObservableCollection<LogEntry>();
        ProjectDetails = new ObservableCollection<DetailRow>();
        ProjectProblems = new ObservableCollection<ProblemRow>();
        ProjectChanges = new ObservableCollection<ProjectChangeRow>();
        Projects = new ObservableCollection<ProjectRow>();
        BuildDiagnostics = new ObservableCollection<BuildDiagnosticRow>();
        BuildDetails = new ObservableCollection<DetailRow>();
        InspectProjectCommand = new AsyncRelayCommand(() => InspectProjectAsync(ProjectPathInput),
            () => !IsProjectBusy && !string.IsNullOrWhiteSpace(ProjectPathInput), HandleCommandException);
        AddProjectCommand = new AsyncRelayCommand(() => InspectProjectAsync(ProjectPathInput),
            () => !IsProjectBusy && !string.IsNullOrWhiteSpace(ProjectPathInput), HandleCommandException);
        RemoveProjectCommand = new AsyncRelayCommand(RemoveSelectedProjectAsync,
            () => !IsProjectBusy && SelectedProject is not null, HandleCommandException);
        BuildProjectCommand = new AsyncRelayCommand(BuildSelectedProjectAsync,
            () => !IsBuilding && !IsProjectBusy && _lastProjectInspection is not null,
            HandleCommandException);
        CopyBuildDiagnosticsCommand = new AsyncRelayCommand(
            CopyBuildDiagnosticsAsync,
            () => HasBuildDiagnostics,
            HandleCommandException);
        _copyNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _copyNoticeTimer.Tick += (_, _) =>
        {
            _copyNoticeTimer.Stop();
            BuildCopyNotice = string.Empty;
        };
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

        LaunchGameCommand = new AsyncRelayCommand(LaunchGameAsync,
            () => !_runtimeRunning && SelectedInstallation?.IsReady == true, HandleCommandException);

        ToggleCreditsCommand = new RelayCommand(() => IsCreditsOpen = !IsCreditsOpen);
        ToggleActivityLogCommand = new RelayCommand(() => IsActivityLogVisible = !IsActivityLogVisible);

        _log.EntryWritten += OnLogEntry;
    }

    public AsyncRelayCommand LaunchGameCommand { get; }

    public string RuntimeStatus
    {
        get => _runtimeStatus;
        private set => SetProperty(ref _runtimeStatus, value);
    }

    private async Task LaunchGameAsync()
    {
        var installation = SelectedInstallation?.Model;
        if (installation is null || _runtimeRunning) return;
        _runtimeRunning = true;
        RuntimeStatus = $"Starting RimWorld {installation.Version.ShortVersion} from {installation.RootPath}…";
        LaunchGameCommand.RaiseCanExecuteChanged();
        try
        {
            using var process = await Task.Run(() => GameProcessLauncher.Start(installation));
            var description = $"RimWorld {installation.Version.ShortVersion} (PID {process.Id})";
            if (!_disposed)
            {
                RuntimeStatus = $"Running {description} from {installation.RootPath}.";
                _log.Info("Runtime", RuntimeStatus);
            }
            await process.WaitForExitAsync();
            if (!_disposed)
            {
                RuntimeStatus = $"{description} exited with code {process.ExitCode}.";
                _log.Info("Runtime", RuntimeStatus);
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                RuntimeStatus = "Could not launch or monitor the game: " + ex.Message;
                _log.Error("Runtime", RuntimeStatus, ex);
            }
        }
        finally
        {
            _runtimeRunning = false;
            if (!_disposed) LaunchGameCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<ProjectChangeRow> ProjectChanges { get; }
    public string MonitorStatus
    {
        get => _monitorStatus;
        private set => SetProperty(ref _monitorStatus, value);
    }

    public ObservableCollection<DetailRow> ProjectDetails { get; }
    public ObservableCollection<ProblemRow> ProjectProblems { get; }

    /// <summary>Mod project directories the user has opened, newest first.</summary>
    public ObservableCollection<ProjectRow> Projects { get; }

    public AsyncRelayCommand InspectProjectCommand { get; }

    public AsyncRelayCommand AddProjectCommand { get; }

    public AsyncRelayCommand RemoveProjectCommand { get; }

    public ProjectRow? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value))
            {
                return;
            }

            RemoveProjectCommand.RaiseCanExecuteChanged();

            if (value is null)
            {
                return;
            }

            ProjectPathInput = value.Path;

            // Selecting the row that was just restored programmatically must not start a second inspection.
            if (_suppressProjectSelection)
            {
                return;
            }

            _ = ActivateSelectedProjectAsync(value);
        }
    }

    public bool HasProjects => Projects.Count > 0;

    public bool HasNoProjects => Projects.Count == 0;

    public string ProjectCountText => Projects.Count switch
    {
        0 => "no projects",
        1 => "1 project",
        var count => $"{count} projects",
    };
    public bool IsProjectBusy
    {
        get => _isProjectBusy;
        private set
        {
            if (!SetProperty(ref _isProjectBusy, value))
            {
                return;
            }

            InspectProjectCommand.RaiseCanExecuteChanged();
            AddProjectCommand.RaiseCanExecuteChanged();
            RemoveProjectCommand.RaiseCanExecuteChanged();
        }
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

    /// <summary>Compiler diagnostics of the last build.</summary>
    public ObservableCollection<BuildDiagnosticRow> BuildDiagnostics { get; }

    /// <summary>What the last build produced, read back from the assembly metadata.</summary>
    public ObservableCollection<DetailRow> BuildDetails { get; }

    public AsyncRelayCommand BuildProjectCommand { get; }

    /// <summary>
    /// Copies every diagnostic of the last build to the clipboard, with a header naming the project, the
    /// installation and the result. Until this existed the only way to report a build error was to retype it.
    /// </summary>
    public AsyncRelayCommand CopyBuildDiagnosticsCommand { get; }

    /// <summary>Feedback for the last copy: "Copied 42 line(s) to the clipboard.", or why it failed.</summary>
    public string BuildCopyNotice
    {
        get => _buildCopyNotice;
        private set
        {
            if (SetProperty(ref _buildCopyNotice, value))
            {
                OnPropertyChanged(nameof(HasBuildCopyNotice));
            }
        }
    }

    public bool HasBuildCopyNotice => BuildCopyNotice.Length > 0;

    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (SetProperty(ref _isBuilding, value))
            {
                BuildProjectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BuildStatus
    {
        get => _buildStatus;
        private set => SetProperty(ref _buildStatus, value);
    }

    /// <summary>Where the assemblies go, so the write into the mod directory is never a surprise.</summary>
    public string BuildTargetText => _lastProjectInspection is null
        ? "Select a project and a usable installation to build."
        : SelectedInstallation?.Model is { IsUsable: true } installation
            ? $"Compiles against {installation.DisplayName} ({installation.VersionDisplay}) and writes to " +
              $"{ResolveOutputDirectory(_lastProjectInspection.RootPath, installation).Path}."
            : "Select a usable RimWorld installation: a mod can only be compiled against real game assemblies.";

    /// <summary>
    /// The folder this mod's next build writes to: the version folder's <c>Assemblies</c> when the mod has one
    /// for the selected installation's version (e.g. <c>1.6/Assemblies</c>), otherwise the root one.
    /// </summary>
    private static ModOutputDirectory ResolveOutputDirectory(string modRootPath, RimWorldInstallation installation)
        => ModOutputDirectoryResolver.Resolve(modRootPath, installation.Version);

    public bool HasBuildDiagnostics => BuildDiagnostics.Count > 0;

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
                    RememberProject(result);
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

    /// <summary>
    /// Rebuilds every project-derived row from one inspection result. Re-rendering (rather than appending)
    /// keeps the load-folder projection consistent when the selected installation changes.
    /// </summary>
    private void DisplayProjectInspection(ModProjectInspection result)
    {
        _lastProjectInspection = result;
        ProjectDetails.Clear();
        ProjectProblems.Clear();
        ProjectDetails.Add(new DetailRow("Directory", result.RootPath));
        ProjectDetails.Add(new DetailRow("Detected folders", result.Folders.Count == 0 ? "none" : string.Join(", ", result.Folders)));
        ProjectDetails.Add(new DetailRow("LoadFolders.xml", DescribeLoadFolders(result.LoadFolders)));
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
        AddLoadFolderProjection(result);
        foreach (var problem in result.Problems) ProjectProblems.Add(new ProblemRow(problem));

        // Build results belong to one project: keeping them would attribute another project's errors to this
        // one, and bumping the sequence discards a build that is still running for the previous project.
        Interlocked.Increment(ref _buildSequence);
        ClearBuildDiagnostics();
        BuildDetails.Clear();
        BuildStatus = "No build has run for this project.";
        OnPropertiesChanged(nameof(BuildTargetText));
        BuildProjectCommand.RaiseCanExecuteChanged();
        ProjectSummary = result.Metadata is null ? "Metadata unavailable; see problems below." :
            result.Problems.Count == 0 ? "Mod directory inspected." : "Mod directory inspected with warnings.";
    }

    private static string DescribeLoadFolders(LoadFoldersDocument document)
    {
        if (!document.Exists)
        {
            return "not found (RimWorld's default versioned-folder rules apply)";
        }

        if (document.Sections.Count == 0)
        {
            return "present, but no version sections were read";
        }

        var sections = document.Sections.Select(section => $"{section.ElementName} ({section.Entries.Count} entries)");
        return $"present — {string.Join(", ", sections)}";
    }

    /// <summary>
    /// Shows which folders RimPulse would expect to load for the selected installation's version. Only this
    /// mod's package ID is treated as active, so conditional entries are reported, never silently dropped.
    /// </summary>
    private void AddLoadFolderProjection(ModProjectInspection result)
    {
        var document = result.LoadFolders;
        if (!document.Exists || document.Sections.Count == 0)
        {
            return;
        }

        var version = SelectedInstallation?.Model.Version;
        string[] activeIds = result.Metadata?.PackageId is { Length: > 0 } packageId ? [packageId] : [];
        var plan = LoadFoldersReader.Evaluate(document, version ?? GameVersion.Unknown, activeIds);

        var basis = activeIds.Length > 0
            ? $"only {activeIds[0]} is treated as active; RimPulse does not know the game's real mod list"
            : "no package ID is known for this mod, so conditional folders cannot be evaluated";
        ProjectDetails.Add(new DetailRow("Load folder basis", basis));

        if (plan.VersionText is null)
        {
            return;
        }

        ProjectDetails.Add(new DetailRow(
            $"Content folders ({plan.VersionText})",
            plan.Folders.Count == 0 ? "none" : string.Join("\n", plan.Folders.Select(DescribeFolder))));

        if (plan.Skipped.Count > 0)
        {
            var skipped = plan.Skipped.Select(entry => $"{entry.Path} — requires {entry.ConditionDisplay}");
            ProjectDetails.Add(new DetailRow("Conditional folders not loaded", string.Join("\n", skipped)));
        }

        foreach (var problem in plan.Problems)
        {
            ProjectProblems.Add(new ProblemRow(problem));
        }
    }

    private static string DescribeFolder(string path) => path == "/" ? "/ (mod root)" : path;

    /// <summary>Adds or refreshes the persisted list entry for a successfully inspected project.</summary>
    private void RememberProject(ModProjectInspection result)
    {
        if (!_configuration.ProjectPaths.Exists(path => PathComparer.Equals(path, result.RootPath)))
        {
            _configuration.ProjectPaths.Insert(0, result.RootPath);
            while (_configuration.ProjectPaths.Count > RimPulseConfiguration.MaxProjectPaths)
            {
                _configuration.ProjectPaths.RemoveAt(_configuration.ProjectPaths.Count - 1);
            }
        }

        _configuration.SelectedProjectPath = result.RootPath;

        var row = Projects.FirstOrDefault(candidate => PathComparer.Equals(candidate.Path, result.RootPath));
        if (row is null)
        {
            row = new ProjectRow(result.RootPath);
            Projects.Insert(0, row);
        }

        UpdateProjectRow(row, result);
        SelectProject(row);
        OnProjectListChanged();
    }

    private static void UpdateProjectRow(ProjectRow row, ModProjectInspection result)
    {
        row.IsMissing = !Directory.Exists(result.RootPath);

        if (result.Metadata is { } metadata)
        {
            row.Title = string.IsNullOrWhiteSpace(metadata.Name) ? row.Title : metadata.Name;
            row.Detail = metadata.PackageId is { Length: > 0 } packageId
                ? metadata.SupportedVersions.Count == 0
                    ? packageId
                    : $"{packageId} · {string.Join(", ", metadata.SupportedVersions)}"
                : "no package ID";
            return;
        }

        row.Detail = result.RootPath;
    }

    private void SelectProject(ProjectRow? row)
    {
        if (ReferenceEquals(_selectedProject, row))
        {
            return;
        }

        _suppressProjectSelection = true;
        try
        {
            SelectedProject = row;
        }
        finally
        {
            _suppressProjectSelection = false;
        }
    }

    private void PopulateProjects()
    {
        Projects.Clear();

        foreach (var path in _configuration.ProjectPaths)
        {
            Projects.Add(new ProjectRow(path) { IsMissing = !Directory.Exists(path) });
        }

        OnProjectListChanged();
    }

    private void OnProjectListChanged()
        => OnPropertiesChanged(nameof(HasProjects), nameof(HasNoProjects), nameof(ProjectCountText));

    private async Task ActivateSelectedProjectAsync(ProjectRow row)
    {
        try
        {
            await InspectProjectAsync(row.Path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HandleCommandException(ex);
        }
    }

    /// <summary>
    /// Discovers the mod's projects, resolves the game assemblies and compiles in dependency order. Runs off
    /// the UI thread; a build started for a project that is no longer selected is discarded.
    /// </summary>
    private async Task BuildSelectedProjectAsync()
    {
        if (IsBuilding || IsProjectBusy)
        {
            return;
        }

        var inspection = _lastProjectInspection;
        if (inspection is null)
        {
            BuildStatus = "Inspect a mod project before building.";
            return;
        }

        var installation = SelectedInstallation?.Model;
        if (installation is null || !installation.IsUsable)
        {
            BuildStatus = "Select a usable RimWorld installation — a mod is compiled against the real game assemblies.";
            _log.Warning("Build", BuildStatus);
            return;
        }

        IsBuilding = true;
        var sequence = Interlocked.Increment(ref _buildSequence);
        ClearBuildDiagnostics();
        BuildDetails.Clear();
        BuildStatus = "Building…";

        // Resolved once: the version folder that decides the output path must not change halfway through a build.
        var target = ResolveOutputDirectory(inspection.RootPath, installation);
        _log.Info(
            "Build",
            $"Building '{inspection.RootPath}' against {installation.DisplayName} ({installation.VersionDisplay}) " +
            $"into '{target.Path}'.");

        try
        {
            var suppressed = 0;

            void AddRow(BuildDiagnosticRow row)
            {
                // The list behind the copy button is never capped: a build that reports more than the panel shows
                // must still copy completely.
                _allBuildDiagnosticLines.Add(row.Display);

                if (BuildDiagnostics.Count >= MaxVisibleBuildDiagnostics)
                {
                    suppressed++;
                    return;
                }

                BuildDiagnostics.Add(row);
            }

            var outputDirectory = target.Path;
            var outcome = await Task.Run(() =>
            {
                var references = GameReferenceResolver.Resolve(installation);

                // The installation doubles as the fallback for a hint path that points somewhere this machine
                // does not have: a project written on another computer should still compile here.
                var searchDirectories = installation.ManagedAssemblyDirectory is { Length: > 0 } managedDirectory
                    ? new[] { managedDirectory }
                    : Array.Empty<string>();
                var buildProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Treat the selected installation as authoritative even when the project was authored on
                    // another machine or the process inherited a different RIMWORLD_DIR value.
                    ["RIMWORLD_DIR"] = installation.RootPath,
                    ["RimWorldDir"] = installation.RootPath,
                };

                if (installation.ManagedAssemblyDirectory is { Length: > 0 } selectedManagedDirectory)
                {
                    buildProperties["RIMWORLD_MANAGED_DIR"] = selectedManagedDirectory;
                    buildProperties["RimWorldManagedDir"] = selectedManagedDirectory;
                }

                var inputs = BuildInputDiscovery.Discover(inspection.RootPath, searchDirectories, buildProperties);
                var build = inputs.HasUnits
                    ? ModBuilder.Build(new ModBuildPlan(inputs.Units, references.AssemblyPaths, outputDirectory))
                    : null;

                // Read the produced assemblies back from metadata, on the same worker thread: this is what the
                // game would load, so it is worth showing, and it never loads the mod into RimPulse.
                AssemblyInspection[] inspections = build is { Succeeded: true }
                    ? build.OutputPaths.Select(AssemblyInspector.Inspect).ToArray()
                    : [];
                return (inputs, references, build, inspections);
            }).ConfigureAwait(true);

            // A newer build (or a different project) supersedes this result.
            if (_disposed || sequence != Interlocked.Read(ref _buildSequence))
            {
                return;
            }

            foreach (var problem in outcome.inputs.Problems)
            {
                AddRow(BuildDiagnosticRow.FromProblem(problem));
            }

            foreach (var problem in outcome.references.Problems)
            {
                AddRow(BuildDiagnosticRow.FromProblem(problem));
            }

            if (outcome.build is null)
            {
                BuildStatus = outcome.inputs.Problems.FirstOrDefault()
                              ?? outcome.references.Problems.FirstOrDefault()
                              ?? "There was nothing to build.";

                var blocked = BuildLogReport.Describe(
                    outcome.inputs,
                    outcome.references,
                    build: null,
                    referenceDirectory: installation.ManagedAssemblyDirectory);
                _log.Warning("Build", blocked.Message, blocked.Detail);
                RaiseBuildDiagnosticsChanged();
                return;
            }

            foreach (var unit in outcome.build.Units)
            {
                foreach (var diagnostic in unit.Compilation.Diagnostics)
                {
                    AddRow(BuildDiagnosticRow.FromDiagnostic(diagnostic, unit.AssemblyName));
                }
            }

            foreach (var problem in outcome.build.Problems)
            {
                AddRow(BuildDiagnosticRow.FromProblem(problem));
            }

            foreach (var assembly in outcome.inspections)
            {
                BuildDetails.Add(new DetailRow($"Assembly ({Path.GetFileName(assembly.AssemblyPath)})", assembly.Summary));

                if (assembly.References.Count > 0)
                {
                    BuildDetails.Add(new DetailRow("References", string.Join(", ", assembly.References)));
                }

                if (assembly.Types.Count > 0)
                {
                    var names = assembly.Types
                        .Take(40)
                        .Select(type => type.FullName)
                        .ToArray();
                    var more = assembly.Types.Count > names.Length || assembly.TypesTruncated
                        ? $"{Environment.NewLine}… and {assembly.Types.Count - names.Length} more"
                        : string.Empty;
                    BuildDetails.Add(new DetailRow("Types", string.Join(Environment.NewLine, names) + more));
                }

                foreach (var problem in assembly.Problems)
                {
                    AddRow(BuildDiagnosticRow.FromProblem(problem));
                }
            }

            if (suppressed > 0)
            {
                BuildDiagnostics.Add(BuildDiagnosticRow.FromProblem(
                    $"… {suppressed} more diagnostic(s) are not listed here but are copied by " +
                    "'Copy all errors'; the log file records the build inputs, its reference set and the first " +
                    "diagnostics of each severity."));
            }

            // The panel was cleared above, which is exactly when this is false: without raising it here the
            // diagnostics panel (and its copy button) never became visible.
            RaiseBuildDiagnosticsChanged();

            BuildStatus = outcome.build.Summary;

            // A build's summary is only a count ("1051 error(s)"), which is useless in a log file afterwards. The
            // detail block carries the inputs, the reference set and the first diagnostics of each severity, so a
            // failed build can be diagnosed from the log alone.
            var report = BuildLogReport.Describe(
                outcome.inputs,
                outcome.references,
                outcome.build,
                installation.ManagedAssemblyDirectory);

            if (outcome.build.Succeeded)
            {
                // The exact files that were written, first in the details: the assembly rows below only show the
                // file name, which does not say which folder the version rule picked.
                if (outcome.build.OutputPaths.Count > 0)
                {
                    BuildDetails.Insert(0, new DetailRow(
                        "Wrote",
                        string.Join(Environment.NewLine, outcome.build.OutputPaths)));
                }

                _log.Info("Build", report.Message, report.Detail);
            }
            else
            {
                _log.Warning("Build", report.Message, report.Detail);
            }
        }
        finally
        {
            IsBuilding = false;
        }
    }

    /// <summary>Clears the visible rows, the complete list and the copy feedback of the previous build.</summary>
    private void ClearBuildDiagnostics()
    {
        BuildDiagnostics.Clear();
        _allBuildDiagnosticLines.Clear();
        BuildCopyNotice = string.Empty;
        _copyNoticeTimer.Stop();
        RaiseBuildDiagnosticsChanged();
    }

    private void RaiseBuildDiagnosticsChanged()
    {
        OnPropertyChanged(nameof(HasBuildDiagnostics));
        CopyBuildDiagnosticsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Puts every diagnostic of the last build on the clipboard, prefixed by what it was built from. Losing the
    /// header would make a pasted error impossible to attribute to a project, an installation and a run.
    /// </summary>
    private async Task CopyBuildDiagnosticsAsync()
    {
        if (_allBuildDiagnosticLines.Count == 0)
        {
            BuildCopyNotice = "There are no build diagnostics to copy.";
            return;
        }

        var text = DescribeBuildForClipboard();
        var copied = await _clipboard.SetTextAsync(text).ConfigureAwait(true);

        if (copied)
        {
            BuildCopyNotice = _allBuildDiagnosticLines.Count == 1
                ? "Copied 1 line to the clipboard."
                : $"Copied {_allBuildDiagnosticLines.Count} lines to the clipboard.";

            _log.Debug("Build", $"Copied {_allBuildDiagnosticLines.Count} build diagnostic(s) to the clipboard.");
        }
        else
        {
            BuildCopyNotice = "The clipboard is not available; the diagnostics are in the log file.";
            _log.Warning("Build", "The clipboard was not available while copying the build diagnostics.");
        }

        _copyNoticeTimer.Stop();
        _copyNoticeTimer.Start();
    }

    private string DescribeBuildForClipboard()
    {
        var builder = new StringBuilder();

        builder.AppendLine("RimPulse build diagnostics");
        builder.Append("Project: ")
            .AppendLine(_lastProjectInspection?.RootPath ?? "no project inspected");

        builder.Append("Installation: ")
            .AppendLine(SelectedInstallation?.Model is { } installation
                ? $"{installation.DisplayName} ({installation.VersionDisplay})"
                : "none selected");

        builder.Append("Result: ").AppendLine(BuildStatus);

        // Which folder the DLLs went to was previously only visible in the build panel, so a pasted report could
        // not answer "where is the built mod?".
        builder.Append("Output: ")
            .AppendLine(_lastProjectInspection is { } inspected && SelectedInstallation?.Model is { IsUsable: true } targetInstallation
                ? ResolveOutputDirectory(inspected.RootPath, targetInstallation).Path
                : "not resolved (no usable installation selected)");

        builder.Append("Copied: ")
            .AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture));

        builder.Append("Log: ").AppendLine(LogFilePath);
        builder.AppendLine();

        foreach (var line in _allBuildDiagnosticLines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private async Task RemoveSelectedProjectAsync()
    {
        var row = SelectedProject;
        if (row is null || IsProjectBusy)
        {
            return;
        }

        IsProjectBusy = true;
        StopProjectMonitor();
        ProjectChanges.Clear();
        MonitorStatus = "Not monitoring a project.";

        try
        {
            _configuration.ProjectPaths.RemoveAll(path => PathComparer.Equals(path, row.Path));
            if (_configuration.SelectedProjectPath is { } selected && PathComparer.Equals(selected, row.Path))
            {
                _configuration.SelectedProjectPath = null;
            }

            Projects.Remove(row);
            SelectProject(null);
            _lastProjectInspection = null;
            ProjectDetails.Clear();
            ProjectProblems.Clear();
            ProjectSummary = "Choose a mod directory to inspect its metadata and structure.";
            OnProjectListChanged();
            _log.Info("Projects", $"Removed '{row.Path}' from the project list.");
            await SaveConfigurationAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsProjectBusy = false;
        }
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
        _copyNoticeTimer.Stop();
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

            LaunchGameCommand.RaiseCanExecuteChanged();
            RebuildSelectedDetails();

            // The load-folder projection depends on the selected game version.
            if (_lastProjectInspection is { } inspection)
            {
                DisplayProjectInspection(inspection);
            }

            OnPropertyChanged(nameof(BuildTargetText));

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

            PopulateProjects();

            foreach (var path in _configuration.ProjectPaths.Where(path => !Directory.Exists(path)).ToArray())
            {
                _log.Warning("Projects", $"The saved project '{path}' is not accessible; it is listed but not inspected.");
            }

            if (!string.IsNullOrWhiteSpace(_configuration.SelectedProjectPath))
            {
                SelectProject(Projects.FirstOrDefault(row => PathComparer.Equals(row.Path, _configuration.SelectedProjectPath!)));
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
