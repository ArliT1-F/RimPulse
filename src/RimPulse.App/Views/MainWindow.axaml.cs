using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RimPulse.App.ViewModels;

namespace RimPulse.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Every label is a SelectableTextBlock, and that control marks a pointer press as handled to start a
        // text selection - which stops the ListBoxItem underneath it from ever seeing the click, so a row could
        // no longer be selected by clicking its text. A tunnelling handler runs before the label, so the row
        // still responds to a click anywhere on it and the text stays selectable.
        AddHandler(
            PointerPressedEvent,
            new EventHandler<PointerPressedEventArgs>(OnPreviewPointerPressed),
            RoutingStrategies.Tunnel);
    }

    private static void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            return;
        }

        for (var current = e.Source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is ListBoxItem item)
            {
                // The same thing ListBoxItem does for an unhandled click; a single-selection list clears the
                // previous row itself.
                item.IsSelected = true;
                return;
            }
        }
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
