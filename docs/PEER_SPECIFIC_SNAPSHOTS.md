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

The distance-dependent HUD failure was in recipient identity lookup, not in cluster
selection: the previous ResolveRemote scanned Player.GetAllPlayers and returned
unresolved when the remote entity unloaded. The builder then sent NoEncounter even
with an active cluster. Managed adapter checks reproduce remote acceptance, duplicate
ACK and sender rejection without an observed Player; no distance dependency was
found in DamageCommit delivery. This does not substitute for logs of the reported
live session.

SessionPlayerIdentity now binds a currently ready nonzero PeerID to a nonzero
Gameplay PlayerID and to the actual ZNetPeer instance for this connection.
The installed assembly inspection confirms these vanilla paths:

- ZNet registers PlayerID on each connected peer's ZRpc; RPC_PlayerID writes
  that peer's m_playerID. A nonzero value is usable without a Player instance.
- Merely having this handler is insufficient: no invocation was found in the
  inspected normal connection/spawn paths. Game.SpawnPlayer actually loads the
  local PlayerProfile and calls ZNet.SetCharacterID.
- The host's RPC_CharacterID associates that character ZDOID with the actual peer.
  ZDOMan.GetZDO(peer.m_characterID) retrieves replicated data without instantiating
  a Unity Player. Its owner must match the peer; ZDOVars.s_playerID must be nonzero.
  If both vanilla identity sources exist, they must agree.

Character ZDO data can arrive after registration. Until a source is available,
the peer receives NoEncounter; no combat facts or other cluster supply identity.
After verification, temporary missing character data (including respawn gaps) does
not revoke that connection's binding. Available replacement data must agree.
An explicitly zero identity remains unresolved while initialization completes.

Player.Start/OnDestroy notifications maintain a small list of observed entities,
bootstrapped once from registered Players when the host session starts. A single
consistency audit of those references checks available Player/ZDO identities before
publication. Absence or an invalid/unloaded view is not an identity source and does
not invalidate a verified vanilla binding. Nonzero disagreement quarantines the
connection; it never silently switches to another PlayerID.

The recipient lookup is O(1). Reusable maps/lists reconcile connected peers and
check observed entities once per publication (O(peers + observed Players)), rather
than scanning Player.GetAllPlayers for every recipient. No scene/world-object scan,
per-frame collection, additional RPC, or change to snapshot frequency is introduced.
This reconciliation cost is included in snapshot-cycle telemetry; identity timing
now measures the recipient lookup.

Disconnect reconciliation drops bindings; a replacement ZNetPeer with the same UID
starts fresh even between publications. World/session close discards the whole
registry and its observed references. Respawn retains the same verified gameplay
identity. Changed identity, duplicate active PlayerID, local-player collision,
ambiguous PeerID or conflicting observations fail closed until reconnect.
Transition-only diagnostics use the existing transport diagnostic switch:
CombatIdentityBound, CombatIdentityRejected, CombatIdentityInvalidated and
CombatSnapshotIdentityUnresolved. Performance diagnostics remain separately opt-in.

Trust model: CombatMeter is a cooperative statistics mod, not an anti-cheat system.
It trusts vanilla peer identity and character ownership/registration to the same
extent as Valheim. These consistency checks protect against stale/conflicting
routing, not a malicious modified client forging vanilla PlayerID or ZDO data.
There is no new identity handshake, and no identity/name/distance/cluster heuristic.

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

The original 4C baseline was 402 checks. The hotfix extends the 465-check release\nbaseline with distance, vanilla-character registration, conflict and lifecycle regressions.
No Valheim runtime test is performed by this milestone's automated workflow.
