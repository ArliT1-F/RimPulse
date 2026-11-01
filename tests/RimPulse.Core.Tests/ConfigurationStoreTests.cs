using RimPulse.Core.Configuration;
using RimPulse.Core.Diagnostics;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class ConfigurationStoreTests
{
    [Fact]
    public async Task LoadAsync_UsesDefaultsWhenFileIsMissing()
    {
        using var temp = new TempDirectory();
        var store = new ConfigurationStore(temp.Combine("config.json"), new DiagnosticLog());

        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadStatus.DefaultsCreated, result.Status);
        Assert.True(result.Configuration.AutoDetectOnStartup);
        Assert.Null(result.Configuration.SelectedInstallationPath);
        Assert.Empty(result.Configuration.CustomInstallationPaths);
        Assert.False(File.Exists(temp.Combine("config.json")));
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsConfiguration()
    {
        using var temp = new TempDirectory();
        var store = new ConfigurationStore(temp.Combine("config.json"), new DiagnosticLog());

        var configuration = new RimPulseConfiguration
        {
            SelectedInstallationPath = "/games/RimWorld",
            AutoDetectOnStartup = false,
            CustomInstallationPaths = ["/games/RimWorld", "/games/RimWorld17"],
        };

        await store.SaveAsync(configuration, TestContext.Current.CancellationToken);
        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadStatus.Loaded, result.Status);
        Assert.Equal("/games/RimWorld", result.Configuration.SelectedInstallationPath);
        Assert.False(result.Configuration.AutoDetectOnStartup);
        Assert.Equal(2, result.Configuration.CustomInstallationPaths.Count);
        Assert.Equal("/games/RimWorld17", result.Configuration.CustomInstallationPaths[1]);
    }

    [Fact]
    public async Task SaveAsync_WritesCamelCaseJsonAndLeavesNoTemporaryFile()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");
        var store = new ConfigurationStore(path, new DiagnosticLog());

        await store.SaveAsync(new RimPulseConfiguration { SelectedInstallationPath = "/games/RimWorld" }, TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("\"selectedInstallationPath\"", json, StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task LoadAsync_RecoversFromCorruptFile()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("config.json", "{ this is not json ");
        var store = new ConfigurationStore(path, new DiagnosticLog());

        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadStatus.RecoveredFromCorruptFile, result.Status);
        Assert.False(File.Exists(path));

        var backups = Directory.GetFiles(temp.Root, "config.json.corrupt-*");
        Assert.Single(backups);
    }

    [Fact]
    public async Task LoadAsync_NormalisesPersistedValues()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("config.json", """
            {
              "schemaVersion": 1,
              "selectedInstallationPath": "   /games/RimWorld   ",
              "customInstallationPaths": [ "/games/RimWorld", " /games/RimWorld ", "", "/games/RimWorld17" ],
              "autoDetectOnStartup": true,
              "unknownFutureSetting": "ignored"
            }
            """);

        var store = new ConfigurationStore(temp.Combine("config.json"), new DiagnosticLog());
        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadStatus.Loaded, result.Status);
        Assert.Equal("/games/RimWorld", result.Configuration.SelectedInstallationPath);
        Assert.Equal(2, result.Configuration.CustomInstallationPaths.Count);
        Assert.Equal("/games/RimWorld", result.Configuration.CustomInstallationPaths[0]);
        Assert.Equal("/games/RimWorld17", result.Configuration.CustomInstallationPaths[1]);
    }

    [Fact]
    public async Task LoadAsync_TreatsEmptyFileAsUnreadable()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("config.json", string.Empty);
        var store = new ConfigurationStore(temp.Combine("config.json"), new DiagnosticLog());

        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadStatus.DefaultsUsedAfterError, result.Status);
    }

    [Fact]
    public async Task SaveAsync_CreatesMissingDirectories()
    {
        using var temp = new TempDirectory();
        var store = new ConfigurationStore(temp.Combine("nested", "deeper", "config.json"), new DiagnosticLog());

        await store.SaveAsync(new RimPulseConfiguration(), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(temp.Combine("nested", "deeper", "config.json")));
    }
}
