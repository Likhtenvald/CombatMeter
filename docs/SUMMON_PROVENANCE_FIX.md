# Summon provenance fix (0.7.1)

## Cause

Version 0.7.0 registered provenance only from `Tameable.RPC_Command`. `Tameable.Command` sends that RPC to the
current owner of the new root ZDO, so the callback is not a reliable casting-client observation point. The runtime
failure showed that no usable registration was produced by that owner-side callback before the pending timeout.

The vanilla `SpawnAbility.Spawn` flow already has a deterministic earlier boundary on the casting client:

```text
Instantiate root
-> get root Tameable
-> Tameable.Command(caster, false)
-> ZNetView.InvokeRPC(root owner, "Command", caster ZDOID, false)
```

At the entry to `Tameable.Command`, the concrete root `Tameable`, its valid `ZNetView/ZDOID`, and the exact
`Humanoid user` supplied by `SpawnAbility` are available together. The 0.7.1 prefix accepts only a summoned-root
instance and a `Player` with a valid caster ZDOID and non-zero stable `PlayerID`. It does not use the player name,
root owner, peer ID, ZDO prefix, distance, or timing.

## Routing

The casting client publishes `root ZDOID -> PlayerID` through the existing 2E attribution transport. A client queues
the existing protocol-v1 attribution message, sends it to the listen host, retries until ACK, and the host validates
the RPC sender against the message source before registering it. A listen-host cast is processed locally. Duplicate
commands remain safe because the host registry accepts the same mapping idempotently.

No wire format or protocol name changed. DamageCommit remains protocol v2 and magic attribution remains v1.

## Diagnostics

The relevant sequence is:

```text
SummonProvenanceRegistrationAttempt
SummonProvenanceRegistered
SummonProvenanceMessageQueued       (remote client)
SummonProvenanceMessageSent         (remote client, including retries)
SummonProvenanceAccepted            (listen host)
```

Registration failures identify `RootZdoUnavailable`, `CasterZdoUnavailable`, `CasterNotPlayer`, `PlayerIdZero`,
`TransportUnavailable`, or `NotExpectedPeer` where applicable.

## Runtime retest

1. A remote client summons a root and lets it hit a troll several times. Confirm the host accepts the exact root
   mapping and credits every effective HP loss to that client's PlayerID.
2. Let the same root poison the troll. Confirm the poison owner resolves through that root mapping and its ticks are
   credited to the same client.

The managed checks cannot execute Unity coroutines, ZNet ownership routing, or Harmony callbacks, so these two
in-game cases remain required.
