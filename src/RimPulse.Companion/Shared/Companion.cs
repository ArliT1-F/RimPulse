using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RimPulse.Protocol;
using Verse;

namespace RimPulse.Companion
{
    /// <summary>
    /// Captures game-owned data on RimWorld's startup thread, then sends only that immutable snapshot from a
    /// background thread. No game APIs are called from the socket thread and no remote commands are accepted.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CompanionStartup
    {
        static CompanionStartup()
        {
            string rawPort = Environment.GetEnvironmentVariable(WireProtocol.PortEnvironmentVariable);
            string token = Environment.GetEnvironmentVariable(WireProtocol.TokenEnvironmentVariable);
            int port;
            if (string.IsNullOrEmpty(token) || token.Length != 64 || !IsUpperHex(token) ||
                !int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                port < 1 || port > 65535)
                return; // A normally started game remains unaffected.

            try
            {
                // This callback runs after the current long event, not on the network thread.
                LongEventHandler.ExecuteWhenFinished(delegate { CaptureAndStart(port, token); });
            }
            catch (Exception ex)
            {
                Log.Warning("[RimPulse] Unable to schedule companion startup: " + ex.Message);
            }
        }

        private static void CaptureAndStart(int port, string token)
        {
            try
            {
                var mods = new List<ModSnapshot>();
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                    mods.Add(new ModSnapshot(mod.Name, mod.PackageIdPlayerFacing, mod.RootDir));

                string frame = MakeFrame(token, Process.GetCurrentProcess().Id, GameAdapter.Version, mods);
                if (Encoding.UTF8.GetByteCount(frame) + 1 > WireProtocol.MaximumFrameBytes)
                {
                    Log.Warning("[RimPulse] The loaded-mod snapshot is too large to send; see the RimPulse protocol limit.");
                    return;
                }
                var thread = new Thread(new ThreadStart(delegate { SendSnapshots(port, token, frame); }));
                thread.IsBackground = true;
                thread.Name = "RimPulse companion";
                thread.Start();
            }
            catch (Exception ex)
            {
                Log.Warning("[RimPulse] Unable to capture runtime status: " + ex.Message);
            }
        }

        private static void SendSnapshots(int port, string token, string frame)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(frame + "\n");
            if (bytes.Length > WireProtocol.MaximumFrameBytes)
                return; // Never emit a partial or invalid snapshot.

            // Retry without calling game APIs. A closed RimPulse does not terminate the game.
            int failures = 0;
            while (failures < 12)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        client.NoDelay = true;
                        client.SendTimeout = 5000;
                        client.Connect(IPAddress.Loopback, port);
                        using (NetworkStream stream = client.GetStream())
                        {
                            stream.ReadTimeout = 5000;
                            AuthenticateServer(stream, token);
                            failures = 0;
                            while (true)
                            {
                                stream.Write(bytes, 0, bytes.Length);
                                Thread.Sleep(WireProtocol.HeartbeatMilliseconds);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    failures++;
                    Thread.Sleep(WireProtocol.HeartbeatMilliseconds);
                }
            }
        }

        // The listener must prove possession of the per-launch secret before receiving the token or mod list.
        // This prevents an unrelated local process that later reuses the port from harvesting the snapshot.
        private static void AuthenticateServer(NetworkStream stream, string token)
        {
            byte[] nonceBytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(nonceBytes);
            string nonce = Hex(nonceBytes);
            byte[] request = Encoding.ASCII.GetBytes("HELLO " + nonce + "\n");
            stream.Write(request, 0, request.Length);
            var response = new StringBuilder();
            while (response.Length < 80)
            {
                int b = stream.ReadByte();
                if (b < 0) throw new IOException("Companion listener closed before authentication.");
                if (b == '\n') break;
                response.Append((char)b);
            }
            string expected;
            using (var hmac = new HMACSHA256(Encoding.ASCII.GetBytes(token)))
                expected = "AUTH " + Hex(hmac.ComputeHash(Encoding.ASCII.GetBytes(nonce)));
            string actual = response.ToString();
            if (actual.Length != expected.Length) throw new IOException("Companion listener authentication failed.");
            int difference = 0;
            for (int i = 0; i < expected.Length; i++) difference |= actual[i] ^ expected[i];
            if (difference != 0) throw new IOException("Companion listener authentication failed.");
        }

        private static bool IsUpperHex(string value)
        {
            foreach (char ch in value)
                if (!((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F'))) return false;
            return true;
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) text.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static string MakeFrame(string token, int pid, string gameVersion, List<ModSnapshot> mods)
        {
            var json = new StringBuilder();
            json.Append("{\"type\":\"status\",\"protocol\":").Append(WireProtocol.Version);
            json.Append(",\"phase\":").Append(Quote(WireProtocol.StartupPhase));
            json.Append(",\"token\":").Append(Quote(token));
            json.Append(",\"pid\":").Append(pid.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"gameVersion\":").Append(Quote(gameVersion));
            json.Append(",\"mods\":[");
            for (int i = 0; i < mods.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"name\":").Append(Quote(mods[i].Name));
                json.Append(",\"packageId\":").Append(Quote(mods[i].PackageId));
                json.Append(",\"rootDir\":").Append(Quote(mods[i].RootDir)).Append('}');
            }
            return json.Append("]}").ToString();
        }

        // No JSON package is required in the game's Mono runtime. Escapes control characters, quotes and
        // backslashes; UTF-8 encoding handles the remaining Unicode text. Invalid surrogates are replaced.
        private static string Quote(string value)
        {
            if (value == null) value = "";
            var result = new StringBuilder(value.Length + 2).Append('"');
            foreach (char ch in value)
            {
                if (ch == '"' || ch == '\\') result.Append('\\').Append(ch);
                else if (ch < 0x20) result.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else result.Append(ch);
            }
            return result.Append('"').ToString();
        }

        private sealed class ModSnapshot
        {
            public readonly string Name;
            public readonly string PackageId;
            public readonly string RootDir;
            public ModSnapshot(string name, string packageId, string rootDir)
            {
                Name = name;
                PackageId = packageId;
                RootDir = rootDir;
            }
        }
    }
}
