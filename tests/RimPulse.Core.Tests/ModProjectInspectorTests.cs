using RimPulse.Core.Projects;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class ModProjectInspectorTests
{
    [Fact]
    public void Inspect_ReadsMetadataFoldersAndDeclaredDependencies()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("About/About.xml", """
            <ModMetaData>
              <name> Test Mod </name><packageId>example.test</packageId>
              <authors><li>Alice</li><li>Bob</li></authors>
              <supportedVersions><li>1.6</li><li>1.7</li></supportedVersions>
              <modDependencies><li><packageId>example.base</packageId><displayName>Base</displayName></li></modDependencies>
              <modDependenciesByVersion><v1.7><li><packageId>example.extra</packageId></li></v1.7></modDependenciesByVersion>
            </ModMetaData>
            """);
        temp.CreateDirectory("Defs");
        temp.CreateDirectory("1.7");
        temp.CreateFile("LoadFolders.xml", "<loadFolders />");

        var result = ModProjectInspector.Inspect(temp.Root);

        Assert.Empty(result.Problems);
        Assert.NotNull(result.Metadata);
        Assert.Equal("Test Mod", result.Metadata.Name);
        Assert.Equal("example.test", result.Metadata.PackageId);
        Assert.Equal("Alice, Bob", result.Metadata.Author);
        Assert.Equal(new[] { "1.6", "1.7" }, result.Metadata.SupportedVersions);
        Assert.Equal(2, result.Metadata.Dependencies.Count);
        Assert.Equal("example.base", result.Metadata.Dependencies[0].PackageId);
        Assert.Null(result.Metadata.Dependencies[0].Version);
        Assert.Equal("v1.7", result.Metadata.Dependencies[1].Version);
        Assert.Contains("About", result.Folders);
        Assert.Contains("Defs", result.Folders);
        Assert.Contains("1.7", result.Folders);
        Assert.True(result.HasLoadFolders);
    }

    [Fact]
    public void Inspect_ReportsMissingDirectoryAndMetadata()
    {
        using var temp = new TempDirectory();
        var missing = ModProjectInspector.Inspect(temp.Combine("missing"));
        Assert.Null(missing.Metadata);
        Assert.Contains(missing.Problems, p => p.Contains("does not exist", StringComparison.Ordinal));
        var noAbout = ModProjectInspector.Inspect(temp.Root);
        Assert.Contains(noAbout.Problems, p => p.Contains("About/About.xml", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<ModMetaData><name>broken</ModMetaData>")]
    [InlineData("<NotModMetaData />")]
    [InlineData("<!DOCTYPE ModMetaData [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><ModMetaData><name>&x;</name></ModMetaData>")]
    public void Inspect_ReportsInvalidOrUnsafeXml(string xml)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("About/About.xml", xml);
        var result = ModProjectInspector.Inspect(temp.Root);
        Assert.Null(result.Metadata);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Inspect_ReportsMissingFieldsAndDependencyIdWithoutDiscardingMetadata()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("About/About.xml", "<ModMetaData><author>Author</author><modDependencies><li><displayName>Base</displayName></li></modDependencies></ModMetaData>");
        var result = ModProjectInspector.Inspect(temp.Root);
        Assert.NotNull(result.Metadata);
        Assert.Equal("Author", result.Metadata.Author);
        Assert.Equal(3, result.Problems.Count);
        Assert.Null(Assert.Single(result.Metadata.Dependencies).PackageId);
    }

    [Fact]
    public void Inspect_DoesNotModifyProjectFiles()
    {
        using var temp = new TempDirectory();
        const string xml = "<ModMetaData><name>Test</name><packageId>test.mod</packageId></ModMetaData>";
        var path = temp.CreateFile("About/About.xml", xml);
        ModProjectInspector.Inspect(temp.Root);
        Assert.Equal(xml, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(temp.Root, "*", SearchOption.AllDirectories));
    }
}
