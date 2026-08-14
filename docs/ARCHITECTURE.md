# COI-Coop architecture notes

## Target model

COI-Coop is being built around a **host-authoritative input lockstep** model.

The intended session flow is:

1. Both peers start from the exact same host save snapshot.
2. Player actions are captured before the game processes them.
3. The host assigns authoritative ordering to commands.
4. Both peers execute the same authoritative command stream at the same simulation boundary.
5. Lightweight state fingerprints detect divergence.
6. Small, understood divergences may eventually be repaired locally; unknown divergence falls back to a host snapshot.

The simulation thread must never block on socket I/O. Network receive/send work belongs on background threads and only queues validated frames for the simulation thread.

## Current compatibility gate

The first game-facing milestone checks the Update 4.2 build for the integration points we expect:

- `Mafi.Core.Input.InputScheduler`
- private `InputScheduler.m_commandsToProcess`
- `InputScheduler.OnCommandProcessed`
- `Mafi.Core.Simulation.ISimLoopEvents`
- normal `IMod.Initialize(...)` dependency resolution

The prototype only observes processed command types. It does not mutate the command queue yet.

## Planned frame pipeline

Future lockstep work should be split into narrow layers:

### Capture

Read the local player command batch at a safe pre-processing boundary. Do not serialize UI state; only commands that affect simulation state belong in the authoritative stream.

### Codec

Serialize an `IInputCommand` into a deterministic byte representation and recreate it on the peer. Prefer the game's own serialization primitives when they are stable for a command type. Add explicit codecs only for commands that contain non-serializable runtime/prototype references.

### Authority

The host seals ordered frames. Client input is submitted to the host and is not authoritative until returned in a host frame.

### Apply

Replace the local to-process batch with the authoritative batch at the same simulation boundary on both peers.

### Verification

Exchange periodic probes such as simulation coordinate, entity count/ID fingerprint, population and selected deterministic RNG/state values. A mismatch must stop normal advancement before it compounds.

### Recovery

Start conservative: if the peers cannot prove a safe local correction, pause and reload a fresh host snapshot. Hot repair can be added later for narrowly understood differences.

## Prior art

During development we found the open-source `C-makabaka/captain-of-industry-cooplink` project (v0.9.15), released publicly in August 2026. It demonstrates that Captain of Industry multiplayer is practical and documents a host-authoritative TCP lockstep approach using the same broad engine integration points.

COI-Coop is currently implementing its own small foundation rather than importing CoopLink wholesale. If substantial CoopLink code is ever incorporated or adapted, its COI-Open license requirements and original-author attribution must be carried forward.

CoopLink v0.9.15 declares compatibility with Captain of Industry v0.8.0 Build 548 only, so Update 4.2 compatibility still needs to be established independently.
