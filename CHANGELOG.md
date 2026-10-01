# Changelog

## 1.0.1

- Fixed player names for damage attributed from supported owned summons and magic sources, including when summon damage is the first combat event.
- Owner display names now come from vanilla session identity and remain available when the remote Player object is unloaded from the host scene.
- Attribution remains host-authoritative and PlayerID-based; names are presentation metadata only. Combat behavior and snapshot protocol are unchanged.

## 1.0.0

- First stable CombatMeter release for single-player and Listen Host multiplayer, with host-authoritative per-player Damage Done, contribution percentage, personal active-time DPS, and Damage Taken.
- Added optional Largest Hit immediately after DPS and optional Deaths as the final column; both are disabled by default and configured locally.
- Independent Combat Clusters keep separate fights separate, merge when PvE interactions connect groups, and do not split again during that encounter. Later encounters can be independent again.
- Recovery remains host-authoritative and cluster-local, including independent periods for multiple dead players.
- Peer-specific snapshots use distance-independent session identity routing and clear stale encounter views. CombatSnapshot v2 carries authoritative Largest Hit and Deaths; all participants must use the same current version.
- Deterministic player colors, contribution bars, and a draggable configurable HUD remain available.
- Fall, Drowning, and Smoke are excluded from combat statistics and encounter activity; other environmental damage retains its existing policy.
- Performance diagnostics remain explicitly opt-in and disabled by default.

## 0.12.1 — Distance-independent multiplayer snapshots

- Fixed missing damage tables when the Listen Host was far from connected clients.
- Recipient identity is retained from the vanilla network session instead of requiring the client's Player object to remain instantiated on the host.
- Remote combat continues to route to the correct Combat Cluster when the remote Player entity unloads on the host.
- Disconnect invalidates identity bindings; reconnect requires fresh establishment, and conflicting mappings remain fail-closed.
- DamageCommit transport and CombatSnapshot v1 are unchanged.
- Successfully validated the fix in a two-PC Listen Host multiplayer test.

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
