# First authoritative replay test

This is the first test where a Captain of Industry command is allowed to travel through the host authority stream and be inserted back into both simulations.

Use a disposable save only.

## Goal

On two local Captain of Industry instances loaded from the same paused save:

1. place one basic building on the host;
2. the local command is removed before normal processing;
3. the host assigns authority sequence `0`;
4. both host and client receive the same `COMMIT`;
5. both instances deserialize and execute the same command;
6. the same building appears in both worlds.

## Prepare the save

Before launching the co-op instances:

1. start Captain of Industry normally;
2. load a test world;
3. pause the simulation;
4. create a new manual save named something disposable such as `COOP_REPLAY_TEST`;
5. exit the game.

Do not use an important save for this milestone.

## Launch

From the repository root, after `git pull`:

### Host

```bat
scripts\launch-replay-host.bat
```

Wait for the first Captain of Industry instance to reach the main menu.

### Client

```bat
scripts\launch-replay-client.bat
```

Both launchers enable `COI_COOP_REPLAY=1` only for the game process they start. The normal game configuration remains replay-off.

If the game/Steam refuses to start a second instance, stop here and report the exact behavior; do not work around it by changing files manually.

## Load the world

In both instances, load the exact same `COOP_REPLAY_TEST` save.

Keep the simulation paused. Do not change speed, unpause, send ships, change recipes, or perform other actions yet.

Wait a few seconds after both worlds finish loading so the persistent session can connect.

## First command

On the HOST instance only, place exactly one simple standalone building in an empty area.

Do not issue a second command yet.

Expected visible result: the same building placement appears in both instances.

## Expected log path

The interesting lines are:

```text
COI-Coop: EXPERIMENTAL AUTHORITATIVE REPLAY = ON
COI-Coop: NET HOST session connected
COI-Coop: NET CLIENT session connected
COI-Coop: REPLAY SUBMIT type=...BatchCreateStaticEntitiesCmd ...
COI-Coop: REPLAY QUEUED seq=0 origin=host type=...BatchCreateStaticEntitiesCmd ...
COI-Coop: REPLAY APPLIED seq=0 origin=host type=...BatchCreateStaticEntitiesCmd
```

Both peers should apply authority sequence `0` for the same command type.

Stop immediately and collect diagnostics if either peer logs:

```text
COI-Coop: REPLAY HALT
COI-Coop: NETWORK RX FAIL
COI-Coop: REPLAY DERIVED/BYPASS
```

`DERIVED/BYPASS` is not automatically a bug, but the command type must be reviewed before the test surface is expanded.

## After the test

Exit both game instances without relying on this test state as a real save. The save is disposable.

Run:

```bat
scripts\collect-diagnostics.bat
```

If two log files were produced, preserve both. The first milestone is successful only when host and client both apply the same authority sequence and produce the same visible building result.
