# Host-owned world: implementation audit, 2026-11-02

## Required player experience

The host owns the only persistent campaign. The host can load it and play alone,
invite a friend into the running world, continue after the friend leaves, save,
and later accept the friend again. The guest receives disposable session state;
no manual matching of saves, client campaign, or simultaneous launch is required.

## Verified repository state

Audit baseline: `220ee0be31f9ca9d4ec697f9b31ba9916fe23342`,
`dev/network-foundation`. The existing implementation is a command replay
prototype, not that player experience.

| Area | Current implementation / missing behavior |
| --- | --- |
| Host alone | `CoiCoopMod.OnBeforeCommandProcessing` clears local commands until connected. |
| Guest leaves | The same hook permanently faults replay after disconnect. |
| Simulation barrier | Clearing commands does not gate simulation updates. Client frame waits block the simulation callback. A real tick/outer-step gate must be verified against game assemblies. |
| Starting state | Internet launcher uploads a disk save once, before launching the host. It cannot snapshot the current live world. |
| Auto-load | Launcher sets `COI_COOP_AUTOJOIN` and cache variables, but no C# consumer exists in this revision. These variables do not implement loading. |
| Rejoin | Relay invalidates the gameplay session. There is no live snapshot capture, reload or catch-up adapter. |
| Desync detection | `CompareStateProbe` skips different simulation coordinates and only logs persistent fingerprint mismatch. That is not recovery or a simulation stop. |
| Persistence | Disposable cache naming/cleanup exists in the launcher. No engine-level guest save/autosave policy is implemented. |

## Changes made in this branch

- Replace gameplay `StreamReader.ReadLine` / `NetworkStream.DataAvailable` mixing
  with one bounded incremental reader shared by handshake and gameplay. It
  consumes coalesced buffered messages and preserves partial lines without
  blocking outgoing traffic.
- Detect idle EOF; exchange heartbeats and expire a silent connection.
- Reject guest gameplay submissions before matching snapshot READY. Previously
  a rejected baseline could still enqueue authoritative commands.
- Publish connection state only after readiness counters are reset.
- Refuse reuse of a progressed or gameplay-ready transport after disconnect.
  This is a correctness guard, **not** seamless reconnect: the future session
  coordinator must install a fresh snapshot and new transport epoch.
- Add four executable lifecycle regression scenarios to CI.

## Validation performed

Using .NET SDK 8.0.427 on Linux:

- Existing TransportSmokeTest: pass.
- Existing BaselineReadySmokeTest: pass.
- Existing PreviewSmokeTest: pass.
- New SessionLifecycleSmokeTest: 4/4 pass (coalesced messages, fragmented input
  with simultaneous output, pre-READY/wrong-baseline commands, EOF/stale rejoin).
- Same new regression harness against baseline source: 0/4 pass. Its public
  RequiresResync assertion was omitted for baseline compatibility; the wire
  rejection expectation was unchanged. The baseline authorization test times
  out due to the separate buffered-read bug before its queue assertion.
- `npm run test:smoke` in relay: pass.
- `git diff --check`: pass.

These are protocol/transport tests. No game DLL build, two-game session,
determinism soak, or host save/reload acceptance has been performed here.
Existing nullable warnings in linked net8 test projects remain.

## Required integration before a playable build

1. Inspect the installed game's `Captain of Industry_Data/Managed` assemblies
   (at minimum Mafi.dll, Mafi.Core.dll and relevant game/UI dependencies) to
   identify real save/load and simulation gating APIs. Do not invent method
   names or replace those APIs with test stubs and call that compatibility.
2. At a safe host simulation boundary, capture a fresh snapshot and its actual
   simulation coordinate, game/mod compatibility identifiers and a new epoch.
   Keep the host as the only persistence authority.
3. Transfer and verify that snapshot; load it automatically on the guest. Hold
   the host briefly at the captured boundary, or retain a bounded deterministic
   journal and implement verified catch-up. Disk-file equality alone does not
   prove equality of the loaded live worlds.
4. Start ordered gameplay only after the guest acknowledges the loaded epoch and
   coordinate. Control simulation advancement, not just the input queue. Handle
   pause and speed changes consistently on both machines.
5. On guest loss, finish already accepted host-authoritative work exactly once,
   detach the guest and let the host continue. Future joins get current host
   state. Guest disconnection must not turn its cache into a second campaign.
6. Add a visible Host/Join/connection-status flow and guest save/autosave policy.
   On divergence, suspend guest mutation and resnapshot rather than log-and-run.

## In-game acceptance gate

Two real game processes with the same verified game/mod build must demonstrate:

- Host loads a normal save, plays alone for several in-game days, then guest joins.
- Both build, demolish, change recipes and logistics, pause and change speed.
- Simulation coordinates and broader subsystem state remain equal at matched
  boundaries; no replay/serialization faults during concurrent activity.
- Guest exits normally, then loses network abruptly; host can continue and save.
- Guest rejoins both cases with no manual save exchange or retained guest save.
- Host restarts from its own save; guest deletes its entire session cache and
  still joins successfully.

Until this gate passes, keep the mod labelled experimental and do not describe
transport CI as evidence of working co-op.
