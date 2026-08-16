# Networking roadmap

## Product goal

COI-Coop should not require Radmin VPN, Hamachi, Steam networking, or manual LAN setup for normal play.

The intended player flow is:

1. Player A clicks **Host co-op**.
2. The mod creates a short-lived session and shows a short join code.
3. Player A sends that code to Player B.
4. Player B clicks **Join co-op**, enters the code, and connects.
5. The mod attempts the best available transport automatically.

Steam may be supported as an optional convenience later, but it must not be a dependency of the multiplayer protocol.

## Connection strategy

### Development mode

Current development transport intentionally binds to loopback (`127.0.0.1`) so two local game processes can exercise the protocol safely.

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
- expiration and cleanup of abandoned sessions.

It must not require a persistent player account for the first usable version.

## Security

Before Internet exposure, replace development plaintext loopback assumptions with:

- authenticated session tokens;
- encrypted transport (TLS/DTLS or an equivalent authenticated encrypted channel);
- random, rate-limited, short-lived join codes;
- replay/duplicate protection already compatible with the host authority sequence;
- strict payload size and protocol-version validation.

## Bandwidth model

Normal gameplay should stay small because COI-Coop transmits player intents / authority commits rather than streaming the whole simulation. Live placement previews are best-effort latest-wins presentation data.

The large transfer is expected during initial save synchronization or recovery. Save transfer should therefore use a separate reliable bulk-transfer path with hashing and resumable/chunked verification rather than blocking the command stream.

## UI target

Minimum polished flow:

- **Host co-op**
  - choose save;
  - create session;
  - display/copy join code;
  - show peer connected / syncing / ready states.
- **Join co-op**
  - enter join code;
  - connect automatically;
  - receive/verify starting save if required;
  - enter the synchronized game when both peers are ready.

Advanced/manual IP entry can remain as a developer/troubleshooting option, not the normal player experience.
