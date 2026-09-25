# Active DPS Time (Milestone 3F)

Version `0.11.2` separates encounter elapsed time from each player's DPS activity time:

```text
DPS = DamageDone / ActiveDpsTime
```

When `ActiveDpsTime == 0`, DPS is zero. `EncounterElapsedSeconds` keeps its existing meaning and wire representation.

## Activity model

Every accepted, attributed PvE `DamageDone > 0` portion for a signed nonzero PlayerID contributes `[factualEventTime, factualEventTime + DPS Idle Timeout]`. Overlapping and touching intervals are merged. The union is clipped to canonical `now`, encounter start, the Active-to-Recovery transition, and Finished time. Direct melee/ranged, supported summon portions, and attributed elemental/DoT portions use the same rule. DamageTaken, PvP, self/environment/unattributed damage, death, respawn, and heartbeats add no interval.

The tracker stores at most 128 closed intervals per player plus one mutable current interval. Older closed intervals are compacted into exact accumulated duration and a monotonic frontier. Already accumulated time never changes. An exceptionally late event entirely older than that frontier is ignored, and one crossing it is clipped; accepted events inside the retained horizon are merged by factual time independently of arrival order. This is the bounded-memory tradeoff for arbitrarily long encounters.

## Live settings

`[Combat] DPS Idle Timeout` defaults to `6` seconds and is clamped to `1..20`. Only the listen host value is used. A still-open interval recomputes its deadline from `LastOffensiveEventTime + current timeout`. Reducing the timeout can close it earlier; increasing it can extend it until it has been permanently closed. Closed and compacted history is never recalculated.

Recovery freezes all open personal intervals at transition time. A later attributed offensive event after Recovery-to-Active starts or merges a new interval while preserving historical duration. Finished freezes at canonical encounter end. A new EncounterId clears every tracker.

## Protocol and runtime checks

`DamageCommit v2`, `Magic Attribution v1`, and `CombatSnapshot v1` are unchanged. The snapshot continues to carry final host-calculated DPS, so clients never recalculate it from local configuration.

Single-player: with defaults, deal one burst and observe DPS decline for about six seconds, then remain stable while the encounter stays Active toward 20 seconds. Attack again before finish and verify the idle gap is excluded. Repeat with DamageTaken only, death/Recovery, Finished, a new encounter, and live changes `6 -> 2 -> 8`.

Multiplayer: set host timeout to 6 and client timeout to 1. Compare both HUDs for identical rows while players use different attack patterns. Verify one player's inactivity does not affect another, and that supported summon/DoT ticks extend only the credited player's activity.


