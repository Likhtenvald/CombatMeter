# Changelog

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
