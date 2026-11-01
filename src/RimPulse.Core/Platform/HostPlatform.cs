namespace RimPulse.Core.Platform;

/// <summary>
/// The operating system RimPulse is running on. Passed explicitly (instead of calling
/// <see cref="OperatingSystem"/> everywhere) so path and detection logic can be unit tested for
/// every platform from any platform, and so Windows support is designed in from the start.
/// </summary>
public enum HostPlatform
{
    Linux = 0,
    Windows = 1,
    MacOS = 2,
}

public static class HostPlatformExtensions
{
    public static HostPlatform Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return HostPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return HostPlatform.MacOS;
        }

        return HostPlatform.Linux;
    }

    /// <summary>
    /// Whether the platform's default file system treats <c>RimWorld</c> and <c>rimworld</c>
    /// as the same path. Used for de-duplicating detected installations.
    /// </summary>
    public static bool IsCaseInsensitiveFileSystem(this HostPlatform platform)
        => platform is HostPlatform.Windows or HostPlatform.MacOS;

    public static string ToDisplayName(this HostPlatform platform) => platform switch
    {
        HostPlatform.Linux => "Linux",
        HostPlatform.Windows => "Windows",
        HostPlatform.MacOS => "macOS",
        _ => "Unknown",
    };
}
