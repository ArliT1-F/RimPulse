using System.Collections.ObjectModel;
using System.Globalization;
using RimPulse.Core.Configuration;
using RimPulse.Core.Runtime;

namespace RimPulse.App.ViewModels;

/// <summary>An installed mod with an explicit selection checkbox; never an invented or downloadable entry.</summary>
public sealed class ProfileModRow(InstalledMod mod, bool enabled, Action<ProfileModRow>? changed = null) : ObservableObject
{
    public InstalledMod Model { get; } = mod;
    public string PackageId => ModCatalogReader.Key(Model.PackageId);
    public string Name => Model.Name;
    public string Detail => $"{Model.SourceLabel} · {Model.PackageId} · {Model.RootPath}";
    public bool Required => Model.IsCore || PackageId == "rimpulse.companion";
    private bool _enabled = enabled;
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, Required || value)) changed?.Invoke(this);
        }
    }
}

/// <summary>Editable draft, separate from the saved configuration until Save profile or Save & launch succeeds.</summary>
public sealed class ProfileEditorViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _loading;
    private string _name = "Development", _search = string.Empty, _status = "Select an installation and refresh the mod catalog.";
    private bool _developerMode = true, _pauseOnLoad = true, _runInBackground = true, _verboseLogging;
    private bool _resetModsOnCrash, _openDeveloperPalette, _nativeQuickStart, _fullscreen;
    private string _width = "1280", _height = "720", _scale = "1";
    private ProfileModRow? _selectedAvailable, _selectedActive;

    public ProfileEditorViewModel(Action changed)
    {
        _changed = changed;
        ToggleModCommand = new RelayCommand(() =>
        {
            if (SelectedAvailable is { Required: false } row) SetEnabled(row, !row.IsEnabled);
        }, () => SelectedAvailable is { Required: false });
        MoveUpCommand = new RelayCommand(() => Move(-1), () => SelectedActive is { Required: false } && ActiveMods.IndexOf(SelectedActive) > 1);
        MoveDownCommand = new RelayCommand(() => Move(1), () => SelectedActive is not null && ActiveMods.IndexOf(SelectedActive) < ActiveMods.Count - 1);
    }

    public string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public string InstallationPath { get; private set; } = string.Empty;
    public string GameVersion { get; private set; } = string.Empty;
    public string? ProjectPath { get; private set; }
    private readonly HashSet<string> _unresolvedMods = new(StringComparer.Ordinal);
    public ObservableCollection<ProfileModRow> AvailableMods { get; } = [];
    public ObservableCollection<ProfileModRow> FilteredMods { get; } = [];
    public ObservableCollection<ProfileModRow> ActiveMods { get; } = [];
    public RelayCommand ToggleModCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public string DataDirectory { get; private set; } = string.Empty;
    public string CatalogStatus { get; private set; } = string.Empty;
    public bool HasProfile => InstallationPath.Length > 0;

    public string Name { get => _name; set { if (SetProperty(ref _name, value ?? string.Empty)) Changed(); } }
    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value ?? string.Empty)) Filter(); }
    }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public ProfileModRow? SelectedAvailable
    {
        get => _selectedAvailable;
        set { if (SetProperty(ref _selectedAvailable, value)) ToggleModCommand.RaiseCanExecuteChanged(); }
    }
    public ProfileModRow? SelectedActive
    {
        get => _selectedActive;
        set { if (SetProperty(ref _selectedActive, value)) RaiseMoveStates(); }
    }
    public bool DeveloperMode { get => _developerMode; set { if (SetProperty(ref _developerMode, value)) { OnPropertyChanged(nameof(CanOpenDeveloperPalette)); Changed(); } } }
    public bool PauseOnLoad { get => _pauseOnLoad; set { if (SetProperty(ref _pauseOnLoad, value)) Changed(); } }
    public bool RunInBackground { get => _runInBackground; set { if (SetProperty(ref _runInBackground, value)) Changed(); } }
    public bool VerboseLogging { get => _verboseLogging; set { if (SetProperty(ref _verboseLogging, value)) Changed(); } }
    public bool ResetModsOnCrash { get => _resetModsOnCrash; set { if (SetProperty(ref _resetModsOnCrash, value)) Changed(); } }
    public bool OpenDeveloperPalette { get => _openDeveloperPalette; set { if (SetProperty(ref _openDeveloperPalette, value)) Changed(); } }
    public bool NativeQuickStart { get => _nativeQuickStart; set { if (SetProperty(ref _nativeQuickStart, value)) Changed(); } }
    public bool CanUseNativeQuickStart => GameVersion == "1.6";
    public bool CanOpenDeveloperPalette => DeveloperMode;
    public bool Fullscreen { get => _fullscreen; set { if (SetProperty(ref _fullscreen, value)) Changed(); } }
    public string Width { get => _width; set { if (SetProperty(ref _width, value ?? string.Empty)) Changed(); } }
    public string Height { get => _height; set { if (SetProperty(ref _height, value ?? string.Empty)) Changed(); } }
    public string Scale { get => _scale; set { if (SetProperty(ref _scale, value ?? string.Empty)) Changed(); } }

    public void Load(DevelopmentProfileSettings profile, string dataDirectory)
    {
        _loading = true;
        try
        {
            Id = profile.Id; InstallationPath = profile.InstallationPath; GameVersion = profile.GameVersion; ProjectPath = profile.ProjectPath;
            DataDirectory = dataDirectory; Name = profile.Name;
            var game = profile.Game;
            DeveloperMode = game.DeveloperMode; PauseOnLoad = game.PauseOnLoad; RunInBackground = game.RunInBackground;
            VerboseLogging = game.VerboseLogging; ResetModsOnCrash = game.ResetModsOnCrash;
            OpenDeveloperPalette = game.OpenDeveloperPalette; NativeQuickStart = game.NativeQuickStart && CanUseNativeQuickStart;
            Fullscreen = game.Fullscreen; Width = game.ScreenWidth.ToString(CultureInfo.InvariantCulture);
            Height = game.ScreenHeight.ToString(CultureInfo.InvariantCulture); Scale = game.UIScale.ToString(CultureInfo.InvariantCulture);
            var enabled = profile.ActiveMods.Select(ModCatalogReader.Key).ToHashSet(StringComparer.Ordinal);
            _unresolvedMods.Clear();
            foreach (var id in enabled.Except(AvailableMods.Select(row => row.PackageId))) _unresolvedMods.Add(id);
            foreach (var row in AvailableMods) row.IsEnabled = row.Required || enabled.Contains(row.PackageId);
            RebuildActive();
            OnPropertiesChanged(nameof(HasProfile), nameof(DataDirectory), nameof(CanUseNativeQuickStart));
        }
        finally { _loading = false; }
    }

    public void SetCatalog(IReadOnlyList<InstalledMod> mods, IReadOnlyList<string> problems)
    {
        var enabled = ActiveMods.Select(row => row.PackageId).Concat(_unresolvedMods).Append("ludeon.rimworld").Append("rimpulse.companion").ToHashSet(StringComparer.Ordinal);
        _unresolvedMods.IntersectWith(enabled);
        foreach (var id in enabled.Except(mods.Select(mod => ModCatalogReader.Key(mod.PackageId)))) _unresolvedMods.Add(id);
        _loading = true;
        try
        {
            AvailableMods.Clear();
            foreach (var mod in mods) AvailableMods.Add(new ProfileModRow(mod, enabled.Contains(ModCatalogReader.Key(mod.PackageId)), row => SetEnabled(row, row.IsEnabled)));
        }
        finally { _loading = false; }
        CatalogStatus = $"{mods.Count} installed mods discovered, including {mods.Count(mod => mod.IsExpansion)} DLC. " +
            (problems.Count == 0 ? "Only downloaded Workshop mods are listed." : string.Join(" ", problems));
        OnPropertyChanged(nameof(CatalogStatus));
        Filter(); RebuildActive();
    }

    public void SetEnabled(ProfileModRow row, bool enabled)
    {
        if (row.Required || _loading) return;
        row.IsEnabled = enabled;
        RebuildActive(); Changed();
    }

    public DevelopmentProfileSettings ToSettings()
    {
        if (!int.TryParse(Width, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(Height, NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            !double.TryParse(Scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
            throw new ArgumentException("Enter whole numbers for width and height, and a numeric UI scale such as 1 or 1.25.");
        var game = new DevelopmentGameSettings
        {
            DeveloperMode = DeveloperMode, PauseOnLoad = PauseOnLoad, RunInBackground = RunInBackground,
            VerboseLogging = VerboseLogging, ResetModsOnCrash = ResetModsOnCrash,
            OpenDeveloperPalette = OpenDeveloperPalette, NativeQuickStart = NativeQuickStart && CanUseNativeQuickStart,
            Fullscreen = Fullscreen, ScreenWidth = width, ScreenHeight = height, UIScale = scale,
        };
        if (!game.HasValidDisplay) throw new ArgumentException("Width must be 640–7680, height 480–4320 and UI scale 0.5–3.");
        return new DevelopmentProfileSettings
        {
            Id = Id, Name = Name, InstallationPath = InstallationPath, GameVersion = GameVersion, ProjectPath = ProjectPath,
            ActiveMods = ActiveMods.Select(row => row.PackageId).Concat(_unresolvedMods).Distinct(StringComparer.Ordinal).ToList(), Game = game,
        };
    }

    public void ApplyPlan(ModLoadPlan plan)
    {
        _loading = true;
        try
        {
            var ids = plan.ActiveMods.Select(ModCatalogReader.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var row in AvailableMods) row.IsEnabled = ids.Contains(row.PackageId);
            _unresolvedMods.Clear();
            foreach (var id in ids.Except(AvailableMods.Select(row => row.PackageId))) _unresolvedMods.Add(id);
            RebuildActive();
            // Show the resolved order, not the pre-dependency draft order.
            var position = plan.ActiveMods.Select(ModCatalogReader.Key).Select((id, index) => (id, index))
                .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
            for (var target = 0; target < ActiveMods.Count; target++)
            {
                var current = ActiveMods.Select((row, index) => (row, index))
                    .Skip(target).MinBy(item => position[item.row.PackageId]);
                ActiveMods.Move(current.index, target);
            }
            RaiseMoveStates();
            Status = plan.Errors.Count > 0 ? string.Join(Environment.NewLine, plan.Errors) :
                $"Ready to save: {plan.ActiveMods.Count} mods, including required dependencies." +
                (plan.Warnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, plan.Warnings) : string.Empty);
        }
        finally { _loading = false; }
    }

    private void RebuildActive()
    {
        var previous = SelectedActive?.PackageId;
        var order = ActiveMods.Select(row => row.PackageId).ToList();
        var rows = AvailableMods.Where(row => row.IsEnabled).OrderBy(row => row.Required ? row.PackageId == "ludeon.rimworld" ? -1 : 0 : 1)
            .ThenBy(row => order.IndexOf(row.PackageId) is var index && index >= 0 ? index : int.MaxValue).ToList();
        ActiveMods.Clear(); foreach (var row in rows) ActiveMods.Add(row);
        SelectedActive = rows.FirstOrDefault(row => row.PackageId == previous) ?? rows.FirstOrDefault();
        RaiseMoveStates();
    }
    private void Move(int direction)
    {
        if (SelectedActive is not { } row) return;
        var index = ActiveMods.IndexOf(row); var next = index + direction;
        if (index < 0 || next < 1 || next >= ActiveMods.Count || ActiveMods[next].Required) return;
        ActiveMods.Move(index, next); SelectedActive = row; RaiseMoveStates(); Changed();
    }
    private void RaiseMoveStates() { MoveUpCommand.RaiseCanExecuteChanged(); MoveDownCommand.RaiseCanExecuteChanged(); }
    private void Filter()
    {
        FilteredMods.Clear();
        foreach (var row in AvailableMods.Where(row => string.IsNullOrWhiteSpace(Search) ||
            (row.Name + " " + row.Detail).Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))) FilteredMods.Add(row);
    }
    private void Changed() { if (!_loading) _changed(); }
}
