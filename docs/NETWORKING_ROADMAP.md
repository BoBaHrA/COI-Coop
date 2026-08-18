# Networking roadmap

## Product goal

COI-Coop should not require Radmin VPN, Hamachi, Steam networking, or manual LAN setup for normal play.

The intended player flow is:

1. Player A clicks **Host co-op** and opens their existing Captain of Industry world.
2. That host world/save is the canonical persistent world for the session.
3. The mod creates a short-lived session and shows a short join code.
4. Player A sends that code to Player B.
5. Player B clicks **Join co-op**, enters the code, and connects to the already-hosted world.
6. The client does not choose, own, or maintain an independent campaign save for that co-op world.
7. Internally, the client may load a temporary synchronized snapshot/cache of the host world so Captain of Industry can run a local deterministic simulation, but that file is an implementation detail rather than a second authoritative save.

Steam may be supported as an optional convenience later, but it must not be a dependency of the multiplayer protocol.

## Host-owned world model

The host owns persistence. The shared campaign exists as the host's save and should behave like a normal host-created co-op world from the player's perspective.

- Only the host save is canonical and survives as the campaign world.
- A client joining the session receives a verified host snapshot and never chooses a separately progressed world.
- Any client-side `.save` file used by the prototype is an ephemeral synchronization cache only. It must never be treated as another source of truth.
- Normal client autosaves must not become a second campaign branch. Session-cache files should eventually be hidden/managed by the mod and cleaned up automatically.
- If the host exits, the shared session ends. Continuing without that host would require an explicit future host-transfer feature.

This model still requires Captain of Industry to run a local simulation on both machines. COI-Coop synchronizes deterministic simulation state; it does not stream the host's rendered game to the client.

## Join and catch-up target

The long-term Join flow should allow the host to already be inside their world when another player connects:

1. The host establishes a safe authority checkpoint and produces a snapshot of the canonical world.
2. The snapshot is uploaded to the session bulk-transfer path with its SHA256 and checkpoint metadata.
3. The host may continue after the checkpoint while accepted authority commands are retained in a bounded catch-up journal.
4. The client downloads and verifies the snapshot, loads the temporary local mirror, then replays authority commands after the checkpoint until it reaches the current host boundary.
5. Only after catch-up succeeds does the client send gameplay READY and enter normal lockstep.

Until the catch-up journal is implemented, the conservative version may temporarily pause/block host progression while a client snapshot is being loaded. What must not happen is allowing an independently stale client save to silently join a newer host world.

## Connection strategy

### Development mode

Current development transport intentionally binds to loopback (`127.0.0.1`) around the Captain of Industry process and bridges those lanes to the Internet relay.

### Production path

Use a small rendezvous/signaling service to resolve an expiring session code to the host session. The service does not simulate Captain of Industry and does not own gameplay authority.

Preferred connection order:

1. direct peer-to-peer connection when the peers are reachable;
2. NAT traversal / hole punching where possible;
3. relay fallback when direct connectivity is blocked by NAT, CGNAT, firewalls, or restrictive networks.

The host remains gameplay-authoritative in all three cases. Switching between direct and relayed transport must not change command ordering semantics.

## Service responsibilities

The rendezvous/relay service may provide:

- creation of short-lived random session IDs / human-readable join codes;
- exchange of ephemeral connection candidates;
- authentication of the two peers with one-time session tokens;
- optional encrypted relay of gameplay packets when direct connectivity is unavailable;
- temporary authenticated storage/transfer of host snapshots for join and recovery;
- expiration and cleanup of abandoned sessions.

It must not require a persistent player account for the first usable version.

## Security

Before Internet exposure, replace development plaintext loopback assumptions with:

- authenticated session tokens;
- encrypted transport (TLS/DTLS or an equivalent authenticated encrypted channel);
- random, rate-limited, short-lived join codes;
- replay/duplicate protection already compatible with the host authority sequence;
- strict payload size and protocol-version validation;
- cryptographic verification of transferred snapshots before they can become a client session cache.

## Bandwidth model

Normal gameplay should stay small because COI-Coop transmits player intents / authority commits rather than streaming the whole simulation. Live placement previews are best-effort latest-wins presentation data.

The large transfer is expected during initial join or recovery. Save transfer should therefore use a separate reliable bulk-transfer path with hashing and, later, resumable/chunked verification rather than blocking the command stream.

## UI target

Minimum polished flow:

- **Host co-op**
  - open/choose the host's real save;
  - create session;
  - display/copy join code;
  - show peer connected / syncing / catching up / ready states.
- **Join co-op**
  - enter join code;
  - connect automatically;
  - receive/verify the host snapshot as an internal session cache;
  - catch up to the current host authority boundary;
  - enter the host world without choosing a separate client campaign save.

Advanced/manual IP entry can remain as a developer/troubleshooting option, not the normal player experience.
