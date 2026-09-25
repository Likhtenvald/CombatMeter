# Milestone 4A: combat cluster core

## Boundary and temporary compatibility

The host sends each canonically accepted, deduplicated, attributed damage event to
both the legacy global EncounterManager and the independent CombatClusterManager.
They share host configuration values, not encounter state or statistics objects.
Deaths, periodic updates and session teardown also reach the cluster core.
Clients do not construct the cluster core. Existing dedicated-server restrictions
are unchanged. Attribution resolution and pending-event handling are unchanged.

The legacy manager remains the sole input to CombatSnapshotBuilder. Snapshot v1,
UI, global broadcast and player colors are unchanged. There is no cluster selection,
aggregate snapshot or per-peer routing. This compatibility path is temporary until
4B/4C, and must not be mistaken for cluster-aware runtime presentation.

## Types and identity

- CombatNode separates stable signed PlayerID from PvE (creator, object ID) identity.
- CombatInteraction extracts endpoints using the existing classification and
  attribution result. It does not resolve attribution or validate/deduplicate again.
- CombatCluster owns its membership set and one independent EncounterManager.
- CombatClusterManager owns active/recovery clusters, node-to-cluster membership,
  session-scoped monotonically allocated IDs, and one retained finished cluster.

Production commits originate in Character.ApplyDamage. Trees, rocks and buildings
are outside that observer boundary. DamageFacts has no world-object type field;
4A does not invent classification from display names or change the wire format.
Normal ignored classifications, including PvP and NPC-to-NPC, create no cluster.

## Create, join, merge and finish

Before routing an event, update every cluster's existing timeout state machine.
Only Active and Recovery encounters retain active membership.

For ordinary relevant PvE damage, connect the player and the known PvE object's
identity. Attributed summon/DoT damage connects each positively attributed player
to the victim once; unassigned damage portions never fabricate a player.

Relevant Damage Taken without an attacker object identity is the explicit
player-only exception. It joins that player's current cluster or creates a new
one. There is no synthetic NPC or synthetic edge. A later PvE interaction joins
its real combatant, and can merge the player-only cluster with another cluster.
Its activity, timeout, and Recovery processing use the existing encounter rules.

No existing endpoint membership creates a new cluster. One existing component
receives new endpoints. Multiple existing components merge into the lowest-ID
component, independently of endpoint/portion enumeration order. All memberships
are remapped; the donor is retired with MergedIntoId and removed from enumeration.
The complete bridge event is applied once, after all component unions.

No edges expire. Membership never splits while an encounter remains live.
On Finished, remove all of that cluster's active memberships and its active entry.
Keep only the most recently ended cluster (ID breaks ties), bounding completed
history to one entry. Other active clusters are untouched. A later interaction
creates a fresh ID and clean encounter. Session reset clears all manager state.

## Merge invariants and semantics

Independent clusters cannot share a player: membership would have already joined
them. AbsorbDisjoint validates that invariant. It moves original per-player
statistics, EncounterParticipant, RecoveryTicket and PlayerDpsActivityTracker
objects into the survivor, then clears the retired manager. No damage is replayed,
no totals are reset on the survivor, and no DPS intervals are reconstructed.
This preserves floating-point totals, compacted DPS history, frozen intervals,
death-incarnation deduplication and exact ticket death/expiry times.

StartTime is the minimum of the two starts. Before applying the bridge,
LastActivityTime is the maximum of the existing activity times. Active wins over
Recovery; two Recovery components remain Recovery until the bridge passes the
existing reengagement check. Tickets are not cleared merely because of merging.
The ordinary bridge advances activity to its event time and clears only eligible
participants' tickets, through the unchanged Accept method.

Existing chronology protection takes precedence for delayed events: a pre-death
bridge cannot clear a Recovery ticket or wake two recovering components, and an
older event cannot move activity backwards. Such bridge damage still counts once.
This preserves the existing Recovery semantics rather than treating merge as an
unconditional reengagement. A frozen player's DPS does not grow simply because
another participant activates the combined cluster.

## Verification

Run from the repository root:

```powershell
dotnet run --project tests/ProbeChecks/ProbeChecks.csproj -c Release
dotnet build DiagnosticDamageProbe.csproj -c Release --no-incremental
```

The baseline 312 checks remain, with 40 cluster-domain checks and six adapter
checks (358 total). Coverage includes player-only progression, isolated finish and
Recovery, multi-component attributed bridges, exactly-once remote delivery,
compacted/frozen DPS preservation, delayed pre-death bridges, no live split,
signed identities, and differential single-component replay against legacy.
Adapter checks verify host-only construction, death routing, shutdown, resolved
summon attribution and preservation of the global snapshot compatibility path.
Managed checks do not execute Unity/Harmony or a Valheim multiplayer runtime.
