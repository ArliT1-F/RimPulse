using Avalonia;

namespace RimPulse.App;

internal static class Program
{
    // Avalonia configuration; keep it in a method so the XAML previewer can reuse it.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
