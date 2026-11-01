using RimPulse.Core.Build;
using RimPulse.Core.Installations;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class GameReferenceResolverTests
{
    [Fact]
    public void Resolve_UsesTheUnityInstallationsManagedAssembliesOnly()
    {
        using var temp = new TempDirectory();
        var root = FakeInstallation.CreateUnity(temp.Combine("RimWorld16"));
        var installation = InstallationProbe.Probe(root);

        var references = GameReferenceResolver.Resolve(installation, includeHostRuntimeAssemblies: false);

        Assert.Empty(references.Problems);
        var names = references.AssemblyPaths.Select(Path.GetFileName).ToArray();
        Assert.Contains("Assembly-CSharp.dll", names);
        Assert.Contains("UnityEngine.CoreModule.dll", names);
        Assert.DoesNotContain(names, name => name!.Contains("System.Private.CoreLib", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_AddsTheHostRuntimeForTheWaylithLayout()
    {
        using var temp = new TempDirectory();
        var root = FakeInstallation.CreateWaylith(temp.Combine("RimWorld17"));
        var installation = InstallationProbe.Probe(root);

        var without = GameReferenceResolver.Resolve(installation, includeHostRuntimeAssemblies: false);
        var with = GameReferenceResolver.Resolve(installation, includeHostRuntimeAssemblies: true);

        Assert.Empty(without.Problems);
        Assert.Equal(3, without.AssemblyPaths.Count);
        Assert.True(with.AssemblyPaths.Count > without.AssemblyPaths.Count);
        Assert.Empty(with.Problems);
    }

    [Fact]
    public void Resolve_ReportsAMissingInstallationInsteadOfThrowing()
    {
        var references = GameReferenceResolver.Resolve(null);

        Assert.True(references.IsEmpty);
        Assert.Contains(references.Problems, problem => problem.Contains("No RimWorld installation", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_ReportsAnIncompleteInstallationButKeepsWhatIsThere()
    {
        using var temp = new TempDirectory();
        var root = FakeInstallation.CreateUnity(temp.Combine("Broken"), includeAllAssemblies: false);
        var installation = InstallationProbe.Probe(root);

        Assert.False(installation.IsUsable);

        var references = GameReferenceResolver.Resolve(installation, includeHostRuntimeAssemblies: false);

        Assert.NotEmpty(references.AssemblyPaths);
        Assert.Contains(references.Problems, problem => problem.Contains("cannot be used", StringComparison.Ordinal));
    }

    [Fact]
    public void HostRuntimeAssemblies_ExposeTheAssembliesTheCompilerRunsOn()
    {
        var assemblies = GameReferenceResolver.HostRuntimeAssemblies;

        Assert.NotEmpty(assemblies);
        Assert.Contains(assemblies, path => Path.GetFileName(path)!.StartsWith("System.", StringComparison.Ordinal));
        Assert.All(assemblies, path => Assert.True(File.Exists(path)));
    }
}
