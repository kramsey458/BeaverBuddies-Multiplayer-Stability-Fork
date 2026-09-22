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
connection panel model, player cursor preferences and animation patch source. Steam and Unity APIs are test doubles; no game or
Steam client is required. Animation tests model a forward-only path cursor and
invalid visual coordinates, not a running Unity water simulation.

`dotnet run --project StabilityTests -- --ping-report` prints how the ping shown over Steam
depends on the players' frame length, with Steam served once per frame and with it also served
between the ticks of a frame. It runs the real transport, server, client and ping tracker over a
fake Steam network with a fixed delay and takes about a minute and a half.

To compare against another checkout:
`dotnet run --project StabilityTests -p:SourceRoot=/absolute/path/to/checkout`

The separate RuntimeChecks executable tests the actual compiled mod's RNG
wrappers and save flags using the installed game's managed assemblies. It also
runs the actual managed UpdateWaterSourcesTask on a small overlapping-source
fixture, verifies identical results across six registration orders after the
ordering fix, and checks water diagnostic snapshots and field hashes:

```
dotnet run --project RuntimeChecks -- /path/to/BeaverBuddies.dll /path/to/Timberborn_Data/Managed /path/to/Harmony-directory
```

Both executables exit nonzero on failure. Neither verifies full multiplayer
determinism or executes Unity's native simulation. Build BeaverBuddies using
the repository's env.props setup before running RuntimeChecks.

RuntimeChecks also clones the installed game's depth-source modifier IL, substitutes
a controlled frame clock and depth-query stub, and exercises the production
timing transpiler. It reproduces frame-rate-dependent output before the patch
and checks matching ramp values after it. This tests the real ramp arithmetic
and emitted patch, but not Harmony installation inside Unity or depth sensing.

RuntimeChecks also runs the Wonders' timing (SF8, `BeaverBuddies/Doc/WonderTiming.md`).
It decodes the installed game's plane catapult, runway and launcher rotation IL, runs the
mod's timing transpilers on it, and drives it, with the game's own animator and Wonder
animation controller, at 10, 30 and 144 FPS: the results differ before the fix and match
bit for bit, ending on the same tick, behind the mod's per-frame gates and inside its tick
scope. The animation runs through the mod's own `WonderTiming.Tick`, also with a
LateGamePerformance-style culling prefix ahead of the gate and after the session has ended;
the runway and the launcher run in the step's order from cloned IL, since their game methods
call Unity, and the mod's IL is checked to call them in that order. It also checks from the
game's IL that planes are only spawned from the two frame updates the tick takes over.
Harmony is not installed (the workshop build cannot patch under .NET 8): the checks call the
mod's patches in Harmony 2.4.1's order and skipping rules. Curves and transforms are simple
stand-ins, so a two-player game with one player's frame rate capped is still the final check.

Water diagnostic ZIPs can be compared with Python (no extra packages):

```
python RuntimeChecks/compare_water_snapshots.py host-water.zip client-water.zip
python -m unittest discover -s RuntimeChecks -p "test_water_snapshots.py"
```
