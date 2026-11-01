using RimPulse.Core.Installations.Steam;
using RimPulse.Core.Platform;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class SteamLibraryReaderTests
{
    [Fact]
    public void ReadLibraryFolders_ReadsModernFormat()
    {
        using var temp = new TempDirectory();
        var steamRoot = temp.CreateDirectory(".steam", "steam");
        var secondLibrary = temp.CreateDirectory("second-library");

        temp.CreateFile(".steam/steam/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"  "{{steamRoot}}"
                }
                "1"
                {
                    "path"  "{{secondLibrary}}"
                }
            }
            """);

        var libraries = SteamLibraryReader.ReadLibraryFolders(steamRoot);

        Assert.Equal(2, libraries.Count);
        Assert.Contains(steamRoot, libraries);
        Assert.Contains(secondLibrary, libraries);
    }

    [Fact]
    public void ReadLibraryFolders_ReadsLegacyFormat()
    {
        using var temp = new TempDirectory();
        var steamRoot = temp.CreateDirectory(".steam", "steam");
        var legacyLibrary = temp.CreateDirectory("legacy-library");

        temp.CreateFile(".steam/steam/steamapps/libraryfolders.vdf", $$"""
            "LibraryFolders"
            {
                "TimeNextStatsReport"  "1610000000"
                "ContentStatsID"       "-1234567890"
                "1"                    "{{legacyLibrary}}"
            }
            """);

        var libraries = SteamLibraryReader.ReadLibraryFolders(steamRoot);

        Assert.Single(libraries);
        Assert.Equal(legacyLibrary, libraries[0]);
    }

    [Fact]
    public void ReadLibraryFolders_FallsBackToSteamRootWhenFileMissing()
    {
        using var temp = new TempDirectory();
        var steamRoot = temp.CreateDirectory(".steam", "steam");

        var libraries = SteamLibraryReader.ReadLibraryFolders(steamRoot);

        Assert.Single(libraries);
        Assert.Equal(steamRoot, libraries[0]);
    }

    [Fact]
    public void ReadLibraryFolders_ReturnsEmptyWhenSteamRootMissing()
    {
        using var temp = new TempDirectory();

        Assert.Empty(SteamLibraryReader.ReadLibraryFolders(temp.Combine("nope")));
    }

    [Fact]
    public void ReadInstalledDirectory_UsesManifestInstallDir()
    {
        using var temp = new TempDirectory();
        var library = temp.CreateDirectory("library");
        temp.CreateFile("library/steamapps/appmanifest_294100.acf", """
            "AppState"
            {
                "appid"     "294100"
                "name"      "RimWorld"
                "installdir"    "RimWorld"
                "StateFlags"    "4"
            }
            """);

        var installed = SteamLibraryReader.ReadInstalledDirectory(library, SteamLibraryReader.RimWorldAppId);

        Assert.Equal(Path.Combine(library, "steamapps", "common", "RimWorld"), installed);
    }

    [Fact]
    public void ReadInstalledDirectory_ReturnsNullWithoutManifest()
    {
        using var temp = new TempDirectory();
        var library = temp.CreateDirectory("library");

        Assert.Null(SteamLibraryReader.ReadInstalledDirectory(library, SteamLibraryReader.RimWorldAppId));
    }

    [Fact]
    public void FindRimWorldInstallRoots_FindsConventionalFolderWithoutManifest()
    {
        using var temp = new TempDirectory();
        var library = temp.CreateDirectory("library");
        var conventional = temp.CreateDirectory("library", "steamapps", "common", "RimWorld");

        var roots = SteamLibraryReader.FindRimWorldInstallRoots(library);

        Assert.Single(roots);
        Assert.Equal(conventional, roots[0]);
    }

    [Fact]
    public void FindRimWorldInstallRoots_DoesNotDuplicateManifestAndConventionalPath()
    {
        using var temp = new TempDirectory();
        var library = temp.CreateDirectory("library");
        temp.CreateDirectory("library", "steamapps", "common", "RimWorld");
        temp.CreateFile("library/steamapps/appmanifest_294100.acf", """
            "AppState"
            {
                "installdir"    "RimWorld"
            }
            """);

        var roots = SteamLibraryReader.FindRimWorldInstallRoots(library);

        Assert.Single(roots);
    }

    [Fact]
    public void GetSteamRootCandidates_CoversLinuxInstallVariants()
    {
        var candidates = SteamLibraryReader.GetSteamRootCandidates("/home/rim", HostPlatform.Linux);

        Assert.Contains(Path.Combine("/home/rim", ".steam", "steam"), candidates);
        Assert.Contains(Path.Combine("/home/rim", ".local", "share", "Steam"), candidates);
        Assert.Contains(
            Path.Combine("/home/rim", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            candidates);
    }

    [Fact]
    public void ReadLibraryFolders_ThrowsForMalformedFile()
    {
        using var temp = new TempDirectory();
        var steamRoot = temp.CreateDirectory(".steam", "steam");
        temp.CreateFile(".steam/steam/steamapps/libraryfolders.vdf", "\"libraryfolders\" { \"0\" {");

        Assert.Throws<ValveKeyValuesFormatException>(() => SteamLibraryReader.ReadLibraryFolders(steamRoot));
    }
}
