namespace RimPulse.Core.Build;

/// <summary>
/// The outcome of looking one <c>PackageReference</c> up in the local NuGet package cache: the assemblies to
/// compile against, or (when nothing was found) the reason, phrased for a mod author.
/// </summary>
public sealed record NuGetPackageResolution(string PackageId, string Version, IReadOnlyList<string> AssemblyPaths, string? Problem)
{
    public bool Resolved => AssemblyPaths.Count > 0;
}

/// <summary>
/// Resolves <c>PackageReference</c> items against packages that are <b>already restored</b> on this machine.
/// </summary>
/// <remarks>
/// <para>
/// RimPulse does not run NuGet restore: that needs the .NET SDK, a package source and network access, none of
/// which a mod build may assume. But it can read the global packages folder (<c>NUGET_PACKAGES</c>, else
/// <c>~/.nuget/packages</c>), where a package is laid out as
/// <c>&lt;id&gt;/&lt;version&gt;/lib/&lt;tfm&gt;/*.dll</c> (or <c>ref/…</c> for reference-only assemblies).
/// A package the author has restored by opening the project in their editor therefore compiles, and a package
/// that has not been restored is reported with the command that would fix it.
/// </para>
/// <para>
/// Only a single exact version is understood. A floating version (<c>2.*</c>) or a range (<c>[2.0,3.0)</c>) is
/// reported rather than guessed at, and central package management (<c>Directory.Packages.props</c>) is not
/// evaluated.
/// </para>
/// </remarks>
public static class NuGetPackageResolver
{
    /// <summary>
    /// Frameworks a .NET Framework project may fall back to, in preference order. Only .NET Standard assets are
    /// usable from <c>net4x</c>: <c>netstandard2.1</c> and modern .NET assemblies are not.
    /// </summary>
    private static readonly string[] NetFrameworkFallbacks = ["netstandard2.0", "netstandard1.6", "netstandard1.3"];

    /// <summary>
    /// Frameworks a modern .NET project may fall back to, in preference order. A .NET Framework assembly is never
    /// offered to a <c>net5.0</c>+ project.
    /// </summary>
    private static readonly string[] ModernFallbacks = ["netstandard2.1", "netstandard2.0", "netcoreapp3.1"];

    /// <summary>Frameworks used when the project declares no recognisable target framework.</summary>
    private static readonly string[] UnknownFallbacks = ["netstandard2.0", "netstandard2.1"];

