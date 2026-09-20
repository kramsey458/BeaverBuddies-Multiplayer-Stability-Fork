# The frame rate log

An optional log for finding out **what causes the drops in frame rate in co-op**. Off by default. It changes nothing about the game: it
only writes down, for every slow frame, where the time went, so that a session played by two people can be compared afterwards.

**Not yet run in a game.** It is tested (the timing, the file format, the analysis script), but no one has recorded a real session with
it yet. If it fails to start it says so in `Player.log` and the game carries on.

## Why it exists

In a lockstep game a slow moment can start on either computer, or in the network between them, and from inside the game the three look
the same:

- **this computer hitched;**
- **the other one hitched and this one is waiting for it** (the game does not draw slower while it waits; it just does not start the next
  tick, so the frame rate stays high and the *tick rate* drops); or
- **nothing hitched and both are waiting on the network.**

There is a fourth thing that looks like a frame rate drop with no extra work behind it: a player who has fallen behind runs the simulation
faster to catch up (up to 10 times), which makes each frame longer until it is level again. The log records the speed the game ran at
next to the speed that was chosen, so that shows.

## Turn it on

Every player, **before** hosting or joining: **Mod Settings → BeaverBuddies → Log Frame Rate Details.** Two numbers next to it:

- **Frame Rate Log: Slow Frame (ms)**, default 50. A frame this long or longer gets a row of its own.
- **Frame Rate Log: Summary Every (ticks)**, default 100. A summary row of the ordinary frames is written this often, so the file also
  records what normal looks like.

The settings are read when a game starts. Changing them in a running game does nothing until the next one.

## Record a session

1. **Every player installs the same build**, from the release zip (the join check compares builds, so a build you compiled yourself
   would be refused).
2. Play the same way you normally do, for **15 to 20 minutes**. If you can, use the same save each time you compare.
3. **Write down when you notice a drop** ("about 8 minutes in"). It is the one thing the log cannot know.
4. Leave the game normally (menu → exit) so the file is finished. If the game crashes the file is still readable; it just has no closing
   line.

"Always Use Detailed Logging" is recorded in the file's header. Play with it the way you normally do, and say which it was: it is the
heaviest optional work this mod does, so a session with it on and a session with it off are different experiments.

## Where the file is

```
%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\BeaverBuddiesDiagnostics
```

Paste that into the address bar of a File Explorer window. The file is named for the role and the player and is new for every game:

```
perf-host-Player-20260920-193045.csv
perf-guest-Player-20260920-193049.csv
```

The name in the middle is the player name from Mod Settings (the Ping name, `Player` unless changed), reduced to letters, digits, `-` and
`_`. Take the newest file. The desync diagnostics that this mod can also write are in the same folder.

## What to send back

- **The file from each player** (both, not just one).
- **When you saw drops**, roughly, and whether it was the same for both of you.
- Optional: each player's `Player.log` (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log`).

The file is plain text. It contains the list of enabled mods with versions, the game and mod build, your computer's operating system,
processor, graphics card and memory, the player name you set, and numbers. It contains no save data, no chat and no addresses.

## What is in the file

Lines starting with `#` are the **header**, and a few notes among the rows. The header records: the role, the player, the mod build, the
game and Unity versions, the computer, the settings (including whether detailed logging was on), **every enabled mod with its version**,
and **which mod has patched which method**, from Harmony's own records. For the methods that run every tick or frame it lists each patch
with the mod that made it, its kind (prefix, postfix, transpiler, finalizer) and its priority, and it lists every method that more than one
mod patches. Harmony cannot see patches made another way; the header says so.

After the header comes one line of column names, then the rows. Every row has all the columns. There are three kinds:

- **F**: one frame that took at least the slow-frame threshold.
- **W**: one wait for the other player that took at least the threshold. It is keyed on the tick that ended the wait.
- **S**: a summary of every frame since the last summary, with times as averages per frame.

**Everything is keyed on the game tick.** The two computers' clocks do not agree, so the tick is what lines two files up.

