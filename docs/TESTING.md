# Network foundation test

Current prototype: local-only version handshake plus bidirectional ping.

## Build

Set `COI_ROOT` to the Captain of Industry install folder and build `src/CoiCoop/CoiCoop.csproj`. The project deploys the mod to the normal COI Mods folder after a successful build.

## Two-process test on one PC

The environment variables below override `config.json` for one process only.

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

Start the host first, then the client.

Expected logs contain:

```text
COI-Coop: HOST handshake + ping completed successfully
COI-Coop: CLIENT handshake + ping completed successfully
```

Without environment overrides, `network_mode` uses `0 = off`, `1 = host`, `2 = client` from the mod config.

This milestone intentionally binds to `127.0.0.1`. LAN and Internet connectivity come only after this probe is verified inside the game.
