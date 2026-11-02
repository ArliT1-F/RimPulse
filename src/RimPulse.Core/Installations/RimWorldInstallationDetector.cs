using RimPulse.Core.Diagnostics;
using RimPulse.Core.Installations.Steam;
using RimPulse.Core.Platform;

namespace RimPulse.Core.Installations;

/// <summary>
/// Detects RimWorld installations from Steam libraries, GOG-style folders, the <c>RIMWORLD_DIR</c>
/// environment variable and any paths the user added by hand.
/// </summary>
public sealed class RimWorldInstallationDetector : IRimWorldInstallationDetector
{
    private readonly InstallationDetectorOptions _options;
    private readonly IDiagnosticLog _log;

    public RimWorldInstallationDetector(InstallationDetectorOptions options, IDiagnosticLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullDiagnosticLog.Instance;
    }

    public Task<IReadOnlyList<RimWorldInstallation>> DetectAsync(
        IEnumerable<string>? additionalRootPaths = null,
        CancellationToken cancellationToken = default,
        bool scanWellKnownLocations = true)
    {
        var extraRoots = additionalRootPaths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray() ?? [];

        // Discovery touches the file system, so it never runs on the UI thread.
        return Task.Run<IReadOnlyList<RimWorldInstallation>>(
            () => Detect(extraRoots, cancellationToken, scanWellKnownLocations),
            cancellationToken);
    }

    /// <summary>Synchronous discovery, exposed for tests and for callers that already run off-thread.</summary>
    public IReadOnlyList<RimWorldInstallation> Detect(
        IEnumerable<string>? additionalRootPaths = null,
        CancellationToken cancellationToken = default,
        bool scanWellKnownLocations = true)
    {
        var candidates = new List<InstallationCandidate>();

        if (additionalRootPaths is not null)
        {
            foreach (var rootPath in additionalRootPaths)
            {
                if (!string.IsNullOrWhiteSpace(rootPath))
                {
                    candidates.Add(new InstallationCandidate(rootPath, InstallationSource.Manual));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.EnvironmentRimWorldDirectory))
        {
            _log.Debug(
                "Installation detection",
                $"Using RIMWORLD_DIR from the environment: '{_options.EnvironmentRimWorldDirectory}'.");
            candidates.Add(new InstallationCandidate(_options.EnvironmentRimWorldDirectory!, InstallationSource.EnvironmentVariable));
        }

        if (scanWellKnownLocations && _options.ScanSteamLibraries)
        {
            CollectSteamCandidates(candidates, cancellationToken);
        }

        if (scanWellKnownLocations && _options.ScanGogDirectories)
        {
            CollectGogCandidates(candidates, cancellationToken);
        }

        var results = ProbeCandidates(candidates, cancellationToken);

        _log.Info(
            "Installation detection",
            $"Installation scan finished: {candidates.Count} candidate path(s), {results.Count} reported installation(s).");

        return results;
    }

    private IReadOnlyList<RimWorldInstallation> ProbeCandidates(
        List<InstallationCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var comparer = _options.Platform.IsCaseInsensitiveFileSystem()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var results = new Dictionary<string, RimWorldInstallation>(comparer);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RimWorldInstallation installation;

            try
            {
                installation = InstallationProbe.Probe(candidate.Path, candidate.Source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning("Installation detection", $"Could not inspect '{candidate.Path}': {ex.Message}");
                continue;
            }

            var key = GetDeduplicationKey(installation.RootPath);

            if (results.TryGetValue(key, out var existing))
            {
                // A path the user added explicitly wins over the same path found automatically,
                // because the user's intent is the more useful label in the UI.
                if (candidate.Source == InstallationSource.Manual && existing.Source != InstallationSource.Manual)
                {
                    results[key] = installation;
                }

                continue;
            }

            if (!installation.IsUsable && candidate.Source is InstallationSource.Steam or InstallationSource.Gog)
            {
                _log.Debug(
                    "Installation detection",
                    $"Skipping auto-detected directory that is not a usable RimWorld installation: '{installation.RootPath}'.",
                    string.Join(Environment.NewLine, installation.Problems));
                continue;
            }

            results[key] = installation;
        }

        return results.Values
            .OrderByDescending(installation => installation.IsUsable)
            .ThenByDescending(installation => installation.IsSupportedVersion)
            .ThenByDescending(installation => installation.Version)
            .ThenBy(installation => installation.RootPath, comparer)
            .ToArray();
    }

    private void CollectSteamCandidates(List<InstallationCandidate> candidates, CancellationToken cancellationToken)
    {
        var homeDirectory = _options.HomeDirectory;

        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            _log.Debug("Installation detection", "No home directory is known; skipping Steam discovery.");
            return;
        }

        foreach (var steamRoot in SteamLibraryReader.GetSteamRootCandidates(homeDirectory, _options.Platform))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(steamRoot))
            {
                continue;
            }

