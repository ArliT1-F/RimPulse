using System.Collections.Specialized;
using Avalonia.Controls;
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

    private void OnActivityLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            // Keep the newest diagnostic line visible without the user having to scroll.
            ActivityLogScroller.ScrollToEnd();
        }
    }
}
