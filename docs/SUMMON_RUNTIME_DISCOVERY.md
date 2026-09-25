# Generic summon provenance (0.7.2)

Runtime discovery proved the post-`Instantiate` callback for Staff of the Wild: one callback contained the exact
caster PlayerID, spawned instance, valid ZNetView, and summoned ZDOID. Version 0.7.2 uses that boundary as the generic
production provenance source. Attribution transport and the 3-second pending damage buffer are unchanged.

## Static findings

`SpawnAbility.Setup` stores its exact caster in private field `m_owner` and starts the private `Spawn` coroutine.
The coroutine creates each concrete object with `Object.Instantiate`, then reads its `ZNetView` and `Projectile`.
Only when `m_commandOnSpawn` is true and the object has `Tameable` does it call `Tameable.Command(m_owner, false)`.
`Command` routes `RPC_Command` to the new object's current ZDO owner. `RPC_Command` resolves the caster ZDOID and may
call `MonsterAI.SetFollowTarget`.

The ordinary `Spawn` method is an iterator factory: execution after `yield` occurs in compiler-generated
`SpawnAbility.<Spawn>d__*.MoveNext`. The discovery build transpiles that exact `MoveNext` and injects a callback
immediately after the root-producing `Object.Instantiate` call. The callback receives the returned `GameObject` and
the same `SpawnAbility`; it reads the exact retained `m_owner`. No scene search, timestamp, position, owner, name, or
spawn-order correlation is used for identity.

Unity calls `Awake` during `Instantiate`, so a new root may already have a valid ZDO when the call returns. If it does
not, a diagnostic-only bounded list holds that exact `GameObject` reference for up to 10 seconds and logs when the
same instance obtains a valid ZDO. The list is capped at 64, removes successful/expired/destroyed entries, and clears
when the plugin stops.

## Expected markers

Startup must show `MagicSummonHookInstalled` for:

- `SpawnAbility.Setup`
- `SpawnAbility.Spawn.MoveNext`
- `Tameable.Command`
- `Tameable.RPC_Command`
- `MonsterAI.SetFollowTarget`

A cast should then show `MagicSummonRuntimeSetupEntered`, `MagicSummonRuntimeSpawnEntered`,
`MagicSummonRuntimeSpawnCreated`, and either immediate or delayed `MagicSummonZdoReadyObserved`. Optional downstream
markers establish whether this prefab actually uses the command route: `MagicSummonCommandObserved`,
`MagicSummonRpcCommandObserved`, and `MagicSummonFollowTargetObserved`.

The decisive root evidence matched `RootInstance` across spawn and ZDO-ready markers, with the caster PlayerID coming
from the spawning ability's retained `m_owner`. The root-only diagnostic filter was removed, so the same complete
payload is emitted for `Skeleton_Friendly`. Both supported prefabs use one centralized allowlist and one registration
path. `Tameable.Command`, `RPC_Command`, and `SetFollowTarget` remain diagnostic only.

For a supported prefab, the callback validates a `Player` caster, non-zero signed `PlayerID`, and valid summon ZDOID,
then calls the existing provenance transport. A listen host processes it locally; a remote client queues it for the
host with the existing sender validation, epoch/sequence deduplication, retry, and ACK.
