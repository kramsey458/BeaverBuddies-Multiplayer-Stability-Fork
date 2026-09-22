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

Both executables exit nonzero on failure. Neither verifies full multiplayer
determinism or executes Unity's native simulation. Build BeaverBuddies using
the repository's env.props setup before running RuntimeChecks.

RuntimeChecks also clones the installed game's depth-source modifier IL, substitutes
a controlled frame clock and depth-query stub, and exercises the production
timing transpiler. It reproduces frame-rate-dependent output before the patch
and checks matching ramp values after it. This tests the real ramp arithmetic
and emitted patch, but not Harmony installation inside Unity or depth sensing.

Water diagnostic ZIPs can be compared with Python (no extra packages):

```
python RuntimeChecks/compare_water_snapshots.py host-water.zip client-water.zip
python -m unittest discover -s RuntimeChecks -p "test_water_snapshots.py"
```
