# Optional Largest Hit and Deaths — CombatMeter 1.0.0

Install the same current CombatMeter version on host and clients. Release 1.0.0
uses snapshot v2. Historical release 0.12.1 uses v1 and cannot interoperate with it.
There is no explicit version negotiation or mixed-version compatibility.

## Authoritative statistics

PlayerCombatStatistics stores LargestHit as Single (the existing effective-damage
type) and Deaths as Int32. Both start at zero. AddDamageDone compares the current
accepted contribution with LargestHit in O(1). Vanilla Damage Done uses accepted
effective HP loss; attributed damage uses only each player's actually credited
DamagePortion, never the full hit split across players. Damage Taken and ignored
Fall/Drowning/Smoke cannot update LargestHit.

EncounterManager.OnPlayerDied increments an existing player's statistics only after
the unchanged participation and death-deduplication checks accept the death.
No HP/death polling, respawn inference or synthetic statistics rows are introduced.
The Int32 counter saturates at Int32.MaxValue rather than overflowing.
Recovery, reengagement and repeated legitimate deaths retain the same statistics.
Cluster merge moves each disjoint player's statistics object unchanged; new
encounters create new zeroed statistics. No histories or extra scans are retained.

The host always collects both fields, regardless of local column visibility.
Remote clients render the authoritative values; the distance-independent session
identity binding and per-cluster routing are unchanged.

## Configuration and HUD

Both settings are client-local, live and false by default:

    [UI]
    Show Largest Hit = false
    Show Deaths = false

Visibility does not control collection or network payloads. Explicit shared slots
in CombatMeterColumns place Player, Damage, optional %, DPS, optional Largest Hit,
Taken, optional Deaths. Both header and row geometry consume this same contract.
Largest Hit is immediately after DPS; Deaths is terminal. Damage bars have no
column slot and do not affect order. Toggle order has no persistent layout state.
Largest Hit uses damage-style whole-number presentation; Deaths is an integer.
Config toggles refresh the current snapshot/preview without waiting for new combat.

Both-off geometry remains unchanged. To prevent overlap at narrow configured
widths, effective panel minimum width grows by 96 units for Largest Hit and 52 for
Deaths; the configured width is not overwritten. The existing layout clamp and
scale apply. No new sorting, colors, icons or gameplay state are added.

## Snapshot v2

One RPC: CombatMeter.CombatSnapshot.v2. ProtocolVersion = 2.
The header and epoch/sequence rules are unchanged. Little-endian player row:

1. PlayerID: Int64
2. DisplayName: BinaryWriter UTF-8 string with 7-bit encoded byte-length prefix
3. DamageDone: Single
4. DPS: Double
5. DamageTaken: Single
6. LargestHit: Single (finite and nonnegative)
7. Deaths: Int32 (nonnegative)

V1 is rejected; there is no runtime fallback or separate metrics channel.
Existing host/sender, epoch, stale/duplicate, row ordering, maximum 64 rows,
16 KiB packet limit and strict end-of-payload validation remain.
Empty NoEncounter clears previous metric rows in the existing snapshot/HUD path.

Payload increase is exactly 8 bytes per row; empty remains 51 bytes.
For one row named P: v1 77 bytes, v2 85 bytes. Two such rows: 103 -> 119 bytes.
These are codec payload bytes, excluding RPC/transport framing.

## Validation

ProbeChecks cover accepted damage and attribution shares; duplicate commits;
accepted/duplicate/outside-encounter deaths; Recovery, new encounters and disjoint
merge; v2 values, malformed numbers, protocol mismatch, trailing/truncated payloads,
bounds and formatting; actual shared column slots/geometry at minimum/default/
maximum widths; remote client receipt without host Player instantiation, isolation,
merge, reconnect and NoEncounter clearing.

The stable baseline passes 531 managed checks. The runtime-tested feature commit
b7d7ba306c452b7c0013518802c0656dd628f284 passed manual Valheim HUD and multiplayer
validation, as confirmed for release acceptance. Release preparation changes only
version metadata and documentation; managed checks do not execute Unity/Harmony.
