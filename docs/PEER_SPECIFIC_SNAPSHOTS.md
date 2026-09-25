# Milestone 4C: peer-specific combat snapshots

## Verified runtime API

The installed assembly referenced by DiagnosticDamageProbe.csproj was inspected
with the existing local ILSpy tool before implementation:

- assembly_valheim.dll SHA-256:
  `96CFC004F7F4A6F30D070BEF39EAFD79C466A137121C4665A2F19FB9C15C6127`
- `Player.GetAllPlayers()` returns the registered `s_players` list; Player lifecycle
  registers/removes instances. This is not a Unity scene scan.
- `Player.GetPlayerID()` reads the gameplay ID from its valid ZDO (`ZDOVars.s_playerID`),
  returning zero when the view is invalid.
- `ZNet.GetPeers()` returns connected peers. `ZNetPeer.IsReady()` checks nonzero UID.
- Existing `ZNetView.IsValid()`, `GetZDO()` and `ZDO.GetOwner()` supply entity ownership.

## Identity and routing

HostPlayerIdentity resolves a remote peer from currently observed Player instances:
valid Player + valid ZNetView/ZDO + ZDO owner equal to the destination peer + nonzero
Player.GetPlayerID(). If different gameplay IDs resolve to one owner, return unresolved.
The local listen-host uses Player.m_localPlayer, also requiring a valid view and
ownership by the host session peer. Signed PlayerIDs remain supported.

Mapping is recomputed each publish cycle without a persistent cache. Missing,
destroyed, invalid, zero-ID or ambiguous observations resolve to no identity.
A remote Player outside the host's currently instantiated/observed entities therefore
receives an empty snapshot until an authoritative entity becomes available. There is
no scene scan, name/position heuristic, damage-based identity, peer==PlayerID shortcut,
or speculative reconnect mapping. Mapping itself does not create membership.

CombatSnapshotBuilder.ForPlayer calls only TryGetClusterForPlayer. A successful
membership lookup builds from that cluster's EncounterManager. NoCluster or unresolved
identity uses Empty: encounter ID 0, NoEncounter, elapsed 0, no rows.
There is no legacy, LastFinished, arbitrary-component or aggregate fallback.
Active and Recovery clusters remain routable; manager Update removes Finished
membership before publication. A new fight naturally publishes its new cluster ID.
Merge requires no migration: the next lookup returns the deterministic survivor.

## Publish cycle and delivery

The existing host-session epoch remains. Sequence increments exactly once per
publish cycle, before building recipient payloads. Every payload in the cycle,
including local and empty snapshots, has that same epoch and sequence.

The local snapshot goes directly to CombatSnapshotStore.Apply, with no self RPC.
Remote ready peers receive individual routed RPCs addressed to peer.m_uid.
Zero, local-host destinations and duplicate destinations are skipped. Single-player
publication sends no remote snapshot. There is no Everybody snapshot broadcast.

CombatSnapshot.ProtocolVersion remains 1, SnapshotRpc remains
CombatMeter.CombatSnapshot.v1, and the codec and its validation are unchanged.
Client receive/store validation remains unchanged: actual-host sender, matching host
session identity, malformed payload rejection, duplicate/stale rejection, and epoch
changes requiring the existing session/store reset. Newer empty snapshots replace
old combat data and the existing UI handles NoEncounter without UI changes.

The legacy global EncounterManager is retained only for transitional diagnostics
and DeathObservation. It is not passed into snapshot construction or selection.
Attribution, Damage Done/Taken, DPS, Recovery, cluster merge and player colors are unchanged.

## Checks

Baseline 377 checks plus 25 new peer-snapshot checks = 402.
Existing snapshot adapter fixtures now register connected peers and observed Player
identity, rather than relying on broadcast. Their obsolete global/Finished snapshot
expectations are updated to per-player/NoEncounter expectations; checks were not removed.

New tests exercise the actual transport with managed Player/ZDO/peer doubles and
client receive paths. Coverage includes A/B versus C isolation, shared rows, bridge
merge, donor retirement, player-only damage, finish/new encounter, local/unknown
identity, owner changes, ambiguous/invalid entities, exact cycle sequencing,
ready/directed/zero/self destinations, v1 round trips and receive validation.

```powershell
dotnet run --project tests/ProbeChecks/ProbeChecks.csproj -c Release
dotnet build DiagnosticDamageProbe.csproj -c Release --no-incremental
```

All 402 checks pass. Release build completes with zero warnings and errors.
No Valheim runtime test is performed by this milestone's automated workflow.
