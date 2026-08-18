# COI-Coop prototype testing

The branch now has three useful test levels:

1. standalone transport/authority smoke tests in GitHub Actions;
2. safe single-process command capture + serializer checks in a real game install;
3. an explicitly gated two-process authoritative replay experiment.

## 1. Build and deploy

From the repository root:

```powershell
scripts\build-mod.bat
```

The helper locates Steam libraries automatically and deploys the mod to:

```text
%APPDATA%\Captain of Industry\Mods\CoiCoop
```

## 2. Safe single-process test

Keep `network_mode = 0`. The experimental replay gate defaults to `false`.

Expected startup lines:

```text
COI-Coop: constructed
COI-Coop: Mafi.Core assembly version ...
COI-Coop: Initialize; gameWasLoaded=True
COI-Coop: COMPATIBILITY OK - InputScheduler command queue found
COI-Coop: pre-processing command hook attached
COI-Coop: command observer attached
COI-Coop: networking is disabled (mode 0)
```

With the development probes enabled, normal player actions should produce lines such as:

```text
COI-Coop: PREPROCESS SEEN Mafi.Core....SomeCommand
COI-Coop: INPUT Mafi.Core....SomeCommand
COI-Coop: SERIALIZE OK type=Mafi.Core....SomeCommand bytes=...
```

Diagnostics can be collected with:

```powershell
scripts\collect-diagnostics.bat
```

## 3. Persistent transport, replay OFF

Environment variables override the shared mod config per process.

Host:

```powershell
$env:COI_COOP_MODE = "1"
$env:COI_COOP_PORT = "27015"
$env:COI_COOP_REPLAY = "0"
& "<COI_ROOT>\Captain of Industry.exe"
```

Client:

```powershell
$env:COI_COOP_MODE = "2"
$env:COI_COOP_PORT = "27015"
$env:COI_COOP_REPLAY = "0"
& "<COI_ROOT>\Captain of Industry.exe"
```

Expected logs include `session connected`, `NETWORK TX`, and `NETWORK RX OK ... replay=OFF`.

## 4. First authoritative replay experiment

This mode is intentionally opt-in and is not yet normal gameplay.

Use a disposable copy of a save and keep both instances paused. Both instances must load the same starting save/state before any synchronized action is attempted. Do not test pause/speed changes yet.

Host:

```powershell
$env:COI_COOP_MODE = "1"
$env:COI_COOP_PORT = "27015"
$env:COI_COOP_REPLAY = "1"
& "<COI_ROOT>\Captain of Industry.exe"
```

Client:

```powershell
$env:COI_COOP_MODE = "2"
$env:COI_COOP_PORT = "27015"
$env:COI_COOP_REPLAY = "1"
& "<COI_ROOT>\Captain of Industry.exe"
```

The replay gate blocks local player commands until the peer is connected. Once both logs show the session connected, make exactly one simple action first, preferably placing a basic building while paused.

Expected host/client flow:

```text
COI-Coop: EXPERIMENTAL AUTHORITATIVE REPLAY = ON
COI-Coop: NET ... session connected
COI-Coop: REPLAY SUBMIT type=...BatchCreateStaticEntitiesCmd bytes=...
COI-Coop: REPLAY QUEUED seq=0 origin=... type=...BatchCreateStaticEntitiesCmd bytes=...
COI-Coop: REPLAY APPLIED seq=0 origin=... type=...BatchCreateStaticEntitiesCmd
```

Both peers should receive and apply the same authority sequence.

If any peer logs one of these, stop the experiment and reload the disposable save:

```text
COI-Coop: REPLAY HALT - ...
COI-Coop: REPLAY BLOCKED - synchronized session is faulted ...
COI-Coop: NETWORK RX FAIL ...
```

`REPLAY DERIVED/BYPASS` is diagnostic rather than an automatic failure. It identifies commands processed without an authority marker, which may be deterministic commands generated internally by the game after the pre-processing hook.

## Current limitations of replay mode

- loopback (`127.0.0.1`) only;
- no automatic save transfer yet;
- no simulation-frame barrier yet;
- no reconnect/resync yet;
- pause/speed are not yet treated as synchronized global state;
- conflict policies exist as a foundation but are not yet mapped to real game command types;
- authoritative state hashes/desync repair are not implemented yet.

The first replay milestone is deliberately narrow: prove that one serialized building command can be host-authorized and executed on both instances from the same paused starting state.
