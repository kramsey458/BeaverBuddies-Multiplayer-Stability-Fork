# Developing BeaverBuddies Stability Fork

How to build and check the mod, and how its networking, connection panel and player cursors work. For playing, see
the [README](README.md). Every change, with how it was checked, is in [STABILITY-CHANGELOG.md](STABILITY-CHANGELOG.md).

## Building from source

1. Clone this repository. Copy `BeaverBuddies/env.props.windows-template` (or the unix one) to
   `BeaverBuddies/env.props` and point it at your Timberborn install and the Harmony and Mod Settings mods.
2. Build: `dotnet build BeaverBuddies/BeaverBuddies.csproj -c "Release Steam"` (`-c Release` builds without Steam
   networking). By default the build copies the mod into `Documents\Timberborn\Mods\BeaverBuddies\`; pass
   `-p:BeaverBuddiesModsPath="<folder>\"` to put it somewhere else.
3. A release zip is the **Release Steam** build, in a folder `BeaverBuddies-Stability-Fork/version-1.1/`.

## Checks

- **StabilityTests** needs only the .NET 8 SDK, and GitHub Actions runs it on every push and pull request:
  `dotnet restore StabilityTests/StabilityTests.csproj --source https://api.nuget.org/v3/index.json`, then
  `dotnet run --project StabilityTests --no-restore`. Add `-- --ping-report` to print how the Steam ping depends on
  each player's frame length.
- **RuntimeChecks** runs the compiled mod against the game's own assemblies, so it needs the game installed:
  `dotnet run --project RuntimeChecks -- <BeaverBuddies.dll> <Timberborn_Data\Managed> <Harmony folder> <Mod Settings Scripts folder>`.
- **Python:** `python -m unittest discover -s RuntimeChecks -p "test_water_snapshots.py"` and
  `python RuntimeChecks/compare_walker_traces.py --self-test`.

What each suite covers: [StabilityTests/README.md](StabilityTests/README.md). None of them can start Unity or prove
full multiplayer determinism, so real play is what confirms behaviour.

## Steam networking

The original BeaverBuddies used Valve's legacy `ISteamNetworking` P2P API, which Valve marks deprecated. This fork
uses `ISteamNetworkingSockets`, the API Valve recommends, from the game's own Steamworks assembly.

- **Lobby.** The host opens a friends-only Steam lobby; the overlay invites into it. Lobby data says whether the host
  still accepts players, so an old invite explains itself instead of hanging.
- **Admission.** The host accepts a connection only from a member of its lobby, so a stranger who knows a Steam ID
  can't connect. A guest that arrives before the host's lobby view catches up gets a five-second grace period.
- **One thread for Steam.** Every Steam call happens on the game's main thread, where Steam callbacks arrive.
  TimberNet's threads use in-memory queues: `Write` never blocks, and `Read` blocks on a queue the pump fills.
  Steam doesn't promise these calls are safe from other threads, so nothing depends on it.
- **Pumped between ticks.** The pump runs once a frame, and also between a tick's buckets (at most once a
  millisecond, right after a tick's events are queued). At a high game speed a frame is mostly simulation, so without
  this the ping grows with the speed. Only data moves there: no state changes, closing or admission. The log reports
  how long data waited, once a minute.
- **Background connect.** The connection completes after `ConnectAsync` returns. The client waits for it on a worker
  thread (up to 45 s) before the compatibility handshake's 15 s clock starts.
- **Throughput.** The send rate ceiling is raised to 8 MB/s and the send buffer to 4 MB (Steam's defaults are
  256 KB/s and 512 KB), so the save transfer isn't throttled. A full buffer keeps data queued for the next frame:
  nothing is dropped and order is kept.
- **Never hangs silently.** A send without progress for 30 s, a connect over 40 s, or a queue past 128 MB fails the
  connection with an explanation. Steam's end reason is translated into plain words, in the error and the log.
- **Steam can't break hosting.** If Steam fails to start, direct-IP hosting carries on. A local close flushes queued
  data for up to 3 s, and data the peer sent before closing is still delivered before end-of-stream.
- **The overlay.** While Steam's overlay is open the game pushes an input-blocking panel. It's removed when the
  overlay closes, or as soon as a dialog opened over it is closed.
- **Reconnect after a desync.** A guest remembers how it joined. **Reconnect (wait for Rehost)** redials the same
  address, or joins the host's current Steam lobby when Steam shows it. Otherwise the guest is told to accept a new
  invite.

**Checks.** StabilityTests runs the production transport (`SteamLinkSocket`, `SteamLinkManager`) against a fake Steam
network. The fake fails any test that calls Steam off the game thread, rejects messages over Steam's 512 KB limit and
uses a small send buffer to force backpressure. It covers background connect, byte-exact ordered transfer of 3 MB
through a tiny buffer, write coalescing, `Write` never blocking with the pump stopped, the queue cap, failure
explanations, the connect timeout, the stall detector, draining before end-of-stream, closing, one bad connection not
affecting another, lobby-only admission with the grace period, and a full TimberNet session over Steam. A protocol
parity check runs one scripted session over a direct connection and over Steam under stress; both peers must end with
identical events and state hashes.

**Not covered by checks:** the thin layer that calls Steam itself (`SteamLinkBackend`, `SteamNet`, `SteamListener`,
`SteamOverlayConnectionService`). It compiles against the game's real Steamworks assembly and runs only in the game,
with two Steam accounts. Steam invites have been confirmed in playtests between the maintainer and a friend,
including a session at a true speed 7 with the ping under 100 ms.

### Two-account playtest

