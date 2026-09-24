# Wonders on the tick (SF8)

Code: `BeaverBuddies/Fixes/WonderTimingFix.cs`. Checks: `RuntimeChecks/WonderChecks.cs`.

In 1.1.14. This is PR #46 (branch `claude/wonder-plane-ticks`, `7391560`), which was closed without merging, as
Timber Together 1.4.0-beta12 reworked it. What changed from #46 is in [In 1.1.14](#in-1114) at the end.

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

Before 1.1.14 the fork only replayed the activation itself (`WonderActivatedEvent`). Everything after it lands on
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

This does **(A)**.

## What runs where, in multiplayer

`WonderTickService` is an `ITickableSingleton`, bound with the other co-op services, so it runs in the
singleton bucket at the start of every tick, after `ReplayService.DoTick` has replayed that tick's events.
It takes the registered Wonders (`EntityComponentRegistry.GetAll<Wonder>()`) in entity ID order and hands
their parts to `WonderTiming.Tick`, which, for each, runs in this order:

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
reads the tick interval inside the tick hook and the frame clock anywhere else. A body with a different number of
reads (a game update) is left as the game has it, and switches the whole takeover off (see
[In 1.1.14](#in-1114)).

In multiplayer the frame updates no longer run the logic. Their prefixes (`[HarmonyPriority(Priority.Last)]`)
skip `WonderAnimationController.Update` and `PlaneCatapult.Update`, and draw instead of running
`PlaneLauncherRotator.Update`, `Plane.Update` for a plane still on the runway, and
`TimbermeshAnimator.UpdateAnimation` for a Wonder's animator from the moment its animation starts
(a postfix on `WonderAnimationController.StartAnimation`, which covers activation, deactivation and load).
Every other animator, and a plane in free flight, keep the game's frame clock.

A gate at `Priority.Last` only sees the call if every earlier prefix let it through, and other mods keep
animator time themselves: see [Other mods](#other-mods). So a Wonder animator's clock (`Time`,
`RepeatedTime`, `PlayingFinished`, what the game's `UpdateTime` changes) is also held around every frame
update outside the tick: a void prefix at `Priority.First` remembers it, and a postfix at `Priority.First`
(Harmony runs postfixes whether or not the original ran) puts it back and draws the pose. The void prefix
cannot skip anything, so it is not a replacing prefix; `Priority.First` only lets it read the clock before
anyone else changes it.

Single player is unchanged: the service is only bound in a co-op game, and every patch and the tick itself
stand down when `EventIO` is null. That also covers a co-op game played on after its session ended
mid-game (`ReplayService.EndSession`, `HandleDesync` and `AbortReplay` reset `EventIO` and leave the game
running): on its next tick `WonderTiming.Tick` puts every part where the last tick left it, draws each
animation's tick pose and stops stepping, and the game's own frame updates carry on from there.

`SpawnPlane` is reached only from `StartEjectingPlane`, which only the rotator's end and the animation's
end call, and those are raised only from the two frame updates the tick now runs (checked from the game's
IL). So in multiplayer the planes are created inside the tick, from the game's random numbers, with the
same IDs for everyone. (PR #46's note said entity creation then interrupts the bucket loop through
`EntityComponentInstantiatePatcher`; in 1.1.2.4 that patch only acts on entities loaded with an ID, and a new entity
needs no frame of its own, since the game initialises it at once. Deletions do: see
`EntityDeletionEndsFramePatcher`, which covers the pilots' and planes' deletion too.)

## Drawing between ticks

At 0.6 s a tick (speed 1), stepping the models once a tick would jerk. The frame updates draw between
the last two ticks' poses instead, by how far the current tick is through its buckets (0 right after the
Wonders ticked, 1 when the tick is complete or none is running):

- A Wonder's animation: the animator's updaters are driven directly at a time between the last two ticks'
  (in the clock postfix). The animator's own time and `PlayingFinished` end every frame as the tick left
  them, and `Enabled` is never touched, so what the game reads and saves is the tick's.
- The launcher's turning part (its local rotation) and a plane on the runway (its position): the
  transforms themselves are drawn, because that is what the game shows. The simulation reads them (the
  plane's spawn point turns with the launcher; the catapult measures the runway), so the tick hook first
  puts each one back exactly where the last tick left it, runs, and only then goes back to drawing. Saves
  do the same first (a prefix on `SaveWriter.WriteToSaveStream`, see below).

## Saves

No key is added, removed or renamed; the game writes the same keys from the same fields
(`AnimationTime`/`IsAnimating`, `RemainingRotation`/`LoadedRotation`/`RotationTime`/`RotationDuration`,
`CurrentPlane`, the plane's `Position`/`Rotation`/`Speed`/`IsFreeFlying`, `PilotsSent`,
`PilotsDestructionProgress`). Saves from before this change load, and saves from a co-op game load in
single player and back.

A save made mid-launch stores the tick's pose, not the drawn one. Every save (the game's own, the rehost
save, the map sent to a joining player) is written by `SaveWriter.WriteToSaveStream`; a void prefix on it
at `Priority.First` puts every tracked part back where the last tick left it before any entity is saved,
and before any other mod's prefix takes a snapshot of the save (LateGamePerformance's background save
does). That matters for the pilots as well as the plane: a pilot rides the plane's seat, and entities are
saved in instantiation order, so the pilot is saved before its plane.

## Differences from single player (all the same on every player)

- The launch follows ticks: the 1 s wait takes 2 ticks, a turn or the runway ends on the first tick that
  passes it. A launch takes a few ticks longer, and the Wonder stays active a few ticks longer.
- The runway's last tick can carry the plane up to one tick of movement past the 10-unit mark (at most
  30 units/s for 0.6 s). This only moves where free flight starts.
