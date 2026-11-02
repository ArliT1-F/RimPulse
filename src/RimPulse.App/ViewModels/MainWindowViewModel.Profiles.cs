using System.Collections.ObjectModel;
using RimPulse.Core.Configuration;
using RimPulse.Core.Diagnostics;
using RimPulse.Core.Installations;
using RimPulse.Core.Platform;
using RimPulse.Core.Projects;
using RimPulse.Core.Runtime;

namespace RimPulse.App.ViewModels;

public sealed record ProfileChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Persistent profile editing and application preferences. Filesystem work stays off the UI thread.</summary>
public sealed partial class MainWindowViewModel
{
    private ModCatalog? _profileCatalog;
    private string? _profileCatalogInstallation;
    private bool _profileBusy, _profileDirty, _suppressProfileChoice;
    private ProfileChoice? _selectedSavedProfile;
    private long _profileCatalogSequence;

    public ProfileEditorViewModel Profile { get; private set; } = null!;
    public AppSettingsViewModel Settings { get; private set; } = null!;
    public ObservableCollection<ProfileChoice> SavedProfiles { get; } = [];
    public AsyncRelayCommand SaveProfileCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshProfileCatalogCommand { get; private set; } = null!;
    public AsyncRelayCommand NewProfileCommand { get; private set; } = null!;
    public AsyncRelayCommand RevertProfileCommand { get; private set; } = null!;
    public AsyncRelayCommand AddSelectedProjectToProfileCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveSettingsCommand { get; private set; } = null!;
    public bool CanEditProfile => !_runtimeRunning && !_runtimeSetupBusy && !IsProfileBusy && SelectedInstallation?.IsReady == true;
    public bool IsProfileBusy { get => _profileBusy; private set { if (SetProperty(ref _profileBusy, value)) { OnPropertyChanged(nameof(CanEditProfile)); RefreshProfileCommands(); RefreshRuntimeCommands(); } } }
    public bool IsProfileDirty { get => _profileDirty; set { if (SetProperty(ref _profileDirty, value)) OnPropertyChanged(nameof(ProfileDraftLabel)); } }
    public string ProfileDraftLabel => IsProfileDirty ? "Unsaved changes — Save profile, or Revert." : "Saved profiles are reused; your normal game configuration is untouched.";
    public string ProfileVersionNote => Profile.CanUseNativeQuickStart
        ? "1.6 native quick test uses RimWorld's normal world generation and a 250×250 map. It is NOT the planned minimal sandbox. Developer Mode exposes the game's own spawn tools and debug actions after startup."
        : "1.7 keeps the normal main-menu startup. Native quick test is disabled until its Waylith behavior is verified. Preference compatibility still needs a real 1.7 check.";
    public ProfileChoice? SelectedSavedProfile
    {
        get => _selectedSavedProfile;
        set
        {
            if (_suppressProfileChoice || ReferenceEquals(_selectedSavedProfile, value)) return;
            if (value is null || CanEditProfile is false || !GuardProfileDraft()) { RaisePreviousProfileChoice(); return; }
            var saved = _configuration.DevelopmentProfiles.FirstOrDefault(profile => profile.Id == value.Id);
            if (saved is null) return;
            _selectedSavedProfile = value; OnPropertyChanged(nameof(SelectedSavedProfile));
            _configuration.SelectedDevelopmentProfileId = value.Id;
            LoadProfile(saved);
            _ = SaveConfigurationAsync(CancellationToken.None);
        }
    }

