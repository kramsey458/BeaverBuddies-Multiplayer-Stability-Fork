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

RuntimeChecks also runs the game's own `BuildingPlacer.ShouldBePlacedFinished` and
`BuildingGoodsRecoveryService.OnBuildingDeconstructed` on a keyboard where dev mode's
Ctrl key is held, with the mod's prefixes in front of them in Harmony's order, in a
co-op game and in single player: before the fix the held key finished the building and
dropped no goods in co-op. It also decodes the IL of the placing, deconstruction and
planting assemblies and fails on any other method there that reads a key and has not
been reviewed. The co-op dev mode notice is run against the game's own dev mode manager
and notification service: shown once in a co-op game when dev mode is on at load or
turned on, never in single player, and never an error when its text is missing.

Water diagnostic ZIPs can be compared with Python (no extra packages):

```
python RuntimeChecks/compare_water_snapshots.py host-water.zip client-water.zip
python -m unittest discover -s RuntimeChecks -p "test_water_snapshots.py"
```
