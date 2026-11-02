using RimPulse.Core.Build;
using RimPulse.Core.Game;
using RimPulse.Core.Projects;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

/// <summary>
/// The output folder decides whether the game ever loads a built assembly, so these tests pin the choice for
/// both mod layouts: the flat one (<c>root/Assemblies</c>) and the versioned one that
/// <c>LoadFolders.xml</c> describes.
/// </summary>
public sealed class ModOutputLayoutTests
{
    private static readonly GameVersion Version16 = GameVersion.Parse("1.6");

    private static ModOutputLocation Resolve(
        TempDirectory temp, string? loadFolders = null, GameVersion? version = null, string? packageId = "example.mod")
    {
        if (loadFolders is not null)
        {
            temp.CreateFile("LoadFolders.xml", loadFolders);
        }

        return ModOutputLayout.Resolve(
            temp.Root,
            LoadFoldersReader.Read(temp.Root),
            version ?? Version16,
            packageId);
    }

    private static string Assemblies(TempDirectory temp, params string[] parts)
        => Path.GetFullPath(Path.Combine([temp.Root, .. parts, ModOutputLayout.AssembliesFolderName]));

    [Fact]
    public void Resolve_WithoutLoadFolders_WritesToTheModRootAssembliesFolder()
    {
        using var temp = new TempDirectory();

        var location = Resolve(temp);

        Assert.Equal(Assemblies(temp), location.Directory);
        Assert.Empty(location.Problems);
        Assert.Empty(location.Notes);
        Assert.Contains("no LoadFolders.xml", location.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WhenTheRootIsLoaded_KeepsTheModRootAssembliesFolder()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6");

        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li>/</li>
                <li>1.6</li>
              </v1.6>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp), location.Directory);
        Assert.Empty(location.Notes);
        Assert.Empty(location.Problems);
        Assert.Contains("loads the mod root", location.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WhenOnlyAVersionFolderIsLoaded_WritesIntoThatFolder()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6", "Defs");
        temp.CreateDirectory("1.6", "Assemblies");

        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li>1.6</li>
              </v1.6>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp, "1.6"), location.Directory);
        Assert.Empty(location.Notes);
        Assert.Empty(location.Problems);
    }

    [Fact]
    public void Resolve_CreatesTheVersionAssembliesFolderWhenItIsMissing()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6", "Defs");

        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li>1.6</li>
              </v1.6>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp, "1.6"), location.Directory);

        // Not a failure: the compiler creates the directory. It is reported so a surprise write is impossible.
        var note = Assert.Single(location.Notes);
        Assert.Contains("the build creates it", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsAssembliesLeftInAnUnloadedRoot()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6", "Assemblies");
        temp.CreateFile("Assemblies/PowerGridManager.dll");

        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li>1.6</li>
              </v1.6>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp, "1.6"), location.Directory);

        // This is the failure this resolution exists to prevent: the old build wrote where the game never looks.
        var problem = Assert.Single(location.Problems);
        Assert.Contains("PowerGridManager.dll", problem, StringComparison.Ordinal);
        Assert.Contains("not loaded for 1.6", problem, StringComparison.Ordinal);
        Assert.Contains(Assemblies(temp, "1.6"), problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PrefersTheLoadedFolderThatAlreadyHoldsAssemblies()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6", "Defs");
        temp.CreateDirectory("Common", "Assemblies");

        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li>1.6</li>
                <li>Common</li>
              </v1.6>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp, "Common"), location.Directory);
    }

    [Fact]
    public void Resolve_ReportsAssembliesInASecondLoadedFolder()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6", "Assemblies");
        temp.CreateDirectory("Common", "Assemblies");
        temp.CreateFile("Common/Assemblies/Shared.dll");

        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li>1.6</li>
                <li>Common</li>
              </v1.6>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp, "1.6"), location.Directory);

        var problem = Assert.Single(location.Problems);
        Assert.Contains("Shared.dll", problem, StringComparison.Ordinal);
        Assert.Contains("also loads", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_FallsBackToTheRootWhenTheNamedFoldersDoNotExist()
    {
        using var temp = new TempDirectory();

        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li>1.6</li>
              </v1.6>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp), location.Directory);
        var problem = Assert.Single(location.Problems);
        Assert.Contains("none of them exists", problem, StringComparison.Ordinal);
        Assert.Contains("appear to have no code", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_FallsBackToTheRootWhenTheVersionSectionIsMissing()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.7", "Assemblies");

        var location = Resolve(temp, """
            <loadFolders>
              <v1.7>
                <li>1.7</li>
              </v1.7>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp), location.Directory);
        Assert.Contains(location.Problems, problem => problem.Contains("no <v1.6> section", StringComparison.Ordinal));
        Assert.Contains(location.Problems, problem => problem.Contains("cannot confirm", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_FallsBackToTheRootWhenTheVersionIsUnknown()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6", "Assemblies");

        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li>1.6</li>
              </v1.6>
            </loadFolders>
            """,
            GameVersion.Unknown);

        Assert.Equal(Assemblies(temp), location.Directory);
        var problem = Assert.Single(location.Problems);
        Assert.Contains("no confirmed version", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_IgnoresFoldersWhoseConditionsDoNotHold()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6", "Assemblies");
        temp.CreateDirectory("1.6", "Optional");

        // The optional folder exists and is listed, but its condition fails, so the game would not load it.
        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li IfModActive="some.other.mod">1.6/Optional</li>
                <li>1.6</li>
              </v1.6>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp, "1.6"), location.Directory);
        Assert.Empty(location.Problems);
    }

    [Fact]
    public void Resolve_ReadsNestedFolders()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6", "Dlc", "Assemblies");

        var location = Resolve(temp, """
            <loadFolders>
              <v1.6>
                <li>1.6/Dlc</li>
              </v1.6>
            </loadFolders>
            """);

        Assert.Equal(Assemblies(temp, "1.6", "Dlc"), location.Directory);
    }
}
