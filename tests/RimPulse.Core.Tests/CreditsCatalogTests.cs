using RimPulse.Core.Credits;
using Xunit;

namespace RimPulse.Core.Tests;

/// <summary>
/// Guards the attribution requirement: every creator and repository RimPulse references has to be featured in
/// the application, so the catalog is asserted rather than trusted to stay complete.
/// </summary>
public sealed class CreditsCatalogTests
{
    /// <summary>The exact set of references RimPulse was asked to feature. Removing one is a regression.</summary>
    private static readonly string[] RequiredUrls =
    [
        "https://github.com/pardeike/Reloader",
        "https://github.com/xiao-e-yun/RimWorldHotReload",
        "https://github.com/pardeike/RimBridgeServer",
        "https://github.com/RimSort/RimSort",
        "https://github.com/Ragonfana/RimworldXsd",
        "https://github.com/RimWorks",
        "https://github.com/RimWorks/Rimworld-Pickle",
        "https://github.com/RimWorks/pickle-template",
        "https://github.com/RimWorks/rimworld-logging-framework",
        "https://github.com/RimWorks/steam-game-image-action",
        "https://github.com/RimWorks/semantic-release-steam",
        "https://github.com/search?q=RimWorldDevMCP&type=repositories",
        "https://github.com/search?q=RimTest+Redux&type=repositories",
    ];

    [Fact]
    public void Catalog_IsNotEmpty()
        => Assert.NotEmpty(CreditsCatalog.Entries);

    [Fact]
    public void Catalog_FeaturesEveryRequiredProjectAndCreator()
    {
        var urls = CreditsCatalog.Entries.Select(entry => entry.Url).ToArray();

        foreach (var requiredUrl in RequiredUrls)
        {
            Assert.Contains(requiredUrl, urls);
        }
    }

    [Fact]
    public void Catalog_FeaturesTheDependenciesThisProjectRunsOn()
    {
        var urls = CreditsCatalog.Entries.Select(entry => entry.Url).ToArray();

        Assert.Contains("https://github.com/AvaloniaUI/Avalonia", urls);
        Assert.Contains("https://github.com/xunit/xunit", urls);

        var avalonia = CreditsCatalog.Entries.Single(entry => entry.Url.EndsWith("AvaloniaUI/Avalonia", StringComparison.Ordinal));
        var xunit = CreditsCatalog.Entries.Single(entry => entry.Url.EndsWith("xunit/xunit", StringComparison.Ordinal));

        Assert.Equal(CreditUsage.Bundled, avalonia.Usage);
        Assert.Equal(CreditUsage.DevelopmentDependency, xunit.Usage);
        Assert.Equal(RequiredUrls.Length + 2, CreditsCatalog.Entries.Count);
    }

    [Fact]
    public void Catalog_HasNoDuplicateEntries()
    {
        var urls = CreditsCatalog.Entries.Select(entry => entry.Url).ToArray();

        Assert.Equal(urls.Length, urls.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryEntry_CreditsACreatorAndAnHttpsLink()
    {
        foreach (var entry in CreditsCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Creator), $"{entry.ProjectName} has no creator.");
            Assert.StartsWith("https://", entry.Url, StringComparison.Ordinal);

            if (entry.HasRelatedUrl)
            {
                Assert.StartsWith("https://", entry.RelatedUrl!, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EveryEntry_ExplainsWhatItIsAndWhyItMatters()
    {
        foreach (var entry in CreditsCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Summary), $"{entry.ProjectName} has no summary.");
            Assert.False(string.IsNullOrWhiteSpace(entry.Relevance), $"{entry.ProjectName} has no relevance note.");
            Assert.True(entry.Summary.Length >= 20, $"{entry.ProjectName} has a uselessly short summary.");
            Assert.True(entry.Relevance.Length >= 20, $"{entry.ProjectName} has a uselessly short relevance note.");
        }
    }

    [Fact]
    public void EveryEntry_StatesItsLicense()
    {
        foreach (var entry in CreditsCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.License), $"{entry.ProjectName} does not state a license.");
        }
    }

    [Fact]
    public void EveryEntry_DeclaresHowItIsUsed()
    {
        foreach (var entry in CreditsCatalog.Entries)
        {
            Assert.True(Enum.IsDefined(entry.Usage), $"{entry.ProjectName} has an invalid usage value.");
            Assert.False(string.IsNullOrWhiteSpace(entry.UsageLabel));
        }
    }

    [Fact]
    public void NoUnlicensedProject_IsMarkedAsAdaptedOrBundled()
    {
        foreach (var entry in CreditsCatalog.Entries.Where(entry => entry.Usage != CreditUsage.Reference))
        {
            Assert.DoesNotContain("no license", entry.License, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GPL", entry.License, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AttributionPolicy_ExplainsTheRules()
    {
        var policy = CreditsCatalog.AttributionPolicy;

        Assert.False(string.IsNullOrWhiteSpace(policy));
        Assert.Contains("credit", policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("license", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsageLabels_AreDistinctPerUsage()
    {
        var labels = Enum.GetValues<CreditUsage>()
            .Select(usage => new CreditEntry("p", "c", "https://example.com", "s", "r", "MIT", usage).UsageLabel)
            .ToArray();

        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TwoThemedEntriesPointAtTheRealProjectsBehindTheSearchLinks()
    {
        var devMcp = CreditsCatalog.Entries.Single(entry => entry.Url.Contains("RimWorldDevMCP", StringComparison.Ordinal));
        var rimTest = CreditsCatalog.Entries.Single(entry => entry.Url.Contains("RimTest", StringComparison.Ordinal));

        Assert.Equal("https://github.com/Jeffrharr/RimWorldDevMCP", devMcp.RelatedUrl);
        Assert.Equal("https://github.com/ilyvion/rimtest-redux", rimTest.RelatedUrl);
    }
}
