# Blueprint preview synchronization

Blueprint libraries are local player data and are **not** synchronized by COI-Coop.

When a player selects a blueprint for placement, Captain of Industry feeds that blueprint's `EntityConfigData` items into a dedicated `StaticEntityMassPlacer`. COI-Coop mirrors only the resulting live placement previews as presentation-only state. The final placement still reaches the shared simulation through the normal authoritative `BatchCreateStaticEntitiesCmd` replay path.

Development transport:

- main port + 3: blueprint latest-wins preview sidecar;
- no blueprint library names, folders, saved presets, or persistent blueprint data are transmitted;
- disconnect/failure of this sidecar must never halt simulation.

This dedicated sidecar is intentionally temporary and isolated while the adapter is validated. The future production transport should multiplex blueprint, ordinary placement, path/ramp preview, and small auxiliary state lanes over the common session transport.