- A Wonder's animation keeps playing when its model is hidden; the game's frame loop skips animators
  whose object is inactive.

## Other mods

LateGamePerformance's `AnimatorCulling` (on by default) has a prefix on `TimbermeshAnimator.UpdateAnimation`
at Harmony's default priority. For an animator none of whose renderers is on screen, or on the frames it
leaves out for an animator far from the camera (`AnimatorLod`), it calls the game's private `UpdateTime`
with the frame's delta itself and returns false. Harmony 2.4.1 then skips every later prefix that returns
bool, so a `Priority.Last` gate alone never saw those frames: a Wonder that one player had off screen
moved on that player's frames as well as on the tick, finished on an earlier tick, and spawned its first
plane (now inside the tick, from the game's random numbers) on a different tick than on a player looking
at it. The clock hold above undoes that: whatever runs in between, the postfix puts the tick's clock back.
Inside the tick the culling prefix may still take the step, but it calls the same `UpdateTime` with the
same tick interval as the game's own `UpdateAnimation`, so the result is identical either way.
`AnimatorCulling` needs no change; its note that the simulation reads animator time still holds, because
in co-op a Wonder's animator time only moves on the tick.

## Limits and what is not changed

- **Free flight stays on frame time**, as in the game: after the runway, nothing simulated reads the
  plane or its pilot. The pilots are hidden and have every component a dead character does not need
  disabled (`DeadComponentDisabler`), and destroying them later drops nothing at their position
  (`GoodCarrier.OnDied` empties their hands). Their root transform rides the plane's seat, so their
  position differs between players. The always-on walker hash (`TEBPatcher`, logged only) leaves out
  walkers that are switched off, which the pilots' are, so a launch does not log a `Walker mismatch`.
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

Offline (`RuntimeChecks`, against the installed game's assemblies). Harmony is not installed there (the
workshop build cannot patch under .NET 8), so the checks call the mod's patches the way Harmony 2.4.1
does: by priority, skipping later bool prefixes once one returned false, running void prefixes and every
postfix regardless, passing `__state`. That model was checked against Lib.Harmony 2.4.1 on .NET 8.

- No Wonder logic method reads the frame clock once the mod's transpilers run; the transpilers refuse
  other bodies.
- In multiplayer each frame update is skipped or only draws outside the tick, and runs in single player;
  every prefix that can skip the original has `Priority.Last`.
- `SpawnPlane` is reachable only through the two frame updates the tick runs, and only the tick hook calls
  them.
- From the mod's IL: `WonderTickService` is bound with the co-op services and calls `WonderTiming.Tick`;
  the tick hands everything back when the session has ended, otherwise puts the drawn poses back before
  it steps; each Wonder's step runs the animation, the catapult and runway, and the launcher, calling the
  game's methods in that order; saves put the ticked poses back first.
- The game's animation (through the mod's own `WonderTiming.Tick` and patches), catapult/runway and
  rotation code, driven at 10, 30 and 144 FPS, differ before the fix and match bit for bit, ending on the
  same tick, after it, with every end raised inside a tick. The same holds with a LateGamePerformance-style
  culling prefix acting on every frame (off screen) or every second frame (far away).
- After the session ends, the animation runs on the game's frames alone and ends where the game ends it.
- No Save or Load is replaced (a guard: this holds before the fix too).

In game (not yet played): two players on an Iron Teeth map, one capped at a low frame
rate (for example 15 FPS) and one uncapped, activate a finished Earth Repopulator with 8 pilots. Watch
all 8 planes launch, the Wonder deactivate and the pilots disappear half an hour later with no desync;
save and rehost once while a plane is on the runway and once while the launcher turns; and activate a
Folktails Earth Recultivator once in co-op (its animation now runs on the tick too) and again after it
deactivates. Repeat the launch with LateGamePerformance installed on both computers (default settings) and
one player's camera turned away from the Wonder while it activates. Also check that the launch looks smooth
at speed 1.

## In 1.1.14

What 1.1.14 (first in Timber Together 1.4.0-beta12) changed from PR #46, and what it added around it:

- **A game update can't leave the mod half patched.** PR #46's three timing transpilers threw when a method read the
  frame clock a different number of times than expected. The mod applies its patches with one `harmony.PatchAll()`,
  so a throw there would stop the rest of the patching, the desync fixes included (the reason #46 was closed).
  Here a transpiler that finds a different body leaves it as the game has it, logs once (*Wonders stay on frame time
  in multiplayer…*) and sets `WonderTiming.Unavailable`. Then:
  - every per-frame gate lets the game's update through (`RunsThisFrame`);
  - `WonderTickService` steps nothing;
  - no animation is taken over.
  The Wonders run on frame time exactly as before 1.1.14, which may desync a Wonder's activation until the mod
  is updated, but nothing else is affected. The RuntimeChecks check this the other way round from #46: an
  incompatible body comes back unchanged, the timing is switched off, and nothing throws.
- **Whether a Wonder can be activated is the host's answer.** `WonderActivatedEvent.activated` is written by the host
  as it plays the event (`Wonder.CanBeActivated`, which reads `IsAnimating`) and followed by guests
  (`WonderActivationFollowsHostPatcher`). With the animation on the tick they agree anyway; this also covers
  `Unavailable` and anything else that could make one computer's answer differ.
- **The walker hash** leaves out switched-off walkers (see [Limits](#limits-and-what-is-not-changed)).
- **Deletions end the frame's ticking** in co-op (`EntityDeletionEndsFramePatcher`): a pilot or plane destroyed in the
  tick is gone for every later bucket on every computer, as any other entity is.
