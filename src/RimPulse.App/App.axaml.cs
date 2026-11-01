using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using RimPulse.App.Services;
using RimPulse.App.Views;

namespace RimPulse.App;

public partial class App : Application
{
    private RimPulseServiceGraph? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                _services = RimPulseServiceGraph.Create();
                var viewModel = _services.CreateMainWindowViewModel();

                desktop.MainWindow = new MainWindow { DataContext = viewModel };
                desktop.Exit += (_, _) =>
                {
                    viewModel.Dispose();
                    _services?.Dispose();
                };

                // Fire and forget: InitializeAsync handles its own errors and never throws.
                _ = viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                // A broken configuration directory must not produce a silent crash on startup.
                Console.Error.WriteLine(ex);

                desktop.MainWindow = new Window
                {
                    Title = "RimPulse — startup failed",
                    Width = 760,
                    Height = 280,
                    Background = new SolidColorBrush(Color.Parse("#0D1015")),
                    // A SelectableTextBlock so the message itself can be copied out of the window.
                    Content = new SelectableTextBlock
                    {
                        Margin = new Thickness(28),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#E9EEF5")),
                        Text = "RimPulse could not start.\n\n" + ex.Message +
                               "\n\nCheck that your home directory and XDG configuration paths are writable, " +
                               "then start RimPulse again. The full error was written to stderr.",
                    },
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
