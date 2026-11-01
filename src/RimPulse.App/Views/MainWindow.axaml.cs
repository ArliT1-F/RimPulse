using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RimPulse.App.ViewModels;

namespace RimPulse.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ActivityLog.CollectionChanged -= OnActivityLogChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;

        if (_viewModel is not null)
        {
            _viewModel.ActivityLog.CollectionChanged += OnActivityLogChanged;
        }
    }

    private async void OnBrowseProject(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsProjectBusy) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select a RimWorld mod directory",
                AllowMultiple = false,
            });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (path is not null) await _viewModel.InspectProjectAsync(path);
        }
        catch (Exception ex)
        {
            _viewModel.ReportProjectPickerError(ex);
        }
    }

    private void OnActivityLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            // Keep the newest diagnostic line visible without the user having to scroll.
            ActivityLogScroller.ScrollToEnd();
        }
    }
}
