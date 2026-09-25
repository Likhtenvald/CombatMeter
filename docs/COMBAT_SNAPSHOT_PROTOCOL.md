# CombatSnapshot protocol v1

Milestone 3A (plugin `0.8.0`) adds a full-state synchronization boundary between the listen-host combat core and future UI. It does not change Damage Done, Damage Taken, DPS, PvE filtering, recovery tickets, magic attribution, or summon attribution.

## Authority and data flow

The listen host is the only snapshot producer. `DamageCommit` and attribution feed the existing host `EncounterManager`; `CombatSnapshotBuilder` projects that authoritative state into one `CombatSnapshot`. The same object is first applied to the host's `CombatSnapshotStore`, then serialized and broadcast to clients. A client only decodes, validates, and atomically replaces its store. It never aggregates damage, advances encounter state, handles recovery, or recalculates DPS.

Dedicated-server support remains outside the current listen-host scope. Single-player uses the host local-store path and skips the network broadcast.

## DTO

`CombatSnapshot` contains:

- `ProtocolVersion` (`1`);
- `HostPeerSessionId` (`Int64`);
- `SnapshotEpoch` (`Guid`);
- monotonically increasing `Sequence` (`Int64`);
- `EncounterId` (`Int64`), `EncounterState`, and authoritative `EncounterElapsedSeconds`;
- an immutable copied list of `CombatSnapshotPlayer` rows.

Each player row contains `PlayerId`, `DisplayName`, `DamageDone`, `Dps`, and `DamageTaken`. Combat totals retain the core's `float` representation; DPS and elapsed time retain `double`. `PlayerId` is the sole identity key: zero is invalid and negative IDs, including `-451055642`, are valid. Names are display metadata only.

`EncounterManager` creates the per-session encounter identity. Its counter starts at zero, increments when a new encounter starts, remains stable through `Active -> Recovery -> Active` and `Finished`, and resets with the host session. `NoEncounter` is encoded as ID zero, zero elapsed time, and no rows.

## Wire format

RPC name: `CombatMeter.CombatSnapshot.v1`. Fields use this explicit binary order:

```text
byte    protocolVersion
int64   hostPeerSessionId
16 byte snapshotEpoch
int64   sequence
int64   encounterId
byte    encounterState
double  encounterElapsedSeconds
byte    playerCount

repeat playerCount:
  int64   playerId
  string  displayName (BinaryWriter UTF-8 length-prefixed form)
  float   damageDone
  double  dps
  float   damageTaken
```

Reflection serialization is not used. Rows are sorted by signed `PlayerId` ascending before encoding. The codec rejects trailing bytes and truncated payloads.

## Heartbeat and replacement semantics

The host publishes at most once every 500 ms from the regular transport update loop, including `NoEncounter` and repeated `Finished` state. Damage observation does not publish per hit. A joining client therefore receives the full current encounter on a subsequent heartbeat without replaying past commits.

Every host network session creates a new random `SnapshotEpoch`; every publication increments `Sequence`. For the active epoch a client accepts only `sequence > lastAcceptedSequence`. Equal sequences are duplicates and lower sequences are stale. Missing sequence numbers need no repair because a later full snapshot replaces all prior state.

There is deliberately no ACK, retry queue, or exactly-once mechanism. A snapshot is replaceable state rather than a transaction; the next 2 Hz heartbeat repairs packet loss.

The store does not silently switch epochs. A network lifecycle reset clears the store first, after which the first snapshot of the new session is accepted. Disconnect, world stop/network replacement, reconnect, and plugin disposal all run this clear path.

## Sender and payload validation

A client compares the actual routed-RPC sender with its current `ZNet.GetServerPeer().m_uid`, then requires the payload's `HostPeerSessionId` to equal that same value. Host processes ignore received snapshot RPCs. A client-originated or stale-host packet therefore cannot replace the store merely by forging a payload field.

Before atomic replacement, the complete payload must satisfy all of these rules:

- supported protocol, nonzero host ID, nonempty epoch, positive sequence;
- defined encounter state and consistent encounter ID/state (`NoEncounter` uses ID zero; other states use a positive ID);
- finite, nonnegative elapsed time;
- at most 64 player rows and at most 64 UTF-16 characters per display name;
- nonzero, unique, strictly ascending player IDs;
- finite, nonnegative Damage Done, DPS, and Damage Taken;
- total encoded packet no larger than 16 KiB and no trailing data.

The builder applies the same numeric and identity rules before host publication. It truncates host-owned display metadata to 64 characters and refuses an impossible oversized or numerically invalid authoritative projection. Any malformed client payload leaves the previously accepted snapshot unchanged.

## Store API and diagnostics

`CombatSnapshotStore.TryGetLatest(out CombatSnapshot)` is the common read path intended for both host and future client UI. `Clear()` removes the snapshot, epoch, and accepted sequence. Snapshot instances and row lists are complete immutable projections, so readers never observe partial row application.

Diagnostics use `CombatSnapshotAppliedLocal`, `CombatSnapshotPublished`, `CombatSnapshotReceived`, `CombatSnapshotAccepted`, `CombatSnapshotIgnored`, and `CombatSnapshotRejected`. State/row changes are logged on host and accepted-client paths; receive logging is capped to once per five seconds. Duplicate, stale, and rejected packets retain explicit reason markers.

## Runtime verification

Single-player smoke test:

1. Start a world and fight a troll for several hits.
2. Wait long enough to observe `Active`, then stop combat until `Finished`.
3. Confirm `CombatSnapshotAppliedLocal` and `CombatSnapshotPublished`, stable `EncounterId`, increasing elapsed/DPS changes during active or recovery state, and frozen values during repeated finished heartbeats.

Listen-host multiplayer smoke test:

1. Start a host and one client; both attack the same troll and each receives damage.
2. Compare host and client logs for the same epoch and sequence: encounter ID/state and every player row must match.
3. Stop combat and confirm the client receives the same frozen `Finished` snapshot.
4. Optionally join the client after combat has begun and confirm a complete current snapshot arrives within the next heartbeat without commit replay.

Managed tests use API doubles and prove builder, codec, ordering, replacement, lifecycle, sender checks, and host/client parity. They do not prove delivery timing, `ZRoutedRpc.Everybody` behavior, callback sender identity, or lifecycle ordering in the live Valheim runtime; those items require the smoke tests above. There is no UI in this milestone.
