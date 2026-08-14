# Network foundation test

Current prototype: local-only handshake and ping.

1. Build `src/CoiCoop/CoiCoop.csproj` with `COI_ROOT` set to the Captain of Industry install folder.
2. Confirm the mod is copied to the Captain of Industry Mods folder.
3. Use `network_mode = 1` for the host instance.
4. Use `network_mode = 2` for the client instance.
5. Keep the default port `27015`.
6. Start host first, then client.

Expected log messages include `HOST handshake completed successfully` and `CLIENT handshake completed successfully`.

The prototype intentionally binds to `127.0.0.1`. LAN support comes after this probe is verified inside the game.
