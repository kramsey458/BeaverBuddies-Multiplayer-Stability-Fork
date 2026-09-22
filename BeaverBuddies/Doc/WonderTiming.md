# Wonders on the tick (SF8)

Code: `BeaverBuddies/Fixes/WonderTimingFix.cs`. Checks: `RuntimeChecks/WonderChecks.cs`.

## The problem

Part of every Wonder, and all of the Iron Teeth Earth Repopulator's plane launch, is simulation that
the game runs on render-frame time (Timberborn 1.1.2.4, `Timberborn.Wonders`, `Timberborn.WonderPlanes`,
`Timberborn.TimbermeshAnimations`):

| Step | Where it runs | What its end changes |
|---|---|---|
| A Wonder's activation or deactivation animation | `AnimatorRegistry.UpdateSingleton` advances every animator by `Time.deltaTime`; `WonderAnimationController.Update` (per frame) sees `PlayingFinished` | `StartAnimationFinished`, and `IsAnimating`, which `AlreadyActivatedWonderBlocker` reads: while it is true the Wonder cannot be activated (every faction's Wonder has this) |
| The catapult's 1 s wait and the 10-unit runway | `PlaneCatapult.Update` (per frame, `Time.deltaTime`), measuring the plane's transform | `PlaneCatapulted`, which starts the launcher's turn |
| The plane on the runway | `Plane.Update` (per frame, `Time.deltaTime`) | where the catapult measures it next |
| The launcher's turn between planes | `PlaneLauncherRotator.Update` (per frame, `Time.deltaTime` twice) | `RotationFinished`: the next plane, or `Wonder.Deactivate()` (the Wonder's need effect stops, the unlock countdown starts) and a 0.5-hour trigger that destroys every pilot |

The fork only replays the activation itself (`WonderActivatedEvent`). Everything after it lands on
whatever tick each player's frames happen to reach it: a player at a lower frame rate, or a guest whose
frames go on while it waits for the host's next tick, deactivates the Wonder and kills its 8 pilots on a
different tick, and the Wonder's effect on beavers lasts a different number of ticks. The planes are also
created from a frame update, outside any tick or replay, so `DeterminismService.ShouldFreezeSeed` gives
their entity IDs the non-game random numbers: every player's planes get different IDs.

The RuntimeChecks reproduce the dependence with the game's own code: a 4.19 s animation ends on tick 7
at 10 and 30 FPS but tick 6 at 144 FPS; a 45-degree turn and a runway launch leave different amounts
after the same tick at 10, 30 and 144 FPS.

## The two options

- **(A) Run it on the tick.** Every logic owner advances only from a tick hook, with the tick interval as
  its clock, the way `WaterSourceTimingFix` fixes the water source. Every player computes the same thing
  from the same ticks. No new events, no host/guest difference.
- **(B) Host-only transition events.** The host detects each transition on its frames and sends it as a
  replay event; guests suppress their own. It needs a new event per transition (a wire change), guests
  that hold each end (animation, runway, turn) where the game would act on it until the host's event
  arrives, idempotent handlers, the host deferring its own transitions to the
  tick boundary, and the same treatment for every Wonder's `IsAnimating`. The host's frame rate would
  still decide the timing; it would just be the same for everyone.

This fork does **(A)**.

## What runs where, in multiplayer

`WonderTickService` is an `ITickableSingleton`, bound with the other co-op services, so it runs in the
singleton bucket at the start of every tick, after `ReplayService.DoTick` has replayed that tick's events.
It takes the registered Wonders (`EntityComponentRegistry.GetAll<Wonder>()`) in entity ID order and, for
each, in this order:

1. **Animation.** If the controller is animating: the game's own `TimbermeshAnimator.UpdateAnimation`
   with the tick interval, then the game's own `WonderAnimationController.Update` (the end check, which can
   start the first plane).
2. **Catapult and runway.** If the catapult is enabled: the game's own `PlaneCatapult.Update`, then, if
   the plane is still on the runway, the game's own `Plane.Update`.
3. **Launcher.** If the rotator is enabled: the game's own `PlaneLauncherRotator.Update` (a turn, or its
   end: the next plane, or the Wonder's deactivation).

A step that starts a later part lets it run from the same tick; an earlier part starts on the next tick.
Transpilers replace `Time.deltaTime` in `PlaneCatapult.UpdatePlane` (1 read),
`PlaneLauncherRotator.UpdateRotation` (2) and `Plane.Update` (2) with `WonderTiming.GetDeltaTime`, which
reads the tick interval inside the tick hook and the frame clock anywhere else. Each transpiler refuses a
body with a different number of reads, as the water fix does.

In multiplayer the frame updates no longer run the logic. Their prefixes (`[HarmonyPriority(Priority.Last)]`)
skip `WonderAnimationController.Update` and `PlaneCatapult.Update`, and draw instead of running
`PlaneLauncherRotator.Update`, `Plane.Update` for a plane still on the runway, and
`TimbermeshAnimator.UpdateAnimation` for a Wonder's animator from the moment its animation starts
(a postfix on `WonderAnimationController.StartAnimation`, which covers activation, deactivation and load).
Every other animator, and a plane in free flight, keep the game's frame clock. Single player is unchanged:
every patch checks `EventIO.IsNull`, and the service is only bound in a co-op game.

