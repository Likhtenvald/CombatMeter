# CombatMeter

CombatMeter is a public test build for Valheim 1.0.15. It shows synchronized party combat statistics in single-player and Listen Host multiplayer.

## What it shows

- Damage Done and each player's contribution percentage
- DPS based on personal active damage time
- Damage Taken
- contribution bars and a draggable HUD
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

## Multiplayer authority

The Listen Host calculates canonical Damage, contribution, DPS, Taken, encounter state, and elapsed time. Clients display the same synchronized results. Combat timing values in a client's local config do not override the host.

CombatMeter is intended for cooperative play and is not an anti-cheat system.

## Configuration

Host-controlled settings:

- `Combat Timeout = 20`
- `Recovery Timeout = 180`
- `DPS Idle Timeout = 6`

UI settings are personal and can differ between players. The complete setting descriptions are available in BepInEx Configuration Manager and in the source project configuration documentation.

Verbose diagnostic categories default to disabled. For a full multiplayer test log, enable all four options under `[Diagnostics]`, reproduce the issue, then disable them again.

## Supported modes

- Single-player
- Listen Host multiplayer

Dedicated servers are not currently supported.

## Known limitations

- Multiplayer validation of this public test build is still in progress.
- Summon and magic attribution covers the implemented provenance paths; unsupported sources can remain unattributed.
- Summons that already existed before the current network session may lack provenance.
- Extremely late DPS events older than compacted activity history receive bounded handling.

More technical details remain available under `docs/` in the source project.

