# Companion protocol v1

This describes the **implemented, experimental, read-only** local protocol. It has only been exercised with
synthetic game assemblies; interoperability with an installed RimWorld 1.6 or 1.7 still needs verification.
There are no commands, remote control, reloads, or unsolicited network listeners in the game.

## Lifecycle and trust

For each game launch RimPulse listens on an ephemeral **IPv4 loopback** port and generates a fresh 32-byte
random secret, encoded as 64 uppercase hexadecimal characters. `RIMPULSE_IPC_PORT` and `RIMPULSE_IPC_TOKEN`
are passed in that child's environment (and may be inherited by its descendants). The companion is inert if
either is absent or invalid. The secret
is not written to the RimPulse log or command line. It is nevertheless visible to sufficiently privileged
local processes, and may be visible to other processes running as the same user. Loopback and this handshake
are not a sandbox or protection from a hostile local account with access to the process environment.

Once game startup invokes the companion, the game thread takes a single snapshot of the game version and
`LoadedModManager.RunningModsListForReading`. Network I/O runs on a background thread. Before the companion
reveals its token or mod list, the two sides exchange newline-terminated ASCII lines:

```text
game → desktop: HELLO <64-character uppercase hex nonce>\n
desktop → game: AUTH <64-character uppercase hex HMAC-SHA256>\n
```

The HMAC key is the ASCII **text of the hexadecimal launch token**, not the decoded random bytes. The HMAC
data is the ASCII text of the hexadecimal nonce, also not decoded. The game uses 32 fresh cryptographic random
bytes for each nonce and compares the `AUTH` line without an early character mismatch exit. If it is wrong,
it disconnects before sending the status frame. The desktop then authenticates the game by verifying the token
inside each status frame and also checks the expected process ID, game `major.minor`,
protocol, field types and limits. This is mutual proof of the shared secret, not a guarantee against another
process that has obtained that secret.

## Status frame

After the proof, the game writes one UTF-8 JSON object per line. The same startup snapshot is repeated every
five seconds as a heartbeat. Example (the illustrative token and path are not real credentials):

```json
{"type":"status","protocol":1,"phase":"startup_snapshot","token":"<per-launch secret>","pid":1234,"gameVersion":"1.6.4851 rev598","mods":[{"name":"Core","packageId":"ludeon.rimworld","rootDir":"/path/to/RimWorld/Data/Core"}]}
```

| Field | Meaning and validation |
| --- | --- |
| `protocol` | Integer `1`; unknown versions are rejected. |
| `type` | Exactly `status`. |
| `token` | The per-launch uppercase hexadecimal token, checked in constant time after length validation. |
| `pid` | Positive integer equal to the actual process RimPulse launched. |
| `gameVersion` | Game-reported string (maximum 128 characters), parseable as the selected installation's `major.minor`. |
| `phase` | Exactly `startup_snapshot`; this is **not** a main-menu, map, or successful-mod-initialization signal. |
| `mods` | Startup snapshot of at most 2,048 mods, in game-reported order. Each element has string `name` (at most 1,024 characters), `packageId` (256) and `rootDir` (8,192). |

The string-length limits above count UTF-16 code units (`string.Length`). Lines must be valid UTF-8 and at most
512 KiB excluding the delimiter; the companion also checks its outgoing
frame including the newline. An oversize snapshot is not sent and the game log gets a warning. Malformed,
unauthenticated or incompatible messages are rejected; the desktop remains available for a later connection.
RimPulse matches the selected development project by **both** package ID and canonical directory, avoiding a
false match when another copy of the same mod is active. The list is not refreshed if the game's mod state
changes later. Displayed mod names and paths come from the game and are not a security boundary.

The desktop allows five seconds for the handshake and first frame, and 20 seconds per later heartbeat. On
failure or disconnect it clears the active snapshot and accepts reconnections while the launched process is
running. The companion waits five seconds between attempts and gives up after 12 consecutive failures;
a successful connection resets that count. Closing RimPulse stops its listener but deliberately **does not
terminate the game**. Process exit also stops monitoring. A restarted or different process is not accepted
under the old process ID; launch it again from RimPulse to establish a new session.

## Changing the protocol

`src/RimPulse.Companion/Shared/Protocol.cs` supplies the shared version, phase and size constants to Core and
to both separately compiled game adapters. Changes to the wire format or readiness semantics require updating
both ends, tests and this document; use a new version for incompatible changes. Do not add privileged game
actions until they have an explicit command contract, authorization policy and real-game verification.
