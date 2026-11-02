using System.Diagnostics;
using RimPulse.Core.Installations;
using RimPulse.Core.Platform;

namespace RimPulse.Core.Runtime;

/// <summary>Starts the selected installation directly, without changing its mod or save configuration.</summary>
public static class GameProcessLauncher
{
    /// <summary>Resolve only executable names documented for the Linux versions of the game.</summary>
    public static string ResolveExecutable(RimWorldInstallation installation, HostPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!installation.IsUsable || !installation.IsSupportedVersion)
            throw new InvalidOperationException("Select a complete installation with a confirmed supported version.");

        if (platform != HostPlatform.Linux)
            throw new PlatformNotSupportedException("Direct game launching is currently supported on Linux only.");

        var name = (installation.Version.Minor, installation.Layout) switch
        {
            (6, InstallationLayout.UnityMono) => "RimWorldLinux",
            (7, InstallationLayout.Waylith) => "RimWorld",
            _ => throw new InvalidOperationException("The game version and installation layout do not match.")
        };
        var executable = Path.Combine(installation.RootPath, name);
        if (!File.Exists(executable))
            throw new FileNotFoundException($"Game executable not found: '{executable}'.", executable);
        return executable;
    }

    /// <summary>Rechecks the installation on disk and returns a handle to the directly launched process.</summary>
    public static Process Start(RimWorldInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var current = InstallationProbe.Probe(installation.RootPath, installation.Source);
        if (current.Version.Major != installation.Version.Major || current.Version.Minor != installation.Version.Minor ||
            current.Layout != installation.Layout)
            throw new InvalidOperationException("The installation changed since it was selected. Re-scan before launching.");

        var executable = ResolveExecutable(current, HostPlatformExtensions.Detect());
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = current.RootPath,
                UseShellExecute = false,
            },
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The game process could not be started.");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}
