# COI-Coop

Experimental two-player shared-world co-op mod for **Captain of Industry**.

![Transport smoke test](https://github.com/BoBaHrA/COI-Coop/actions/workflows/transport-smoke-test.yml/badge.svg?branch=dev%2Fnetwork-foundation)

Development is currently on `dev/network-foundation`.

## Goal

The goal is intentionally simple: **two players control the same authoritative world together**.

Both players should be able to build, configure the factory, use the ship/world-map layer, pause/change speed, and issue normal game actions without duplicate packets or conflicting state corruption.

This is not separate-player simulation, PvP, or independent economies.

## Conflict model

- The host assigns one authoritative sequence to every accepted action.
- Duplicate client commands are ignored.
- Most simultaneous actions are simply applied in host order.
- Stale/invalid actions are rejected when their target is no longer valid.
- Selected state-setting intents may be marked as `Supersede`.
- Example: if both players order the same ship to different destinations nearly simultaneously, the latest accepted valid destination wins.
- Order-sensitive commands such as toggles are never collapsed blindly.

## Current status

- Native COI mod manifest and .NET Framework 4.8 project.
- Explicit `CoiCoop.CoiCoopMod` entry point using the `IMod` lifecycle.
- Local TCP protocol with version handshake and bidirectional PING/PONG.
- Standalone transport smoke test running in GitHub Actions.
- Validated locally against `Mafi.Core 0.8.7.0`.
- `InputScheduler.m_commandsToProcess` compatibility confirmed.
- Live `IInputCommand` capture confirmed for construction, demolition, pause, farm settings, and machine recipe settings.
- Development command serialization round-trip probe added.
- Host authority sequencing, duplicate suppression, and latest-intent batch collapse added.
- Windows helper scripts for automatic build/deploy and filtered diagnostic collection.

## Next milestones

1. Verify command serialization round-trip on the current game build.
2. Send the first serialized command packet between two game instances.
3. Replay that command through the client `InputScheduler` at a safe simulation boundary.
4. Expand coverage across main-map building/settings/logistics actions.
5. Capture and synchronize world-map/ship actions as a first-class subsystem.
6. Add host save transfer, late join, desync probes, and recovery.

See `docs/TESTING.md`, `docs/ARCHITECTURE.md`, and `docs/COOP_SCOPE.md` for details.
