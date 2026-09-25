# Magic attribution probe (Milestone 2D)

Static source: local Valheim 1.0.15 `assembly_valheim.dll`, decompiled under `work/decompiled`.

## Summoned root

The cast enters `SpawnAbility.Setup(Character owner, ...)`, which retains the caster only in the runtime
`m_owner` field. `SpawnAbility.Spawn()` instantiates `m_spawnPrefab`. When `m_commandOnSpawn` is enabled it calls
`Tameable.Command(owner)`, sending the caster's `ZDOID` to owner-side `Tameable.RPC_Command`.

`RPC_Command` resolves that ZDOID to a Player and assigns it as `MonsterAI`'s runtime follow target. The only value
persisted on the summon ZDO by this path is `ZDOVars.s_follow`, containing the **player name**. No summoner PlayerID or
summoner ZDOID is written. The summon ZDO owner is network ownership and is not a stable summoner identity. Generic
`s_creator` belongs to `Piece`; `s_ownerZDOUserId/s_ownerZDOId` are effect/sound metadata and are not written by this
summon path.

The probe instruments `SpawnAbility.Setup`, casting-side `Tameable.Command`, and a summoned-root hit observed at
`Character.ApplyDamage`. This captures the concrete root/caster pair before vanilla routes `RPC_Command` to the
root-owning peer. Runtime testing remains necessary for the actual network route and durable host mapping.

## Elemental DoT

Owner-side `Character.RPC_Damage` resolves the direct hit attacker before resistance and armor processing. It then
extracts fire, spirit, and poison damage, clears those components from the direct hit, and calls private
`AddFireDamage`, `AddSpiritDamage`, and `AddPoisonDamage`. Those helpers receive only damage and variant; attacker is
not forwarded.

Fire and spirit use separate `SE_Burning` instances/hashes. `AddFireDamage` and `AddSpiritDamage` add incoming damage
to a shared remaining-damage pool, recompute damage per tick, and reset time. Contributions from multiple attackers
are mixed in one pool with no source field. Poison uses one `SE_Poison`; `AddDamage` replaces its pool only when the
new damage is at least the remaining pool, otherwise it does nothing. It also stores no source.

`StatusEffect.SetAttacker` is empty in the base class. `SE_Burning` and `SE_Poison` do not override it. Tick updates
construct fresh `HitData` objects and never call `SetAttacker`, explaining `AttackerZDOID=0:0`. Spirit ticks use
`HitType.Burning` with spirit damage.

The probe carries a reentrant per-thread diagnostic context only across `Character.RPC_Damage` and its nested
elemental helper calls. A Harmony finalizer clears that context if the original throws. It logs the actual
post-resistance damage passed to the helper, effect identity, pool before/after, and the still-known direct attacker.
This context is diagnostic only and never changes `DamageCommit`.

Vanilla cannot reliably assign every future Burning/Spirit tick to one player after multiple sources have contributed
to the shared pool. Poison has a possible winning-source interpretation when a hit replaces the pool, but runtime
ordering and refresh behavior must be confirmed before implementing it.
