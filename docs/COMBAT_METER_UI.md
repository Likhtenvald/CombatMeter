# Combat Meter UI MVP

Milestones 3B–3F provide the read-only in-game meter, configurable host encounter timing, and personal active-time DPS for plugin `0.11.2`. The HUD is a consumer of `CombatSnapshotStore`; it does not read local encounter timeout config or calculate transitions. Damage totals, DPS, elapsed time, encounter state, and membership all arrive already computed by the listen host in `CombatSnapshot v1`.

The listen host defaults to a 20-second combat inactivity timeout, a 180-second recovery timeout, and a 6-second personal DPS idle timeout. Client-local values do not affect UI state: Active, Recovery, Finished, elapsed time, and DPS continue to come exclusively from the host snapshot.

## Presentation model

`CombatMeterPresenter` maps a complete snapshot to `CombatMeterViewModel`. The model contains encounter ID, state text, authoritative time text, `ShouldShow`, and immutable row models. Each row contains the identity PlayerID plus display-only name, damage, DPS, and taken strings.

Presentation order is Damage Done descending, then signed PlayerID ascending. This does not change the PlayerID-ascending wire order. Empty names use `Player <PlayerID>`. Damage and Taken use invariant whole-number formatting; DPS and elapsed time use one invariant decimal place. The mapper preserves Unicode and the complete snapshot name; the Unity view alone clamps names longer than 25 UTF-16 characters with an ellipsis.

`CombatMeterPresenter` also derives the presentation-only damage share:

```text
totalDamage = sum(all displayed DamageDone)
contribution = totalDamage > 0 ? player.DamageDone / totalDamage : 0
```

The normalized contribution is clamped to `0..1` and formatted independently with one decimal place, such as `56.7%`. Display rounding is not adjusted to force the visible sum to 100%. If total damage is zero, every contribution, percentage, and bar width is zero. Invalid presentation input is locally rendered as zero without weakening snapshot validation or mutating snapshot objects.

`CombatMeterPresenter` remembers snapshot epoch and sequence. It rebuilds only when either changes and resets when the store has no snapshot or the session UI is destroyed. A new EncounterId therefore replaces the displayed rows without retaining history.

## Unity view and lifecycle

`CombatMeterUiController` uses the built-in `UnityEngine.UI` stack: overlay Canvas, CanvasScaler, Image, and legacy Text. It first searches loaded runtime fonts for Cyrillic support, then tries Unity's built-in legacy fonts. No font asset or external UI dependency is bundled.

The controller is created by the plugin but creates its Canvas only after a non-dedicated local world session and local player exist. It destroys the Canvas on disconnect, network stop/destruction, plugin shutdown, disabled UI, or loss of the ready-world condition. Creation is idempotent, so returning to a world creates one new root. A failed creation logs one bounded warning and retries later; combat and snapshot processing continue independently.

The panel uses a stable top-left anchor and pivot. Its safe default is `(X=24, Y=-150)`, placing it 150 canvas pixels below the top edge rather than over the vanilla hotkey area. It has a translucent dark background, fixed numeric columns, a variable player-name column, and dynamic height. Rows grow on demand up to the snapshot limit of 64 and are subsequently updated, activated, or hidden rather than recreated each heartbeat.

Each pooled row contains reusable `Background` and `DamageFill` `UnityEngine.UI.Image` objects behind Player, Damage, %, DPS, and Taken text. `DamageFill` uses a left-anchored RectTransform whose horizontal anchor equals the player's contribution. Thus its width is the share of total party damage, never the share relative to the leading player. Both decorative images have `raycastTarget=false`. The fill uses one neutral blue tint; no player colors, Taken bar, DPS bar, ranking number, or icon is added.

The visually separate 32 px header is the mouse drag handle. Dragging is enabled only in edit mode. Pressing the left mouse button over the header captures the starting canvas-local pointer and anchored position; movement updates only `RectTransform.anchoredPosition`. Releasing clamps the final position and writes it once to BepInEx config. Exiting edit mode during a drag also clamps and persists the current position.

`CombatMeterLayout` is a Unity-independent geometry helper. It sanitizes scale, width, and opacity and clamps the scaled window against canvas bounds with a 10 px margin. Creation, row-count/height changes, width or scale changes, and canvas resolution changes all re-run clamping. If a 64-row scaled window is taller than the canvas, clamping keeps the header reachable; an oversized window cannot physically fit in full.

## Visibility and configuration

The config entries are:

```ini
[UI]
UI Enabled = true
Toggle Key = F8
Edit Mode Key = LeftControl + F8
UI Scale = 1.0
Window Width = 480
Background Opacity = 0.65
Show Damage Bars = true
Show Damage Percent = true
Damage Bar Opacity = 0.25

[UI Position]
X = 24
Y = -150
```

