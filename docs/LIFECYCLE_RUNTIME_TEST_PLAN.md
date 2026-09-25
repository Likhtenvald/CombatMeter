# Player lifecycle runtime test plan

Use Valheim 1.0.15 with the same `CombatMeter.dll` on the listen host and both clients. Keep
`EnableLifecycleDiagnosticLogging = true`. Preserve each peer's complete BepInEx log and note the wall-clock time of
each action.

## A. Host death and respawn

1. Start a listen-host world and connect one client.
2. Record the initial `PlayerLifecycleInstanceObserved` lines on both peers.
3. Kill the host once and wait for the respawn to finish.
4. Preserve host and client logs.

## B. Client death and respawn

1. Kill the connected client once and wait for respawn.
2. Preserve both logs without reconnecting.

## C. Client death during PvE

1. Produce accepted PvE damage from both players.
2. Kill the client, stop damage, respawn, return, and deal PvE damage again.
3. Compare lifecycle markers with `DamageCommitAccepted` timestamps.

## D. Host death during PvE

Repeat scenario C with the listen host as the dying player.

For every death/respawn compare `HookName`, `LocalPeerId`, `IsHost`, `PlayerID`, `PlayerZDOID`,
`CurrentOwnerPeerId`, `IsOwner`, `IsLocalPlayer`, and `CharacterInstanceIdentity`. Count each hook per peer. Verify
whether PlayerID stays stable while instance identity and ZDOID change. Do not infer transport requirements until both
logs have been compared.

## Integration verification (0.5.1)

With an encounter active, kill the host and then the remote client. For each death, the listen-host log must contain
exactly one `PlayerLifecycleDeathCommitted`; client-side callbacks must produce `PlayerLifecycleDeathIgnored` with
`Reason=NotHost`. Repeated callbacks for the same PlayerID and PlayerZDOID must report `Reason=Duplicate`.

Stop PvE damage after a committed death and verify recovery after 10 seconds. Respawn alone must leave the encounter
in recovery; the next accepted PvE commit must resume it without resetting totals or StartTime. No custom lifecycle RPC
should appear in either log.

