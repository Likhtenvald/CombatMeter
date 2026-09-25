# Magic attribution core (Milestone 2E)

The factual `DamageCommit` remains the canonical accepted damage event. Protocol version 2 adds factual attacker
ZDOID components and a factual DoT kind (`Poison`, `Burning`, or `Spirit`); it does not rewrite NPC/None attackers as
players. After host validation and deduplication, `MagicAttributionResolver` creates an `AttributedDamageEvent` for
statistics and encounter processing.

## Summons

The production source is a callback injected immediately after `Object.Instantiate` in
`SpawnAbility.<Spawn>*.MoveNext`. It receives the exact spawned object and the same `SpawnAbility`, whose `m_owner`
is the exact caster retained by vanilla. The explicit combat-summon allowlist currently contains
`staff_greenroots_tentaroot` and `Skeleton_Friendly`. A valid spawned ZDOID and any non-zero signed Int64 PlayerID
produce a separate addressed `SummonProvenanceMessage`. It contains source peer/epoch/sequence, summon ZDOID, and
summoner PlayerID. The host checks
RPC sender against source peer and applies a bounded sliding dedup window. Client delivery uses a bounded FIFO,
one-second retry, and directed ACK.

The host registry is keyed by summon ZDOID and stores stable PlayerID. Current ZDO owner, name, peer prefix, proximity,
and last-caster guesses are not used. The registry is capped at 512 entries and is cleared with the network session.
Ownership migration does not alter an existing mapping; a conflicting remap is rejected.

Normally the ZDO is valid when `Instantiate` returns. Otherwise a bounded 64-entry, ten-second queue retains the
same spawned `GameObject` reference and PlayerID until that same instance has a valid ZDO. It is cleared on success,
destruction, timeout, network-session shutdown, disconnect, and plugin shutdown. Provenance is never inferred from
ZDO ownership, follow target, command state, player name, timing, position, or spawn order.

An accepted NPC commit with an unknown attacker ZDOID waits in a 256-entry, three-second attribution buffer. Mapping
arrival replays only resolver/statistics processing. Transport acceptance and dedup are never rerun. Expiry applies
the original unattributed semantics.

## DoT

The victim owner sends accepted pool updates over the same attribution channel. Source is either direct PlayerID,
summon ZDOID resolved by the host registry, or unattributed.

Poison stores the owner of the last pool update that vanilla actually accepted. An ignored weaker application leaves
the owner unchanged; an accepted anonymous replacement clears it.

Burning and Spirit have separate ledgers keyed by victim ZDOID and kind. Each positive pool delta is added to the
source's outstanding contribution. Actual `EffectiveHpLoss` ticks are divided proportionally, including an
unattributed bucket. The final portion receives floating-point remainder, so portions sum to the observed HP loss.
Outstanding balances decrease by their distributed portions. Raw pool damage is never counted as statistics.

Ticks that arrive before their pool update use a separate bounded three-second attribution buffer with the same
resolver-only replay rule.

Player-sourced logical damage against a Player produces no statistics and cannot start or extend the PvE encounter.

## Known limits

The cooperative trust model validates message sender/session and deduplicates delivery; it is not anti-cheat against
a modified source client making false PlayerID claims. Burning/Spirit allocation is proportional accounting over
vanilla's mixed pool, because vanilla retains no per-source tick provenance. Unused pool remainder never becomes
Damage Done.
