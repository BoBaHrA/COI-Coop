# First real Internet co-op relay test

This gate is for two players on different Internet connections/countries. It skips inbound port forwarding and uses an authenticated WebSocket relay. The host remains the simulation authority; the relay only forwards bytes.

## Architecture

Both Captain of Industry processes keep using the already-tested loopback gameplay lanes:

- 27015 authoritative gameplay
- 27016 building ghosts + sandbox state
- 27017 path/ramp ghosts
- 27018 blueprint ghosts

`InternetRelayBootstrap` wraps those byte streams in four outbound WebSocket connections. Both players therefore connect outward to the same relay and can be behind NAT/CGNAT.

## 1. Deploy the relay on Render

The repository contains `render.yaml` and `relay/`.

In Render:

1. New -> Blueprint.
2. Connect `BoBaHrA/COI-Coop`.
3. Select branch `dev/network-foundation` for this development gate.
4. Apply the Blueprint.
5. Wait for `coi-coop-relay` to become Live.
6. Copy its public HTTPS URL, e.g. `https://coi-coop-relay-xxxx.onrender.com`.
7. Opening `<relay-url>/health` should return JSON with `ok: true`.

Render terminates TLS. Public WebSocket clients use the same service through `wss://.../relay`.

## 2. Build the latest mod on the host PC

```bat
scripts\build-mod.bat
```

The local COI 0.8.7 build is authoritative because GitHub CI cannot compile against proprietary game DLLs.

## 3. Build an Internet friend kit

The base-game starting save should already have byte-identical `_HOST` / `_CLIENT` copies.

```bat
scripts\prepare-internet-friend-kit.bat -SourceSaveName "COOP_LAN_BASE" -RelayUrl "https://YOUR-RELAY.onrender.com"
```

Send `dist\COI-Coop-Internet-Client.zip` to the remote player.

## 4. Host

Run:

```bat
scripts\launch-internet-host.bat
```

Enter the relay HTTPS URL when prompted. The launcher calls `POST /api/session`, receives a short session code and a private host token, and starts Captain of Industry with the relay bridge enabled.

Send only the displayed session code to the other player. Do not send the host token; it remains only in the launcher/game process environment.

Load `COOP_LAN_BASE_HOST`.

## 5. Remote client

1. Extract `COI-Coop-Internet-Client.zip`.
2. Close Captain of Industry if it is running.
3. Run `INSTALL_AND_LAUNCH.bat`.
4. Enter the session code from the host.
5. Enable `COI Co-op Prototype` if needed.
6. Load `COOP_LAN_BASE_CLIENT`.

The client launcher exchanges the session code for a separate client token. Both peers then establish outbound authenticated WebSockets for all four lanes.

## Expected logs

Both peers should show messages similar to:

```text
COI-Coop: INTERNET RELAY enabled role=HOST session=ABCD-2345 ...
COI-Coop: RELAY gameplay WSS connected session=ABCD-2345 role=host lane=0
COI-Coop: RELAY gameplay relay connected to local host session
```

Client:

```text
COI-Coop: INTERNET RELAY enabled role=CLIENT session=ABCD-2345 ...
COI-Coop: RELAY gameplay local listener 127.0.0.1:27015
COI-Coop: RELAY gameplay WSS connected session=ABCD-2345 role=client lane=0
COI-Coop: NET CLIENT session connected
```

Once gameplay READY is reached, test commands and ghosts in both directions.

## Development security / limitations

- Session codes expire after two hours by default.
- Host/client receive separate 256-bit random bearer tokens.
- Tokens are sent in the WebSocket Authorization header, not in URLs.
- The relay forwards opaque binary lane data and does not simulate the world.
- The current relay stores sessions only in memory and is intentionally single-instance. Do not horizontally scale it yet.
- This is relay-first. Direct P2P/NAT traversal comes after the Internet gameplay gate is green.
- Presentation lanes may reconnect independently; authoritative gameplay still fails closed on a real gameplay disconnect.
