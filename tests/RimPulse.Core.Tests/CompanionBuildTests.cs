using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using RimPulse.Core.Build;
using RimPulse.Core.Configuration;
using RimPulse.Core.Game;
using RimPulse.Core.Installations;
using RimPulse.Core.IPC;
using RimPulse.Core.Runtime;
using RimPulse.Core.Tests.TestSupport;
using RimPulse.Protocol;
using Xunit;

namespace RimPulse.Core.Tests;

/// <summary>Tests use a tiny API stand-in; this does not substitute for a real-game integration test.</summary>
public sealed class CompanionBuildTests
{
    [Fact]
    public void Version16_SelectsAndCompilesItsOwnAdapterAgainstSyntheticReferences()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("game");
        FakeInstallation.CreateUnity(root);
        var managed = Path.Combine(root, "RimWorldLinux_Data", "Managed");
        var stub = temp.CreateFile("FakeGame.cs", """
            using System; using System.Collections.Generic;
            namespace Verse {
              public sealed class StaticConstructorOnStartupAttribute : Attribute { }
              public static class LongEventHandler { public static void ExecuteWhenFinished(Action action) { action(); } }
              public sealed class ModContentPack { public string Name => "m"; public string PackageIdPlayerFacing => "a.b"; public string RootDir => "/m"; }
              public static class LoadedModManager { public static List<ModContentPack> RunningModsListForReading = new List<ModContentPack>(); }
              public static class Log { public static void Warning(string message) { } }
            }
            namespace RimWorld { public static class VersionControl { public static string CurrentVersionStringWithRev => "1.6.1 rev3"; } }
            """);
        var stubResult = ModCompiler.Compile(new CompileRequest("Assembly-CSharp", [stub], GameReferenceResolver.HostRuntimeAssemblies,
            OutputPath: Path.Combine(managed, "Assembly-CSharp.dll"), EmitDebugSymbols: false));
        Assert.True(stubResult.Succeeded, string.Join("; ", stubResult.Diagnostics.Select(d => d.Display)));
        // These framework symlinks are test stand-ins, not a claim that .NET 10 is RimWorld 1.6's runtime.
        foreach (var reference in GameReferenceResolver.HostRuntimeAssemblies)
        {
            var destination = Path.Combine(managed, Path.GetFileName(reference));
            if (!File.Exists(destination)) File.CreateSymbolicLink(destination, reference);
        }
        var paths = new AppPaths(temp.CreateDirectory("config"), temp.CreateDirectory("state"));
        var result = CompanionInstaller.BuildAndLink(InstallationProbe.Probe(root), paths);
        Assert.True(File.Exists(Path.Combine(result.ModDirectory, "1.6", "Assemblies", "RimPulse.Companion.dll")));
        Assert.True(Directory.Exists(result.GameLink));
    }

    [Fact]
    public async Task Version17_CompilesAgainstSelectedAssembliesAndSendsStartupSnapshot()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("game");
        FakeInstallation.CreateWaylith(root, "1.7.1 rev3");
        var stub = temp.CreateFile("FakeGame.cs", """
            using System;
            using System.Collections.Generic;
            namespace Verse {
              [AttributeUsage(AttributeTargets.Class)] public sealed class StaticConstructorOnStartupAttribute : Attribute { }
              public static class LongEventHandler { public static void ExecuteWhenFinished(Action action) { action(); } }
              public sealed class ModContentPack {
                public string Name => "Example \"mod\" – 测试";
                public string PackageIdPlayerFacing => "author.example";
                public string RootDir => "/tmp/example project";
              }
              public static class LoadedModManager {
                public static List<ModContentPack> RunningModsListForReading = new List<ModContentPack> { new ModContentPack() };
              }
              public static class Log { public static void Warning(string message) { } }
            }
            namespace RimWorld { public static class VersionControl { public static string CurrentVersionStringWithRev => "1.7.1 rev3"; } }
            """);
        var assemblyPath = Path.Combine(root, "Assembly-CSharp.dll");
        var fakeGame = ModCompiler.Compile(new CompileRequest("Assembly-CSharp", [stub], GameReferenceResolver.HostRuntimeAssemblies,
            OutputPath: assemblyPath, EmitDebugSymbols: false));
        Assert.True(fakeGame.Succeeded, string.Join("; ", fakeGame.Diagnostics.Select(d => d.Display)));
        var paths = new AppPaths(temp.CreateDirectory("config"), temp.CreateDirectory("state"));
        var selected = InstallationProbe.Probe(root);
        Assert.False(CompanionInstaller.IsInstalled(selected, paths));
        var result = CompanionInstaller.BuildAndLink(selected, paths);
        Assert.True(Directory.Exists(result.GameLink));
        Assert.True(File.Exists(Path.Combine(result.ModDirectory, "1.7", "Assemblies", "RimPulse.Companion.dll")));
        Assert.True(CompanionInstaller.IsInstalled(selected, paths));
        Assert.True(CompanionInstaller.BuildAndLink(selected, paths).AlreadyLinked);
        File.WriteAllText(Path.Combine(root, "Version.txt"), "1.7.2");
        Assert.False(CompanionInstaller.IsInstalled(InstallationProbe.Probe(root), paths));
        File.WriteAllText(Path.Combine(root, "Version.txt"), "1.7.0");

        using var session = new CompanionSession();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = Channel.CreateUnbounded<CompanionConnection>();
        var monitor = session.MonitorAsync(Process.GetCurrentProcess().Id, new GameVersion(1, 7),
            change => events.Writer.TryWrite(change), cancellation.Token);
        var oldPort = Environment.GetEnvironmentVariable(WireProtocol.PortEnvironmentVariable);
        var oldToken = Environment.GetEnvironmentVariable(WireProtocol.TokenEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(WireProtocol.PortEnvironmentVariable, session.ChildEnvironment[WireProtocol.PortEnvironmentVariable]);
            Environment.SetEnvironmentVariable(WireProtocol.TokenEnvironmentVariable, session.ChildEnvironment[WireProtocol.TokenEnvironmentVariable]);
            Assembly.LoadFrom(assemblyPath);
            var companion = Assembly.LoadFrom(Path.Combine(result.ModDirectory, "1.7", "Assemblies", "RimPulse.Companion.dll"));
            var startup = companion.GetType("RimPulse.Companion.CompanionStartup", throwOnError: true)!;
            RuntimeHelpers.RunClassConstructor(startup.TypeHandle);
            var connected = await events.Reader.ReadAsync(cancellation.Token);
            Assert.True(connected.Connected);
            Assert.Equal("1.7.1 rev3", connected.Snapshot!.GameVersion);
            var mod = Assert.Single(connected.Snapshot.Mods);
            Assert.Equal("Example \"mod\" – 测试", mod.Name);
            Assert.Equal("author.example", mod.PackageId);
            Assert.Equal("/tmp/example project", mod.RootDir);

            // The game must reject an impostor listener before sending the token or any mod data.
            using var impostor = new TcpListener(IPAddress.Loopback, 0);
            impostor.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)impostor.LocalEndpoint).Port, cancellation.Token);
            using var accepted = await impostor.AcceptTcpClientAsync(cancellation.Token);
            var authenticate = startup.GetMethod("AuthenticateServer", BindingFlags.NonPublic | BindingFlags.Static)!;
            var gameAttempt = Task.Run(() =>
            {
                try
                {
                    authenticate.Invoke(null, [client.GetStream(), session.ChildEnvironment[WireProtocol.TokenEnvironmentVariable]]);
                    return false;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is IOException) { return true; }
            });
            using var impostorReader = new StreamReader(accepted.GetStream(), Encoding.ASCII, false, 128, leaveOpen: true);
            var hello = await impostorReader.ReadLineAsync(cancellation.Token);
            Assert.NotNull(hello);
            Assert.StartsWith("HELLO ", hello!, StringComparison.Ordinal);
            Assert.DoesNotContain(session.ChildEnvironment[WireProtocol.TokenEnvironmentVariable], hello!, StringComparison.Ordinal);
            await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes("AUTH " + new string('0', 64) + "\n"), cancellation.Token);
            Assert.True(await gameAttempt.WaitAsync(cancellation.Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WireProtocol.PortEnvironmentVariable, oldPort);
            Environment.SetEnvironmentVariable(WireProtocol.TokenEnvironmentVariable, oldToken);
            cancellation.Cancel();
            session.Dispose();
            await monitor;
        }
    }
}
