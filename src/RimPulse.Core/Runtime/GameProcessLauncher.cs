using System.Diagnostics;
using RimPulse.Core.Installations;
using RimPulse.Core.Platform;

namespace RimPulse.Core.Runtime;

/// <summary>Optional per-process settings; neither environment variables nor arguments change global settings.</summary>
public sealed record GameLaunchOptions(string? SaveDataDirectory = null, IReadOnlyDictionary<string, string>? Environment = null, bool NativeQuickStart = false);

/// <summary>Starts the selected installation directly, without editing its normal mod or save configuration.</summary>
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
    public static Process Start(RimWorldInstallation installation, GameLaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var current = InstallationProbe.Probe(installation.RootPath, installation.Source);
        if (current.Version != installation.Version || current.Layout != installation.Layout)
            throw new InvalidOperationException("The installation changed since it was selected. Re-scan before launching.");

        var executable = ResolveExecutable(current, HostPlatformExtensions.Detect());
        if (options?.NativeQuickStart == true && (current.Version.Minor != 6 || current.Layout != InstallationLayout.UnityMono))
            throw new PlatformNotSupportedException("Native quick start is supported for RimWorld 1.6 only; its 1.7 equivalent has not been verified.");
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
            if (options?.SaveDataDirectory is { } directory)
            {
                if (!Path.IsPathFullyQualified(directory) || directory.Contains('='))
                    throw new ArgumentException("The separate save-data directory must be an absolute path without '='.", nameof(options));
                process.StartInfo.ArgumentList.Add("-savedatafolder=" + directory);
            }
            if (options?.NativeQuickStart == true) process.StartInfo.ArgumentList.Add("-quicktest");
            if (options?.Environment is { } environment)
                foreach (var (name, value) in environment) process.StartInfo.Environment[name] = value;
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
