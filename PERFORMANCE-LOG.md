# Frame rate log

A recurring drop in frame rate during a co-op game can start in three different places, and from
inside the game they look the same: this computer hitched, the other computer hitched and this one is
waiting for it, or neither hitched and the two are waiting on the network. This writes down enough to
tell them apart.

It is off unless you turn it on. It only reads clocks and counters: it never records or replays an
action, never touches the random number generator and never changes anything the simulation reads, so
a session with it on simulates exactly what the same session would without it.

## Using it

1. **Both players** turn on **Log Frame Rate Details** in the mod's settings, under Developer
   Settings. Both, or there is nothing to compare.
2. Optionally set **Slow Frame Threshold** (default 50 ms, which is 20 frames per second) and
   **Frame Rate Summary Interval** (default every 100 ticks).
3. Play. Keep the session long enough to hit the problem several times.
4. Return to the main menu, which closes the file cleanly.
5. Both players send their file from
   `%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\BeaverBuddiesDiagnostics\`, named
   `perf-<your name>-<host or guest>-<date>.csv`.

Then, with both files:

```
python RuntimeChecks/compare_perf_logs.py host.csv guest.csv
```

## What is in the file

Header lines start with `#`: the mod version, who was hosting, the map, whether detailed logging was
on, every enabled mod with its version, and — from Harmony — who has patched which method, with the
priority and whether it is a method that runs every tick or frame. That last part is there because
two mods patching the same hot method is one of the things worth ruling out.

Then one line per slow frame, and a summary line every so many ticks so there is a baseline and not
only the spikes. Every line is keyed on the game tick, because two computers' clocks do not agree but
their tick numbers do.

| Column | What it is |
| --- | --- |
| `kind`, `tick`, `frames` | `spike` for one slow frame, `summary` for a window; the tick it ended on |
| `frameMs`, `maxFrameMs` | how long the frame took, measured from one of the game's per-frame simulation entries to the next, so drawing is included |
| `ticksDone`, `waitFrames` | ticks the simulation ran, and frames that ran none because the other player's events had not arrived |
| `ticksBehind` | how far behind the newest tick received this player was |
| `hashMs` | the per-tick entity order and position hash, over every entity |
| `traceMs` | the detailed-logging work: the water and moisture hashes and the trace events |
| `sendMs`, `sendBytes`, `sendCount` | serializing, hashing, compressing and writing this player's own events |
| `readMs`, `replayMs` | reading and deserializing the other player's events, and replaying them |
| `ioUpdateMs`, `steamPumpMs`, `waitCheckMs` | draining the network queues, handing data to Steam, and asking whether the next tick's events have arrived |
| `gc0`, `gc1`, `gc2`, `heapBytes` | collections since the previous line, and the managed heap |
| `speed`, `targetSpeed`, `speedChanges` | the speed the game ran at, the speed that was chosen, and how many times it changed |
| `hostPacingPct`, `fpsPacingPct`, `holding` | the host easing off for a guest that cannot keep up |
| `saving` | a save was in progress, so an autosave colliding with a frame is visible rather than guessed at |

A frame's time is not all accounted for, and deliberately so: the columns add up to the work this mod
does, and the report says what percentage is left over. If most of a slow frame is unattributed, the
cause is the game itself or another mod, not this one.

## Waiting is not a blocked wait

Worth knowing when reading the numbers: a player that is up to date does not block waiting for the
other. The tick loop asks whether the next tick's events have arrived, and if they have not the frame
simply runs no ticks and ends. So waiting shows up as `waitFrames` and `ticksDone` of zero across
several frames in a row, not as a long `waitCheckMs`.

## Cost

Each measured point reads one static flag, and with logging off that is all it does. With it on, each
point reads the clock twice and adds to a number. Rows go into a buffer allocated when the session
starts, and a thread of its own turns them into text and writes the file twice a second, so nothing
about writing the log happens on the frame being measured. If that thread ever fell behind, the
oldest rows are dropped rather than the buffer growing, and the file says how many.
