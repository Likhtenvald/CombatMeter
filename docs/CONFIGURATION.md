# Configuration authority

Local settings affect only the current player's HUD or local diagnostic logging. Host settings use the Listen Host's value for canonical combat and statistics behavior across the session. Non-host clients may retain different values in their own config files; those values are ignored by aggregation, and clients render the host's finished `CombatSnapshot` values.

Existing section and key names are preserved in `0.12.0`, with a new independent performance diagnostics key, so HUD position, hotkeys, and presentation preferences migrate without reset. Every setting is read live. BepInEx range validation and the pure core sanitizers protect numeric combat values; UI layout additionally replaces non-finite values and clamps size, opacity, and position before applying them.

| Setting | Default | Bounds | Scope | Meaning |
|---|---:|---|---|---|
| Diagnostics / EnableDiagnosticLogging | false | Boolean | Local | Detailed owner-side HP observations. |
| Diagnostics / EnableTransportDiagnosticLogging | false | Boolean | Local | Damage transport diagnostics. |
| Diagnostics / EnableLifecycleDiagnosticLogging | false | Boolean | Local | Player lifecycle diagnostics. |
| Diagnostics / EnableMagicAttributionDiagnosticLogging | false | Boolean | Local | Summon and elemental attribution diagnostics. |
| Diagnostics / EnablePerformanceDiagnosticLogging | false | Boolean | Local | Collects and reports performance metrics when this process is the host; no gameplay effect. |
| UI / UI Enabled | true | Boolean | Local | Shows the local Combat Meter. |
| UI / Toggle Key | F8 | Shortcut | Local | Shows or hides the local HUD. |
| UI / Edit Mode Key | LeftControl+F8 | Shortcut | Local | Enters or exits local edit mode. Shift+F8 keeps its existing reset behavior. |
| UI / UI Scale | 1 | 0.5–2.0 | Local | Changes local HUD size. |
| UI / Window Width | 480 | 350–800 | Local | Changes local HUD width. |
| UI / Background Opacity | 0.65 | 0–1 | Local | Changes local panel opacity. |
| UI / Show Damage Bars | true | Boolean | Local | Shows contribution bars. |
| UI / Show Damage Percent | true | Boolean | Local | Shows contribution percentages. |
| UI / Damage Bar Opacity | 0.25 | 0–1 | Local | Changes contribution bar opacity. |
| UI Position / X | 24 | Finite; canvas-clamped | Local | Persists horizontal HUD position. |
| UI Position / Y | -150 | Finite; canvas-clamped | Local | Persists vertical HUD position. |
| Combat / Combat Timeout | 20 | 5–60 seconds | Host | How long combat may remain inactive before the encounter ends or enters Recovery. |
| Combat / Recovery Timeout | 180 | 30–600 seconds | Host | How long a dead player may return to the same encounter. |
| Combat / DPS Idle Timeout | 6 | 1–20 seconds | Host | How long time continues to count toward personal DPS after outgoing PvE damage. |

All diagnostic categories default to disabled. Other diagnostic switches do not implicitly enable performance diagnostics. Enabling performance diagnostics live starts a fresh reporting window; disabling discards the partial window and bypasses telemetry timing/counters.

There is intentionally no ConfigSync, ServerSync, config RPC, or config locking. Clients do not require host configuration values because `CombatSnapshot v1` already transports authoritative encounter state, elapsed time, Damage, DPS, and Taken.

