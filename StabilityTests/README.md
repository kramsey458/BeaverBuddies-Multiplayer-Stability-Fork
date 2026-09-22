# Stability regression checks

The suite includes fragmented-stream handshake tests for matching, mismatched and
legacy peers, timeout cleanup, and failure notification in both directions.
The production ReplayExecution helper is tested with partial mutation, an error
handler that also throws, and early-stop/nested-scope cases.

RuntimeChecks also invokes all ten production RNG scope prefixes/finalizers
with nesting and cleanup, and checks restoration of the ticker flag. To attempt
actual Harmony patch installation on a managed fixture, set
`BEAVERBUDDIES_TEST_HARMONY=1`. This optional integration test fails under the
current .NET 8 harness because the installed MonoMod dependency cannot access
SignatureHelper.GetMethodSigHelper. It is not counted in the passing checks;
live Unity/Harmony installation is not exercised by these checks.

Run `dotnet run --project StabilityTests` from the repository root with .NET 8.
This builds TimberNet and links the production SteamLinkSocket, SteamLinkManager,
connection panel model, player cursor preferences, animation patch, always-on desync check (DesyncCheck) and desync dialog decisions (DesyncDialogPlan) source. Steam and Unity APIs are test doubles; no game or
Steam client is required. Animation tests model a forward-only path cursor and
invalid visual coordinates, not a running Unity water simulation.

