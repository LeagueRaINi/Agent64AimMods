# Agent 64 Aim Mods

BepInEx 6 (IL2CPP) plugin for *Agent 64: Spies Never Die*. Makes the aim reticle follow your
mouse directly instead of easing toward it.

Nothing is patched on disk, it all happens at runtime.

## What's going on

Hold aim and the camera locks, then the mouse drags a reticle around a fixed box on screen.
The `Agent` script keeps that position in a Vector2 at `+0x220`. It tracks the mouse 1:1 and
snaps back to `(0, 0)` the moment you let go.

The reticle you actually see is a separate RectTransform that trails `+0x220` and catches up
exponentially. Fitted over an 888 frame capture at roughly 180fps:

| model                          | best fit   | RMSE    |
| ------------------------------ | ---------- | ------- |
| `Lerp(current, target, k)`      | k = 0.1000 | 10.7 px |
| `Lerp(current, target, dt * s)` | s = 17.0   | 10.7 px |
| `SmoothDamp(smoothTime)`        | T = 0.055  | 12.1 px |

Works out to about 7 frames of lag with a 0.3s tail.

Be clear about what that table is though: it's curve fitting, not the game's code. I never
found the easing in the binary. I checked `Agent.LateUpdate`, both helpers it calls, the
reticle widget's methods, and all 72 callers of `RectTransform.set_anchoredPosition`, and it
wasn't in any of them. What I am confident about is that the reticle exponentially chases
`+0x220`, since the capture shows it and this plugin's own offset detector later rediscovered
the field knowing nothing except that relationship.

Also worth noticing that the top two rows fit within 0.05px of each other. At a steady
framerate you can't tell a fixed per frame step from a `dt` scaled one, so whether the easing
gets worse at higher framerates is still an open question. There's a way to check it further
down.

The reticle isn't only decoration, and this part I did confirm in the code. `Agent.LateUpdate`
walks `Agent+0x158` (HUD) to `+0x30` (widget) to `+0x18` (RectTransform), reads its position,
and builds the weapon's aim rotation out of it. So the lagging reticle is what the gun aims
along, which is why the gun looks like it drifts toward wherever you're turning and then
settles.

## Mods

All three are independent and can be toggled while playing.

| Setting             | Default | Hotkey | Effect |
| ------------------- | ------- | ------ | ------ |
| `InstantAim`        | `true`  | `F7`   | Reticle and weapon follow the mouse exactly while aiming. |
| `InstantRecentre`   | `true`  | `F8`   | Drops the leftover drift after you release aim. |
| `AlwaysShowReticle` | `false` | `F9`   | Keeps the reticle on screen instead of only while aiming. |

Toggling one puts a message on screen so you don't have to go looking at the console. The
plugin also says so when it loses the aim target and when it finds it again, since those are
the times something looks broken and you'd want to know why. `[Notifications]` in the config
turns them off or moves them: `Anchor` takes `TopLeft`, `TopCentre`, `TopRight`, `BottomLeft`,
`BottomCentre` or `BottomRight`, and `Seconds` and `FontSize` do what they sound like.

`InstantRecentre` handles the gap where the game has already snapped `+0x220` back to
`(0, 0)` but the reticle is still coasting toward it. The weapon aims along that stale
position for the ~0.3s it takes to arrive, so a shot fired right after releasing aim can land
up to 40px off centre. It doesn't touch idle weapon sway, which comes from somewhere else.
While you're hip firing steadily the target sits at `(0, 0)` anyway, so there's nothing here
to change.

`AlwaysShowReticle` is off by default since it changes the HUD rather than the feel. All four
of the widget's show/hide methods have the same body, resolving `GetComponent<Image>()` into
widget `+0x20` and disabling it, so visibility is just `Image.enabled` on the same GameObject
the position gets written to. Re-enabling it every frame is enough. Two caveats: it marks the
aim target the game feeds the weapon, which sits dead centre while hip firing, and it stays up
in menus and cutscenes.

