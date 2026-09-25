# Recovery tickets (Milestone 2F.1, version 0.7.5)

## Configurable host timing (Milestone 3E)

Plugin `0.11.2` supplies the pure encounter core with a mutable `EncounterSettings` object. Defaults and config bounds are:

```text
[Combat]
Combat Timeout = 20       # 5..60 seconds
Recovery Timeout = 180   # 30..600 seconds
DPS Idle Timeout = 6     # 1..20 seconds
```

Only the listen host constructs and updates the canonical settings object. Clients may have different local config values, but they have no `EncounterManager` and only render the resulting state from `CombatSnapshot v1`.

`inactive >= Combat Timeout` fires the Active transition. Without tickets this freezes the encounter at `LastActivityTime + Combat Timeout`; with a valid ticket it enters Recovery. A ticket expires when `now >= DeathTime + Recovery Timeout`.

Configuration Manager changes are read on each host transport update and applied before the next encounter evaluation. Reducing Combat Timeout may therefore finish or recover an already inactive encounter immediately. Recovery ticket deadlines are recomputed from their original death times using the new Recovery Timeout. DPS Idle Timeout controls personal active DPS windows without changing encounter transitions. Invalid direct values fall back to 20/180/6; finite values are clamped to the documented ranges.

Diagnostics emit `CombatEncounterSettings` once when a host session starts and `CombatEncounterSettingsChanged` only when sanitized values change. Client-local settings are not logged as authoritative.

Encounter recovery is driven only by current per-player tickets. The former
`EncounterParticipant.HasDiedDuringEncounter` flag was sticky for the lifetime of an encounter and could make a
player's old death trigger Recovery after that player had already returned to combat. It has been removed.

## Model

Each participating non-zero signed `PlayerID` can have one `RecoveryTicket`:

```text
PlayerID
DeathTime
ExpiryTime = DeathTime + 180 seconds
```

Times use the same monotonic seconds supplied to `EncounterManager`; wall-clock UTC is not used. A canonical death
creates a ticket. A later accepted death for a new runtime incarnation refreshes the same entry. Duplicate death
callbacks for one incarnation remain suppressed by `EncounterParticipant` and do not modify the ticket.

Respawn observation only marks the runtime participant alive and does not remove the ticket. The ticket is removed
when that PlayerID participates in a relevant PvE classification, either Damage Done to a non-player or Damage Taken
from NPC/environment, and only when the factual event chronology is strictly later than `DeathTime`. Activity by
another player cannot remove it. Negative PlayerIDs are valid; zero is not a valid participant identity.

## Ordering guard

`DamageCommit.TimestampUtcTicks` is captured with the factual owner-side HP-loss event. At session creation,
`DamageCommitTransport` records a UTC/monotonic anchor pair and projects that timestamp into the encounter's monotonic
seconds domain. Host processing time is kept separate. Ticket clearing uses the exact rule:

```text
eventTime > ticket.DeathTime
```

An earlier or equal event produces `RecoveryTicketClearIgnored reason=EventNotAfterDeath`. It cannot clear the ticket,
advance `LastActivityTime` while already in Recovery, or reactivate Recovery. This covers the killing blow, buffered
pre-death hits, and late events from an earlier life after a second death. A chronologically later relevant event can
clear the ticket without requiring a respawn callback.

No incarnation guard is applied to combat events because DamageCommit does not carry the current player incarnation.
Death callback deduplication continues to use `PlayerID + PlayerZDOID` where available.

## State transitions

At `LastActivityTime + 10.000`, Active enters Recovery when at least one unexpired ticket exists. Otherwise it finishes
at that exact soft-timeout boundary. At `DeathTime + 180.000`, a ticket is expired; at 179.999 seconds it remains
valid. Recovery finishes at the final remaining ticket's exact expiry. If relevant PvE activity occurs first,
Recovery returns to Active without changing encounter start time or clearing other players' tickets.

After reactivation, another soft timeout checks the tickets that remain at that time. This can enter Recovery again
for another player or finish normally when no ticket remains. Finished, new encounter start, session reset, world
stop, disconnect, and plugin shutdown cannot carry tickets into a later encounter.

## Diagnostics

Lifecycle diagnostics emit only changes:

- `RecoveryTicketCreated`
- `RecoveryTicketRefreshed`
- `RecoveryTicketCleared reason=Reengaged`
- `RecoveryTicketClearIgnored reason=EventNotAfterDeath`
- `RecoveryTicketExpired`
- `EncounterStateChanged Active->Recovery reason=PendingRecovery`
- `EncounterStateChanged Active->Finished reason=SoftTimeoutNoRecovery`
- `EncounterStateChanged Recovery->Active reason=RelevantPveActivity`
- `EncounterStateChanged Recovery->Finished reason=NoPendingRecovery`

Damage totals and DPS formulas are unchanged. Death creates no damage statistic, and recovery/runback time remains in
the common encounter duration.

The UTC-to-monotonic projection is exact for events captured on the listen host. For remote-owner commits it depends
on peer wall-clock alignment because the unchanged DamageCommit v2 protocol has no synchronized source-monotonic
timestamp. Addressing arbitrary malicious or badly skewed client clocks would require a later protocol change.