The Workshop checks run the mod project's real PostBuild step (`dotnet msbuild
BeaverBuddies/BeaverBuddies.csproj -t:PostBuild`) into a scratch Documents folder in the
system temp directory, never the one in `env.props`. They check the `workshop_data.json` a
build leaves beside the mod, which Timberborn's Workshop uploader reads to choose the item
it updates: a build never leaves the original project's item (3293380223) there, removes a
copy of it that an earlier build left, and keeps one the uploader wrote for a new item, even
when that item's id or name contains the original id.
They need the .NET SDK on the PATH, but no game files.

GitHub Actions runs this suite on Windows for every push and pull request
(`.github/workflows/tests.yml`), restoring its packages from nuget.org. RuntimeChecks needs
the game's assemblies, which cannot be redistributed, so it runs only locally.

`dotnet run --project StabilityTests -- --ping-report` prints how the ping shown over Steam
depends on the players' frame length, with Steam served once per frame and with it also served
between the ticks of a frame. It runs the real transport, server, client and ping tracker over a
fake Steam network with a fixed delay and takes about a minute and a half.

To compare against another checkout:
`dotnet run --project StabilityTests -p:SourceRoot=/absolute/path/to/checkout`
The Workshop checks then run that checkout's PostBuild step too.

The separate RuntimeChecks executable tests the actual compiled mod's RNG
wrappers and save flags using the installed game's managed assemblies. It also
runs the actual managed UpdateWaterSourcesTask on a small overlapping-source
fixture, verifies identical results across six registration orders after the
ordering fix, and checks water diagnostic snapshots and field hashes:

```
dotnet run --project RuntimeChecks -- /path/to/BeaverBuddies.dll /path/to/Timberborn_Data/Managed /path/to/Harmony-directory
```

RuntimeChecks also checks which types a multiplayer frame may create. Frames are read with
Newtonsoft's `TypeNameHandling.All`, so every `$type` in one names a type to create, and the
`ReplayEventBinder` only lets actions and what they carry through. A frame that names any other type
(a harmless sentinel stands in for a dangerous one), or a list, array, map or Nullable of it, even
with the elements' own types left out, is refused before anything is created. So are Unity objects,
delegates and reflection types, and a generic action whose type arguments no action carries. Because
a frame that leaves a `$type` out gets the declared type without the binder being asked, no action
may declare a Unity object, a delegate or a reflection type at any depth either. Every action the
mod sends reads back unchanged, with every field filled in, and is written exactly as it is without
the binder, so the event hash does not change. Actions from another mod's assembly loaded from bytes
(standing in for MixedStorage's `StorageAllocationEvent`) pass, with the classes they declare. A frame
that cannot be read (a refused type, an action from a mod that is not installed, no type at all, or a
group of actions holding an empty entry or another group) is fed to the real guest and host event
IO: the guest stops the session with a reason naming the type and its assembly and plays nothing
more of that tick, and the host logs it, keeps the guest's other actions and carries on.

Both executables exit nonzero on failure. Neither verifies full multiplayer
determinism or executes Unity's native simulation. Build BeaverBuddies using
the repository's env.props setup before running RuntimeChecks.

StabilityTests also runs the host's send lanes (1.1.14): frames in order, posting that never waits, a guest stuck past
the limit reported stalled, and a real loopback session where one guest stops reading while the host broadcasts 2 MB:
every broadcast returns at once and the reading guest gets everything. A connection whose writes really block (whatever
the machine's socket buffers hold) checks that the stalled guest is dropped after the limit. It also checks that both
ends read on dedicated threads above normal priority, and it runs a frame-by-frame model of a guest behind an easing
host (`PaceChecks`).

RuntimeChecks also runs the Wonders' timing (1.1.14, PR #46 reworked, `BeaverBuddies/Doc/WonderTiming.md`).
- It decodes the installed game's plane catapult, runway and launcher rotation IL, runs the mod's timing transpilers
  on it, and drives it, with the game's own animator and Wonder animation controller, at 10, 30 and 144 FPS.
- The results differ before the fix, and after it they match bit for bit and end on the same tick, behind the mod's
  per-frame gates and inside its tick scope.
- The animation runs through the mod's own `WonderTiming.Tick`, also with a LateGamePerformance-style culling prefix
  ahead of the gate and after the session has ended.
- A transpiler given a body it does not expect leaves it unchanged and switches the Wonder timing off, without
  throwing.
- From the game's IL: planes are only spawned from the two frame updates the tick takes over.

`ReviewFixChecks` covers 1.1.14's other fixes against the compiled mod: saves, deletions, levers, random sources,
buildings from other mods, and pacing and Steam wiring.

The mod's Harmony prefixes follow one rule for their priority. A prefix that replaces the
game's method (returns false to skip it) carries `[HarmonyPriority(Priority.Last)]`, so
another mod's prefix on that method runs before it on every computer, whatever the load
order. A prefix that records a multiplayer action (through `ReplayEvent.DoPrefix`,
`DoEntityPrefix`, an event's own `DoPrefix` helper, or `ReplayService.RecordEvent` itself)
carries `[HarmonyPriority(Priority.First)]`, also when it refuses or replaces the method in
other cases. When the local player acts it records the action and skips the method, which
then runs, with every other mod's prefix on it, while the action is played on every
computer at the same tick. Ahead of it, another mod's prefix would run at the click on that
computer only, and one that returned false would make Harmony skip the recording prefix, so
the action would never be sent. MixedStorage's Priority.Last prefixes on
`SingleGoodAllower.Allow` and `Disallow` rely on this. RuntimeChecks finds the recording
prefixes in the compiled mod's instructions (through their lambdas and the events' helpers,
and the automation prefix applied with harmony.Patch) and requires each to be
Priority.First. It fails if it misses one of six it must find or finds fewer than 50 in
all, if it takes a replacing prefix for a recording one, and if another of the mod's
prefixes patches the same method as a recording prefix (the automation prefix's methods,
listed in code, are not seen there).

RuntimeChecks also runs the game's own DistrictPreviewsValidator on a preview
building, with a district service standing in for this computer's preview road
graph (which holds the local player's hovered tool previews). Outside a replay
it must refuse the building while those roads join two districts; while events
replay the mod's prefix must accept it without reading them, at
Priority.Last. It must be the mod's only patch on that method (no postfix or
second prefix under any name), and no other placement check may be
overridden. Patches applied with harmony.Patch at run time are not seen by
these checks. Harmony is not
installed: the checks run the mod's prefixes the way Harmony would, so a live
two-player game with one player hovering a district-joining path is still the
final check.

RuntimeChecks also clones the installed game's depth-source modifier IL, substitutes
a controlled frame clock and depth-query stub, and exercises the production
timing transpiler. It reproduces frame-rate-dependent output before the patch
and checks matching ramp values after it. This tests the real ramp arithmetic
and emitted patch, but not Harmony installation inside Unity or depth sensing.

RuntimeChecks also runs the game's own planting leveling (TerrainAreaService,
TerrainPicker and GridTraversal) over a small made-up terrain seen through two
layer views. A mark or unmark is recorded through the mod's own planting
prefix, sent through the network JSON settings, and played through the event's
Replay and the game's MarkArea / UnmarkArea on a computer with the other view;
the tiles it acts on must be the ones the marking player leveled. Harmony is
not installed: the checks run the mod's prefixes on the leveling the way
Harmony would, so a live two-player game with different layer views is still
the final check.

RuntimeChecks also runs the game's own `BuildingPlacer.ShouldBePlacedFinished` and
`BuildingGoodsRecoveryService.OnBuildingDeconstructed` on a keyboard where dev mode's
Ctrl key is held, with the mod's prefixes in front of them in Harmony's order, in a
co-op game and in single player: before the fix the held key finished the building and
dropped no goods in co-op. It also decodes the IL of the placing, demolishing,
deconstruction and planting assemblies and fails on any method there that reads a key,
the tools' own input handling included, unless it is patched or listed as reviewed
with its reason (dev mode's instant unlock and plant spawner are listed as known
local-only dev mode tools). The co-op dev mode notice is run against the game's own
event bus, dev mode manager and notification service: shown each time dev mode is
turned on in a co-op game, never when it is turned off, never in single player, and
never an error when its text is missing.

Water diagnostic ZIPs can be compared with Python (no extra packages):

```
python RuntimeChecks/compare_water_snapshots.py host-water.zip client-water.zip
python -m unittest discover -s RuntimeChecks -p "test_water_snapshots.py"
```

RuntimeChecks also runs the mod's prefix on the game's Ticker.TickOnce (the
Tick once key pressed while the game is paused), the way Harmony would: in a
co-op game it must skip the game's method, show one warning notice when the
shared game is paused, and record one shared pause instead when only this
computer stands still (a guest waiting for the host, a host waiting for a
guest). It must skip it with no notice after a failed multiplayer action, and
let it run in single player. It also checks that the speed panel still calls
Ticker.TickOnce, that the prefix is `[HarmonyPriority(Priority.First)]` (it
records the shared pause, so it is a recording prefix), that
the game scene binds the notice, and that the notice text is in the built
English localization. Harmony is not installed, so pressing the key in a live
co-op game is still the final check.

Joining closes once the host has played an action that changes the game before the first tick,
because a player who joins gets the save the host loaded and only what is played after it
connected. StabilityTests runs the real server and client: a guest whose build check is running, or
whose save is being prepared, when joining closes is refused with the host's reason and never sent
the save, while a guest already admitted still gets what the host plays next. RuntimeChecks checks,
against the compiled mod, that every ReplayEvent type is in a table of events that change the game
or not (a new type fails until it is added; a type from another assembly counts as changing it),
that such an action at tick 0 closes joining with its own reason and one that does not change the
game, or one at a later tick, does not, that the replay loop closes joining after playing the action
and before queueing it to be sent, and that ChangesGame() added no field, property or JSON key
(those of ReplayEvent and of every event that overrides it are pinned, so a wire change fails).
