# Changelog

Every change this fork makes relative to the original BeaverBuddies `v1.1` branch at commit
`a13b1f20dacb6e30efa967cc8ac83e73779c0755` (24 August 2026), built against Timberborn
1.1.2.4. For a plain-language summary, see the [README](README.md). Future releases add a new
entry above the current one.

## 1.0.9-framethrottler-preview2 (pre-release)

## 1.0.9-control-fix-preview (pre-release)

A pre-release for testing, on top of 1.0.8. Every player should install this build.

### Controls that stopped answering after a message was closed

Reported after a disconnect or a resync attempt: once the message was closed, the controls did not
work as expected, and Escape did not open the menu. Going through every way a session can end, in
this mod and in the game's own code, found five separate causes. Each leaves the game running but
ignoring the player. **None of them has been seen in a running game.** They come from reading the
code, and each fix changes a decision that is checked on its own.

- **After "Multiplayer has stopped", the menu could not be opened.** A multiplayer action that
  fails to replay stops multiplayer for the rest of that game and blocks every further action, so a
  half-applied action cannot make things worse. The block also covered the game menu: Escape and
  the options button both open it through the same call. The message tells the player to return to
  the main menu, so the only way out was to kill the game. The menu now opens, and everything else
  stays blocked.
- **A dropped connection left the dead session in place.** When a guest lost the connection during
  a game, its network was closed but the session stayed installed. Every action, the menu included,
  was then queued for a session that no longer existed and never played, and the game was held
  paused. The message meant to explain it was shown through the main menu's dialogs, which no longer
  exist once a game has loaded, and the fallback looked the dialog up where it is never registered,
  so nothing was shown at all. The session now ends the way a desync ends it: what the player does
  applies here again, the game stays paused, and the game itself shows the reason and the way out
  (open the menu to save, or to return to the main menu and join again). If the connection drops
  while the game is still loading, the message appears as soon as the game is up.
- **A cancelled or failed join or host left a dead session in the main menu.** Cancelling the host's
  lobby, or a join that failed after the connection was made (a host that had already started, a
  build mismatch), left the closed session installed until the main menu was loaded again. Whatever
  was played next from that menu, single player included, then started as a multiplayer game with
  nobody to talk to: paused for good, and Escape did nothing. A session that ends before it has a
  game is now cleared away, and a host who cancels a rehost from a running game goes back to
  playing locally.
- **Steam's overlay closing under a dialog.** While the overlay is open the game pushes an empty
  panel that blocks input, and pops it when the overlay closes, but only if it is still on top. If a
  dialog opened over it in between (an invite that cannot be joined, a connection error), the game
  left it in place for good: once the dialog was closed, a panel that no key could close sat on top
  and swallowed every key press, until the overlay was opened again. The panel is now removed as
  soon as the dialog above it is closed.
- **Input held when a session stops is cleared.** A desync already cleared the keys and mouse
  buttons held when its dialog appeared, so they did not carry over once it was closed. A failed
  action and a lost connection now do the same.

## 1.0.8 (pre-release)


A pre-release for testing. Same network format as 1.0.9-framethrottler-preview, so the two can
play together, but only the host's build decides how the easing behaves.

### The frame rate easing no longer see-saws

- Played once with the floor at 20 fps: no desync, and the guest's average frame rate went from
  5 to 11 fps (1.0.8, same colony, true speed 7) to 21 to 27 fps. But the host changed speed 68
  times in seven minutes, between 60% and 95%, and never settled. Two causes. The guest's
  one-second frame rates are noisy (anything from 2 to 59 fps within a few seconds, because a
  garbage collection or an autosave takes most of one second), and three bad seconds in a row
  were enough for a drop. And after every drop the host climbed straight back, 5% every three
  seconds, into the speed that had just caused the trouble.
- The rule now looks at the middle value of the guest's last five reports, which one or two bad
  seconds cannot move. Below the floor: drop 10% and start a fresh set of five reports, so the
  next decision only sees frame rates from after the drop.
- Speeding back up takes six good reports in a row instead of three.
- The percentage the host had to drop from is remembered. It does not climb back to it for a
  minute of play, then tries once; if that fails again from the same percentage the wait
  doubles, up to four minutes. Changing the floor, switching it off, or the guest leaving
  forgets it.
- The line in `Player.log` also gives the middle value the decision was made on.
- In a model of a guest that is fine up to 80% and collapses above it, the host stays between
  75% and 85% and tries the higher speed at most six times in eighteen minutes; the first
  version tried every twenty seconds.

### Validation

- Release Steam and non-Steam builds succeed with no warnings. 177 StabilityTests, 64
  RuntimeChecks against the built mod and 2 Python checks pass.
- Not yet played in a multiplayer session.

## 1.0.9-chatbox-fix-preview (pre-release)

A pre-release for testing, on top of everything in 1.0.9-framethrottler-preview (and so in 1.0.8).
Every player should install this build: the game warns when mod versions differ, and mixed versions
are untested. Nothing here touches the network or the simulation.

### The chat and the panel are smaller, line up with the game's panels, and stay in front

From a screenshot of 1.0.9-framethrottler-preview: the chat was as tall as the whole top of the
panel, so with everything the host sees it ran down to the bottom of the screen and the game's
alerts ("Nothing to do in range") were drawn over its text box; and the pacing lines were long
enough to push the panel to its widest, wider than the game's beaver counters above it.

- **A compact chat.** The chat has a fixed height (150 interface units, about five lines and the
  box to type in) instead of matching the section above it, so it no longer grows with the rest
  of the panel.