`SpawnPlane` is reached only from `StartEjectingPlane`, which only the rotator's end and the animation's
end call, and those are raised only from the two frame updates the tick now runs (checked from the game's
IL). So in multiplayer the planes are created inside the tick, from the game's random numbers, with the
same IDs for everyone. Entity creation interrupts the bucket loop as it does for any other entity
(`EntityComponentInstantiatePatcher`).

## Drawing between ticks

At 0.6 s a tick (speed 1), stepping the models once a tick would jerk. The frame updates draw between
the last two ticks' poses instead, by how far the current tick is through its buckets (0 right after the
Wonders ticked, 1 when the tick is complete or none is running):

- A Wonder's animation: the animator's updaters are driven directly at a time between the last two ticks'.
  The animator's own time, `PlayingFinished` and `Enabled` are never touched, so what the game reads and
  saves is the tick's.
- The launcher's turning part (its local rotation) and a plane on the runway (its position): the
  transforms themselves are drawn, because that is what the game shows. The simulation reads them (the
  plane's spawn point turns with the launcher; the catapult measures the runway), so the tick hook first
  puts each one back exactly where the last tick left it, runs, and only then goes back to drawing. Saves
  do the same first (prefixes on `PlaneLauncherRotator.Save` and `Plane.Save`).

## Saves

No key is added, removed or renamed; the game writes the same keys from the same fields
(`AnimationTime`/`IsAnimating`, `RemainingRotation`/`LoadedRotation`/`RotationTime`/`RotationDuration`,
`CurrentPlane`, the plane's `Position`/`Rotation`/`Speed`/`IsFreeFlying`, `PilotsSent`,
`PilotsDestructionProgress`). Saves from before this change load, and saves from a co-op game load in
single player and back. A save made mid-launch stores the tick's pose, not the drawn one.

## Differences from single player (all the same on every player)

- The launch follows ticks: the 1 s wait takes 2 ticks, a turn or the runway ends on the first tick that
  passes it. A launch takes a few ticks longer, and the Wonder stays active a few ticks longer.
- The runway's last tick can carry the plane up to one tick of movement past the 10-unit mark (at most
  30 units/s for 0.6 s). This only moves where free flight starts.
- A Wonder's animation keeps playing when its model is hidden; the game's frame loop skips animators
  whose object is inactive.

## Limits and what is not changed

- **Free flight stays on frame time**, as in the game: after the runway, nothing simulated reads the
  plane or its pilot. The pilots are hidden and have every component a dead character does not need
  disabled (`DeadComponentDisabler`), and destroying them later drops nothing at their position
  (`GoodCarrier.OnDied` empties their hands). Their root transform rides the plane's seat, so their
  position differs between players. `TEBPatcher` folds every walker's position into its "Move hash"
  (debug logging only on main); any always-on walker position check must skip flying pilots or it will
  report the launch as a desync.
- `TimbermeshAnimator.Play` restores an interrupted animation's time when the same animation is played
  twice in one render frame (`Time.frameCount`). A Wonder only plays its animation on activation (blocked
  while it animates) and deactivation, so this cannot happen twice in one frame.
- `WonderUnselector` (it unselects the Wonder 0.5 s after activation) is interface only and is left alone.
- The game's own load of a launch in progress is odd, in single player too: `PlaneLauncherRotator.Load`
  enables the rotator whenever its saved rotation is not 0, and its first update then raises
  `RotationFinished` although nothing was turning. A save made while any plane but the first is on the
  runway (the launcher has turned) therefore skips the next pilot on load (`StartEjectingPlane` counts
  it, `CatapultPlane` ignores it because a plane is still on the catapult), or deactivates the Wonder at
  once if it was the last plane. This mod leaves that as it is: every player loads the same save, and it
  now happens on the first tick after the load instead of the first frame. A rehost during the runway in
  the two-player test may show it.
- An exception from the game inside a Wonder's step is logged once and the tick goes on: every player
  runs the same step and fails the same way.

## Testing

Offline (`RuntimeChecks`, against the installed game's assemblies; Harmony is not installed there):
no Wonder logic method reads the frame clock once the mod's transpilers run; the transpilers refuse other
bodies; in multiplayer each frame update is skipped or only draws outside the tick, and runs in single
player; `SpawnPlane` is reachable only through the two frame updates the tick runs, and only the tick
hook calls them; the game's animation, catapult/runway and rotation code, driven at 10, 30 and 144 FPS,
differ before the fix and match bit for bit, ending on the same tick, after it; no Save or Load is
replaced.

In game, **not safe to merge without it**: two players on an Iron Teeth map, one capped at a low frame
rate (for example 15 FPS) and one uncapped, activate a finished Earth Repopulator with 8 pilots. Watch
all 8 planes launch, the Wonder deactivate and the pilots disappear half an hour later with no desync;
save and rehost once while a plane is on the runway and once while the launcher turns; and activate a
Folktails Earth Recultivator once in co-op (its animation now runs on the tick too) and again after it
deactivates. Also check that the launch looks smooth at speed 1.
