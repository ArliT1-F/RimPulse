using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using RimPulse.Core.Diagnostics;

namespace RimPulse.App.Converters;

/// <summary>
/// Colours a log level label. Returned brushes are cached so the log list does not allocate brushes
/// while scrolling.
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly IBrush DebugBrush = new SolidColorBrush(Color.Parse("#68758A"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#8A97A8"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F0C674"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#F07A7E"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            LogLevel.Warning => WarningBrush,
            LogLevel.Error => ErrorBrush,
            LogLevel.Info => InfoBrush,
            _ => DebugBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("The log level brush converter is one-way only.");
}
