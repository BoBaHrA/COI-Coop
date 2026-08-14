# COI-Coop

Experimental cooperative multiplayer mod for **Captain of Industry**.

![Transport smoke test](https://github.com/BoBaHrA/COI-Coop/actions/workflows/transport-smoke-test.yml/badge.svg?branch=dev%2Fnetwork-foundation)

Development is currently on `dev/network-foundation`.

## Current status

- Native COI mod manifest and .NET Framework 4.8 project.
- Explicit `CoiCoop.CoiCoopMod` entry point using the `IMod` lifecycle.
- Local TCP protocol with version handshake and bidirectional PING/PONG.
- Standalone transport smoke test running in GitHub Actions.
- Update 4.2 compatibility probe for `InputScheduler.m_commandsToProcess`.
- Development command observer for processed `IInputCommand` types.
- Windows helper scripts for automatic build/deploy and filtered diagnostic collection.

The transport is intentionally restricted to `127.0.0.1` until the first in-game compatibility test succeeds.

## Next milestone

Load the prototype on the current Captain of Industry build and verify that:

1. `CoiCoopMod.Initialize()` runs successfully.
2. `InputScheduler` and `ISimLoopEvents` are resolved.
3. `InputScheduler.m_commandsToProcess` still exists on Update 4.2.
4. Building/demolishing something produces `COI-Coop: INPUT ...` log entries.
5. Two local game processes complete the network handshake.

See `docs/TESTING.md` for the test procedure and `docs/ARCHITECTURE.md` for design notes.
