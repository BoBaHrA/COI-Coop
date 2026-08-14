# Co-op scope

The goal is deliberately narrow: **two players cooperatively control one Captain of Industry world**.

This is not an attempt to create separate player-owned simulations, PvP, independent economies, or a second game layer. Both players see and modify the same authoritative state.

## Core model

- One peer is the authoritative host.
- Both players may issue normal game actions.
- Every accepted action receives a single host sequence number.
- Both peers apply accepted actions in that same host order.
- Duplicate packets/commands are ignored.
- Invalid stale actions are rejected instead of being forced into the world.
- Regular simulation continues to run on both peers from the same starting save.

## Conflict policy

Most actions should **not** need bespoke locking. The host's total command order is the default conflict resolution mechanism.

Examples:

- Player A changes a machine setting and then Player B changes it: B's later host sequence naturally becomes the resulting state.
- Player A starts deconstruction while Player B tries to configure the same entity: the second action is applied only if that entity/action is still valid.
- Both players submit the same command packet twice because of reconnect/retry: the duplicate command ID is ignored.

Some actions are better treated as **superseding intents**. Within one authority batch, only the newest intent for the same conflict key needs to survive.

Primary example:

- Both players order the same ship to different destinations nearly simultaneously. The latest accepted valid order for `ship:<id>:destination` wins.

Superseding must be opt-in per command family. Commands such as toggles are order-sensitive and must not be collapsed blindly.

## Game areas that must be covered

### Island / main map

- static construction and demolition;
- transports, pipes, balancers and topology-changing edits;
- machine recipes, priorities, enable/disable states and other building settings;
- farms, mining/dumping designations, vehicle/logistics assignments;
- research and other global actions;
- pause and game speed.

### World map / ship layer

This is a first-class subsystem, not an optional extra.

- ship destination/orders;
- exploration;
- battles and interactions;
- cargo/trading operations;
- any global state changed from the world-map UI.

World-map actions use the same authoritative command stream where possible. If a world-map operation bypasses `InputScheduler`, it needs a dedicated capture/replay hook.

## Non-goals for the first playable version

- separate inventories/economies per player;
- player ownership of buildings;
- permissions/roles beyond host authority;
- competitive play;
- public Internet matchmaking;
- perfect hot-join without a host snapshot;
- sophisticated merge semantics for every conflicting UI gesture.

## Definition of "playable co-op"

Two players load the same host world, can build and configure the factory together, can use the world map/ship without duplicating or corrupting actions, can pause/change speed coherently, and remain in sync during normal play.
