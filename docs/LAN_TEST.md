# Two-PC LAN co-op gate

This is the first physical-PC networking milestone. It deliberately preserves the already-tested loopback gameplay and preview protocols and adds a temporary LAN bridge around them.

## Ports

Internal loopback lanes remain unchanged:

- `27015` authoritative gameplay
- `27016` building ghosts + sandbox state
- `27017` path/ramp ghosts
- `27018` blueprint ghosts

For the LAN development bridge the host exposes TCP:

- `27115` gameplay tunnel
- `27116` preview tunnel
- `27117` path/ramp tunnel
- `27118` blueprint tunnel

These external ports are temporary development plumbing and will be replaced by the final multiplexed session transport.

## Prerequisites

Both PCs must have:

1. the same Captain of Industry / Mafi.Core version;
2. the same COI-Coop build;
3. byte-identical starting save data;
4. network access to the host PC on TCP `27115-27118`.

The blueprint library itself does not need to match between players.

## Host

1. Build/deploy the latest mod with `scripts\build-mod.bat`.
2. Prepare two identical save copies with `scripts\prepare-replay-saves.bat` if needed.
3. Copy the `_CLIENT.save` file and the deployed COI-Coop mod to the second PC.
4. Run `scripts\launch-lan-host.bat`.
5. The launcher prints candidate LAN IPv4 addresses. Give the appropriate `192.168.x.x` / `10.x.x.x` address to the client.
6. Load the `_HOST` save.

The host bridge binds `0.0.0.0:27115-27118` and forwards each lane to the proven local loopback sessions.

## Client

1. Install the exact same COI-Coop build.
2. Put the byte-identical `_CLIENT.save` into the Captain of Industry save directory.
3. Run `scripts\launch-lan-client.bat`.
4. Enter the host's LAN IPv4 address when prompted.
5. Load the `_CLIENT` save.

The client bridge accepts the game's existing loopback connections on `127.0.0.1:27015-27018` and forwards them to the host's `27115-27118` LAN endpoints.

## Firewall

On the host, allow `Captain of Industry.exe` on **Private networks** if Windows Defender Firewall prompts. If no prompt appears and the client cannot connect, verify inbound TCP access to `27115-27118` before changing co-op code.

## Expected logs

Host should show lines similar to:

```text
COI-Coop: LAN HOST bridge enabled bind=0.0.0.0 external=27115-27118 -> loopback=27015-27018
COI-Coop: LAN gameplay tunnel listening on 0.0.0.0:27115 -> 127.0.0.1:27015
COI-Coop: LAN gameplay tunnel connected
COI-Coop: NET HOST session connected
```

Client should show:

```text
COI-Coop: LAN CLIENT bridge enabled host=192.168.x.x external=27115-27118 <- loopback=27015-27018
COI-Coop: LAN gameplay tunnel connected
COI-Coop: NET CLIENT session connected
```

All four tunnels should connect once their corresponding gameplay/preview sessions start.

## Gate

Do not merge the development PR based on a successful connection alone. The LAN gate is complete only after a normal two-PC gameplay session proves:

- both peers reach gameplay READY;
- commands work in both directions;
- ordinary/multi/path/ramp/blueprint ghosts work;
- sandbox source/sink state works;
- world map/fleet actions still work;
- no unexpected `REPLAY HALT`, `NETWORK RX FAIL`, `SERIALIZE FAIL`, or persistent state mismatch occurs during play.
