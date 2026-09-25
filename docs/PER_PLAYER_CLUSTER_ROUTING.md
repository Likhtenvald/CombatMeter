# Milestone 4B: per-player cluster routing

`CombatClusterManager.TryGetClusterForPlayer(long playerId, out CombatCluster cluster)`
is the host-side routing entry point for a known stable gameplay PlayerID.
It performs one membership lookup using `CombatNode.Player(playerId)`.

- A member of an Active or Recovery cluster receives `true` and that exact cluster.
- Missing membership receives `false` and `null`, including unknown/zero PlayerIDs.
- Player-only environmental/unattributed Damage Taken clusters use the same lookup.
- Join and merge are immediately visible through 4A's authoritative membership map.
  All donor players route to the deterministic lowest-ID survivor; no routing cache
  or separate mapping needs invalidation.
- After the manager processes Finished, membership is removed and routing returns
  no cluster. Retained LastFinished is not a routing source. A new encounter returns
  the newly allocated cluster ID.
- Session reset clears membership. Queries cannot recreate it on reconnect.

This is a side-effect-free query of the current domain state, not a lifecycle tick.
The host must continue calling the existing Update path to process timeouts before
reading the resulting membership. The query has no clock parameter and does not
expire tickets, update DPS trackers, create clusters, or refresh activity.

No selection uses names, positions, damage, DPS, UI row order, connection order or
cluster enumeration. There is no legacy, last-finished, nearest, largest, first or
newest-cluster fallback.

No peer-to-PlayerID adapter is introduced or required by this API. A network peer ID
must not be passed as though it were a PlayerID; equal numerical representations do
not establish identity. Obtaining authoritative PlayerID for future per-peer
snapshots remains a separate integration concern for 4C. This milestone adds no
network messages or mapping heuristics.

Legacy global encounter, snapshot builder/codec/broadcast and UI are untouched.
4B exposes the query for future consumers; it does not route existing snapshots.

Verification: baseline 358 checks plus 19 routing checks = 377. Tests cover missing,
player-only, independent, joined, merged, retired, finished, new and Recovery
membership; query purity and repeatability; reset; signed PlayerIDs; peer identity
separation; and independence from names, damage rank and creation/query order.
The existing 4A, attribution, Recovery/DPS and transport/snapshot checks remain.

```powershell
dotnet run --project tests/ProbeChecks/ProbeChecks.csproj -c Release
dotnet build DiagnosticDamageProbe.csproj -c Release --no-incremental
```