    /// <summary>The folder NuGet extracts packages into, or null when it cannot be determined.</summary>
    public static string? GlobalPackagesFolder
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".nuget", "packages");
        }
    }

    /// <param name="packagesFolder">
    /// The packages folder to read; defaults to <see cref="GlobalPackagesFolder"/>. Passing it explicitly keeps
    /// tests away from the machine's real NuGet cache.
    /// </param>
    public static NuGetPackageResolution Resolve(
        string packageId,
        string? version,
        string? targetFramework,
        string? packagesFolder = null)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return new NuGetPackageResolution(packageId ?? string.Empty, version ?? string.Empty, [], "A package reference declares no id.");
        }

        var trimmedVersion = (version ?? string.Empty).Trim();

        if (LooksFloating(trimmedVersion))
        {
            return Unresolved(
                packageId,
                trimmedVersion,
                $"the package reference '{packageId} {trimmedVersion}' uses a floating version or a range, which RimPulse does not evaluate.");
        }

        if (trimmedVersion.Length == 0)
        {
            return Unresolved(
                packageId,
                trimmedVersion,
                $"the package reference '{packageId}' declares no version (central package management is not evaluated).");
        }

        packagesFolder ??= GlobalPackagesFolder;
        if (packagesFolder is null)
        {
            return Unresolved(packageId, trimmedVersion, $"the NuGet global packages folder could not be determined, so '{packageId}' was not referenced.");
        }

        var packageDirectory = FindPackageDirectory(packagesFolder, packageId, trimmedVersion);
        if (packageDirectory is null)
        {
            return Unresolved(
                packageId,
                trimmedVersion,
                $"the package '{packageId} {trimmedVersion}' is not in the NuGet cache ('{packagesFolder}'); restore it " +
                $"once (dotnet restore, or open the project in your editor) or reference the assembly with a <HintPath>.");
        }

        var frameworkFolders = CandidateFrameworkFolders(packageDirectory);
        if (frameworkFolders.Count == 0)
        {
            return Unresolved(
                packageId,
                trimmedVersion,
                $"the package '{packageId} {trimmedVersion}' ships no lib/ or ref/ folder, so it provides no assembly to compile against.");
        }

        var chosen = ChooseFrameworkFolder(frameworkFolders, targetFramework);
        if (chosen is null)
        {
            return Unresolved(
                packageId,
                trimmedVersion,
                $"the package '{packageId} {trimmedVersion}' has assemblies for {string.Join(", ", frameworkFolders.Select(folder => folder.Label))} " +
                $"but none is compatible with the project's target framework ('{targetFramework ?? "unspecified"}').");
        }

        string[] assemblies;

        try
        {
            assemblies = Directory.GetFiles(chosen, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unresolved(packageId, trimmedVersion, $"the package folder '{chosen}' could not be read: {ex.Message}");
        }

        if (assemblies.Length == 0)
        {
            return Unresolved(
                packageId,
                trimmedVersion,
                $"the package '{packageId} {trimmedVersion}' has no .dll in '{Path.GetFileName(Path.GetDirectoryName(chosen))}/{Path.GetFileName(chosen)}'.");
        }

        return new NuGetPackageResolution(
            packageId,
            trimmedVersion,
            assemblies.Order(StringComparer.Ordinal).ToArray(),
            null);
    }

    private static NuGetPackageResolution Unresolved(string packageId, string version, string problem)
        => new(packageId, version, [], problem);

    private static bool LooksFloating(string version)
        => version.Contains('*')
           || version.Contains('[')
           || version.Contains(']')
           || version.Contains('(')
           || version.Contains(')')
           || version.Contains(',');

    /// <summary>
    /// NuGet folder names are lower-cased and use the normalised version, so the direct path is the common case;
    /// a directory listing is only a fallback for a hand-built cache.
    /// </summary>
    private static string? FindPackageDirectory(string packagesFolder, string packageId, string version)
    {
        var normalizedVersion = version.Split('+')[0];

        var candidates = new[]
        {
            Path.Combine(packagesFolder, packageId.ToLowerInvariant(), normalizedVersion.ToLowerInvariant()),
            Path.Combine(packagesFolder, packageId, normalizedVersion),
            Path.Combine(packagesFolder, packageId.ToLowerInvariant(), normalizedVersion),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Every <c>lib/&lt;tfm&gt;</c> and <c>ref/&lt;tfm&gt;</c> folder, as relative labels like <c>lib/net472</c>.</summary>
    private static IReadOnlyList<FrameworkFolder> CandidateFrameworkFolders(string packageDirectory)
    {
        var folders = new List<FrameworkFolder>();

        foreach (var kind in new[] { "ref", "lib" })
        {
            var parent = Path.Combine(packageDirectory, kind);
            if (!Directory.Exists(parent))
            {
                continue;
            }

            string[] children;

            try
            {
                children = Directory.GetDirectories(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(new FrameworkFolder($"{kind}/{Path.GetFileName(child)}", child));
            }
        }

        return folders;
    }

    /// <summary>
    /// Picks the folder MSBuild would: an exact target-framework match, then the nearest lower .NET Framework
    /// version for a <c>net4x</c> project, then <c>netstandard2.0</c>. A package that only ships one folder is
    /// accepted as-is; anything more ambiguous is left unresolved instead of guessed.
    /// </summary>
    private static string? ChooseFrameworkFolder(IReadOnlyList<FrameworkFolder> folders, string? targetFramework)
    {
        var target = NormalizeFramework(targetFramework);

        if (target is null)
        {
            // No target framework at all: a package with exactly one framework folder is unambiguous; anything
            // else is left unresolved rather than compiled against an assembly that may be the wrong one.
            var labels = folders.Select(folder => folder.Label.Split('/')[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (labels.Length == 1)
            {
                return folders[0].Path;
            }

            return FirstMatching(folders, UnknownFallbacks);
        }

        // An exact match is what MSBuild uses, and ref/ beats lib/ because those are the compile-time assemblies.
        var exact = FirstMatching(folders, [target]);
        if (exact is not null)
        {
            return exact;
        }

        if (TryParseNetFramework(target, out var targetNumber))
        {
            var nearest = folders
                .Where(folder => TryParseNetFramework(FolderLabel(folder), out var number) && number <= targetNumber)
                .OrderByDescending(folder => ParseNetFrameworkNumber(FolderLabel(folder)))
                .ThenBy(folder => folder.Label.StartsWith("ref/", StringComparison.Ordinal) ? 0 : 1)
                .FirstOrDefault();

            return nearest?.Path ?? FirstMatching(folders, NetFrameworkFallbacks);
        }

        if (TryParseModern(target, out var targetMajor))
        {
            var nearest = folders
                .Where(folder => TryParseModern(FolderLabel(folder), out var major) && major <= targetMajor)
                .OrderByDescending(folder => ParseModernMajor(FolderLabel(folder)))
                .ThenBy(folder => folder.Label.StartsWith("ref/", StringComparison.Ordinal) ? 0 : 1)
                .FirstOrDefault();

            return nearest?.Path ?? FirstMatching(folders, ModernFallbacks);
        }

        return FirstMatching(folders, UnknownFallbacks);
    }

    private static string FolderLabel(FrameworkFolder folder) => folder.Label.Split('/')[1];

    /// <summary>The first folder whose framework name is one of <paramref name="frameworks"/>, in that order.</summary>
    private static string? FirstMatching(IReadOnlyList<FrameworkFolder> folders, IReadOnlyList<string> frameworks)
    {
        foreach (var framework in frameworks)
        {
            var match = folders.FirstOrDefault(folder => string.Equals(FolderLabel(folder), framework, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Path;
            }
        }

        return null;
    }

    private static string? NormalizeFramework(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return null;
        }

        var trimmed = targetFramework.Trim();

        // A multi-targeting list ("net472;net10.0") is already reduced by the caller; be defensive anyway.
        var separator = trimmed.IndexOf(';');
        if (separator >= 0)
        {
            trimmed = trimmed[..separator].Trim();
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Recognises a .NET Framework folder name and gives it a comparable number: <c>net472</c> and
    /// <c>net4.7.2</c> are 472, <c>net48</c> is 480, <c>net45</c> is 450. <c>netstandard*</c>, <c>netcoreapp*</c>
    /// and modern .NET (<c>net6.0</c>, <c>net10.0</c>) are deliberately <b>not</b> .NET Framework: a single
    /// number cannot express "greater than any 4.x" for them, and mixing them in would make a <c>net10.0</c>
    /// project resolve a <c>net472</c> folder.
    /// </summary>
    private static bool TryParseNetFramework(string label, out int number)
    {
        number = 0;

        if (!label.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var body = label[3..];
        if (body.Length == 0)
        {
            return false;
        }

        var majorPart = body.Split('.')[0];
        if (majorPart.Length == 0 || !majorPart.All(char.IsAsciiDigit))
        {
            return false;
        }

        // "net4.7.2" -> 4; "net472" -> 472; "net6.0" -> 6; "net10.0" -> 10.
        var major = int.Parse(majorPart, System.Globalization.CultureInfo.InvariantCulture);
        var hasDot = body.Contains('.');

        if (hasDot)
        {
            if (major > 4)
            {
                return false;
            }
        }
        else if (major.ToString(System.Globalization.CultureInfo.InvariantCulture)[0] is not ('2' or '3' or '4'))
        {
            // Packed names for modern .NET ("net50", "net60", "net100") never start with 2, 3 or 4.
            return false;
        }

        var digits = body.Replace(".", string.Empty);

        if (!digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var value = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        number = digits.Length <= 2 ? value * 10 : value;
        return true;
    }

    private static int ParseNetFrameworkNumber(string label)
        => TryParseNetFramework(label, out var number) ? number : 0;

    /// <summary>
    /// Recognises a modern .NET folder name (<c>net5.0</c> … <c>net10.0</c>) and returns its major version.
    /// <c>netstandard*</c>, <c>netcoreapp*</c> and the packed .NET Framework names are not modern .NET.
    /// </summary>
    private static bool TryParseModern(string label, out int major)
    {
        major = 0;

        if (!label.StartsWith("net", StringComparison.OrdinalIgnoreCase) || !label.Contains('.'))
        {
            return false;
        }

        var body = label[3..];
        var majorPart = body.Split('.')[0];

        if (majorPart.Length == 0 || !majorPart.All(char.IsAsciiDigit))
        {
            return false;
        }

        var parsed = int.Parse(majorPart, System.Globalization.CultureInfo.InvariantCulture);
        if (parsed < 5)
        {
            return false;
        }

        major = parsed;
        return true;
    }

    private static int ParseModernMajor(string label)
        => TryParseModern(label, out var major) ? major : 0;

    private sealed record FrameworkFolder(string Label, string Path);
}