- **The panel is as wide as the beaver counters above it.** Its width is now measured from the
  game's own population panel (a root element named `Counters`) in the same corner each time the
  panel refreshes, so it lines up with it at any UI scale. Without those counters it follows the
  nearest visible panel above it; with nothing to follow it sizes to its text as before. A width
  outside 180 to 520 is never followed. Each change is written to `Player.log` with the widths of
  the panels in that corner, so a session shows what it followed if it ever looks wrong.
- **Shorter words.** The pacing text is what made the panel wide, and three labels wrapped onto two
  lines. **Slowest guest behind** is now **Guest behind**, **Easing off for guests** is **Easing
  off**, **Slowest guest fps** is **Guest fps**; the values read "75% of speed", "75% (frame rate)"
  and "waiting for a guest". A new check keeps every label within its column and every pacing text
  within 20 characters, and it fails on the old strings.
- **In front of the alerts while you type.** While the cursor is in the chat box, the panel's
  corner of the game's interface is drawn in front of the other corners, where the alerts are, and
  it goes back to its place when the cursor leaves (or the chat is hidden, collapsed or reset).
  The game defines each corner as ignoring the pointer, so this changes only what is drawn on
  top. If the game ever stopped positioning its corners on their own, it is left alone and a line
  says so in `Player.log`.

Not verified: none of this has been seen in the game. The decisions (which width to follow, the
height, the string lengths) are covered by checks; the measuring, the drawing order and how it
looks are not, and are the things to look at first.


## 1.0.9-framethrottler-preview (pre-release)

A pre-release for testing, on top of everything in 1.0.8. Every player should install this build:
guests send the host one more number than before, and a 1.0.8 host ignores replies that carry it
(it would lose its ping and lag figures for that guest).

### The host can ease off for a guest's frame rate

- With the large colony speed limit removed, a slower computer can keep up with the simulation
  and still have a bad time: in a real session a guest stayed within a few ticks of the host at
  a true speed 7 while drawing 13 to 14 frames a second, because the simulation took about 63%
  of every second on that computer. The existing easing only looks at how many ticks behind a
  guest is, so it never reacted.
- New host choice, **Ease off below**: Off (the default), 20, 30, 45 or 60 fps. It is a line in
  the connection panel that the host clicks to pick the next value, and the same setting is in
  the mod settings. Only the host's value is ever used, and it can be changed at any time during
  a session.
- Guests report their frames per second in the reply they already send to the host's ping
  probe, about once a second. A guest reports nothing while its game window is in the
  background, where the system throttles it and the figure says nothing about the computer, and
  the host forgets a guest's figure as soon as a reply arrives without one.
- While the slowest guest stays below the floor for three reports in a row, the host drops 10%
  of the chosen speed, down to 30%. It climbs back 5% once the guest has been clear of the floor
  by some headroom (a quarter of the floor, at least 5 fps) for three reports. In between it
  holds, which keeps it from see-sawing, because easing off is exactly what raises the guest's
  frame rate. A paused game and speed 1 are never eased, and switching the choice off or the
  guest leaving restores full speed at once.
- It combines with the existing easing by taking the lower of the two percentages, never both
  multiplied, and the hold for a guest far behind still wins. The panel says which one is
  holding the host back: **Easing off for guests** reads "N% of chosen speed (guest frame
  rate)" when it is this one. The host also sees **Slowest guest fps**. Each change is written
  to `Player.log`.
- In the test model of a guest that draws 15 fps at a true speed 7, a floor of 30 brings it back
  above 30 fps with the host settled at 50% of the chosen speed and no further changes; a fast
  guest is never slowed at any floor; and full speed returns when the guest's load drops.
- Like the other pacing, this changes how fast the host works through ticks, never which tick
  anything happens on, so it cannot change what anyone simulates.

### Validation

- Release Steam and non-Steam builds succeed with no warnings. 176 StabilityTests (fifteen new in
  `FrameRatePacingChecks`: the frame rate meter, the reply format and its limits, the rule step
  by step, how it combines with lag easing, the model above, a real host and guest session in
  which the host reads the guest's frame rate and forgets it when the guest stops reporting, and
  the panel), 64 RuntimeChecks against the built mod and 2 Python checks pass.
- Not yet played in a multiplayer session. The clickable line in the panel is new and has not
  been seen in the game.

## 1.0.9-tickspeed-preview (pre-release)

A pre-release for testing, on top of 1.0.8. Every player should install this build.

### The ping over Steam no longer grows with the game speed

Reported as a good ping at a low game speed and 200 to 300 ms at a high one.

