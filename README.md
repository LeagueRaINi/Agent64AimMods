# Agent 64 Aim Mods

Changes how *Agent 64: Spies Never Die* eases its aim reticle. BepInEx 6 (IL2CPP) plugin.
No files are patched — everything is applied at runtime.

The easing is deliberate design, not a bug. This makes it optional.

## What the game does

Holding the aim button locks the camera and lets the mouse drag a reticle around a fixed
screen box, GoldenEye style. The `Agent` script keeps that position in a Vector2 at
`+0x220`, updated 1:1 with the mouse and reset to `(0, 0)` the instant aim is released.

The reticle you actually see is a separate RectTransform that eases toward `+0x220` at a
hardcoded `0.1` per frame. Measured over an 888-frame capture:

| model                          | best fit   | RMSE    |
| ------------------------------ | ---------- | ------- |
| `Lerp(current, target, k)`      | k = 0.1000 | 10.7 px |
| `Lerp(current, target, dt * s)` | s = 17.0   | 10.7 px |
| `SmoothDamp(smoothTime)`        | T = 0.055  | 12.1 px |

That is roughly a 7-frame lag with a ~0.3s tail. Because the step is per frame rather than
per second, **the higher your framerate the stronger it gets** — almost certainly not
intended, since the game predates the framerates it now runs at.

It is also not only cosmetic. `Agent.LateUpdate` walks `Agent+0x158` (HUD) → `+0x30`
(widget) → `+0x18` (RectTransform), reads that position, and builds the weapon's aim
rotation from it. The eased reticle *is* the weapon's aim input, which is why the gun
appears to sway toward where you are turning and then settle.

## Mods

Each is independent and toggleable in game.

| Setting             | Default | Hotkey | Effect |
| ------------------- | ------- | ------ | ------ |
| `InstantAim`        | `true`  | `F7`   | Reticle and weapon follow the mouse exactly while aiming. |
| `InstantRecentre`   | `true`  | `F8`   | Removes the leftover drift after aim is released. |
| `AlwaysShowReticle` | `false` | `F9`   | Keeps the reticle on screen instead of only while aiming. |

`InstantRecentre` covers the case where the game has already snapped `+0x220` back to
`(0, 0)` but the reticle is still coasting toward it. During that window the weapon aims
along a stale position, so a shot fired immediately after releasing aim can land up to
~40 px off centre. It does **not** touch idle weapon sway, which comes from a different
path — while hip firing steadily the target sits at `(0, 0)` anyway, so there is nothing
for this to change.

`AlwaysShowReticle` is off by default because it changes the HUD rather than the feel. All
four of the reticle widget's show/hide methods have the same body — they resolve
`GetComponent<Image>()` into widget `+0x20` and disable it — so visibility is nothing more
than `Image.enabled` on the same GameObject the position is written to, and re-enabling it
each frame is enough. Two things to expect: the reticle marks the aim target the game feeds
the weapon, which sits dead centre while hip firing, and it stays visible in menus and
cutscenes.

The hotkeys flip the settings live, which makes A/B comparison against stock behaviour easy.

## Why it is written this way

The reticle is written in **both** `Update` and `LateUpdate`:

- Unity runs every `Update` before any `LateUpdate`, so the `Update` write is what
  `Agent.LateUpdate` reads when it poses the weapon. Without it the weapon trails by a frame.
- The game re-eases the reticle in its own `LateUpdate`, so writing again afterwards is what
  makes the un-eased position the one the canvas renders.

Both scene lookups sweep every object, so they are retried at most twice a second and only
while a reference is missing.

The configured offset is not trusted blindly. On resolve the plugin walks IL2CPP's own field
table for the `Agent` class, requires a `UnityEngine.Vector2` at that offset and caches the
resulting field handle; the per-frame read then goes through `il2cpp_field_get_value`. If the
game moves or retypes the field, the plugin says so and falls back to detection.

## Surviving a game update

If `TargetOffset` stops naming a `Vector2` field — which is what a game update does — the
plugin finds the field again by behaviour instead of by address and saves the answer back to
the config. `AutoDetectOffset` in `[Advanced]` controls this; the normal path is untouched
while the configured offset still works.

The signal it keys on is the easing itself. Every frame the reticle satisfies

```
delta = k * error        delta = how far the reticle moved
                         error = how far it was from the target beforehand
```

so the plugin fits that line, by least squares, for every non-static `Vector2` field on the
class at once, and scores each by its coefficient of determination. The true target converges
on 1. An unrelated field cannot explain the reticle's movement and stays near 0. A field that
merely sits near the target scores in proportion to how far off it is, because its error term
is inflated while the movement it has to explain is not. A field parked at the origin
correlates *negatively* while you aim away from centre, and is thrown out by the
plausible-rate bound.

A result is only adopted once all of the following hold, which in practice means a few
seconds of actually aiming:

| Guard       | Requirement |
| ----------- | ----------- |
| Samples     | 120 frames minimum |
| Signal      | 2000 px of accumulated error, so a still screen proves nothing |
| Fit quality | winner explains 90% of the reticle's movement |
| Margin      | winner beats the runner up by 0.15, so ties are rejected |
| Rate        | implied easing between 0.01 and 0.95 per frame |

Detection can only observe, never write, so the mods stay inactive until it resolves; a
fruitless attempt restarts every ~30 seconds. Being a fit rather than a fixed constant, it
still works if a patch changes the easing rate.

To verify the path works, set `TargetOffset` to a nonsense value such as `0x999` and play. It
should log the real offset within a few seconds of aiming and repair the config itself.

## Install

1. Install BepInEx 6 (IL2CPP, x64) into the game folder.
2. Run the game once so BepInEx generates `BepInEx/interop`.
3. Drop `Agent64AimMods.dll` into `BepInEx/plugins`.

Config is written to `BepInEx/config/agent64.aimmods.cfg` on first run.

Expect this line in the console on load:

```
[Info : Agent 64 Aim Mods] Agent 64 Aim Mods 1.2.0 loaded. InstantAim ON (F7), InstantRecentre ON (F8), AlwaysShowReticle OFF (F9).
```

## Building

`Agent64AimMods.csproj` references assemblies from the game directory directly. Point
`GameDir` at your install, then:

```
dotnet build -c Release
```

The build copies the DLL into `BepInEx/plugins` automatically.

## Notes

- Offsets (`0x220`, the reticle path, the script name) are exposed under `[Advanced]` in the
  config, so a game update that shifts them can be corrected without a rebuild.
- Every character in the game runs the `Agent` script, so the instance nearest the camera is
  taken to be the player.
