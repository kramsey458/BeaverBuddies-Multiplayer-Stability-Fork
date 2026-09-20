# The frame rate log

An optional log for finding out **what causes the drops in frame rate in co-op**. Off by default. It changes nothing about the game: it
only writes down, for every slow frame, where the time went, so that a session played by two people can be compared afterwards.

**Only run in a game once so far, with the first version of the log.** This version adds a great deal to it (see below), and none of the
additions has been run in a game yet. If a part fails to start it says so in the file and in `Player.log`, that part stays off, and
the game carries on.

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

## What it answers

The first recording (a 35 minute co-op session) showed that the synchronisation between the players costs almost nothing, that one
computer had far longer pauses from garbage collection than the other, and that about 600 KB of garbage is made every tick. This version
is built to say **why**, so that a fix can be chosen:

- **Where the garbage comes from:** how much each section of the tick allocates (this mod's own work, the game's singletons, and each kind
  of entity), and how many collections there are.
- **Why one computer's collections are so much longer:** its launch options, `boot.config`, and the game's garbage collection settings
  are written into the header, and an optional experiment (below) tries to switch incremental collection on while the game runs.
- **Why a computer sits at a low frame rate:** the spread of frame times (vertical sync shows as peaks at 16.7 and 33.3 ms), the time in
  each of Unity's phases of a frame, whether the game thread is busy or waiting, and Unity's own frame timing where it is available.
- **What this mod itself costs:** the per-tick entity pass split into its parts, the animation update this mod replaces, speed changes, and
  every hot patch counted.

## Turn it on

Every player, **before** hosting or joining, in **Mod Settings → BeaverBuddies**:

- **Log Frame Rate Details**: on. Everything below depends on it.
- **Frame Rate Log: GC Experiment**: on (see "The garbage collection experiment"). Turn it on for both players; it does nothing on a computer
  that already collects incrementally.
- **Frame Rate Log: Slow Frame (ms)**, default 50, and **Summary Every (ticks)**, default 100: leave them.

The settings are read when a game starts. Changing them in a running game does nothing until the next one.

## Record a session

1. **Every player installs the same build**, from the release zip (the join check compares builds, so a build you compiled yourself
   would be refused).
2. **Change nothing else.** Play the way you normally do, with the same graphics settings, mods and save you had when you saw the drops.
   The point is to record what you have, not something tidier.
3. Play **at least 25 minutes**. Time matters more than what you do: the experiment starts about 9 minutes in and the pauses after it need
   time to show. Use both slower speeds and speed 7 for a good while each.
4. **Write down when you notice a drop** ("about 8 minutes in"). It is the one thing the log cannot know.
5. Let the game **autosave at least once**, and leave normally (menu → exit) so the files are finished. If the game crashes the files are
   still readable; they just have no closing line.

"Always Use Detailed Logging" is recorded in the header. Play with it the way you normally do, and say which it was.

## The garbage collection experiment

About 9 minutes into a session (game tick 4000), if the game is **not** collecting garbage incrementally, the log tries to switch that on
by setting Unity's incremental time slice, reads back whether the game now collects incrementally, and writes what it found into the file as
`# event` lines. The collection pauses before and after that tick can then be compared. It changes how the game collects garbage, never
what it simulates. If the game already collects incrementally it does nothing. It only runs if you turn on **Frame Rate Log: GC Experiment**.

## Where the files are

```
%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\BeaverBuddiesDiagnostics
```

Paste that into the address bar of a File Explorer window. There are **two files for every game**, named for the role and the player:

```
perf-host-Player-20260920-193045.csv
perf-host-Player-20260920-193045-profile.csv
```

The first is the frame log; the second, ending in `-profile`, says which entities and singletons the tick's time and garbage go to. The
name in the middle is the player name from Mod Settings (the Ping name, `Player` unless changed), reduced to letters, digits, `-` and `_`.
Take the newest pair.

## What to send back

- **Both files from each player**: four in all, not just the frame logs.
- **When you saw drops**, roughly, and whether it was the same for both of you.
- Optional: each player's `Player.log` (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log`).

The files are plain text. They contain the list of enabled mods with versions, the game and mod build, your computer's operating system,
processor, graphics card and memory, the player name you set, the game's `boot.config` and launch options, and numbers. They contain no
save data, no chat and no addresses.

## What is in the frame log