| Column | Meaning |
|---|---|
| `type` | `F`, `W` or `S` |
| `frame`, `tick` | The frame number since the log started, and the game tick when the row was written |
| `utcMs` | The wall clock, milliseconds since 1970 UTC. Only meaningful within one file |
| `frames` | How many frames the row covers (1 for `F`, the count for `S`) |
| `frameMs`, `maxFrameMs` | The frame time (average in `S`) and the slowest frame in the row |
| `ticks`, `buckets` | Ticks run in the frame, and steps of the tick loop (a tick is many buckets) |
| `waiting` | Frames in which the game wanted to start a tick and could not because the other player's events had not arrived |
| `waitMs` | Time spent waiting for the other player that ended in this row (in `W`, that wait) |
| `speed`, `target` | The speed the game ran at and the speed that was chosen. `speed` above `target` is catching up |
| `behind` | How many ticks behind the newest one received (guests) |
| `pacePct`, `hold` | The host's easing for a slow guest, in percent, and whether it is holding still for one |
| `saving`, `focused` | 1 if a save ran in the frame; 1 if the game window was in front (a window in the background is throttled by the system) |
| `gameMs` | The tick loop itself, minus everything below: the game's own ticking, and other mods' patches on it |
| `tebMs` | The pass over every entity each tick (order and position hash, animator sync) |
| `replayMs` | Replaying the events of a tick |
| `serMs`, `hashMs`, `compMs`, `sendMs` | Serializing an event; adding it to the event hash; JSON and gzip; writing to the connection |
| `recvMs`, `deserMs` | The game thread's side of receiving; turning received JSON into events |
| `steamMs` | Serving Steam networking |
| `logMs`, `detailMs` | This mod's log lines; work that exists only with detailed logging on (traces, whole-map hashes) |
| `uiMs`, `saveMs` | The connection panel and overlay; saving the game |
| `otherMs` | The rest of the frame: drawing, other UI, other mods, the system. A slow frame where this is most of the time did not slow down because of this mod |
| `gcDelta` | Garbage collections during the frame |
| `heapMB` | Managed memory in use |
| `allocKB` | How much `heapMB` changed since the previous frame. Negative means a collection freed memory |
| `msgOut`, `bytesOut`, `msgIn`, `bytesIn` | Messages and bytes sent and received (compressed) |
| `probeUs` | What the log itself cost this frame, in microseconds |
| `dropped` | Rows lost because the file writer fell behind. Should be 0 |

The times are **exclusive**: a scope inside another is taken out of the outer one, so the slots never overlap. `gameMs` through `saveMs`
and `otherMs` add up to `frameMs`.

There is no fixed hash interval in this mod to line slow frames up with. The entity hash (`tebMs`) runs every tick, and with detailed
logging on so do the whole-map hashes (`detailMs`).

## Read the two files together

Python 3, nothing to install:

```
python RuntimeChecks/compare_perf_logs.py perf-host-....csv perf-guest-....csv
```

It does these things, in this order:

1. **Compares the two mod lists**, the game version and the mod build. If they differ it says so and stops, because then nothing is
   comparable, and a difference in mods can cause desyncs on its own. `--force` goes on anyway.
2. Describes what is normal for each player: frame time, where it goes, garbage collection and allocation, waiting.
3. **Lines the logs up by tick** and puts each slow stretch into one class: a hitch on one player with a matching wait on the other,
   garbage collection on both at once, waits on both with nothing slow to explain them (network or batching), a wait followed by a
   catch-up burst, a hitch the other player never noticed, or a wait on one player with no hitch on the other.
4. Says where the time went in slow frames, and how much of it was outside every probe in this mod.
5. Looks for a rhythm in the slow frames and compares it with the periodic things that exist (a 1 Hz status message, a 10 Hz cursor
   message, the save, garbage collection).

It ends with what the numbers point to. These are pointers, not proofs. `--all` lists every episode, `--events-csv file` writes them out,
and it also reads a single file. To see what a report looks like without playing:

```
dotnet run --project StabilityTests -- --write-perf-sample some-folder
python RuntimeChecks/compare_perf_logs.py some-folder/perf-host-Example-20260101-000000.csv some-folder/perf-guest-Example-20260101-000000.csv
```

Those two files are made up by a script and are not from a game.

## What it does to the game

It only observes. It never records or replays an action, never uses the game's random numbers, never touches the event hash and never
changes anything the simulation reads, so a session with it on simulates exactly what the same session would without it.

- **Off (the default):** every measured point starts by reading one static flag. Nothing is allocated.
- **On:** times are read from a clock into arrays allocated when the game starts, and rows go into a fixed buffer of about 2.7 MB. A
  thread of its own turns them into text and writes them twice a second, so no disk work happens on the frame being measured. If that
  thread ever falls behind, new rows are dropped and counted (`dropped`) instead of the buffer growing. The per-frame path allocates
  nothing (a test checks this). `probeUs` records what the log costs, so the file shows whether it is a problem of its own making.
- Only the game thread is timed. Work on other threads (receiving and decompressing on the network thread, for example) is not, though
  the messages and bytes are counted.
- If anything inside the log fails it switches itself off and the game carries on.

## Build and test

```
dotnet build BeaverBuddies/BeaverBuddies.csproj -c "Release Steam" --no-restore -p:BeaverBuddiesModsPath="C:\somewhere\else\BeaverBuddies\"
dotnet run --project StabilityTests
python -m unittest discover -s RuntimeChecks -p "test_perf_logs.py"
```

Pass `BeaverBuddiesModsPath`, or the build writes into your real `Mods` folder. To compare a build with someone else's, both must use the
same compiled files: the join check compares the module ID of each build, and any rebuild gets a new one.

## Removing it

The timing lives in `TimberNet/Perf/` and the session and header in `BeaverBuddies/Perf/`. The rest is paired `PerfProbe.Begin` and
`PerfProbe.End` calls, and a few `PerfProbe` counters, at the measured points: search for `PerfProbe` and `PerfSession`. Deleting the two
folders and those lines, and the three settings in `Settings.cs`, removes it.
