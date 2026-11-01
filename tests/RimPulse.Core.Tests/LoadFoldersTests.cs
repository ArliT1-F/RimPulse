using RimPulse.Core.Game;
using RimPulse.Core.Projects;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class LoadFoldersTests
{
    private const string SampleLoadFolders = """
        <loadFolders>
          <v1.6>
            <li>/</li>
            <li>1.6</li>
            <li IfModActive="example.dlc">1.6/Dlc</li>
            <li IfModNotActive="example.rival">1.6/NoRival</li>
          </v1.6>
          <v1.7>
            <li>/</li>
            <li>1.7</li>
            <li IfModActiveAny="example.a, example.b">1.7/Either</li>
            <li IfModActiveAll="example.a,example.b">1.7/Both</li>
          </v1.7>
        </loadFolders>
        """;

    private static LoadFoldersDocument ReadSample(TempDirectory temp)
    {
        temp.CreateFile("LoadFolders.xml", SampleLoadFolders);
        return LoadFoldersReader.Read(temp.Root);
    }

    [Fact]
    public void Read_ParsesSectionsAndConditions()
    {
        using var temp = new TempDirectory();
        var document = ReadSample(temp);

        Assert.Empty(document.Problems);
        Assert.True(document.Exists);
        Assert.Equal(new[] { "v1.6", "v1.7" }, document.SectionNames);

        var section = Assert.Single(document.Sections, s => s.VersionText == "1.6");
        Assert.Equal(4, section.Entries.Count);
        Assert.True(section.Entries[0].IsRoot);
        Assert.Empty(section.Entries[0].Conditions);
        Assert.Equal("example.dlc", Assert.Single(section.Entries[2].Conditions).PackageIds[0]);
        Assert.Equal(LoadFolderConditionKind.NotActive, Assert.Single(section.Entries[3].Conditions).Kind);
    }

    [Fact]
    public void Evaluate_SelectsTheMatchingVersionAndKeepsFileOrder()
    {
        using var temp = new TempDirectory();
        var document = ReadSample(temp);

        var plan = LoadFoldersReader.Evaluate(document, GameVersion.Parse("1.6"), ["example.dlc"]);

        Assert.Empty(plan.Problems);
        Assert.Equal("1.6", plan.VersionText);
        Assert.Equal(new[] { "/", "1.6", "1.6/Dlc", "1.6/NoRival" }, plan.Folders);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Evaluate_ReportsConditionalFoldersThatDoNotApply()
    {
        using var temp = new TempDirectory();
        var document = ReadSample(temp);

        var plan = LoadFoldersReader.Evaluate(document, GameVersion.Parse("1.6"), ["other.mod"]);

        Assert.Equal(new[] { "/", "1.6" }, plan.Folders);
        Assert.Equal(2, plan.Skipped.Count);
        Assert.Contains("IfModActive=", plan.Skipped[0].ConditionDisplay, StringComparison.Ordinal);
        Assert.Contains("IfModNotActive=", plan.Skipped[1].ConditionDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_TreatsAnyAsOrAndAllAsAnd()
    {
        using var temp = new TempDirectory();
        var document = ReadSample(temp);
        var version = GameVersion.Parse("1.7");

        var oneActive = LoadFoldersReader.Evaluate(document, version, ["example.a"]);
        Assert.Contains("1.7/Either", oneActive.Folders);
        Assert.DoesNotContain("1.7/Both", oneActive.Folders);

        var bothActive = LoadFoldersReader.Evaluate(document, version, ["Example.A", "example.b"]);
        Assert.Contains("1.7/Either", bothActive.Folders);
        Assert.Contains("1.7/Both", bothActive.Folders);
    }

    [Fact]
    public void Evaluate_RequiresEveryConditionOnAnEntry()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("LoadFolders.xml", """
            <loadFolders>
              <v1.6>
                <li IfModActive="a.mod" IfModNotActive="b.mod">Both</li>
              </v1.6>
            </loadFolders>
            """);
        var document = LoadFoldersReader.Read(temp.Root);

        Assert.Equal(new[] { "Both" }, LoadFoldersReader.Evaluate(document, GameVersion.Parse("1.6"), ["a.mod"]).Folders);
        Assert.Empty(LoadFoldersReader.Evaluate(document, GameVersion.Parse("1.6"), ["a.mod", "b.mod"]).Folders);
        Assert.Empty(LoadFoldersReader.Evaluate(document, GameVersion.Parse("1.6"), ["b.mod"]).Folders);
    }

    [Fact]
    public void Evaluate_IgnoresTheLegacySteamSuffixWhenMatchingPackageIds()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("LoadFolders.xml", """
            <loadFolders>
              <v1.6>
                <li IfModActive="example.mod_steam">SteamOnly</li>
              </v1.6>
            </loadFolders>
            """);
        var document = LoadFoldersReader.Read(temp.Root);

        Assert.Equal(new[] { "SteamOnly" }, LoadFoldersReader.Evaluate(document, GameVersion.Parse("1.6"), ["example.mod"]).Folders);
    }

    [Fact]
    public void Evaluate_ExplainsMissingFileAndMissingVersionSection()
    {
        using var temp = new TempDirectory();
        var missing = LoadFoldersReader.Read(temp.Root);
        Assert.False(missing.Exists);

        var missingPlan = LoadFoldersReader.Evaluate(missing, GameVersion.Parse("1.6"), []);
        Assert.Empty(missingPlan.Folders);
        Assert.Contains("default versioned-folder rules", Assert.Single(missingPlan.Problems), StringComparison.Ordinal);

        var document = ReadSample(temp);
        var wrongVersion = LoadFoldersReader.Evaluate(document, GameVersion.Parse("1.5"), []);
        Assert.Empty(wrongVersion.Folders);
        var problem = Assert.Single(wrongVersion.Problems);
        Assert.Contains("v1.5", problem, StringComparison.Ordinal);
        Assert.Contains("v1.6", problem, StringComparison.Ordinal);

        var unknownVersion = LoadFoldersReader.Evaluate(document, GameVersion.Unknown, []);
        Assert.Contains("no confirmed RimWorld version", Assert.Single(unknownVersion.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ReportsUnrecognisedSectionsButKeepsValidOnes()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("LoadFolders.xml", """
            <loadFolders>
              <nonsense><li>/</li></nonsense>
              <v1.6><li>/</li></v1.6>
            </loadFolders>
            """);

        var document = LoadFoldersReader.Read(temp.Root);

        Assert.Equal(new[] { "v1.6" }, document.SectionNames);
        var problem = Assert.Single(document.Problems);
        Assert.Contains("nonsense", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_AcceptsAWellFormedFileWithoutProblems()
    {
        using var temp = new TempDirectory();
        var document = ReadSample(temp);

        Assert.Empty(document.Problems);
        Assert.Equal(2, document.Sections.Count);
    }

    [Theory]
    [InlineData("<notLoadFolders />", "root element")]
    [InlineData("<loadFolders><v1.6><li>broken</loadFolders>", "not well-formed")]
    [InlineData("<!DOCTYPE loadFolders [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><loadFolders><v1.6><li>&x;</li></v1.6></loadFolders>", "not well-formed")]
    public void Read_ReportsMalformedOrUnsafeFiles(string xml, string expectedFragment)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("LoadFolders.xml", xml);

        var document = LoadFoldersReader.Read(temp.Root);

        Assert.Empty(document.Sections);
        Assert.Contains(document.Problems, problem => problem.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../outside", "outside the mod")]
    [InlineData("/absolute", "outside the mod")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    public void Read_RejectsEntriesThatEscapeTheMod(string value, string expectedFragment)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("LoadFolders.xml", $"<loadFolders><v1.6><li>{value}</li></v1.6></loadFolders>");

        var document = LoadFoldersReader.Read(temp.Root);

        var section = Assert.Single(document.Sections);
        Assert.Empty(section.Entries);
        Assert.Contains(document.Problems, problem => problem.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_ReportsUnknownConditionAttributesAndKeepsKnownOnes()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("LoadFolders.xml", """
            <loadFolders>
              <v1.6>
                <li IfModActive="a.mod" IfModActiv="typo">Folder</li>
              </v1.6>
            </loadFolders>
            """);

        var document = LoadFoldersReader.Read(temp.Root);

        var entry = Assert.Single(Assert.Single(document.Sections).Entries);
        Assert.Equal("a.mod", Assert.Single(entry.Conditions).PackageIds[0]);
        Assert.Contains(document.Problems, problem => problem.Contains("IfModActiv", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspector_SurfacesTheLoadFoldersDocument()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("About/About.xml", "<ModMetaData><name>Test</name><packageId>example.test</packageId></ModMetaData>");
        temp.CreateFile("LoadFolders.xml", SampleLoadFolders);

        var result = ModProjectInspector.Inspect(temp.Root);

        Assert.Empty(result.Problems);
        Assert.True(result.HasLoadFolders);
        Assert.Equal(2, result.LoadFolders.Sections.Count);
    }
}
