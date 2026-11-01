namespace RimPulse.App.Services;

public readonly record struct LinkLaunchResult(bool Succeeded, string? Message)
{
    public static LinkLaunchResult Success { get; } = new(true, null);

    public static LinkLaunchResult Failure(string message) => new(false, message);
}

/// <summary>Opens a web link in the user's browser. Abstracted so view models never touch the OS directly.</summary>
public interface ILinkLauncher
{
    LinkLaunchResult Open(string url);
}

/// <summary>
/// Opens links through the platform shell (<c>xdg-open</c> on Linux, the default browser on Windows/macOS)
/// and refuses anything that is not an absolute http(s) URL, so a malformed catalog entry cannot turn into
/// a shell invocation.
/// </summary>
public sealed class SystemLinkLauncher : ILinkLauncher
{
    public LinkLaunchResult Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return LinkLaunchResult.Failure("Only absolute http(s) links can be opened.");
        }

        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });

            if (process is null)
            {
                return LinkLaunchResult.Failure("The operating system did not start a browser.");
            }

            process.Dispose();
            return LinkLaunchResult.Success;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException
                                       or PlatformNotSupportedException)
        {
            return LinkLaunchResult.Failure(ex.Message);
        }
    }
}
