using RimPulse.Core.Monitoring;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class ProjectChangeClassifierTests
{
    [Theory]
    [InlineData("Source|Thing.cs", ProjectChangeCategory.CSharp)]
    [InlineData("About|About.xml", ProjectChangeCategory.AboutMetadata)]
    [InlineData("1.7|Languages|English|Keyed|Strings.xml", ProjectChangeCategory.Language)]
    [InlineData("Defs|Thing.xml", ProjectChangeCategory.Xml)]
    [InlineData("Textures|Thing.PNG", ProjectChangeCategory.Texture)]
    [InlineData("Sounds|Thing.ogg", ProjectChangeCategory.Audio)]
    [InlineData("LoadFolders.xml", ProjectChangeCategory.Xml)]
    [InlineData("notes.txt", ProjectChangeCategory.Other)]
    public void ClassifiesProjectPaths(string path, ProjectChangeCategory expected)
    {
        Assert.Equal(expected, ProjectChangeClassifier.Classify(Path.Combine(path.Split('|'))));
    }

    [Theory]
    [InlineData(".git|config")]
    [InlineData("Source|obj|Debug|generated.cs")]
    [InlineData("Source|bin|output.dll")]
    [InlineData("Defs|Thing.xml~")]
    [InlineData("Defs|.Thing.xml.swp")]
    [InlineData("Defs|Thing.tmp")]
    [InlineData("..|outside.xml")]
    public void IgnoresGeneratedTemporaryAndUnsafePaths(string path)
    {
        Assert.Null(ProjectChangeClassifier.Classify(Path.Combine(path.Split('|'))));
    }
}
