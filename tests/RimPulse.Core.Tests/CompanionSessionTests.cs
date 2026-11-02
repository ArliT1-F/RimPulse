using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RimPulse.Core.Game;
using RimPulse.Core.IPC;
using RimPulse.Core.Runtime;
using RimPulse.Core.Tests.TestSupport;
using RimPulse.Protocol;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class CompanionSessionTests
{
    private const string Token = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private static readonly GameVersion Version = new(1, 6);

    private static string Frame(string token = Token, int pid = 1234, int protocol = 1, string gameVersion = "1.6.4871 rev591")
        => JsonSerializer.Serialize(new
        {
            type = "status", protocol, phase = WireProtocol.StartupPhase, token, pid, gameVersion,
            mods = new[] { new { name = "A \"quoted\" mod – 测试", packageId = "author.mod", rootDir = "/tmp/my mod" } },
        });

    [Fact]
    public void ParseFrame_RequiresMatchingSecretProcessVersionAndProtocol()
    {
        var snapshot = CompanionSession.ParseFrame(Frame(), Token, 1234, Version);
        Assert.Equal("1.6.4871 rev591", snapshot.GameVersion);
        Assert.Equal("A \"quoted\" mod – 测试", Assert.Single(snapshot.Mods).Name);
        Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(Frame(token: new string('A', 64)), Token, 1234, Version));
        Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(Frame(pid: 99), Token, 1234, Version));
        Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(Frame(protocol: 2), Token, 1234, Version));
        Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(Frame().Replace(WireProtocol.StartupPhase, "main_menu", StringComparison.Ordinal), Token, 1234, Version));
        Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(Frame(gameVersion: "1.7.1"), Token, 1234, Version));
        Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(Frame(gameVersion: "not-a-version"), Token, 1234, Version));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"type\":\"status\",\"protocol\":\"one\"}")]
    [InlineData("[]")]
    public void ParseFrame_RejectsWrongShapes(string frame)
        => Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(frame, Token, 1234, Version));

    [Fact]
    public void ParseFrame_RejectsOversizedMessagesAndInvalidMods()
    {
        Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(new string('x', WireProtocol.MaximumFrameBytes + 1), Token, 1234, Version));
        var broken = Frame().Replace("\"rootDir\":\"/tmp/my mod\"", "\"rootDir\":42", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(broken, Token, 1234, Version));
        var tooMany = JsonSerializer.Serialize(new
        {
            type = "status", protocol = 1, phase = WireProtocol.StartupPhase, token = Token, pid = 1234, gameVersion = "1.6.1",
            mods = Enumerable.Range(0, 2049).Select(_ => new { name = "m", packageId = "a.b", rootDir = "/m" }),
        });
        Assert.Throws<InvalidDataException>(() => CompanionSession.ParseFrame(tooMany, Token, 1234, Version));
    }

    [Fact]
    public async Task Session_AuthenticatesBothSidesRejectsWrongClientAndAcceptsReconnect()
    {
        using var session = new CompanionSession();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = Channel.CreateUnbounded<CompanionConnection>();
        var monitor = session.MonitorAsync(1234, Version, change => events.Writer.TryWrite(change), cancellation.Token);
        var token = session.ChildEnvironment[WireProtocol.TokenEnvironmentVariable];
        Assert.Equal(64, token.Length);
        Assert.NotEqual(Token, token);

        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync(IPAddress.Loopback, session.Port, cancellation.Token);
            await AuthenticateAsync(bad, token, cancellation.Token);
            await SendAsync(bad, Frame(token: Token), cancellation.Token); // valid format, wrong secret
            var rejected = await events.Reader.ReadAsync(cancellation.Token);
            Assert.False(rejected.Connected);
            Assert.Contains("rejected", rejected.Message, StringComparison.Ordinal);
        }
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, session.Port, cancellation.Token);
            await AuthenticateAsync(client, token, cancellation.Token);
            await SendAsync(client, Frame(token: token), cancellation.Token);
            var connected = await events.Reader.ReadAsync(cancellation.Token);
            Assert.True(connected.Connected);
            Assert.Equal(1234, connected.Snapshot!.ProcessId);
            await SendAsync(client, Frame(token: token), cancellation.Token); // a heartbeat is not another connect
        }
        var disconnected = await events.Reader.ReadAsync(cancellation.Token);
        Assert.False(disconnected.Connected);

        using (var retry = new TcpClient())
        {
            await retry.ConnectAsync(IPAddress.Loopback, session.Port, cancellation.Token);
            await AuthenticateAsync(retry, token, cancellation.Token);
            await SendAsync(retry, Frame(token: token), cancellation.Token);
            Assert.True((await events.Reader.ReadAsync(cancellation.Token)).Connected);
        }
        cancellation.Cancel();
        session.Dispose();
        await monitor;
    }

    [Fact]
    public void ProjectMatch_RequiresBothPackageIdAndRealDirectory()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("project");
        var other = temp.CreateDirectory("other");
        var snapshot = new RuntimeSnapshot("1.6.1", 1, [new RuntimeMod("Example", "author.mod", other)]);
        Assert.Contains("does not match", RuntimeProjectMatch.Describe(snapshot, "author.mod", project), StringComparison.Ordinal);
        if (OperatingSystem.IsLinux())
        {
            var link = Path.Combine(temp.Root, "linked-project");
            Directory.CreateSymbolicLink(link, project);
            snapshot = new RuntimeSnapshot("1.6.1", 1, [new RuntimeMod("Example", "author.mod", link)]);
            Assert.Contains("present in the startup", RuntimeProjectMatch.Describe(snapshot, "AUTHOR.MOD", project), StringComparison.Ordinal);

            var parent = temp.CreateDirectory("parent");
            var nestedProject = Directory.CreateDirectory(Path.Combine(parent, "nested")).FullName;
            var parentLink = Path.Combine(temp.Root, "linked-parent");
            Directory.CreateSymbolicLink(parentLink, parent);
            snapshot = new RuntimeSnapshot("1.6.1", 1, [new RuntimeMod("Nested", "author.mod", nestedProject)]);
            Assert.Contains("present in the startup", RuntimeProjectMatch.Describe(snapshot, "author.mod", Path.Combine(parentLink, "nested")), StringComparison.Ordinal);
        }
    }

    private static async Task AuthenticateAsync(TcpClient client, string token, CancellationToken cancellation)
    {
        const string nonce = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HELLO " + nonce + "\n"), cancellation);
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 128, leaveOpen: true);
        var response = await reader.ReadLineAsync(cancellation);
        var expected = HMACSHA256.HashData(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(nonce));
        Assert.Equal("AUTH " + Convert.ToHexString(expected), response);
    }

    private static async Task SendAsync(TcpClient client, string frame, CancellationToken cancellation)
        => await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(frame + "\n"), cancellation);
}
