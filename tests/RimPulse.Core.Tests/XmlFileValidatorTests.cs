using RimPulse.Core.Tests.TestSupport;
using RimPulse.Core.Validation;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class XmlFileValidatorTests
{
    [Fact]
    public void Check_ReportsWellFormedXmlWithoutClaimingSemanticValidity()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Defs/Test.xml", "<Defs><Unknown /></Defs>");
        var result = XmlFileValidator.Check(temp.Root, Path.Combine("Defs", "Test.xml"));
        Assert.Equal(XmlCheckStatus.WellFormed, result.Status);
        Assert.Contains("game semantics not checked", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<Defs><Broken></Defs>")]
    [InlineData("<!DOCTYPE Defs [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><Defs>&x;</Defs>")]
    public void Check_RejectsMalformedXmlAndExternalEntities(string text)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Defs/Test.xml", text);
        var result = XmlFileValidator.Check(temp.Root, Path.Combine("Defs", "Test.xml"));
        Assert.Equal(XmlCheckStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("<Defs><ThingDef><defName>Thing_A</defName></ThingDef></Defs>", XmlCheckStatus.WellFormed)]
    [InlineData("<Defs><ThingDef><defName>Shared</defName></ThingDef><RecipeDef><defName>Shared</defName></RecipeDef></Defs>", XmlCheckStatus.WellFormed)]
    [InlineData("<Patch><Operation Class=\"PatchOperationSequence\" /></Patch>", XmlCheckStatus.WellFormed)]
    [InlineData("<LanguageData><key>value</key></LanguageData>", XmlCheckStatus.Failed)]
    [InlineData("<Defs><ThingDef><defName>A</defName></ThingDef><ThingDef><defName>A</defName></ThingDef></Defs>", XmlCheckStatus.Failed)]
    [InlineData("<Defs><ThingDef><defName>A</defName><defName>B</defName></ThingDef></Defs>", XmlCheckStatus.Failed)]
    public void Check_AppliesBasicStructureRulesToDefsFiles(string text, XmlCheckStatus expected)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Defs/Test.xml", text);
        var result = XmlFileValidator.Check(temp.Root, Path.Combine("Defs", "Test.xml"));
        Assert.Equal(expected, result.Status);
        if (expected == XmlCheckStatus.Failed)
            Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void Check_DoesNotCheckMissingNonXmlOrOutsidePaths()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("readme.txt", "not xml");
        Assert.Equal(XmlCheckStatus.NotChecked, XmlFileValidator.Check(temp.Root, "missing.xml").Status);
        Assert.Equal(XmlCheckStatus.NotChecked, XmlFileValidator.Check(temp.Root, "readme.txt").Status);
        Assert.Equal(XmlCheckStatus.NotChecked, XmlFileValidator.Check(temp.Root, Path.Combine("..", "outside.xml")).Status);
        Assert.Equal(XmlCheckStatus.NotChecked, XmlFileValidator.Check(temp.Root, Path.GetFullPath(temp.Combine("absolute.xml"))).Status);
    }
}