Over Steam the ping is not only the network. Steam is only served from the game thread, and the
game thread served it once per frame. A probe passes four of those pumps on its way round: the host
sending it, the guest receiving it, the guest sending the reply (queued just after the pump that
delivered the probe, so it waits a whole frame) and the host receiving that. Run through the real
transport and ping code over a fake Steam network, that comes to about a frame and a half per
player, so the panel showed roughly the round trip on the wire plus 1.5 x (the host's frame + the
guest's frame). The waits also delayed real traffic, not only the number: every action a guest
made and every tick the host sent waited for the same pumps.

At a low game speed a frame is short and this is a few milliseconds. At a high speed it is not.
The game ticks as many of a tick's 129 buckets in a frame as the frame's time, multiplied by the
game speed, asks for, so the simulation is spread over the frames it needs and a frame lasts
roughly the non-simulation work divided by (1 - the share of the main thread the simulation
takes). The share grows with the speed. In a host log at a true speed 7 the simulation took 51%
of the main thread (44 ms a tick, 11.7 ticks a second) and frames were 17 to 19 ms, about twice
what they are at speed 1. A computer that needs 90% of its main thread at that speed has frames ten
times as long as an idle one, and a guest that is catching up (it runs up to speed 10) can need
more than 100%. So the ping rises with the game speed, and most of all with the frame length of the
slower computer.

- The game's tick loop now lets Steam move data between the buckets of a tick, at most once every
  millisecond, and straight after a tick's events are queued for the guests. This only moves data
  (one native call per connection when nothing is waiting), runs on the game thread like every
  other Steam call, and does nothing outside a Steam session. The once-per-frame pump is unchanged
  and still does everything else: connecting, closing and failures.
- The simulation over the fake Steam network (5 ms each way) shows the pings below, in ms. The last
  column adds a 12 ms part of every tick that cannot be interrupted (the singletons and the wait for
  the parallel work), which the extra pumps cannot reach.

  | Host frame | Guest frame | Once per frame | Between ticks | Between ticks, 12 ms uninterruptible |
  |---:|---:|---:|---:|---:|
  | 8 ms | 8 ms | 35 | 32 | 31 |
  | 17 ms | 17 ms | 58 | 18 | 20 |
  | 17 ms | 60 ms | 123 | 17 | 19 |
  | 17 ms | 100 ms | 177 | 17 | 19 |
  | 100 ms | 100 ms | 323 | 13 | 18 |
  | 200 ms | 200 ms | 713 | 13 | 17 |

  What is left is the part of each frame that is not simulation (rendering, the garbage collector),
  where nothing can be served, so it is a few milliseconds at most for a frame like the ones in the
  host log.
- A direct-IP connection is not affected: its data moves on its own threads, not on the frame.
- The log now says how long data waited, once a minute while someone is connected, for example
  `Steam link timing over 60 s: data waited for the game thread 17.3 ms on average and up to 118 ms
  if Steam were only served once per frame (4 gaps over 50 ms); with the pumping between ticks it
  waited 1.6 ms on average and up to 24 ms (0 gaps over 50 ms).` The first figures are what a
  once-per-frame pump would have cost in that session and the second are what it cost, so one
  session shows both, to read next to the ping in the panel.

Tested: the transport, the between-ticks pump and the ping over fake Steam are covered by
automated checks, including the table above, which is reproduced by
`dotnet run --project StabilityTests -- --ping-report`. **Not verified in the game or with real
Steam.** The explanation follows from the game's code and a host log; the guest's frame length at
a high speed has not been measured, so it is not confirmed that this accounts for all of the 200
to 300 ms. The timing line in Player.log on both computers is what to look at.


## 1.0.8

The current release. It contains everything in the 1.0.4 to 1.0.7 pre-releases below, which were
never full releases themselves. Every player should install this build.

The fork owner played this build in multiplayer at a true speed 7 (large colony speed limit
removed), the configuration in which 1.0.7 desynced within minutes both times it was tried, and
reported that it works great, with no desync in a ten minute session.

### A desync at high speed: a beaver's zipline state came from the animation

Seen twice in one evening at a true speed 7 (large colony speed limit removed), with and without
other mods' route map changes. Comparing the two players' verbose logs tick by tick showed the
same thing both times: entity order and random state identical, then the **move hash** (where
every walking character is) differing, and two to six ticks later a beaver arriving at a building
on one computer and not the other, which is when the random state differs and the desync is
reported. Both times it began a few ticks after a hitch on the guest, while it was catching up.

One input to walking speed is not simulation state. In a flooded tile a beaver's speed is
multiplied by its water penalty modifiers, and the zipline's is `IsOnZipline ? 0.5 : 1`.
`ZiplineVisitor.IsOnZipline` is switched by an event from the per-frame movement animation, when
the animated model crosses onto or off a zipline corner. Which frame that is, and so whether it
falls before or after that beaver's own tick, depends on frame rate and on how many ticks a frame
carried. At low speed there are many frames per tick and both computers switch at nearly the same
point; at a true speed 7, and above all on a guest catching up with several ticks per frame, they
can differ by a tick.

- In a multiplayer session the modifier now asks `ZiplinePathTracker` instead, which holds the same
  fact as simulation state: it follows the path corners the walker actually moved along, from the
  tick, and it is saved with the game. The values are the game's own. The animation, harness and
  swimming visuals still follow the animated model. Single player is untouched.
- This is the one frame-timed input to walking speed found by reading the game's code. The logs
  could not say which beaver differed, so it was not proven to be the cause of those two desyncs;
  what is known is that the desync did not come back in the session described above. If one
  does, the next item will say which beaver and why.

### Walker diagnostics, written with the water diagnostics on a desync

While debug mode is on, every walking character's position, path (next corner, corner count, last
corner, corner speed), speed inputs (base speed, bonus multiplier) and both zipline flags are kept
for the last 192 ticks, and written to `BeaverBuddiesDiagnostics/walkers-*.tsv` on both computers
when a desync is reported. Floats are written as exact bits. 192 ticks because the host is ten to
twenty ticks ahead of a guest by the time a desync is reported, and the first difference is
several ticks before that; the water snapshots of the two computers did not overlap at all.
`RuntimeChecks/compare_walker_traces.py <host> <guest>` prints the first tick and character that
differ and which columns differ. Nothing is recorded with debug mode off.

### The host no longer waits for a guest that is still loading

