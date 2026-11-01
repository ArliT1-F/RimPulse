using RimPulse.Core.Game;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class GameVersionTests
{
    [Theory]
    [InlineData("1.6.4851 rev1248", 1, 6, "4851 rev1248")]
    [InlineData("1.7", 1, 7, null)]
    [InlineData("1.7.0", 1, 7, "0")]
    [InlineData("  1.6.4871  ", 1, 6, "4871")]
    public void TryParse_ReadsVersionFileText(string text, int major, int minor, string? build)
    {
        Assert.True(GameVersion.TryParse(text, out var version));

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
        Assert.True(version.IsKnown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("alpha")]
    [InlineData("1")]
    [InlineData("1.x")]
    [InlineData("0.6")]
    [InlineData("1.6.abc")]
    public void TryParse_RejectsInvalidText(string? text)
    {
        Assert.False(GameVersion.TryParse(text, out var version));
        Assert.Equal(GameVersion.Unknown, version);
    }

    [Theory]
    [InlineData("1.6", true)]
    [InlineData("1.7", true)]
    [InlineData("1.5", false)]
    [InlineData("1.8", false)]
    [InlineData("2.6", false)]
    public void IsSupported_OnlyAccepts16And17(string text, bool expected)
    {
        Assert.Equal(expected, GameVersion.Parse(text).IsSupported);
    }

    [Fact]
    public void Unknown_IsNotSupported()
    {
        Assert.False(GameVersion.Unknown.IsKnown);
        Assert.False(GameVersion.Unknown.IsSupported);
        Assert.Equal("unknown", GameVersion.Unknown.ShortVersion);
        Assert.Equal("unknown", GameVersion.Unknown.ToString());
    }

    [Fact]
    public void ToString_RoundTripsVersionFileText()
    {
        Assert.Equal("1.6.4851 rev1248", GameVersion.Parse("1.6.4851 rev1248").ToString());
        Assert.Equal("1.7", GameVersion.Parse("1.7").ToString());
        Assert.Equal("1.6", GameVersion.Parse("1.6.4851").ShortVersion);
        Assert.Equal("4851", GameVersion.Parse("1.6.4851").Build);
        Assert.Equal("1.6.4851", GameVersion.Parse("1.6.4851").ToString());
    }

    [Fact]
    public void CompareTo_OrdersByMajorThenMinor()
    {
        var oneSix = GameVersion.Parse("1.6");
        var oneSeven = GameVersion.Parse("1.7");

        Assert.True(oneSix.CompareTo(oneSeven) < 0);
        Assert.True(oneSeven.CompareTo(oneSix) > 0);
        Assert.Equal(0, oneSix.CompareTo(GameVersion.Parse("1.6")));
        Assert.True(GameVersion.Unknown.CompareTo(oneSix) < 0);
    }

    [Fact]
    public void Parse_ThrowsForGarbage()
        => Assert.Throws<FormatException>(() => GameVersion.Parse("not-a-version"));
}
