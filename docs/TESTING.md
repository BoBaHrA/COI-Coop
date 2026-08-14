# COI-Coop prototype testing

The current branch has two independent checks:

1. a standalone TCP handshake/PING smoke test (already automated in GitHub Actions), and
2. an in-game Update 4.2 compatibility probe that needs a real Captain of Industry installation.

## 1. Build and deploy the mod

From the repository root in PowerShell:

```powershell
.\scripts\build-mod.ps1
```

The script tries to locate Captain of Industry in Steam libraries automatically. You can also pass the path explicitly:

```powershell
.\scripts\build-mod.ps1 -CoiRoot "D:\SteamLibrary\steamapps\common\Captain of Industry"
```

A successful build deploys these files to:

```text
%APPDATA%\Captain of Industry\Mods\CoiCoop
```

## 2. Single-process game API compatibility test

Keep `network_mode = 0` for this test. Launch Captain of Industry normally, enable **COI Co-op Prototype**, and load an existing save.

Open the latest log in:

```text
%APPDATA%\Captain of Industry\Logs
```

Expected startup lines include:

```text
COI-Coop: constructed
COI-Coop: Mafi.Core assembly version ...
COI-Coop: Initialize; gameWasLoaded=True
COI-Coop: COMPATIBILITY OK - InputScheduler command queue found
COI-Coop: command observer attached
```

If the private command queue changed in Update 4.2, the log should instead contain:

```text
COI-Coop: COMPATIBILITY FAIL - InputScheduler.m_commandsToProcess was not found. Game API changed.
```

With `trace_commands = true`, perform a few actions in the loaded world:

- place one building,
- place one transport segment,
- demolish something,
- change a building setting if convenient.

The log should gain entries similar to:

```text
COI-Coop: INPUT Mafi.Core....SomeCommand
```

Those command names are the data we need for the first real synchronization experiment.

## 3. Two-process local network probe

Only after the single-process compatibility probe succeeds.

Environment variables override `config.json` for one process only.

Host terminal:

```powershell
$env:COI_COOP_MODE = "1"
$env:COI_COOP_PORT = "27015"
& "$env:COI_ROOT\Captain of Industry.exe"
```

Client terminal:

```powershell
$env:COI_COOP_MODE = "2"
$env:COI_COOP_PORT = "27015"
& "$env:COI_ROOT\Captain of Industry.exe"
```

Start the host first, then the client. The current host probe waits up to 30 seconds for a connection.

Expected log messages:

```text
COI-Coop: HOST handshake + ping completed successfully
COI-Coop: CLIENT handshake + ping completed successfully
```

The prototype intentionally binds to `127.0.0.1`. LAN/Internet support comes after the in-game integration points are verified.

## What to send back after the first game test

The useful lines are every log line containing `COI-Coop:` plus the command names produced by the four actions above. There is no need to send the entire log unless the game throws an exception.