            IReadOnlyList<string> libraries;

            try
            {
                libraries = SteamLibraryReader.ReadLibraryFolders(steamRoot);
            }
            catch (Exception ex) when (ex is ValveKeyValuesFormatException or IOException or UnauthorizedAccessException)
            {
                _log.Warning(
                    "Installation detection",
                    $"Could not read the Steam library list in '{steamRoot}': {ex.Message}");
                continue;
            }

            _log.Debug("Installation detection", $"Steam root '{steamRoot}' lists {libraries.Count} library folder(s).");

            foreach (var library in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(library))
                {
                    _log.Debug("Installation detection", $"Steam library folder does not exist: '{library}'.");
                    continue;
                }

                IReadOnlyList<string> installRoots;

                try
                {
                    installRoots = SteamLibraryReader.FindRimWorldInstallRoots(library);
                }
                catch (Exception ex) when (ex is ValveKeyValuesFormatException or IOException or UnauthorizedAccessException)
                {
                    _log.Warning(
                        "Installation detection",
                        $"Could not read the RimWorld Steam manifest in '{library}': {ex.Message}");
                    continue;
                }

                foreach (var installRoot in installRoots)
                {
                    if (Directory.Exists(installRoot))
                    {
                        candidates.Add(new InstallationCandidate(installRoot, InstallationSource.Steam));
                    }
                }
            }
        }
    }

    private void CollectGogCandidates(List<InstallationCandidate> candidates, CancellationToken cancellationToken)
    {
        var homeDirectory = _options.HomeDirectory;

        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return;
        }

        foreach (var folderName in _options.GogParentFolderNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parentDirectory = Path.Combine(homeDirectory, folderName);

            if (!Directory.Exists(parentDirectory))
            {
                continue;
            }

            string[] directories;

            try
            {
                directories = Directory.GetDirectories(parentDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning("Installation detection", $"Could not list '{parentDirectory}': {ex.Message}");
                continue;
            }

            foreach (var directory in directories)
            {
                if (LooksLikeRimWorldRoot(directory))
                {
                    candidates.Add(new InstallationCandidate(directory, InstallationSource.Gog));
                }
            }
        }
    }

    /// <summary>Cheap structural check so GOG folders full of other games are not probed in depth.</summary>
    private static bool LooksLikeRimWorldRoot(string directory)
        => File.Exists(Path.Combine(directory, VersionFileReader.VersionFileName))
           || File.Exists(Path.Combine(directory, "Assembly-CSharp.dll"))
           || File.Exists(Path.Combine(directory, "Waylith.dll"))
           || Directory.Exists(Path.Combine(directory, "RimWorldLinux_Data"))
           || Directory.Exists(Path.Combine(directory, "RimWorldWin64_Data"));

    private static string GetDeduplicationKey(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return string.Empty;
        }

        try
        {
            // Steam libraries are frequently symlinked; dedupe on the real target.
            var directory = new DirectoryInfo(rootPath);
            var target = directory.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? directory.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return rootPath;
        }
    }

    private readonly record struct InstallationCandidate(string Path, InstallationSource Source);
}
