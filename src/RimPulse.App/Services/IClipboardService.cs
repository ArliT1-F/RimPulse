using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace RimPulse.App.Services;

/// <summary>
/// Puts text on the system clipboard. Abstracted so a view model can offer "copy" without referencing a
/// window or any Avalonia control.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Replaces the clipboard contents with <paramref name="text"/>. Must be called on the UI thread, which is
    /// where a command runs. Returns false when there is no window yet or the platform refused the request.
    /// </summary>
    Task<bool> SetTextAsync(string text);
}

/// <summary>
/// The real clipboard of the running window. Failure is reported, never thrown: a build result the user is
/// trying to copy must not turn a copy attempt into an application error.
/// </summary>
public sealed class SystemClipboardService : IClipboardService
{
    public async Task<bool> SetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            // A caller off the UI thread is marshal-ed rather than failed: the copy is still the user's intent.
            return await Dispatcher.UIThread
                .InvokeAsync<bool>(() => SetTextAsync(text))
                .ConfigureAwait(true);
        }

        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or NotSupportedException
                                       or PlatformNotSupportedException
                                       or ObjectDisposedException)
        {
            return false;
        }
    }

    private static IClipboard? GetClipboard()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?
            .Clipboard;
}
