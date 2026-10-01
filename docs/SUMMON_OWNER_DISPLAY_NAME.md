# Summon-owner display names — CombatMeter 1.0.1

Baseline: main b7ce679fe83fe6113dc9eac387d318e19405d5b6, plugin 1.0.0,
531 managed checks. No protocol change required.

## Confirmed root cause

MagicAttributionProbe observes supported SpawnAbility-created NPCs, reads the
caster PlayerID, and registers summon ZDO -> PlayerID provenance through the
existing MagicAttribution.v1 path. SummonProvenanceRegistry preserves that mapping.
MagicAttributionResolver resolves NPC damage into an AttributedDamageEvent with
DamagePortion.PlayerId and the accepted damage amount. Neither provenance nor
DamagePortion includes a name.

CombatClusterManager selects/merges membership using that PlayerID and passes the
same event to EncounterManager.Accept. Previously the latter always supplied an
empty PveClassification display name. CombatStatisticsAggregator.GetOrCreate could
therefore create the owner's first PlayerCombatStatistics with an empty name.
CombatSnapshotBuilder carried that value unchanged, and CombatMeterPresenter
rendered its existing safe Player <ID> fallback. Correct ownership did not imply
that the host had learned a name from an earlier direct hit.

## Existing vanilla source and narrow repair

In the inspected game API, ZNet.RPC_PeerInfo reads the vanilla character name and
stores it in the connected ZNetPeer.m_playerName. RPC_PlayerID associates the
Gameplay PlayerID with that peer. Existing SessionPlayerIdentity validates that ID
against the character ZDO when available and quarantines identity conflicts.
This fix adds presentation metadata to the existing Binding, not another identity
registry. The existing PlayerID dictionary provides O(1) name lookup only when
Resolve confirms the current binding is valid. Names never establish identity.

A ready, nonzero, non-conflicting peer binding retains its last non-empty vanilla
name. Entity unload does not remove it. Disconnect removes the binding; replacing
the peer object creates a fresh binding; session/world close drops the entire
identity object. An old observed Player cannot seed a new connection's name.
Local host metadata comes from its validated local Player, without a scene scan.
Names from departed players can remain in their existing encounter statistics,
just like other encounter history; they are not retained as session lookup data
for future encounters or transferred to another PlayerID.

EncounterManager receives an optional name resolver from the transport through
the existing cluster acceptance path. It supplies the name for already-resolved
attribution portions before aggregation. The delegate is allocated once per host
session, not per hit. PlayerCombatStatistics retains its non-empty-only update
rule and one row per PlayerID. Both legacy diagnostics and cluster statistics use
the same metadata. Attribution, amounts, membership and death/Recovery logic are
unchanged.

At the existing identity synchronization boundary before snapshot publication,
valid metadata also updates an existing statistics row without creating a row or
adding damage. Thus a late name repairs an unnamed row without requiring another
hit. A new connection's hit arriving before the first identity synchronization
may initially have empty internal metadata; its first outgoing snapshot receives
the name once vanilla identity/name are available. Cadence is unchanged.

## Initial visibility and trust

The host need never observe the remote Player GameObject: the ready vanilla peer
supplies m_playerName and the existing validated session binding supplies PlayerID.
The initial-visibility case is covered without a custom handshake or payload
change. Zero/unresolved/conflicting identities and missing names retain the safe
fallback; names are never fabricated or used to guess ownership.

This uses the existing cooperative-game trust model for vanilla peer identity and
presentation metadata, not an anti-cheat guarantee. A malicious modified client's
chosen name is not authenticated. Names cannot change provenance or routing IDs.
No additional logs expose names.

## Regression coverage and cost

553 managed checks (531 baseline plus 22 focused adapter regressions) cover first
summon damage, repeated and mixed direct/summon hits, unknown/late/empty names,
entity unload, initial invisibility, disconnect, peer replacement, reconnect,
world reset, same-name players, isolation/merge, conflicts, zero identity, local
host, Recovery/metrics preservation, and no additional GetAllPlayers scans.
Tests exercise production provenance registration, attribution, cluster statistics,
directed snapshots, client decoding and HUD presentation through managed doubles.
The user successfully completed manual Valheim runtime testing with functional
commit 60efaa6e9d57ec60abc59529df61cc3914692dcb. Release preparation freezes that
implementation and changes only version metadata and documentation.

Storage is O(connected players), name lookup O(1), metadata synchronization reuses
existing peer iteration and O(1) statistics lookups. No per-hit telemetry, name
history, added RPC, per-hit allocation, extra snapshot, or scene scan is introduced.
CombatSnapshot.v2 and MagicAttribution.v1 remain unchanged. Release 1.0.1 changes
version metadata and release documentation only; previous release packages remain
frozen.
