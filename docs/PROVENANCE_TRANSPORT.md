# Provenance transport lifecycle (0.7.3)

The magic-attribution wire protocol remains version 1. A provenance event is identified everywhere by the same
`EventId`: signed source peer ID, source epoch GUID, and positive sequence. Sequence is monotonic within one epoch;
retries serialize the original message and therefore retain its EventId.

## Lifecycle

1. `DamageCommitTransport.ObserveSummonProvenance` creates the message with `EventSequence.Next`.
2. `PublishAttribution` puts a remote message in the bounded 256-entry `AttributionOutbox`.
3. `Pump` asks `AttributionOutbox.Due` for the head. Retry interval is one second; one head is in flight at a time.
4. `SendAttribution` sends the message to the current server peer.
5. `ReceiveAttribution` decodes it on the host, logs receipt, and calls `ProcessAttribution`.
6. `AttributionAcceptor` validates sender/source identity and payload, then applies its 2048-sequence dedup window.
7. `ProcessAttribution` applies a newly accepted mapping once. A repeated accepted EventId returns `Duplicate` and
   does not repeat registry or pending-replay side effects.
8. The host ACKs both `Accepted` and `Duplicate`. This recovers from a lost first ACK.
9. `ReceiveAttributionAck` accepts ACK only from the current server peer, decodes the exact EventId, and calls
   `AttributionOutbox.Complete`. Only a matching head is removed; an unknown ACK changes no state.

An unacknowledged head expires 30 seconds after its first send, produces `SummonProvenanceRetryExpired`, and is
removed so later provenance cannot remain blocked forever. Disconnect, session replacement, world stop, and plugin
shutdown reset the whole outbox immediately.

## Diagnostics

Host markers are `SummonProvenanceReceived`, `SummonProvenanceAccepted`, `SummonProvenanceDuplicate`,
`SummonProvenanceRejected`, and `SummonProvenanceAckSent`. Client markers are
`SummonProvenanceMessageQueued`, `SummonProvenanceMessageSent`, `SummonProvenanceAckReceived`,
`SummonProvenanceMessageCompleted`, `SummonProvenanceAckUnknown`, and `SummonProvenanceRetryExpired`.

Version 0.7.2 already encoded and sent ACKs, and duplicate receives were also ACKed. It did not log ACK receipt or
outbox completion, and its head could retry forever. Therefore the old logs cannot distinguish a lost ACK, rejected
ACK sender, malformed ACK, or missing host receive. They also explain why a later summon could remain queued behind
one stuck EventId. Version 0.7.3 makes each stage observable and bounds that head-of-line failure.
