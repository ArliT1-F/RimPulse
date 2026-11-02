using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimPulse.Core.Game;
using RimPulse.Protocol;

namespace RimPulse.Core.IPC;

/// <summary>A mod reported by the running game's own mod manager, in load order.</summary>
public sealed record RuntimeMod(string Name, string PackageId, string RootDir);

/// <summary>A single game-startup snapshot, not a guarantee that every mod loaded without errors.</summary>
public sealed record RuntimeSnapshot(string GameVersion, int ProcessId, IReadOnlyList<RuntimeMod> Mods);

/// <summary>Connection changes and rejected initial messages, without exposing credentials.</summary>
public sealed record CompanionConnection(bool Connected, RuntimeSnapshot? Snapshot, string Message);

/// <summary>
/// A per-launch, loopback-only, read-only status endpoint. The random token is passed only in the child
/// process environment; no command channel is exposed. The lifetime is owned by RimPulse, not the game.
/// </summary>
public sealed class CompanionSession : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _token;
    private bool _disposed;

    public CompanionSession()
    {
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>Pass these values to the selected game's environment, never to a log or command line.</summary>
    public IReadOnlyDictionary<string, string> ChildEnvironment => new Dictionary<string, string>
    {
        [WireProtocol.PortEnvironmentVariable] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [WireProtocol.TokenEnvironmentVariable] = _token,
    };

    /// <summary>Accepts reconnects until cancelled; unauthenticated, malformed and mismatched clients are rejected.</summary>
    public async Task MonitorAsync(int expectedProcessId, GameVersion expectedVersion,
        Action<CompanionConnection> onChange, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        if (expectedProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(expectedProcessId));
        if (!expectedVersion.IsSupported) throw new ArgumentException("A supported game version is required.", nameof(expectedVersion));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                var connected = false;
                try
                {
                    using var stream = client.GetStream();
                    var reader = new BoundedLineReader(stream);
                    using (var handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        handshakeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                        var hello = await reader.ReadLineAsync(handshakeDeadline.Token).ConfigureAwait(false);
                        if (hello is null || !hello.StartsWith("HELLO ", StringComparison.Ordinal) || hello.Length != 70 ||
                            !hello.AsSpan(6).ToString().All(Uri.IsHexDigit))
                            throw new InvalidDataException("Invalid companion challenge.");
                        var proof = HMACSHA256.HashData(Encoding.ASCII.GetBytes(_token), Encoding.ASCII.GetBytes(hello[6..]));
                        var response = Encoding.ASCII.GetBytes("AUTH " + Convert.ToHexString(proof) + "\n");
                        await stream.WriteAsync(response, handshakeDeadline.Token).ConfigureAwait(false);
                    }
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        deadline.CancelAfter(connected ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(5));
                        var line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                        if (line is null) break;
                        var snapshot = ParseFrame(line, _token, expectedProcessId, expectedVersion);
                        if (!connected)
                        {
                            connected = true;
                            onChange(new CompanionConnection(true, snapshot, "Companion connected; startup mod snapshot received."));
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Handshake/heartbeat timeout; the next client may still reconnect.
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or DecoderFallbackException)
                {
                    if (!connected && !cancellationToken.IsCancellationRequested)
                        onChange(new CompanionConnection(false, null,
                            "Companion message rejected (invalid protocol, authentication, process, version or data); still waiting."));
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    // A disconnected client is not a reason to stop accepting future clients.
                }
                finally
                {
                    if (connected && !cancellationToken.IsCancellationRequested)
                        onChange(new CompanionConnection(false, null, "Companion disconnected; waiting for it to reconnect."));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed || cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (_disposed || cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>Validates the bounded, authenticated v1 message before exposing any game data.</summary>
    public static RuntimeSnapshot ParseFrame(string frame, string expectedToken, int expectedProcessId, GameVersion expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (Encoding.UTF8.GetByteCount(frame) > WireProtocol.MaximumFrameBytes)
            throw new InvalidDataException("Companion message is too large.");

        using var document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "status" ||
            !root.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var version) || version != WireProtocol.Version ||
            !root.TryGetProperty("phase", out var phase) || phase.ValueKind != JsonValueKind.String || phase.GetString() != WireProtocol.StartupPhase ||
            !root.TryGetProperty("token", out var tokenNode) || tokenNode.ValueKind != JsonValueKind.String ||
            !TokenEquals(tokenNode.GetString(), expectedToken) ||
            !root.TryGetProperty("pid", out var pidNode) || pidNode.ValueKind != JsonValueKind.Number || !pidNode.TryGetInt32(out var pid) || pid != expectedProcessId ||
            !root.TryGetProperty("gameVersion", out var versionNode) || versionNode.ValueKind != JsonValueKind.String ||
            versionNode.GetString() is not { Length: <= 128 } ||
            !GameVersion.TryParse(versionNode.GetString(), out var actualVersion) ||
            actualVersion.Major != expectedVersion.Major || actualVersion.Minor != expectedVersion.Minor ||
            !root.TryGetProperty("mods", out var modNodes) || modNodes.ValueKind != JsonValueKind.Array || modNodes.GetArrayLength() > 2048)
            throw new InvalidDataException("Invalid or mismatched companion status message.");

        var mods = new List<RuntimeMod>(modNodes.GetArrayLength());
        foreach (var mod in modNodes.EnumerateArray())
        {
            if (mod.ValueKind != JsonValueKind.Object ||
                !TryString(mod, "name", 1024, out var name) ||
                !TryString(mod, "packageId", 256, out var packageId) ||
                !TryString(mod, "rootDir", 8192, out var rootDir))
                throw new InvalidDataException("Invalid mod in companion status message.");
            mods.Add(new RuntimeMod(name, packageId, rootDir));
        }
        return new RuntimeSnapshot(versionNode.GetString()!, pid, mods);
    }

    private static bool TryString(JsonElement element, string property, int limit, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.String) return false;
        var text = node.GetString();
        if (text is null || text.Length > limit) return false;
        value = text;
        return true;
    }

    private static bool TokenEquals(string? actual, string expected)
    {
        if (actual is null || actual.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Stop();
    }

    private sealed class BoundedLineReader(NetworkStream stream)
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _position;
        private int _length;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (_position == _length)
                {
                    _length = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _position = 0;
                    if (_length == 0)
                    {
                        if (line.Length == 0) return null;
                        throw new InvalidDataException("Incomplete companion message.");
                    }
                }

                var start = _position;
                while (_position < _length && _buffer[_position] != (byte)'\n') _position++;
                var count = _position - start;
                if (line.Length + count > WireProtocol.MaximumFrameBytes) throw new InvalidDataException("Companion message is too large.");
                line.Write(_buffer, start, count);
                if (_position < _length)
                {
                    _position++; // newline
                    return StrictUtf8.GetString(line.GetBuffer(), 0, checked((int)line.Length)).TrimEnd('\r');
                }
            }
        }
    }
}