On a rehost the host kept the old session's tick count, compared it with the joining guest's tick
zero and logged `Host pacing: waiting for a guest that is 150 ticks behind`. It cleared by itself
and cost nothing, because the game was loading. A guest that has not ticked yet is no longer
counted as behind.

## 1.0.7 (pre-release, included in 1.0.8)

A pre-release for testing, on top of 1.0.6. Every player should install this build.

### A chat box in the connection panel

Below the connection panel, in the same rectangle and exactly as tall as the section above it, is
a chat box for the players in the game. See [CONNECTION-PANEL.md](CONNECTION-PANEL.md) for how it
behaves; in short:

- Type in the box and press Enter. Each line reads `Name: message`, the name in that player's
  Ping Color. The log follows new messages unless you scroll up.
- The host numbers every message and sends it to every player, the sender included, so everyone
  sees one conversation in one order.
- **Full history.** The host keeps the whole session's conversation (up to 2,000 messages) and
  sends all of it to a player who joins later. It goes out in a few compressed frames after the
  joining guest has its save, state and init event, and a message sent during the join is
  either in that history or queued behind it, never both and never neither. Chat is per session:
  a reload or a rehost starts an empty chat.
- Collapsing the panel hides the chat; the collapsed header shows how many new messages there
  are. A new optional key binding, **Chat: start typing**, puts the cursor in the box (unbound
  until chosen; clicking the box always works).
- While the cursor is in the box the game's hotkeys are switched off, using the game's own
  mechanism for text boxes, so typing does not move the camera. Focus is released whenever the
  chat is hidden, collapsed, or the scene ends, so the hotkeys cannot stay off.

