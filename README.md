# CombatMeter

CombatMeter 0.12.0 shows host-authoritative combat statistics for Valheim 1.0.15 in single-player and Listen Host multiplayer.

## What it shows

- Damage Done and each player's contribution percentage
- DPS based on personal active damage time
- Damage Taken
- contribution bars with deterministic player colors and a draggable HUD
- synchronized encounter state and elapsed time

DPS does not keep falling forever while you dodge, reposition, or recover stamina. After you deal PvE damage, only a short configurable activity window counts toward your DPS time. Longer idle gaps are excluded.

## Installation

Install with a Thunderstore-compatible mod manager after the package is published, or manually place `CombatMeter.dll` under `BepInEx/plugins/CombatMeter/`. The Valheim BepInEx pack is required.

### Upgrade from development builds

Remove the old `DiagnosticDamageProbe.dll` before installing CombatMeter. Do not keep both DLLs in the same profile. On first launch, CombatMeter imports recognized values from `local.valheim.diagnosticdamageprobe.cfg` only when the new `Likhtenvald.CombatMeter.cfg` has no user settings. The old file is left untouched.

## Controls

- `F8`: show or hide the meter
- `Ctrl+F8`: enter or leave edit mode
- drag the header while editing to move the HUD
- `Shift+F8`: reset its position
- `Escape`: leave edit mode

Scale, width, opacity, contribution bars, hotkeys, visibility, and position are local preferences for each player.

## Combat clusters and multiplayer authority

CombatMeter groups combat by actual PvE interactions. Players fighting unrelated enemies can have independent combat meters. Combat clusters merge when their interactions connect, for example when players fight the same PvE combatant. Distance is not a clustering rule.

The Listen Host calculates canonical Damage, contribution, DPS, Taken, encounter state, and elapsed time. Each player receives the authoritative snapshot for their own current combat cluster; unrelated fights do not share HUD rows or statistics. Players without a current cluster receive an empty encounter view. Combat timing values in a client's local config do not override the host.

Recovery remains host-authoritative and local to each combat cluster. Multiple dead players in one cluster can have independent Recovery periods.

Fall, Drowning, and Smoke damage do not start, extend, resume, or contribute to CombatMeter combat encounters. Other environmental damage is not universally excluded: Poisoned, Burning, Tree, and Incinerator retain their existing behavior.

Damage contribution bars use deterministic colors based on PlayerID. The palette has 12 slots, so different players may share a color.

CombatMeter is intended for cooperative play and is not an anti-cheat system.

## Configuration

Host-controlled settings:

- `Combat Timeout = 20`
- `Recovery Timeout = 180`
- `DPS Idle Timeout = 6`

UI settings are personal and can differ between players. The complete setting descriptions are available in BepInEx Configuration Manager and in the source project configuration documentation.

All diagnostic categories are disabled by default. Performance measurement is an optional troubleshooting facility with its own `[Diagnostics] EnablePerformanceDiagnosticLogging = false` setting. Other diagnostic switches do not enable it; when disabled, performance timing and counters are bypassed.

## Supported modes

- Single-player
- Listen Host multiplayer

Dedicated servers are not currently supported.

## Known limitations

- Summon and magic attribution covers the implemented provenance paths; unsupported sources can remain unattributed.
- Summons that already existed before the current network session may lack provenance.
- Extremely late DPS events older than compacted activity history receive bounded handling.

More technical details remain available under `docs/` in the source project.

## License

CombatMeter is distributed under the [MIT License](LICENSE).