## How it works

The reticle gets written in both `Update` and `LateUpdate`. Unity runs every `Update` before
any `LateUpdate`, so the `Update` write is what `Agent.LateUpdate` reads when it poses the
weapon (without it the gun trails by a frame), and the `LateUpdate` write lands after the
game's easing so it's the position the canvas renders.

Both scene lookups sweep every object in the scene, so they only retry twice a second and only
while something is actually missing.

The offset isn't trusted blindly either. On resolve the plugin walks IL2CPP's field table for
`Agent`, requires a `UnityEngine.Vector2` at that offset, and caches the field handle. Reads
go through `il2cpp_field_get_value` from there. If the game moves or retypes the field you get
a log line saying so rather than garbage on screen.

## Surviving a game update

If `TargetOffset` stops pointing at a `Vector2` field, which is what a game update tends to
do, the plugin goes looking for it by behaviour instead of by address and writes the answer
back to the config. `AutoDetectOffset` in `[Advanced]` controls it. None of this runs while
the configured offset still works.

What it keys on is the easing itself. Every frame the reticle satisfies

```
delta = k * error        delta = how far the reticle moved
                         error = how far it was from the target beforehand
```

so it fits that line by least squares across every non-static `Vector2` field on the class at
once and scores each by R². The real target converges on 1. Unrelated fields can't explain the
movement and sit near 0. A field that merely happens to sit near the target scores lower the
further off it is, because its error term is inflated while the movement it has to account for
isn't. A field parked at the origin correlates negatively once you aim away from centre and
gets thrown out by the rate bound.

Nothing is adopted until all of these hold, which in practice takes a few seconds of actually
aiming:

| Guard       | Requirement |
| ----------- | ----------- |
| Samples     | 120 frames minimum |
| Signal      | 2000px of accumulated error, so a still screen proves nothing |
| Fit quality | winner explains 90% of the reticle's movement |
| Margin      | winner beats the runner up by 0.15, so ties are rejected |
| Rate        | implied easing between 0.01 and 0.95 per frame |

Detection only observes, it never writes, so the mods stay off until it resolves. A fruitless
attempt restarts every 30 seconds or so. Since it's a fit rather than a baked in constant, it
still works if a patch changes the easing rate.

To check the path works, set `TargetOffset` to something nonsensical like `0x999` and play. It
should log the real offset within a few seconds of aiming and repair the config on its own.

### The framerate question

The detector fits and logs the easing rate, which makes it a usable instrument. Force
detection with `TargetOffset = 0x999`, play, and note the `easing ... per frame` value. Then do
it again with your framerate capped a lot lower. If the rate doesn't move, it's a fixed per
frame step and the easing really does get stronger the faster your machine runs. If the rate
scales with frame time, it's `dt` based and framerate independent.

## Install

1. Install BepInEx 6 (IL2CPP, x64) into the game folder.
2. Run the game once so BepInEx generates `BepInEx/interop`.
3. Drop `Agent64AimMods.dll` into `BepInEx/plugins`.

Config lands in `BepInEx/config/agent64.aimmods.cfg` on first run.

You should see this on load:

```
[Info : Agent 64 Aim Mods] Agent 64 Aim Mods 1.3.0 loaded. InstantAim ON (F7), InstantRecentre ON (F8), AlwaysShowReticle OFF (F9).
```

## Building

`Agent64AimMods.csproj` references assemblies straight out of the game directory. Point
`GameDir` at your install and run:

```
dotnet build -c Release
```

The build drops the DLL into `BepInEx/plugins` for you.

## Notes

- `0x220`, the reticle path and the script name all live under `[Advanced]` in the config, so
  a game update that shifts them can be fixed without rebuilding.
- Every character in the game runs the `Agent` script, so the instance nearest the camera gets
  treated as the player.