Lines starting with `#` are the **header**, and a few notes among the rows. The header records: the role, the player, the mod build, the
game and Unity versions, the computer (processor, graphics card and driver, display and refresh rate, vertical sync), the game's garbage
collection state, the settings, **every enabled mod with its version**, **which mod has patched which method** (from Harmony's own records,
for the methods that run every tick or frame and every method more than one mod patches; Harmony cannot see patches made another way),
what each measurement source can do on this computer (`# capability` lines, and `# capability-final` lines at the end saying whether it
actually produced anything), what measuring costs (`# calibration`), how big the heap was while the game was loading (`# milestone`), and the
game's `boot.config` and launch options (`# bootconfig`, `# cmdline`).

After the header comes one line of column names, then the rows. Every row has all the columns. There are three kinds:

- **F**: one frame that took at least the slow-frame threshold.
- **W**: one wait for the other player that took at least the threshold. It is keyed on the tick that ended the wait.
- **S**: a summary of every frame since the last summary. In S rows times and the Unity figures are **averages per frame**; allocation,
  counts and the two histograms are **totals** over the summary's frames.

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

**Where the time went** (milliseconds; exclusive, so a section inside another is taken out of the outer one and all of these plus
`otherMs` add up to `frameMs`):

| Column | Meaning |
|---|---|
| `gameMs` | The tick loop itself, minus everything below: the game's own ticking, and other mods' patches on it |
| `tebMs` | The pass over every entity each tick (order and position hash), minus the next two |
| `tebLookMs`, `tebAnimMs` | Inside that pass: looking up each entity's animator (one lookup in sixteen is timed and scaled up), and bringing each walker's model up to date |
| `replayMs` | Replaying the events of a tick |
| `serMs`, `hashMs`, `compMs`, `sendMs` | Serializing an event; adding it to the event hash; JSON and gzip; writing to the connection |
| `recvMs`, `deserMs` | The game thread's side of receiving; turning received JSON into events |
| `steamMs` | Serving Steam networking |
| `logMs`, `detailMs` | This mod's log lines; work that exists only with detailed logging on (traces, whole-map hashes) |
| `uiMs`, `saveMs` | The connection panel and overlay; saving the game |
| `animMs` | This mod's replacement of the game's per-character animation update, every frame |
| `speedMs` | Changing the game speed (which notifies every animated building) |
| `parMs` | The game thread waiting for the parallel part of the tick to finish |
| `otherMs` | The rest of the frame: drawing, other UI, other mods, the system |

**Garbage and memory:**

| Column | Meaning |
|---|---|
| `gcDelta` | Garbage collections during the frame |
| `heapMB` | Managed memory in use |
| `allocKB` | How much `heapMB` changed since the previous frame. Negative means a collection freed memory |
| `gameKB` … `parKB`, `otherKB` | Kilobytes allocated by each of the sections above (sections that are called too often to measure are left out and count in the one around them), and by everything outside them. Totals in `S` rows |
| `monoHeapMB`, `monoUsedMB`, `nativeMB`, `workingMB` | Unity's managed heap reserved and in use, its total allocated memory, and the process's working set. Read only when a row is written |

**Counts** (totals in `S` rows): `entities` and `movers` (visited by the entity pass; the walkers among them), `entTicks` (calls of the game's
entity tick), `nAnim`, `nTime` (reads of `Time.time`), `nDayNight`, `nRng` (random number calls), `nGuid`, `nSpawn` (entities created),
`nSound`, `nInput`, `nTicker` (calls of the per-frame patches) and `nSpeed` (speed changes). `parTickMs` is the game's own figure for how long
its parallel tick took.

**The computer and Unity:**

| Column | Meaning |
|---|---|
| `mainCpuMs`, `mainMcyc`, `procCpuMs` | Processor time the game thread used (milliseconds, and millions of cycles) and the whole process used, in the frame. Windows only. The game thread's is charged in scheduler quanta, so one frame's figure is coarse and only the summary rows are exact |
| `plTime`, `plInit`, `plEarly`, `plFixed`, `plPre`, `plUpdate`, `plLate`, `plPost` | Milliseconds in each of Unity's phases of a frame. The wait for vertical sync is in one of them (usually `plPost`) |
| `prGcBytes`, `prGcCount`, `prDraw`, `prSetPass`, `prBatches`, `prTris` | Unity's profiler counters, where a release build has them: bytes and count of allocations, draw calls, set-pass calls, batches, triangles |
| `ftCpu`, `ftMain`, `ftRender`, `ftGpu`, `ftWait` | Unity's frame timing, where it is enabled: the frame on the processor, the main thread, the render thread, the graphics card, and the main thread waiting to present |
| `fh0` … `fh16` | How many frames fell in each frame-time bucket (edges in the `# histogram` header line). Vertical sync shows as peaks at 16.7 and 33.3 ms |
| `th0` … `th5` | How many frames ran 0, 1, 2, 3-4, 5-9, and 10 or more ticks |
| `msgOut`, `bytesOut`, `msgIn`, `bytesIn` | Messages and bytes sent and received (compressed) |
| `probeUs`, `overheadUs` | What the log cost this frame: closing the frame, and the estimated cost of the timed sections and samples in it. Both in microseconds |
| `dropped` | Rows lost because the file writer fell behind. Should be 0 |

