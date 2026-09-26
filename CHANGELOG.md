# Changelog

## 0.12.0 — Combat clusters and multiplayer routing

- Added independent combat clusters based on actual PvE interactions; unrelated simultaneous fights no longer share one global table.
- Added per-player authoritative snapshots from the Listen Host, with clusters merging when combat interactions connect them.
- Improved encounter finishing and stale HUD clearing through cluster routing and empty snapshots for players without a current cluster.
- Kept Recovery local to each combat cluster, including independent Recovery periods for multiple dead players.
- Added deterministic player colors to Damage contribution bars.
- Excluded Fall, Drowning, and Smoke from combat statistics and from starting, extending, or resuming encounters and Recovery.
- Added lightweight performance instrumentation with a separate opt-in diagnostic switch, disabled by default; disabled telemetry bypasses measurement and counters.
- Successfully validated the peer-specific snapshots, environmental policy, and player-color runtime candidate in a two-PC Listen Host multiplayer session.

## 0.11.2 — Public test build

- Added synchronized Damage Done, DPS, contribution percentage, contribution bars, and Damage Taken.
- Added personal Active DPS Time so long idle gaps do not continually reduce DPS.
- Added host-controlled encounter, recovery, and DPS timing.
- Added draggable and configurable local HUD presentation.
- Added summon and elemental attribution for the currently supported provenance paths.
- Clarified local UI versus host combat configuration authority.
- Renamed the public plugin and DLL to CombatMeter.
- Added one-time migration from the previous development config.
- Disabled verbose diagnostic categories by default while keeping them available for testing.

### Known limitations

- Dedicated servers are not supported.
- Multiplayer validation is ongoing.
- Some unsupported or pre-existing summon/magic sources may remain unattributed.
