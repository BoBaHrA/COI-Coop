# Blueprint preview test

1. Build and deploy the current `dev/network-foundation` mod.
2. Start the usual host/client replay pair from byte-identical saves.
3. On one peer, open the blueprint library and select an existing blueprint.
4. Move and rotate the blueprint for several seconds without placing it.
5. The other peer should see the selected blueprint's live placement ghost even though that peer does not have the same saved blueprint in its own library.
6. Cancel placement: the remote ghost should clear.
7. Repeat client -> host.

Expected logs:

- `BLUEPRINT PREVIEW sidecar starting on 127.0.0.1:27018`
- `BLUEPRINT targeted controller ...`
- `BLUEPRINT GHOST TX START ... pieces=N`
- `BLUEPRINT GHOST RX START ... pieces=N`
- `BLUEPRINT REMOTE MULTI GHOST pieces=N`

If the ghost is missing, run `scripts\collect-replay-diagnostics.bat` and `scripts\collect-blueprint-api.bat`.