## What is in the profile file

One row per entity kind (`E`: a prefab such as a beaver or a farm house) and per singleton (`G`) for each summary window, with `calls`, `ms`
and `allocKB` **estimated by sampling**: only every Nth call is timed (N is chosen every window so that the timing costs at most a quarter of a
millisecond a tick) and the result is scaled up. `sampled` says how many calls it is based on, and `maxMs` is the slowest sampled call. Names
are given in `# name|kind|id|name|assembly` lines before the rows that use them; for a singleton the assembly names the mod it comes from.
The entity rows add up to most of `gameMs`, and the singleton rows to the rest of the tick.

## Read the files together

Python 3, nothing to install. Give it the two frame logs; it finds each profile file beside its log:

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
5. Looks for a rhythm in the slow frames and compares it with the periodic things that exist.
6. Shows the spread of frame times, Unity's phases, whether the game thread was busy or waiting, what allocates and how much, the per-tick
   counts, the profile (which entities and singletons the tick's time and garbage go to), what each computer could measure, how the two
   computers' settings differ, and what the garbage collection experiment did to the pauses.

It ends with what the numbers point to. These are pointers, not proofs. `--all` lists every episode, `--events-csv file` writes them out,
and it also reads a single file. To see what a report looks like without playing:

```
dotnet run --project StabilityTests -- --write-perf-sample some-folder
python RuntimeChecks/compare_perf_logs.py some-folder/perf-host-Example-20260101-000000.csv some-folder/perf-guest-Example-20260101-000000.csv
```

Those files are made up by a script and are not from a game.

## What it does to the game

It only observes. It never records or replays an action, never uses the game's random numbers, never touches the event hash and never
changes anything the simulation reads, so a session with it on simulates exactly what the same session would without it. The one optional
thing that changes anything is the garbage collection experiment, which changes how the game collects garbage and nothing else.

- **Off (the default):** every measured point starts by reading one static flag, and nothing is allocated or installed.
- **On:** times are read from a clock into arrays allocated when the game starts, and rows go into fixed buffers. A thread of its own turns
  them into text and writes them twice a second, so no disk work happens on the frame being measured. If that thread ever falls behind, new
  rows are dropped and counted (`dropped`) instead of the buffer growing. The per-frame path allocates nothing (a test checks this).
- **While a log runs**, and only then, it also puts a timing marker at the start and end of each phase of Unity's frame, times each
  singleton's tick with a patch made for the purpose, and asks Windows how much processor time the game thread used. The markers and the
  patch are removed when the log ends.
- **What measuring costs** is measured when the log starts (`# calibration`), estimated for every frame (`overheadUs`) and written in the
  header, so the file shows whether it is a problem of its own making. The timing of entities is sampled and kept inside a budget of about a
  quarter of a millisecond a tick.
- Only the game thread is timed. Work on other threads (receiving and decompressing on the network thread, for example) is not, though
  the messages and bytes are counted.
- If anything inside the log fails it switches itself off (or, for an optional source, just that source), and the game carries on.

## Build and test

```
dotnet build BeaverBuddies/BeaverBuddies.csproj -c "Release Steam" --no-restore -p:BeaverBuddiesModsPath="C:\somewhere\else\BeaverBuddies\"
dotnet run --project StabilityTests
python -m unittest discover -s RuntimeChecks -p "test_perf_logs.py"
```

Pass `BeaverBuddiesModsPath`, or the build writes into your real `Mods` folder. To compare a build with someone else's, both must use the
same compiled files: the join check compares the module ID of each build, and any rebuild gets a new one.

`RuntimeChecks/perf_columns.txt` lists the columns the game writes. The tests check it against the code, and the Python tests read it, so the two
cannot drift apart. If you change the columns, regenerate it with `dotnet run --project StabilityTests -- --print-perf-columns > RuntimeChecks/perf_columns.txt`.

## Removing it

The timing lives in `TimberNet/Perf/` and the session, header and Unity-specific parts in `BeaverBuddies/Perf/`. The rest is paired
`PerfProbe.Begin` and `PerfProbe.End` calls, a few `PerfProbe.Count` calls, and the per-entity hook in the entity tick patch, at the measured
points: search for `PerfProbe`, `PerfProfile`, `PerfMilestones` and `PerfSession`. Deleting the two folders and those lines, and the four
settings in `Settings.cs`, removes it.