    private void InitializeProfiles()
    {
        Profile = new ProfileEditorViewModel(() => IsProfileDirty = true);
        Settings = new AppSettingsViewModel();
        SaveProfileCommand = new AsyncRelayCommand(() => SaveProfileAsync(), () => CanEditProfile && Profile.HasProfile, HandleCommandException);
        RefreshProfileCatalogCommand = new AsyncRelayCommand(() => RefreshProfileCatalogAsync(), () => CanEditProfile, HandleCommandException);
        NewProfileCommand = new AsyncRelayCommand(NewProfileAsync, () => CanEditProfile, HandleCommandException);
        RevertProfileCommand = new AsyncRelayCommand(RevertProfileAsync, () => CanEditProfile && Profile.HasProfile, HandleCommandException);
        AddSelectedProjectToProfileCommand = new AsyncRelayCommand(AddSelectedProjectToProfileAsync,
            () => CanEditProfile && SelectedProject is not null, HandleCommandException);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !_disposed, HandleCommandException);
    }

    private void RefreshProfileCommands()
    {
        SaveProfileCommand?.RaiseCanExecuteChanged(); RefreshProfileCatalogCommand?.RaiseCanExecuteChanged();
        NewProfileCommand?.RaiseCanExecuteChanged(); RevertProfileCommand?.RaiseCanExecuteChanged();
        AddSelectedProjectToProfileCommand?.RaiseCanExecuteChanged(); SaveSettingsCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditProfile));
    }

    private bool GuardProfileDraft()
    {
        if (!IsProfileDirty) return true;
        Profile.Status = "Save profile or Revert before switching profiles or installations."; StatusMessage = Profile.Status;
        return false;
    }
    private void RaisePreviousProfileChoice()
    {
        _suppressProfileChoice = true;
        try { OnPropertyChanged(nameof(SelectedSavedProfile)); } finally { _suppressProfileChoice = false; }
    }
    private void LoadProfile(DevelopmentProfileSettings settings)
    {
        Profile.Load(settings.Clone(), PersistentDevelopmentProfile.DataDirectory(_paths, settings));
        IsProfileDirty = false;
        Profile.Status = "Saved settings apply at the next launch. Refresh the catalog after changing mods on disk.";
        OnPropertyChanged(nameof(ProfileVersionNote));
    }

    private void SelectProfileForInstallation()
    {
        var installation = SelectedInstallation?.Model;
        _suppressProfileChoice = true;
        try
        {
            SavedProfiles.Clear(); _selectedSavedProfile = null;
            if (installation is not { IsUsable: true, IsSupportedVersion: true })
            {
                Profile.Load(new DevelopmentProfileSettings(), "Select a supported installation first.");
                IsProfileDirty = false; OnPropertyChanged(nameof(SelectedSavedProfile)); return;
            }
            var matching = _configuration.DevelopmentProfiles.Where(profile =>
                PathComparer.Equals(InstallationProbe.NormalizePath(profile.InstallationPath), installation.RootPath) &&
                profile.GameVersion == installation.Version.ShortVersion).ToArray();
            foreach (var profile in matching) SavedProfiles.Add(new ProfileChoice(profile.Id, profile.Name));
            var selected = matching.FirstOrDefault(profile => profile.Id == _configuration.SelectedDevelopmentProfileId) ?? matching.FirstOrDefault();
            if (selected is not null)
            {
                _selectedSavedProfile = SavedProfiles.First(choice => choice.Id == selected.Id);
                LoadProfile(selected);
            }
            else
            {
                var draft = NewProfileSettings(installation);
                LoadProfile(draft); IsProfileDirty = true;
                Profile.Status = "New profile draft. Refresh the catalog, choose mods and settings, then Save profile.";
            }
            OnPropertyChanged(nameof(SelectedSavedProfile));
        }
        finally { _suppressProfileChoice = false; }
        if (installation is { IsUsable: true, IsSupportedVersion: true }) _ = RefreshProfileCatalogAsync();
    }

    private DevelopmentProfileSettings NewProfileSettings(RimWorldInstallation installation) => new()
    {
        Name = $"Development {installation.Version.ShortVersion}", InstallationPath = installation.RootPath,
        GameVersion = installation.Version.ShortVersion, ProjectPath = SelectedProject?.Path,
    };

    private async Task NewProfileAsync()
    {
        if (!CanEditProfile || !GuardProfileDraft() || SelectedInstallation?.Model is not { IsUsable: true, IsSupportedVersion: true } installation) return;
        // Persist the old choice before replacing it, so the selected-ID invariant survives a restart.
        var draft = NewProfileSettings(installation);
        _configuration.SelectedDevelopmentProfileId = null;
        LoadProfile(draft); _selectedSavedProfile = null; RaisePreviousProfileChoice(); IsProfileDirty = true;
        Profile.Status = "New unsaved profile. Existing save data is never overwritten by creating a profile.";
        await SaveConfigurationAsync(CancellationToken.None);
    }

    private Task RevertProfileAsync()
    {
        if (!CanEditProfile) return Task.CompletedTask;
        var saved = _configuration.DevelopmentProfiles.FirstOrDefault(profile => profile.Id == Profile.Id);
        LoadProfile(saved ?? NewProfileSettings(SelectedInstallation!.Model));
        _selectedSavedProfile = saved is null ? null : SavedProfiles.FirstOrDefault(choice => choice.Id == saved.Id);
        RaisePreviousProfileChoice();
        IsProfileDirty = saved is null;
        return Task.CompletedTask;
    }

    private async Task<bool> RefreshProfileCatalogAsync(bool partOfLaunch = false)
    {
        if (SelectedInstallation?.Model is not { IsUsable: true, IsSupportedVersion: true } installation || (!partOfLaunch && (_runtimeRunning || _runtimeSetupBusy)) || IsProfileBusy) return false;
        if (IsProfileDirty && _profileCatalogInstallation is not null &&
            !PathComparer.Equals(_profileCatalogInstallation, installation.RootPath))
        { GuardProfileDraft(); return false; }
        var sequence = ++_profileCatalogSequence;
        IsProfileBusy = true; Profile.Status = "Reading installed mods and DLC…";
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var platform = HostPlatformExtensions.Detect();
            var catalog = await Task.Run(() => ModCatalogReader.Read(installation, home, platform));
            if (_disposed || sequence != _profileCatalogSequence || SelectedInstallation?.Model.RootPath != installation.RootPath) return false;
            _profileCatalog = catalog; _profileCatalogInstallation = installation.RootPath;
            Profile.SetCatalog(catalog.Mods, catalog.Problems);
            var plan = ModLoadOrder.Resolve(catalog, Profile.ToSettings().ActiveMods, installation.Version, SelectedProjectPackageId());
            // Keep missing selections in the draft so validation cannot silently turn an incomplete profile green.
            foreach (var error in plan.Errors) _log.Warning("Profiles", error);
            Profile.Status = plan.Errors.Count == 0 ? "Catalog refreshed. Save profile to resolve dependencies and persist this load order." : string.Join(Environment.NewLine, plan.Errors);
            _log.Info("Profiles", $"Catalog refreshed: {catalog.Mods.Count} mods; {catalog.Problems.Count} discovery notes.");
            return true;
        }
        catch (Exception ex)
        {
            _profileCatalog = null; _profileCatalogInstallation = null;
            Profile.Status = "Could not read the mod catalog: " + ex.Message; _log.Error("Profiles", Profile.Status, ex);
            return false;
        }
        finally { if (!_disposed) IsProfileBusy = false; }
    }

    private string? SelectedProjectPackageId()
    {
        if (Profile.ProjectPath is null) return null;
        return _lastProjectInspection?.RootPath == Profile.ProjectPath ? _lastProjectInspection.Metadata?.PackageId : null;
    }

    private async Task<bool> SaveProfileAsync(bool partOfLaunch = false)
    {
        if ((!CanEditProfile && !partOfLaunch) || (!partOfLaunch && _runtimeRunning) || SelectedInstallation?.Model is not { IsUsable: true, IsSupportedVersion: true } installation) return false;
        if (_profileCatalog is null || !PathComparer.Equals(_profileCatalogInstallation, installation.RootPath))
        {
            Profile.Status = "Refresh the mod catalog before saving this profile."; return false;
        }
        try
        {
            var draft = Profile.ToSettings();
            if (draft.ProjectPath is not null)
            {
                var inspection = await Task.Run(() => ModProjectInspector.Inspect(draft.ProjectPath, installation.Version));
                if (string.IsNullOrWhiteSpace(inspection.Metadata?.PackageId)) throw new InvalidOperationException("The profile's development project no longer has a readable package ID.");
                if (!draft.ActiveMods.Contains(ModCatalogReader.Key(inspection.Metadata.PackageId))) draft.ActiveMods.Add(ModCatalogReader.Key(inspection.Metadata.PackageId));
            }
            var plan = ModLoadOrder.Resolve(_profileCatalog, draft.ActiveMods, installation.Version, null);
            Profile.ApplyPlan(plan);
            if (plan.Errors.Count > 0) { _log.Warning("Profiles", "Profile was not saved: " + string.Join(" ", plan.Errors)); return false; }
            draft.ActiveMods = plan.ActiveMods.ToList(); draft.Normalize();
            var index = _configuration.DevelopmentProfiles.FindIndex(profile => profile.Id == draft.Id);
            if (index >= 0) _configuration.DevelopmentProfiles[index] = draft;
            else
            {
                if (_configuration.DevelopmentProfiles.Count >= 30) throw new InvalidOperationException("The limit is 30 saved profiles. Reuse an existing profile.");
                _configuration.DevelopmentProfiles.Add(draft);
            }
            _configuration.SelectedDevelopmentProfileId = draft.Id;
            if (!await SaveConfigurationAsync(CancellationToken.None)) return false;
            _suppressProfileChoice = true;
            try
            {
                var choice = new ProfileChoice(draft.Id, draft.Name);
                var previous = SavedProfiles.FirstOrDefault(saved => saved.Id == draft.Id);
                if (previous is not null) SavedProfiles.Remove(previous);
                SavedProfiles.Add(choice); _selectedSavedProfile = choice;
                OnPropertyChanged(nameof(SelectedSavedProfile));
            }
            finally { _suppressProfileChoice = false; }
            Profile.Load(draft, PersistentDevelopmentProfile.DataDirectory(_paths, draft));
            IsProfileDirty = false;
            Profile.Status = $"Saved. {plan.ActiveMods.Count} mods will load at the next development launch." +
                (plan.Warnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, plan.Warnings) : string.Empty);
            _log.Info("Profiles", $"Saved profile '{draft.Name}' for {installation.Version.ShortVersion}; {plan.ActiveMods.Count} active mods.");
            return true;
        }
        catch (Exception ex)
        {
            Profile.Status = "Profile was not saved: " + ex.Message; _log.Warning("Profiles", Profile.Status); return false;
        }
    }

    private async Task AddSelectedProjectToProfileAsync()
    {
        if (!CanEditProfile || SelectedInstallation?.Model is not { } installation || SelectedProject?.Path is not { } path) return;
        IsProfileBusy = true;
        try
        {
            var link = await Task.Run(() => DevelopmentProjectLink.Link(installation, path));
            if (_disposed) return;
            var settings = Profile.ToSettings(); settings.ProjectPath = path;
            Profile.Load(settings, Profile.DataDirectory); IsProfileDirty = true;
            IsProfileBusy = false;
            await RefreshProfileCatalogAsync();
            var inspection = await Task.Run(() => ModProjectInspector.Inspect(path, installation.Version));
            var row = Profile.AvailableMods.FirstOrDefault(mod => mod.PackageId == ModCatalogReader.Key(inspection.Metadata?.PackageId ?? string.Empty));
            if (row is not null) Profile.SetEnabled(row, true);
            Profile.Status = $"Project linked at {link}; its declared dependencies will be included when you save. Save profile to keep it.";
        }
        catch (Exception ex) { Profile.Status = "Could not add project: " + ex.Message; _log.Warning("Profiles", Profile.Status); }
        finally { if (!_disposed) IsProfileBusy = false; }
    }

    /// <summary>Imports only into the currently selected saved profile; never overwrites an existing file.</summary>
    public async Task ImportProfileContentAsync(string sourcePath, string expectedProfileId)
    {
        if (!CanEditProfile || SelectedSavedProfile is null || SelectedSavedProfile.Id != expectedProfileId || Profile.Id != expectedProfileId)
        {
            Profile.Status = "Select a saved profile and close the game before importing. If you switched profiles, choose the file again.";
            return;
        }
        if (IsProfileDirty)
        {
            Profile.Status = "Save profile or Revert before importing a save or scenario.";
            return;
        }
        var saved = _configuration.DevelopmentProfiles.FirstOrDefault(profile => profile.Id == expectedProfileId);
        if (saved is null) { Profile.Status = "Save the profile before importing."; return; }
        IsProfileBusy = true;
        Profile.Status = "Copying the selected file into the development profile…";
        try
        {
            var destination = await Task.Run(() => ProfileContentImporter.Import(_paths, saved, sourcePath));
            if (_disposed) return;
            _log.Info("Profiles", "Imported profile content to " + destination);
            if (Profile.Id == expectedProfileId)
                Profile.Status = $"Copied to {destination}. Load it in RimWorld; compatibility and mod requirements are not checked. Disable native quick test to use the main menu.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            var message = "Import failed (existing files are never overwritten): " + ex.Message;
            if (!_disposed && Profile.Id == expectedProfileId) Profile.Status = message;
            _log.Warning("Profiles", message);
        }
        finally { if (!_disposed) IsProfileBusy = false; }
    }

    public void ReportProfileContentPickerError(Exception exception)
    {
        Profile.Status = "Could not select a save or scenario: " + exception.Message;
        _log.Warning("Profiles", Profile.Status);
    }

    private async Task SaveSettingsAsync()
    {
        Settings.ApplyTo(_configuration);
        if (await SaveConfigurationAsync(CancellationToken.None))
        {
            IsActivityLogVisible = Settings.ShowActivityLog;
            if (_log is RimPulse.Core.Diagnostics.DiagnosticLog log) log.MinimumLevel = Settings.MinimumLogLevel;
            Settings.Report("Saved. Build options apply to the next build; startup detection applies on the next app start.");
            _log.Info("Configuration", "Application settings saved through the GUI.");
        }
        else Settings.Report("Could not save settings. See Activity for the reason.");
    }
}
