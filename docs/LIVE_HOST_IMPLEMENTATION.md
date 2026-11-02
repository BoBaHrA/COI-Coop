# Live host implementation (0.1.0 preview)

The `COI_COOP_LIVE=1` path replaces the old start-together replay path. It uses
the uploaded **Mafi.Core 0.8.8.0** assemblies. Proprietary assemblies and inspected
engine implementation are not part of this repository or distributable.

## Boundaries and persistence

- Host solo input is untouched until an authenticated guest requests a world.
- A separate snapshot socket queues work for `SimLoopEvents.Sync`.
- `SaveManager.SaveGame(..., Gzip)` captures current memory state, after a normal
  pending save has completed. No preselected disk save is published to the guest.
- Host UI commands waiting at capture are retained outside the serialized
  scheduler and replayed after READY. Simulation-derived queued commands remain
  in the snapshot and run locally once, without duplicate guest submissions.
- The current engine's `SetSimPause` gates missing frames at command-processing
  boundaries. It does not sleep on the sim thread. `AdaptiveSimSpeedMode.Uncapped`
  fixes the step budget across differently performing machines. The host's
  previous mode is restored when it returns to solo play.
- SHA-256 binds the snapshot; an unpredictable per-snapshot ticket binds the
  gameplay connection; hash of the game assemblies and mod DLL rejects different
  builds before capture. This does not yet fingerprint every third-party mod.
- Guest verifies its `GameNameConfig.LoadedFile` and downloaded file hash before
  attaching. The supported `--launch-last-game` switch loads the newly created
  disposable cache. A wrong selected file is rejected by the controller.
- Guest autosave is disabled through `SetCustomAutoSaveInterval(Duration.Zero)`.
  Its GameName comes from the unique cache directory, so manual saves are also
  disposable. The launcher deletes only marked cache directories, after exit.
- Guest disconnect closes its epoch. Host drains accepted work and retained
  original local commands, restores its pause/speed policy and accepts a new
  snapshot request. Rejoin is a fresh Join launcher run, not an in-process reload.

The live transport is direct TCP (gameplay port and port+1 for snapshots).
Legacy relay and mutating preview sidecars are not attached to live sessions.
Loopback legacy tests remain supported independently.

## Tests and limits

`GameApiSmokeTest` links the actual locally supplied game assemblies. It invokes
the real `SimLoopEvents` with a no-op command processor and the production
`LiveSimulationGate`. It verifies 500 paused wait iterations, UI-end callbacks,
and 300 matched outer frames at 1x/3x/20x with unequal wait schedules and pauses.
This tests the real simulation loop gate, not a whole generated island.

`LiveWorldSmokeTest` tests snapshot authentication/build rejection, byte-exact
transfer, ticket-bound gameplay, disconnect and joining a newer snapshot with
fresh authority counters. It uses synthetic snapshot bytes; it is a transport
test and does not validate the engine's complete save/load round trip.

Build and engine tests:

```powershell
dotnet build src/CoiCoop -c Release -p:COI_ROOT="C:\path\to\Captain of Industry" -p:DeployToModsFolder=false
dotnet run --project tools/GameApiSmokeTest -c Release -p:COI_ROOT="C:\path\to\Captain of Industry"
dotnet run --project tools/LiveWorldSmokeTest -c Release
```

Still requires two real Unity processes to validate snapshot capture/load,
initialization, gameplay input/callback behavior, long-run determinism, host
save/reload and the Windows startup flow. Probe coverage remains structural,
not a complete hash of economy, terrain, logistics and RNG state. No claim of
full in-game acceptance is made by this branch or its CI.