1. Host with **Enable Steam Networking** on. `Player.log` should show `Steam networking started`,
   `Steam relay network: Current`, `Steam is listening for players` and `Steam lobby created with ID ...`.
2. Choose **Invite Friends**; the overlay should open. If not, the log says the lobby isn't ready yet.
3. The friend accepts. The host's log shows `Steam link to <name>: accepted`, then `... Connecting -> Connected`, and
   the friend appears in the list of connected players.
4. Start the game, play, then have the friend leave. The host keeps running.
5. Repeat with the friend's game **closed** when they accept (the launch invite).
6. Repeat with an invite sent after the host unpauses. Expect the "already started or changed" message.
7. Host with Steam Networking **off** and check that direct IP works.

If something fails, collect both `Player.log` files. Every state change and close is logged with Steam's numeric end
reason and debug text.

## Connection panel, chat and player cursors

Pings, the roster, chat and cursor activity travel on a side lane beside the game's actions.

- **Never part of the game.** Side-lane frames are intercepted on the receive thread before the event queue. They
  are never in the replay script or the desync hash, and never sent to a guest until its save, state and init frames
  have been written. Every frame is validated; a malformed one is dropped and never ends the session. If the panel,
  the chat or the cursor overlay fails, it switches itself off and the game carries on.
- **Ping.** Once a second the host sends each guest a probe, answered on the guest's network thread. The reply also
  carries the guest's tick and frame rate (**Guest behind**, **Guest fps**). The host smooths the results and sends
  every guest a short roster. Over Steam, data moves only while a game thread serves Steam, so the ping includes a
  short wait at each end: at most a millisecond during the simulation.
- **Chat.** The host numbers every message and sends it to everyone, the sender included, so all see one order. It
  keeps the history (up to 2,000 messages) for late joiners, strips control characters and `<` `>`, and allows a
  burst of six messages, then two a second. Chat sends no gameplay event.
- **Cursors.** At most ten samples a second, in unscaled time. Each connection keeps only the newest state per
  player, drained by one pooled task through the same framed write path as game actions. The host assigns each
  guest's identity (the host is player 0). The receive mailbox keeps at most 64 states, and a remote display expires
  after three seconds without updates.
- **Editing notices** come from the mod's shared entity-action path. Third-party UI that bypasses it, zipline tools
  and global technology unlocks don't show **Editing**.
- **Cursor styles** are saved locally to `BeaverBuddiesCursorStyles.json` in the game's persistent data folder, keyed
  by display name (name plus player number when two connected players share a name). A missing or damaged file means
  default styles. Your own chat color is kept under the key `#you`.
- **Panel width** follows the game's population panel above it, else the nearest panel above, else its own text
  (210–300 px). The width it followed is written to `Player.log`. The chat is laid out below the panel, so a long
  message wraps and never widens it.

**Checks.** StabilityTests covers the round-trip tracker, the wire formats and every malformed roster, probe, chat and
activity frame, and real host and guest sessions: pings per guest, the roster, bad frames ignored, departed guests,
and nothing sent before a joining guest has its save. In those sessions the side lane never changes the hash, the
event script or tick progress, and gameplay keeps its order under a chat or activity flood. It also covers the chat
log, rate limit and cleaning, player colors, the style store (clamping, damaged files, atomic saves), the panel's
wording, states and sizing, the frame-rate easing, and that every string asked for exists in the English file.
RuntimeChecks passes against the compiled mod.

**Not covered by checks:** how cursors, labels and outlines render, and the Player cursors dialog's layout and
sliders. Those need the real game.

### What has been played

- 1.1.10 was played in multiplayer over Steam invites for more than an hour, in 300+ colonies, and worked very well.
  1.1.11's chat colors were played too. The speed boost and **You, in the chat** were played in 1.1.13.
- Screenshots from real sessions show the panel and chat drawing, the sync dot as the only dot, player rows as a name
  and a ping, your own row in bold with a dash, the boxed collapse button, and chat names in each player's color.
- **Not checked one by one:** the panel matching the counters' width, the chat clearing the game's alerts and being
  drawn in front of them while you type, the three other corners, the settings and optional keys, and names already
  written changing color. For the chat: Enter, Esc, a click on the game and the optional key giving the keyboard
  back, the hotkeys staying off while you type, long messages wrapping, the log following new messages, and the mouse
  wheel scrolling it without zooming the camera.

### Two-player cursor playtest

1. Install the same build on every computer. Check that **Player activity indicators** is in Mod Settings, and pick
   different names and colors.
2. Host and join. Move around the same building from different camera angles; check cursors on terrain and buildings.
3. Select different buildings, then the same one. Check the outlines and labels, that your own selection color wins,
   and that you can click through a cursor marker.
4. Change a recipe, worker count or automation setting: **Editing** shows for three seconds. Leaving a panel open
   shows **Viewing**.
5. Open Esc → **Player cursors**. Drag each slider and check the other player's cursor updates live. Use **Reset**,
   reopen the dialog, then restart the game and check the style is remembered. In **You, in the chat**, pick a
   preset: your own chat name changes on your screen only.
6. Have a third player join (or rename one) while the dialog is open. Try two players with the same name.
7. Repeat while paused and at high speed. Hover the UI, alt-tab, turn activity off and on, remove a selected building
   and disconnect a guest: stale marks clear within about three seconds.

## Design notes

Wonders on the tick: [BeaverBuddies/Doc/WonderTiming.md](BeaverBuddies/Doc/WonderTiming.md). How the original keeps
games in step: the [original project's wiki](https://github.com/thomaswp/BeaverBuddies/wiki/How-does-it-work%3F).
