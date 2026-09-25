# Player lifecycle probe (Milestone 2C)

Static source: local Valheim 1.0.15 `assembly_valheim.dll`, decompiled in `work/decompiled`.

## Instrumented candidates

- `Player.OnDeath()` prefix: the direct death lifecycle method. `Character.CustomFixedUpdate` calls
  `CheckDeath()` only for the ZDO owner; `Player.OnDeath()` itself also returns early for a non-owner.
- `Player.RPC_OnDeath(long)` prefix: the `OnDeath` broadcast receiver. It runs the visual proxy path and is
  intentionally logged beside `Player.OnDeath` to determine which peers receive it and whether the owner also
  observes its own broadcast.
- `Game.SpawnPlayer(...)` postfix when `m_respawnAfterDeath` is true: the direct local post-death spawn path.
  It runs after the new Player has been made local, profile data (including stable PlayerID) has been loaded,
  ZNet character ID has been assigned, and `Player.OnSpawned` has run.
- `Player.Start()` postfix and `Player.OnDestroy()` prefix: generic instance boundary markers. They are not treated
  as death or respawn by themselves; they let host and client logs show replacement Player objects and ZDOs.

`Player.OnRespawn()` was not instrumented: static call-site search in this assembly finds only its declaration.
Using it as the primary respawn signal would therefore be speculative.

The probe only logs. It does not call `EncounterManager.OnPlayerDied` or `OnPlayerRespawned`, and it adds no RPC.

## Runtime questions

The assembly proves the local control flow, but only a two-peer run can establish RPC delivery and object replication
behavior in practice: which peer sees each marker, exact counts/order, whether the listen host creates a new proxy for
a remote respawn, stable PlayerID, changed instance/ZDOID, and whether lifecycle transport is needed.
