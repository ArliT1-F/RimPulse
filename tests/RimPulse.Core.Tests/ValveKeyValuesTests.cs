using RimPulse.Core.Installations.Steam;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class ValveKeyValuesTests
{
    private const string LibraryFoldersVdf = """
        // Auto-generated Steam file
        "libraryfolders"
        {
            "0"
            {
                "path"      "/home/rim/.steam/steam"
                "label"     ""
                "contentid" "1234567890"
            }
            "1"
            {
                "path"      "/mnt/games/SteamLibrary"
                "apps"
                {
                    "294100"  "9876543210"
                }
            }
            "contentstatsid" "42"
        }
        """;

    [Fact]
    public void Parse_ReadsNestedSections()
    {
        var document = ValveKeyValues.Parse(LibraryFoldersVdf);

        var section = document.GetSection("libraryfolders");

        Assert.NotNull(section);
        Assert.Equal(3, section!.Children.Count);
        Assert.Equal("/home/rim/.steam/steam", section.Children[0].GetValue("path"));
        Assert.Equal("/mnt/games/SteamLibrary", section.Children[1].GetValue("path"));
        Assert.Equal("9876543210", section.Children[1].GetSection("apps")!.GetValue("294100"));
    }

    [Fact]
    public void Parse_IgnoresComments()
    {
        var document = ValveKeyValues.Parse("""
            // a comment
            "key" "value" // trailing comment
            """);

        Assert.Equal("value", document.GetValue("key"));
    }

    [Fact]
    public void Parse_HandlesEscapedQuotes()
    {
        var document = ValveKeyValues.Parse("\"name\" \"a \\\"quoted\\\" value\"");
        Assert.Equal("a \"quoted\" value", document.GetValue("name"));
    }

    [Fact]
    public void Parse_IsCaseInsensitiveForKeyLookups()
    {
        var document = ValveKeyValues.Parse("\"AppState\" { \"installdir\" \"RimWorld\" }");
        Assert.Equal("RimWorld", document.GetSection("appstate")!.GetValue("INSTALLDIR"));
    }

    [Fact]
    public void Parse_ThrowsOnUnclosedSection()
        => Assert.Throws<ValveKeyValuesFormatException>(() => ValveKeyValues.Parse("\"a\" { \"b\" \"c\""));

    [Fact]
    public void Parse_ThrowsWhenKeyHasNoValue()
        => Assert.Throws<ValveKeyValuesFormatException>(() => ValveKeyValues.Parse("\"a\" \"b\" \"c\""));

    [Fact]
    public void Parse_ThrowsOnUnexpectedClosingBrace()
        => Assert.Throws<ValveKeyValuesFormatException>(() => ValveKeyValues.Parse("}"));

    [Fact]
    public void Parse_ThrowsOnUnterminatedQuote()
        => Assert.Throws<ValveKeyValuesFormatException>(() => ValveKeyValues.Parse("\"a \"b\""));
}