How it travels: chat frames use the same separate lane as cursor activity and the connection
status feed. They are handled on the receive thread before they can reach the game's event queue,
so they never enter the replay script or the desync hash. The host assigns each message's sender
and number (a guest's own claims are ignored), rate-limits each guest to a burst of six and then
two a second, and everything a peer sends is cleaned (plain text, one line, 200 characters, no `<`
or `>`) and validated; a malformed frame is dropped and never ends the session. The lane gained an
in-order queue for this: its existing latest-wins queue would have dropped messages.

If the chat fails it disables itself and the rest of the panel carries on.

Not verified: none of it has run in the game yet. The transport is covered by automated checks
over real host and guest sessions; the look, the keyboard handling and the mouse wheel over the
log are not, and are the things to look at first.

## 1.0.6 (pre-release, included in 1.0.8)

A pre-release for testing, on top of 1.0.5. Every player should install this build.

### A failing message handler can no longer crash the game

1.0.5 fixed the one handler that crashed a guest's game in 1.0.4. The way it got there was still
open: the network layer's `Update` called its `OnError`, `OnSessionFault` and `OnMapReceived`
subscribers unprotected, from the game's update loop, and they show dialogs and load scenes. An
exception in any of them was an uncaught exception, which is the game's crash screen.

- `TimberNetBase` now calls every subscriber by itself inside a `try`. One that throws is written
  to `Player.log` and the others still run.
- If loading the save received from the host fails, the guest is told ("The save from the host
  arrived but could not be loaded") instead of being left in the menu with no explanation.
- The join messages reached from Steam callbacks (`ShowJoinError`, `ShowConnectionMessage`) are
  guarded the same way: a message that cannot be shown is logged.

### The host waits for a guest that is very far behind

Easing off (1.0.4) stops at 30% of the chosen speed. With the large colony speed limit removed,
30% of speed 7 is still about 3.5 ticks a second, so a guest that has stopped altogether (a long
save, a long garbage collection, a stalled connection) keeps falling behind while the host queues
events for it. Left long enough, that fills Steam's send buffer, and a full buffer that makes
no progress for 30 seconds ends the connection.

- When the slowest guest is more than **60 ticks** behind (about five seconds at a true speed 7)
  the host stands still, at any speed, until that guest is within **10 ticks**, then carries on.
- Waiting does not change the easing percentage: one stall says nothing about what a computer
  can sustain. A guest that leaves, or whose connection times out, releases the host at once.
- The connection panel shows the host **waiting for a guest to catch up** while it applies, and
  both changes are written to `Player.log`.
- In the test model a guest frozen for 30 seconds is never more than about 60 ticks behind,
  and full speed returns afterwards.

This changes how fast the host works through ticks, never which tick anything happens on, so
it cannot change what anyone simulates.

## 1.0.5 (pre-release, included in 1.0.8)

A pre-release for testing, on top of 1.0.4. Every player should install this build.

### A guest could be dropped, and then crash, right after a long load

Seen in a 1.0.4 session: a guest finished a 40 second load, its first network read failed with
`Steam networking error: ppOutMessages must be the same size as nMaxMessages!`, the connection
closed, and the game then crashed with a `NullReferenceException` in `PanelStack.Show`.

- **The dropped connection.** Steam messages are read in batches into a buffer of 64, up to 256
  per update. Steamworks.NET refuses a read whose requested count differs from the buffer's
  length. The code asked for "whatever is left of the 256" on each call, which is 64 for every
  call except the last one of an update when between 193 and 255 messages are waiting. That only
  happens when many messages have piled up, as they do while a guest spends a long time loading
  (more so with debug mode on, which sends more). Every read now asks for exactly one full
  buffer; the 256 is a soft limit that can be passed by less than one buffer. The loop lives in
  `ReceiveBatching.Drain` so it can be tested: a new check drives it with 0 to 1000 waiting
  messages against a fake that refuses a mismatched count, as Steam's wrapper does.
- **The crash.** The guest's "could not connect" handler is created in the main menu and used
  that menu's dialog stack. It is also what reports a connection lost later, in the game, when
  that stack no longer exists; showing the dialog threw, nothing caught it, and the game
  crashed. The handler now falls back to the game's own dialog, and if that fails too it only
  logs. A lost connection can no longer crash the game from here.

Neither change affects what anyone simulates.

## 1.0.4 (pre-release, included in 1.0.8)

A pre-release for testing. Every player should install this build: the game warns when mod
versions differ, and mixed versions are untested.

### Game speed

- New setting, **Remove the large colony speed limit** (off by default). Timberborn slows its
  own speed settings as the population grows (`GameSpeedThrottler`): above speed 1 it runs at
  `1 + (speed - 1) x factor`, and the factor falls with population. In a colony of about 350,
  speed 7 ran at 3.4 (5.7 ticks a second where speed 7 asks for 11.7) and speed 3 at 1.8,
  while the simulation was using about a third of the time a true speed 7 allows on the
  computer it was measured on. With the setting on, the chosen speed is the speed.
- In multiplayer **the host's choice applies to everyone** for the whole session. Every
  computer applies that scaling by itself, so if the host removed it and a guest did not, the
  host would run twice as fast, and because the guest's catch-up speed is scaled down too it
  could never recover. The choice travels in the message a guest receives when it joins; a
  guest's own setting is ignored during a session, and a host changing the setting mid-session
  changes nothing until the next one. In single player the setting applies at once.
- This only changes how fast ticks are worked through, never what happens in them, so it
  cannot change what anyone simulates.

### The host eases off for a guest that cannot keep up

- A guest that falls behind speeds itself up (1.0.2). That recovers from hitches, but a
  computer that cannot sustain the chosen speed at all falls further behind every second
  however hard it tries. Removing the speed limit makes that more likely, so the host now
  notices and slows a little, only when it has to.
- Guests report the tick their game has reached in the reply they already send to the host's
  ping probe, about once a second, so the host knows how far behind each guest is.
- Nothing happens while the slowest guest is within 15 ticks, or is further behind but closing
  the gap. If it is more than 15 behind and has not gained for four reports in a row, the host
  drops to 85% of the chosen speed; while already easing, two reports are enough for the next
  15% step, down to a floor of 30%. Four reports for the first step, because lag also grows
  for as long as a single stall lasts (a save, a long garbage collection, the window in the
  background) and a fast computer recovers from that by itself. Once every guest is within 4
  ticks the host climbs back 5% per report, so a guest that was only slow for a while gets
  full speed back. Speed 1 and a paused game are never eased.
- In the test model of a guest whose computer manages 8 ticks a second at speed 7 (which asks
  for 11.7), the host settles at 7.9 and the guest is never more than 31 ticks behind; without
  easing it is 660 behind after three minutes and still falling. A guest that manages 10 gets
  10.1, one that manages 5 gets 4.8. A fast guest with hitches, or with a single stall of up
  to 4 seconds, never slows the host.
- The connection panel shows the host **Slowest guest behind**, and **Easing off for guests**
  with the percentage while it applies. Each change is also written to `Player.log`.
- Like the catch-up rule, this changes how fast the host works through ticks, not which tick
  anything happens on.

### Validation

- Release Steam and non-Steam builds succeed with no warnings. 132 StabilityTests (sixteen new
  in `HostPacingChecks`: who decides the speed limit, the reply format and its limits, the
  easing rule step by step, the model above, a real host and guest session in which the host
  reads the guest's lag from its replies, and the panel), 64 RuntimeChecks against the built
  mod and 2 Python checks pass.
- Not yet played in a multiplayer session.

## 1.0.3

Every player should install this build: it exchanges a little extra information when someone
joins, so it will not join a session with an earlier version.

### Mod list warning

- When a player joins, the host and the guest each send the other their list of enabled mods
  (ID, name and version) as part of the compatibility handshake, and each compares the two
  lists. If they differ, both players are shown a warning that names the mods that are on only
  one computer or at different versions. The host sees it in the lobby, before choosing Start
  Game; the guest sees it as soon as the game has loaded. It is also written to `Player.log`.
- It is only a warning and never stops anyone joining: mods that only change the interface are
  harmless, and only the players can tell which mods matter. It exists because a mod that acts
  on one computer only makes the games drift apart into a desync: in a real session one player
  had an extra housing mod, which switched another housing mod off on their computer alone. The
  join check only compared this mod and the game, so nothing said so.
- The exchange happens after the build check has passed, on the same connection and inside the
  same time limit, so a different build is still refused before any mod list is sent. The list
  is bounded (32 KB compressed, at most 300 mods, names shortened and stripped of control
  characters), and anything unreadable is ignored: a malformed or oversized list can never end
  the session or put odd text in the warning. If a computer cannot read its own mod list it
  sends a marker the other side ignores, so nobody is told that every mod differs.
- The warning text is English only for now; other languages show the English text.

### Validation

- Release Steam and non-Steam builds succeed with no warnings. 116 StabilityTests (nineteen
  new: the list format and its limits, the comparison, the message, the exchange during the
  handshake including a refused build and an oversized list, and real host and guest sessions),
  64 RuntimeChecks (five new, using the game's own mod objects) and 2 Python checks pass.
- The warning has not yet been seen in a running game.

## 1.0.2

Every player should install this build: the game warns when mod versions differ, and mixed
versions are untested.

### Performance

- A guest now catches up to the host before it falls far behind at high game speeds. The original
  rule sped a guest up only once it was more ticks behind than the game speed: more than 1 tick at
  speed 1, but more than 7 ticks at speed 7. Both players run at the same nominal speed, so every
  hitch on the guest added lag that nothing recovered until it passed that mark, and at speed 7 a
  guest sat 3 to 5 ticks behind (about 0.3 to 0.4 s before it saw the result of its own actions, on
  top of the network delay). A guest now starts catching up once it is more than 2 ticks behind,
  whatever the game speed, and continues until it is within 1. It aims for a small buffer and not
  zero, because a guest with nothing queued has to wait for the host's heartbeat before every tick.
- This only changes how quickly a player works through ticks it has already received. The host
  still decides which tick every event runs on, so it cannot change what any player simulates. The
  host is unaffected (it is never behind), a paused game keeps the original rule exactly, and a
  guest is never slower to catch up than before. The 10x cap is unchanged.
- The catch-up speed changes less often than before. Every speed change notifies each animated
  building, and "ticks behind" naturally flickers by one as the host's tick arrives and the guest's
  finishes, so the original rule changed speed on almost every tick once it was active. Within one
  catch-up the speed now only rises, then drops back once. In the test model of a guest that loses
  0.3 s every 5 s at speed 7, average lag falls from 7.6 to 2.2 ticks and speed changes from 1174 to
  92 over two minutes; at speed 3, from 3.3 to 1.9 ticks and from 530 to 62; speed 1 is unchanged.
- If a guest's computer cannot sustain the chosen speed at all, no catch-up rule helps: compare the
  tick rate in the connection panel on both computers (about 11.7 per second at speed 7).

### Validation

- Release Steam build succeeds with no warnings. 59 RuntimeChecks pass against the built mod.
- 97 StabilityTests pass, ten of them new (`CatchUpSpeedChecks`): exact behaviour at each speed,
  never slower than the original rule, the paused case, the host case, and the hitching-guest
  model above. The rule is a pure function (`BeaverBuddies/CatchUpSpeed.cs`) linked into the tests.
- Two Python checks pass, and the non-Steam build also succeeds with no warnings.
- The fork owner played this build in multiplayer and reported that it works great.

## 1.0.1

Every player must install this build; it will not join a session with 1.0.0.

### Performance

- A guest's actions are sent to the host as soon as they are made, instead of at the next tick
  boundary. The host still decides which tick they run on (a guest never plays or hashes its own
  actions), so this only removes about half a tick of input delay, roughly 0.3 s at normal speed,
  and cannot change what any player simulates.
- The per-tick desync traces no longer carry stack traces over the network. A trace records its
  stack as an object and formats it only when a desync report is written, so detailed logging
  costs much less CPU and the trace payload is far smaller. Desync reports still include your own
  stacks; the other player's traces appear as messages only.
- Each tick, only characters that move are examined for animation state. Buildings are skipped
  after one component lookup instead of four.
- The per-frame animation update reads the simulation clock and the tick length as plain values
  instead of calling into Unity several times for every animated character, and looks up each
  character's tick bucket once instead of twice. The result is computed with the same arithmetic.
- The "Client trying to tick before receiving Heartbeat" warning is logged once per tick instead
  of on every check.

### Install folder

- The install folder inside the download is now `BeaverBuddies-Stability-Fork` (it was `BeaverBuddies-StabilityPreview`). If you
  installed an earlier download, delete the old folder before copying in the new one, because both
  share a mod ID and would conflict.

### Validation

- Release Steam and non-Steam builds succeed with no warnings. 87 StabilityTests, 59 RuntimeChecks
  (four new ones cover the trace payload and the desync report) and 2 Python checks pass.
- The fork owner played this build and reported that it works great.

## 1.0.0

The first official release of this fork. See `STEAM-INVITES.md`, `CONNECTION-PANEL.md` and `PLAYER-ACTIVITY.md`.

### Steam friend invites

- Replace the legacy `ISteamNetworking` P2P transport (deprecated by Valve) with
  `ISteamNetworkingSockets`, so Steam friends can be invited from Steam's overlay without
  Hamachi or port forwarding. It is offered alongside direct IP, not instead of it.
- All Steam calls run on the game thread; TimberNet talks to Steam through queues. Writes
  never block, and the connection completes in the background instead of inside the
  3-second wait in `TimberClient.Start()`.
- The host accepts only players who joined its friends-only Steam lobby. The lobby records
  whether the host is still accepting players, so an old invite explains itself. A friend
  whose game was closed joins through Steam's launch invite (`+connect_lobby`).
- Raise Steam's send rate and buffer limits so the save transfer is not throttled (the
  original capped it at 128 KB/s). Every connection failure, stall or timeout ends with an
  explanation that includes Steam's own end reason, in the error dialog and in `Player.log`.
- The transport keeps unread data between reads, validates read ranges and wakes blocked
  readers when a connection closes. A comment in the original's Steam read routine says it
  "will fail" if Steam merges several messages into one packet.
- A Steam failure can no longer prevent hosting over direct IP.
- TimberNet: transports can report why they failed and complete connecting in the
  background (`IFailureDescriber`, `IConnectionAwaitable`).
- Remove an unused upstream handler that still called the legacy Steam P2P API.

### Connection panel

- A small HUD panel during multiplayer showing connected players, each player's ping
  (green, yellow or red), whether you are in sync, the tick rate, game speed, how far a
  guest is behind the host, and whether players are connected directly or through Steam.
- It collapses to one line by clicking its title (remembered), and can be hidden from Mod
  Settings or with an optional key. Its corner is a setting.
- Ping is measured by the network layer (a probe once a second, answered on the guest's
  network thread) so it works the same over Hamachi, direct IP and Steam. Probes and the
  player roster use the separate presentation lane: never replayed, never hashed, never
  sent to a guest that is still joining, and validated on arrival.
- The panel docks into the game's own HUD layout, and only reads: it sends no gameplay
  event, and if it fails it disables itself.

### Player activity

- Show other players' translucent, colored cursors with names, remote selection
  outlines in each player's color, and **Viewing / Editing** labels on buildings.
- An in-game **Player cursors** dialog (Options menu) sets, per connected player, the
  cursor's color (their color, presets or exact RGB), size (50%-300%) and transparency
  (0%-90%). Choices are local, applied live, and remembered by player name in
  `BeaverBuddiesCursorStyles.json`.
- The **Player activity indicators** setting (on by default) turns sharing on and off.
- Activity uses its own lane on the existing connection, separate from the replay
  script and desync hash: host-assigned identities, latest-wins coalescing, no
  game-thread blocking, and nothing sent to a guest until its join has finished.

### Compatibility and connection safety

- Negotiate compatibility before requesting or loading the shared map. Compare the running
  game version, full mod version, and loaded BeaverBuddies/TimberNet module IDs. Replacing
  files without restarting cannot disguise an old process. Reject builds that lack the
  check and mismatched binaries. Bound the compatibility wait to 15 seconds, close failed
  connections, and report an update/restart message. The original only warned about a
  version mismatch after the save had loaded.
- Stop replay after a failed action, discard pending actions, pause the session, notify
  connected peers, and restore the replay flag even if error handling throws. Block
  further simulation and rehosting until the scene is reloaded; the affected player is told
  to reload a known-good save. A failure stop contains partial state; it does not roll back
  the action or recover unsaved progress, and peer notification is best-effort if the
  connection has already failed.
- Lock complete network frames so concurrent header and payload writes cannot mix.
- Close corrupted or failed connections, stop consuming events after failure, and deliver
  client error callbacks through the update thread.
- Close discarded client connections and prevent an old socket's cleanup from
  unregistering its replacement. Synchronize socket registry access.
- Synchronize client-list access during joins, broadcasts and shutdown.

### Desync fixes

- **Water and frame rate.** Replace the render-frame clock in
  `WaterDepthStrengthModifier.GetStrengthModifier` with Timberborn's configured simulation
  tick interval during multiplayer. This prevents different frame rates from producing
  different water-seep output. Inject `ITickService` into the existing water-source
  buffer. Preserve the game's depth thresholds, hysteresis, fade speed, disabled-state
  reset and maximum-strength clamp. Single-player keeps its original frame clock. Validate
  that the targeted method contains exactly one clock call to replace. The fork owner
  confirmed this resolved their reported badtide desync. A regression experiment
  reproduced different output at 30 and 144 FPS using the installed game's ramp
  instructions, then verified identical output after the production transpiler; its depth
  query and frame clock are test doubles, and it does not start Unity or install Harmony
  into a live game. Evaporation settings are unchanged. The source ramp now advances by
  simulation seconds rather than local frame duration, so its timing can differ from
  upstream.
- **Water-source ordering.** Apply water-source simulation snapshots in a consistent order
  by coordinates, strength and contamination. Preserve the live source registry and
  values. This corrects a demonstrated order-dependent case when multiple sources affect
  the same water column: the installed game's source-update task produced three results
  across six registration orders; canonical ordering produced one. This case was not
  established as the cause of the reported badtide desync.
- **Saving flag.** Restore the previous saving flag after exit saves, including
  exceptions, using a Harmony finalizer. Clear stale saving state when resetting between
  scenes. Restore the previous flag after deferred normal saves using `try/finally`. This
  addresses a defect consistent with immediate join-time desync reports, in which one
  peer omitted a moisture trace because its saving flag remained set.
- **Random numbers.** Preserve nested gameplay and non-gameplay RNG classification and
  restore it after exceptions in random-selection wrappers. Make all ten RNG
  classification patches exception-safe, counting nested calls instead of using simple set
  membership, and restore the ticker's prior RNG classification in a finalizer, including
  nested updates. The ordinary RNG check stays enabled regardless of detailed logging
  settings.
- **Equal-distance demolition jobs.** Choose exactly equal-distance jobs by persistent
  entity ID in multiplayer. Preserve nearer-job preference, eligibility, priority and
  reservation rules; single-player behavior is unchanged. Not confirmed in a live session,
  and the logs of the incident that prompted it did not prove an equal-distance tie
  caused it.
- **Stuck controls.** Recover input on desync notification and multiplayer scene load,
  including direct-IP Save and Rehost, using the game's built-in device reset. Recovery is
  deferred to Update and consumes cached held/down/release binding state. Repeated requests
  are coalesced; devices are not reset every frame and saved keybindings are not
  changed. The service is registered only in multiplayer scenes. The native device reset
  is mocked in regression tests. This is a targeted recovery measure, not a proven root
  cause, and the original report has not been confirmed fixed.
- **Entity IDs.** Apply regenerated entity IDs to the entity builder and fail explicitly if
  a unique ID cannot be found after the retry limit.

### Crash fixes

- **Animation.** Reset the animation path cursor before interpolation that can move
  backward between ticks. Fall back to the simulation position for non-finite visual
  coordinates before water and swimming listeners consume them. The fork owner reported
  that this fixed their crashing issue.
- **Demolition-selection replay.** `ClearResourcesMarkedEvent` looked up each entity in a
  replayed demolition selection with no null check, so if builders had already demolished
  some of the selected entities before the event arrived (it was stamped for tick 1771 and
  replayed at 1773), a `NullReferenceException` aborted the whole session. Missing
  entities are now skipped with a warning, the same way `BuildingsDeconstructedEvent` does,
  and an event with nothing left is skipped. The host re-stamps and forwards events at its
  own tick, so both sides skip the same entities and stay in sync. The regression check
  replays a stale selection against a real empty entity registry and fails on the old
  code. A selection where only some entities are missing needs live Unity objects, so it
  is untested, and the fix has not been confirmed in a live session. `DuplicationEvent` has
  the same kind of weak spot and is left unchanged.

### Performance

- Recycle expired water diagnostic arrays, preserving snapshot retention and captured
  values while removing steady-state map-sized allocations.
- Optimize ordered event insertion and bulk removal of consumed backlog.
- Parse each incoming transport frame once instead of twice.
- Gate routine event, packet and tick logging before formatting and serialization.
- Synthetic tests measured about 85 MB versus zero bytes allocated for 16 warmed-up
  captures, and 438.92 ms versus 0.28 ms for 4,000 ordered inserts. These are not FPS
  tests. Detailed logging, synchronous sends and throttling remain performance costs.

### Diagnostics

- Add separate hashes for active depth, contamination, overflow, geometry, inactive
  storage and source inputs without disabling existing checks.
- With detailed logging enabled, retain up to four water-map snapshots within a 64 MiB
  budget and write a local ZIP on desync. Reset capture between sessions and catch
  diagnostic failures. No automatic diagnostic upload exists.
- Add a Python tool to compare retained snapshots by tick, cell, field and exact
  floating-point bits.
- Include individual water-source coordinates and exact strength and contamination bits in
  detailed traces. Record selected demolition target IDs and distances there too, with up
  to eight eligible candidates in verbose local logs, without treating harmless
  candidate-order differences as synchronized trace mismatches.
- Add transport, animation and player-activity regression tests and a compiled-mod
  runtime test executable.

### Other

- Disable the original project's in-game changelog dialog, which appeared whenever the mod
  version changed.
- Report the mod version as exactly the release version, without a source-commit suffix.

## Installation

1. Fully close Timberborn on every computer.
2. Download `BeaverBuddies-Stability-Fork-1.0.3.zip` from the
   [latest release](https://github.com/kramsey458/BeaverBuddies-Stability-Fork/releases/latest),
   extract it, and copy the `BeaverBuddies-Stability-Fork` folder into
   `Documents/Timberborn/Mods`. If you installed an earlier download, delete its old
   `BeaverBuddies-StabilityPreview` folder first: the two share a mod ID and would conflict.
3. Make sure **Harmony** and **Mod Settings** are enabled, then enable **BeaverBuddies -
   Stability Fork**, version **1.0.3**, on every computer. Disable the Workshop
   BeaverBuddies and any duplicate local copies: they share one mod ID.
4. Every player must use the same build. Test on a copied save first.

For a diagnostic session, enable **Always Use Detailed Logging** on both peers. It adds
overhead. If a desync occurs, keep both Player.log files and the newest water ZIP from each
peer under:

`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\BeaverBuddiesDiagnostics`

Large maps may retain fewer snapshots; peers running far apart may have no shared retained
ticks. Old ZIPs remain until removed. See `RuntimeChecks/compare_water_snapshots.py` for
comparison commands.

## Validation and limits

The 1.0.3 validation run passed **182 checks**: 116 in `StabilityTests` (network transport,
the Steam transport against a simulated Steam network, direct-versus-Steam protocol parity,
animation, player activity, ping measurement, the connection panel, the guest catch-up
rule and the mod list warning), 64 in
`RuntimeChecks` (the compiled mod running against the game's own assemblies) and two Python
archive-comparison checks. The mod builds against Timberborn 1.1.2.4 with no warnings.
Tests require .NET 8; game-dependent checks additionally require the user's installed game
assemblies and Harmony directory. No proprietary game assemblies, decompiled game code,
player logs or saves are included in this fork.

The Steam transport is tested against a fake Steam network that fails any Steam call made
off the game thread. Two protocol-parity checks run one scripted session (about 60 events
in each direction, one of 220 KB, plus cursor traffic) over a direct connection and over
Steam under stress, and require both peers to end with identical events and state hashes;
corrupting one byte in the Steam path makes them fail. The layer that calls the real Steam
client is not covered by the automated checks; it has been confirmed in real playtests.

The fork owner's two-player playtests confirmed: the animation crash fix, the badtide
desync fix, the player activity indicators, Steam invites and the connection panel, and
that the compatibility check, failed-action stop, input-delay and performance changes play well. They
confirm the reported issues and that this build plays well, not universal determinism.

Not confirmed in a live session: the demolition-selection crash fix, equal-distance
demolition tie-breaking and the stuck-controls recovery. Only two players have been tested.
Everyone in a session must run the identical build. Other mods are compared as a warning
only, and settings are not compared. Text added by this fork is English only. Other game versions and combinations of
mods may still have unrelated problems. No Housing Optimize changes are included. Upstream
authorship and GPL licensing are preserved in `License.txt` and the repository history.