Scale is sanitized to `0.5–2.0`, width to `350–800`, and opacity to `0–1`; Configuration Manager changes apply on the next frame without recreating the Canvas. Position is saved only after drag release, reset, or corrective clamping. Session destruction removes Unity objects but preserves these config values for the next world.

Damage bars, the percent column, and bar opacity also update live. Disabling percentages removes the header and row text and recomputes column positions rather than leaving a blank column. At the 350 px minimum width, numeric columns keep fixed compact widths and the player name truncates more aggressively. Width/scale changes continue through the existing clamp path.

The window is hidden for `NoEncounter`, visible for Active and Recovery, and remains visible with frozen values for Finished. F8 changes only the local user's visibility preference. Shift+F8 resets and persists the safe default without changing that preference.

Ctrl+F8 enters or exits explicit edit mode. It temporarily forces the meter visible even if F8 previously hid it or no encounter exists. In `NoEncounter`, the controller renders two local preview rows at 66.7% and 33.3%, including their bars, without creating or changing a snapshot. The header changes to `Combat Meter [EDIT MODE]` with a highlighted drag surface and an exit hint. F8 is ignored while editing; Shift+F8 still resets position. On exit, the previous hidden/visible semantics return unchanged.

Escape exits edit mode and is suppressed in `Menu.Update` for that Unity frame, preventing the same press from also opening the pause menu. Opening inventory, map, pause/menu, console, text input, store, popup, build selection, disconnecting, losing the local player, or stopping the world exits edit mode cleanly.

Static inspection of the Valheim 1.0.15 assemblies identified `GameCamera.UpdateMouseCapture()` as the canonical `ZCursor` owner. A Harmony postfix changes `ZCursor` to visible/unlocked only while explicit edit state is active. On exit the controller calls the original method again with edit state already cleared, so vanilla inventory/menu/map conditions decide the restored cursor state. It does not hard-code a locked/hidden exit state.

`PlayerController.TakeInput(bool)` is the shared gate used by controller `FixedUpdate` and mouse-look `LateUpdate`. Its Harmony postfix returns false while editing, suppressing attacks, block/use, movement, and camera look. This MVP therefore blocks all player controls rather than only mouse controls. Snapshot updates and UI configuration remain active.

Diagnostics additionally include `CombatMeterEditModeEntered` and `CombatMeterEditModeExited reason=Toggle|Escape|Lifecycle|VanillaModal`. Cursor state and mouse movement are never logged per frame.

## Runtime verification

Single-player:

1. Load a world and confirm the window is hidden before combat.
2. Attack a troll and verify Player, Damage, DPS, Taken, state, and authoritative elapsed time.
3. Take damage, stop combat, and verify Finished remains visible with frozen values.
4. Toggle F8 in and out of combat, then start a new encounter and verify rows reset to the new EncounterId.
5. Return to the menu and re-enter a world; confirm there is one Canvas and no previous-world data.
6. Drag from the header toward all four edges and confirm the 10 px clamp, then re-enter the world and confirm restoration.
7. Change scale, width, and opacity through Configuration Manager and verify live updates; use Shift+F8 to restore `(24, -150)`.
8. Outside combat press Ctrl+F8: verify preview and cursor, drag without camera/attack input, then exit with Ctrl+F8 and Escape.
9. Hide with F8, enter edit mode, and verify preview is temporary; after exit the meter must be hidden again.
10. Enter edit mode and open a vanilla modal or leave the world; verify normal cursor and controls are restored.
11. Verify a one-player fight shows 100.0%; in multiplayer compare host/client percentages and fill proportions for the same snapshot.
12. Toggle bars and percentages and change bar opacity live; inspect width 350 and scales 0.5/2.0 for text overlap.

Listen-host multiplayer:

1. Have host and client both deal and receive damage in one encounter.
2. Verify both HUDs show the same two rows, ordering, values, state, and elapsed snapshot.
3. Verify Finished appears on both and remains stable.
4. Toggle F8 independently on each peer and verify neither preference affects the other.

Managed tests additionally cover 60/30/10, equal, single-player, all-zero, mixed-zero, decimal, independent rounding, sorting, signed-ID ties, Recovery, new encounter replacement, invalid local input, and snapshot immutability. Actual Image rendering, narrow-width typography, host/client visual parity, Harmony execution order, and cursor/input behavior require runtime tests.

The window has no mouse resize, docking, grid snapping, theme/font/color editor, clickable sorting, history, Taken/DPS bars, per-ability details, or row interaction. Position, scale, width, opacity, bar options, and visibility are local presentation settings and are never synchronized between peers.


