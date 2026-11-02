using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using RimPulse.App.ViewModels;

namespace RimPulse.App.Views;

public partial class ProfilesTab : UserControl
{
    public ProfilesTab() => AvaloniaXamlLoader.Load(this);

    private async void OnImportProfileContent(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || !model.CanEditProfile) return;
        if (model.SelectedSavedProfile is not { } profile || model.IsProfileDirty)
        {
            model.Profile.Status = "Save the profile and close the game before importing a save or scenario.";
            return;
        }
        try
        {
            var topLevel = TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("No window is available for the file picker.");
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select a RimWorld save or scenario",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("RimWorld saves and scenarios") { Patterns = ["*.rws", "*.rsc"] }],
            });
            var selected = files.FirstOrDefault();
            if (selected is null) return;
            var path = selected.TryGetLocalPath();
            if (path is null) throw new InvalidOperationException("Select a file on a local filesystem.");
            await model.ImportProfileContentAsync(path, profile.Id);
        }
        catch (Exception ex) { model.ReportProfileContentPickerError(ex); }
    }
}
