# BeaverBuddies Stability Fork

Multiplayer co-op for Timberborn, with **Steam friend invites**, an **in-game connection panel**, and a long list of crash and desync fixes.

[![Latest release](https://img.shields.io/github/v/release/timbermods/BeaverBuddies-Stability-Fork?label=latest&labelColor=172620&color=e0812f&style=flat-square)](https://github.com/timbermods/BeaverBuddies-Stability-Fork/releases/latest) ![Timberborn 1.1.2.4](https://img.shields.io/badge/Timberborn-1.1.2.4-2a4034?labelColor=172620&style=flat-square) ![Tested on Windows with the Steam version](https://img.shields.io/badge/tested_on-Windows_%2B_Steam-2a4034?labelColor=172620&style=flat-square) [![GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-2a4034?labelColor=172620&style=flat-square)](License.txt)

**[Download](https://github.com/timbermods/BeaverBuddies-Stability-Fork/releases/latest)** · [Install](#install) · [Website](https://timbermods.github.io/BeaverBuddies-Stability-Fork/) · [Changelog](STABILITY-CHANGELOG.md) · [Steam invites](STEAM-INVITES.md) · [Connection panel](CONNECTION-PANEL.md) · [More Timberborn mods](https://github.com/timbermods)

> [!NOTE]
> **1.1.11 is stable and working.** 1.1.10 has been tested extensively in multiplayer, including in large colonies, and no desyncs have been recorded so far. 1.1.11 is that release with a visual change on top (each player gets a color of their own, and only the name is colored in the chat), and it is working without issues. That is a record of what has been played, not a guarantee: see [Things to know](#things-to-know-before-you-play) for what has not been tried.

This is an independent fork of [thomaswp/BeaverBuddies](https://github.com/thomaswp/BeaverBuddies), the original multiplayer mod. It keeps everything the original does (players build one colony together in real time, each with their own camera and interface, multi-start maps, map pings, hosting and joining from the in-game menus) and builds on top of it. All credit for the multiplayer design belongs to the original project. Please report problems with *this fork* here, not to the original project.

## Highlights

- **Steam invites work.** Invite a Steam friend from Steam's own overlay and they join with a click: no Hamachi, no port forwarding. Confirmed in real playtests with a friend over Steam. Direct IP still works, and you can offer both at once.
- **A connection panel in the game.** See who is connected, each player's ping, whether you are in sync, the tick rate and more, in a small panel you can collapse or hide, with a chat box below it.
- **A low ping at a high game speed.** Over Steam, data used to wait for the end of every frame, and at a high game speed a frame is mostly simulation, so the ping climbed with the speed. The mod now lets Steam move data between the ticks of a frame. In a playtest at a true speed 7 the ping stayed under 100 ms, where it had been 200 to 300 ms (details in [CONNECTION-PANEL.md](CONNECTION-PANEL.md)).
- **The host can ease off for a slow guest.** The host picks a frame rate floor (Off, 20, 30, 45 or 60 fps) in the connection panel; while a guest stays below it the host slows the game a little, and speeds back up by itself. It only changes how fast the host works through ticks, never what happens in them. The current rule has not been played yet.
- **Controls and the menu come back after a session ends.** After a disconnect, a failed action or a cancelled join, the game no longer keeps ignoring the player (Escape opens the menu again). Confirmed after a disconnect; the other cases are tested only.
- **See what your teammates are doing.** Colored, translucent cursors, selection outlines, and "Viewing / Editing" labels on buildings, with per-player cursor color, size and transparency. Each player starts with a color of their own.
- **Fewer crashes and desyncs.** Specific, documented fixes for water, animation, random numbers, saving, demolition and input problems (details [below](#how-this-fork-improves-on-the-original)). This reduces known causes; it is **not** a guarantee that a desync can never happen.
- **Mismatched builds are caught early.** Joining with a different build is refused before the save is sent, with a message that says what to do, instead of failing halfway through.
- **Mismatched mods are flagged.** When someone joins, both players are warned if their lists of mods differ, naming the mods that are on only one computer or at different versions, so a mismatched mod is caught in the lobby instead of as a desync later. It is a warning, not a block.
- **Failures are explained.** A failed connection or multiplayer action ends with a plain-language reason (including Steam's own error code) instead of a silent hang.
- **Tested.** 287 automated checks, including runs against the game's own assemblies. See [Testing](#testing-and-verification).

## Install

**You need:** Timberborn (this release is built and tested against **1.1.2.4**), with the **Harmony** and **Mod Settings** mods enabled. Every player must run the same game version too.

1. Download `BeaverBuddies-Stability-Fork-1.1.11.zip` from the [latest release](https://github.com/timbermods/BeaverBuddies-Stability-Fork/releases/latest).
2. **Close Timberborn.**
3. Extract the zip and copy the `BeaverBuddies-Stability-Fork` folder into `Documents\Timberborn\Mods`. If you installed an earlier download, delete its old `BeaverBuddies-StabilityPreview` folder first: the two share a mod ID and would conflict.
4. Start Timberborn and enable **BeaverBuddies - Stability Fork** (v1.1.11) in the mod list. **Disable the Workshop BeaverBuddies and any other BeaverBuddies copy**: they share the same mod ID and will conflict.
5. **Every player must install the exact same download** and restart the game. This is the most common cause of trouble; see [Things to know](#things-to-know-before-you-play).

This fork is distributed through GitHub Releases only. The Steam Workshop and mod.io pages linked further down belong to the original project.

## Host and join

**Host**
1. Load the save you want to play and choose **Host co-op game**.
2. Bring your friends in: for Steam choose **Invite Friends**; for direct IP give them your IP address (default port **25565**, which must be forwarded, or use a VPN such as Hamachi).
3. When your friends appear in the connected-player list, choose **Start Game**.

**Join**
- **Steam:** accept the invite. If Timberborn is closed, Steam launches it and joins for you. With **Allow Friends to Join Directly via Steam** on, a friend can also use **Join Game** from Steam's friends list.
- **Direct IP:** from the main menu choose **Join co-op game** and enter the host's IP address or domain name.

Guests receive a copy of the host's save (kept under **Online Games**). Nobody can join after the host chooses **Start Game**.

**If a desync happens:** the host chooses **Save and Rehost**, then guests choose **Reconnect (wait for Rehost)**, which joins the way they joined before: a direct-IP guest redials the address it used, and a Steam guest joins the host's new Steam lobby if Steam shows it (the host has **Allow Friends to Join Directly via Steam** on). Otherwise a Steam guest accepts a fresh invite.

## Steam invites

Steam friend invites are a first-class way to play, alongside direct IP.

- **Requirements:** both players online in Steam, both owning Timberborn, and both running the exact same build.
- **Settings** (Mod Settings → BeaverBuddies): **Enable Steam Networking** and **Allow Friends to Join Directly via Steam**.
- **Who can join:** the host opens a friends-only Steam lobby, and only players who joined that lobby are accepted. A stranger who knows your Steam ID cannot connect.
- **How it works:** connections go straight between players when Steam can find a route and are otherwise relayed through Steam's network. Valve documents that relaying keeps players' IP addresses hidden from each other. The original used Valve's older networking API, which Valve now marks as deprecated; this fork uses the current one.
- **If Steam has a problem,** hosting over direct IP still works. Hamachi has also been tested to create a virtual LAN to avoid port forwarding and works.
- **Status:** confirmed working in real playtests between the maintainer and a friend. More details, including how to read the log if something fails, are in [STEAM-INVITES.md](STEAM-INVITES.md).

## The connection panel

A small panel appears in the top-left corner during a multiplayer game.

<img src="docs/assets/connection-panel.png" width="280" alt="Screenshot of the in-game connection panel as the host sees it: In sync, the host's own row in bold with a dash and one guest at 19 ms, tick rate 11.7 ticks per second, speed 7x, the host pacing lines, a Steam connection and a chat with two colored lines.">

*The panel as the host sees it during a Steam co-op session (a screenshot from 1.1.10, where the whole chat line took the player's color; since 1.1.11 only the name does).*

| It shows | Meaning |
| --- | --- |
| **Players** | Everyone in the session, host first, each as a name and a ping. Your own row is bold and shows a dash instead of a ping. |
| **Ping** | Round-trip time between you and that player. Normal text: 80 ms or less. Yellow: up to 160 ms. Red: higher, or "No response". "...": not measured yet. |
| **Sync status** | In sync, Catching up, Waiting for host, Connection unstable, Out of sync, or Disconnected. The dot beside it is green, yellow or red with the status, and is the panel's only dot while it is expanded. |
| **Tick rate and speed** | Simulation ticks per second right now, and the game speed or Paused. |
| **Behind host** | Guests only: how many ticks this game is behind the host (0 or 1 is normal). |
| **Pacing lines** | Host only: **Guest behind**, **Easing off** and **Guest fps**, and the clickable **Ease off below** (Off, 20, 30, 45 or 60 fps), which sets when the host slows the game for a guest whose frame rate is low. |
| **Connection** | Direct or Steam. |

- **Collapse it** by clicking its title or the small boxed button at the right of the header; it shrinks to one line and remembers your choice.
- **Hide it or move it** in Mod Settings → BeaverBuddies: **Connection panel** (Expanded / Collapsed / Hidden) and **Connection panel position** (any corner).
- **Optional key:** bind **Toggle connection panel** under Options → Bindings → BeaverBuddies. It is unbound until you choose a key.
- **Chat:** below the panel, in the same box, type a message and press Enter. Everyone in the game sees it in the same order, and a player who joins later is sent the whole conversation. Bind **Chat: start typing** in the same place to jump into the box from the keyboard (also unbound until you choose a key). Each name is drawn in the color of that player's cursor: the color they chose, or the one you set for them under **Options → Player cursors**. A player who has not chosen a color gets one of their own by player number (the host orange, then blue, green, pink, purple, teal, red and lime), so players do not all start out yellow. Chat lasts for the session and is not saved with the game.

Ping is measured by the network layer (a tiny probe once a second, answered on the guest's network thread), so it means the same thing over Steam, Hamachi and direct IP, and it never touches the game simulation. Over Steam it also includes the short wait for each game to serve Steam, which the mod keeps to a few milliseconds while the game is ticking, however fast it runs. Full details: [CONNECTION-PANEL.md](CONNECTION-PANEL.md).

## How this fork improves on the original

The comparison below is against the original project's `v1.1` branch at the point this fork branched (commit `a13b1f2`, 24 August 2026). Since then the fork has changed 134 files (about 16,600 lines added, tests and documentation included). As of September 2026 the original's `v1.1` branch has not moved since that commit, so this comparison is current.

Each item says how well it is confirmed: **confirmed** means the maintainer verified it in a real multiplayer playtest; **tested** means it is covered by automated regression checks but has not been confirmed in a live session.

**Connections and Steam**

- **Steam networking rebuilt on Valve's current API.** The original used the older, deprecated API, with a fixed 128 KB/s cap on the save transfer. Failures now end with Steam's own reason in plain language, and a Steam problem can no longer stop direct-IP hosting. *Confirmed with a real Steam friend.*
- **Steam packet handling made robust.** A comment in the original's Steam read routine says it "will fail" if Steam merges several messages into one packet, and it logs "This is probably a bug!" when bytes are left over. The rebuilt transport keeps unread data between reads, checks read ranges and wakes blocked readers when a connection closes. It is tested with messages split mid-event and with a 220 KB event. Each network frame is also written under a lock, so a header and its payload can never be interleaved. *Tested.*
- **Mismatched builds refused up front.** The original only warned about a version mismatch after the save had loaded. The fork checks the game version and the exact mod build before the save is transferred, with a time limit and a clear message. *Tested.*
- **A failed multiplayer action stops safely.** Replay stops after a failed action, pending actions are discarded, the session pauses and peers are told, so two games do not quietly drift apart. Connection cleanup bugs were fixed at the same time. *Tested.*
- **The ping over Steam stays low at a high game speed.** Steam used to be served once per frame, and a ping probe waits for that at four points, so at a high speed (long frames) the ping grew with the frame length on both computers. Steam is now also served between the ticks of a frame; a simulation with the real transport shows 100 ms frames on both sides going from 323 ms to 13 ms. *Confirmed: in a playtest at a true speed 7 the ping stayed under 100 ms, where it had been 200 to 300 ms. The guest's frame length was never measured, so the cause is inferred from the game's code and a host log.*
- **A session that ends leaves the game working.** A lost connection, a failed action, a cancelled host or join and Steam's overlay closing under a dialog each used to leave the game running but ignoring the player, sometimes with no way to open the menu. The game now ends the session cleanly, says why, and keeps the menu and controls working. *Tested; the maintainer confirmed that the controls work after a disconnect, and the other cases have not been seen in the game.*
- **The host can ease off for a guest's frame rate.** A guest that keeps up in ticks but draws a few frames a second is now something the host can react to, using the middle of the guest's last five frame rate reports, dropping 10% at a time and remembering the speed that caused trouble. *Tested; an earlier version of the rule was played once (it worked but changed speed too often), the current rule has not been played.*
- **Direct connections send at once.** Over a direct (IP) connection each tick's events and the ping probes are small writes, and the system could hold each one back until the one before it was acknowledged (Nagle's algorithm), which can add up to about 200 ms; Steam connections already sent them at once, and now direct ones do too. The host also paused its game thread about 31 ms for every 32 KB of a tick's events beyond the first; now only the save sent to a joining player is paced, on that player's own connection thread. *Tested (real sockets over loopback, and the transport with a large event); not yet played.*
- **The desync dialog fits public builds and Steam guests.** It asked every player to press Enable Logging, a button that only builds with a report upload token have (public builds have none), and a Steam guest's **Reconnect (wait for Rehost)** dialled the direct-IP address in the settings (127.0.0.1 unless changed). The sentence now appears only with the button, and Reconnect joins the way the guest joined. *Tested (the dialog's decisions, and that the compiled dialog and reconnect use them); not yet played.*

**Desyncs and determinism**

- **Water no longer depends on frame rate.** The depth-limited water source advanced using render-frame time, so players at different frame rates saw different water. It now uses the simulation tick interval in multiplayer. *Confirmed: resolved a reported "badtide" desync.*
- **Water sources applied in a consistent order.** With several sources affecting one column, the installed game produced three different results across six registration orders; the fork produces one. This was not established as the cause of the badtide desync. *Tested.*
- **A fuller desync check.** Each guest used to compare only a quarter of the host's random-number state (the first of its four words). It now compares all of it, and on every tick also how many entities tick in each batch (with a few of their IDs, a different few each tick) and exactly where every walking character stands, so a beaver in a different place or an entity one game has and the other does not stops the game within two ticks instead of whenever it first changes a random draw. The desync dialog and log are the same as before; the log line says which of these differed. *Tested (the transport and the comparison, with games that differ only in one of these); not yet played.*
- **Stale saving flag fixed.** A flag could stay set after an exit save, making one player skip a moisture calculation, consistent with reported desyncs right at join. *Tested.*
- **Random-number bookkeeping made safe.** Nested random-number scopes are counted correctly and restored even when an error interrupts them. *Tested.*
- **Equal-distance demolition jobs chosen deterministically**, by persistent target IDs. *Tested; not yet confirmed in a playtest.*
- **Entity ID collisions handled explicitly.** A regenerated ID is now applied, and the game fails with a clear error if no unique ID can be found. *Tested.*
- **Tick once is off in co-op.** The game's Tick once key (period by default) pauses a running game and advances one tick when the game is paused. That tick bypassed multiplayer: it ran on the pressing player's computer only, without the shared actions, and the desync check could not see it. It could also happen during play, because in co-op a player's own game often stands still for a moment while the shared game runs (a guest waiting for the host's next tick, a host waiting for a slow guest), and the key then took that player's game for paused. In multiplayer the key now only pauses the game for everyone; pressed while the game is paused for everyone, it shows a notice instead. *Tested; not yet confirmed in a playtest.*
- **Dev mode's Ctrl keys no longer change another player's building.** With dev mode on, the game reads Ctrl ("place finished", "don't recover goods") while a placement or deletion is played, and in co-op that happens on every computer: a player holding Ctrl while someone else's building was placed or deleted got a finished building, or no recovered goods, on their computer alone. In a co-op game neither key is read any more, so both do nothing there; single player is unchanged. *Tested (the game's own placement and goods recovery code with Ctrl held); not yet played.*
- **Stuck-controls recovery.** Input state is reset after a desync, a failed action, a lost connection and when a multiplayer game loads. *Tested with a mocked device reset.*
- **A late joiner no longer misses what was done while the game waited.** A player who joins gets the save the host loaded and then only what is played after they connect, but joining stayed open until the first tick. So a building placed, an area marked or a priority changed while the game waited paused for them was missing from their game: a placement usually ended in a desync soon after, and the rest drifted apart silently. The first such action now closes joining, as unpausing does, and a player still joining is told to ask the host to save and rehost. Speed changes, pings and the other messages that change nothing leave it open. *Tested (the real server and client, and which actions close joining, against the compiled mod); not yet played.*

**Crashes**

- **Animation crash.** A path cursor that could move backward between ticks, and non-finite visual coordinates, are handled. *Confirmed.*
- **Demolition-selection crash.** Replaying an area selection that included buildings already demolished used to end the whole session. Missing ones are now skipped. *Tested; not yet confirmed in a live session.*

**Awareness and usability**

- **Player activity.** Other players' cursors, selection outlines, and Viewing / Editing labels, plus a **Player cursors** dialog (Options menu) for each player's color, size and transparency. See [PLAYER-ACTIVITY.md](PLAYER-ACTIVITY.md). *Confirmed.*
- **Steam invites and the connection panel**, described above. *Confirmed.*
- **A compact chat and a panel that lines up with the game's own.** The chat has a fixed, short height, the panel is as wide as the game's beaver counters, and it is drawn in front of the game's alerts while you type. *Tested. The layout has been seen in the game in screenshots; that the panel matches the width of the game's counters and is drawn in front of its alerts has not been checked against one.*
- **A plainer connection panel, and chat in the cursor colors.** While the panel is expanded the dot beside the sync status is its only dot. A player's row is a name and a ping (a guest sees its own ping to the host on the host's row), your own row is bold with a dash, and the collapse button has a box around it so it is not mistaken for that dash. A chat name is drawn in the color of that player's cursor, and names already written change when you change that color. A player who has not picked a color gets one of their own by player number (the host orange, then blue, green, pink, purple, teal, red and lime), so players are no longer all yellow at the start; only the name is colored, and the message stays in the normal text color. *Confirmed in real play: a screenshot of a host and a guest shows the rows, the boxed button and chat lines in the players' colors, the maintainer played 1.1.10 for more than an hour over Steam invites in large colonies (300+), and the maintainer played the color change in 1.1.11 and reported that it works without issues. Names already written changing color after you change a cursor color has not been checked.*

**Performance.** Fewer allocations from diagnostics, faster handling of the event backlog, one JSON parse per network message instead of two, and routine logging skipped unless needed. In synthetic tests, 4,000 ordered event inserts went from about 439 ms to under 1 ms, and 16 diagnostic captures stopped allocating about 85 MB. These are not frame-rate measurements. *Confirmed to play well in a two-player playtest.*

**The per-tick pass over every entity.** In co-op this mod visits every entity in a bucket before it ticks, to keep the walkers' animation in step between the players. It used to look up a component on all of them, and fold all of them into two hashes that only the detailed log prints, on every tick. In a two-player recording of a colony with 11,464 entities (361 of them walkers) that pass took about 7 ms of a 30 to 36 ms tick. It now remembers which entities walk and stopped hashing every entity; the expected saving is roughly 3 to 6 ms per tick (an estimate). It does not change what is simulated. *Tested with synthetic checks (adding and removing entities at random). Confirmed working in real play (1.1.10, before the fuller desync check): the maintainer played more than an hour over Steam invites in large colonies (300+) with it. The effect on frame rate has not been measured.* Since the fuller desync check (above) the pass hashes again, but only each bucket's size, one entity ID in eight and the walkers' positions, which a synthetic run puts at about a fifth of the old hashing's cost. *Tested (synthetic benchmark); not yet played.* It does not fix the frame rate that falls over a long session at a high speed: in that recording most of the time was spent outside this mod, in Unity's late-update phase, and what runs there is not known.

**What the fork does not change.** It does not make desyncs impossible, and it has not been tried on more than two players. Everything the original provides (multi-start maps, pings, the pause-reduction setting, hosting and joining from the menus) is still there.

## Things to know before you play

- **Everyone must run the exact same build.** The mod compares the game version and the mod's own files when someone joins. A copy someone compiled themselves can be refused even when the version number matches. If one player is on a different build over Steam, joining can look like it is hanging on "Receiving map...".
- **Other mods should match; you get a warning when they do not.** When someone joins, both players are shown which mods are on only one computer or at different versions. It is only a warning: mods that only change the interface are usually harmless, but a mod that changes the simulation (a housing mod, for example) will make the games drift apart. Settings are not compared, so settings that affect the simulation (for example **Reduce the number of forced pauses**) should match too.
- **Join before the host starts.** Nobody can join a game that has already started, or one in which a player has already placed, marked or changed something while it waited. After a desync the host uses **Save and Rehost**.
- **Desyncs can still happen.** This fork reduces known causes, not all of them.
- **Dev mode is for single player.** Most of its tools (the instant unlock with Ctrl-click, a construction site's Finish now, deleting beavers or any object, the debug panels) change only the computer they are used on and desync a co-op game. A notice says so when dev mode is turned on in a co-op game. Its Ctrl keys for placing a building finished and for not recovering goods are off in co-op.
- **Tested with two players**, on Windows, with the Steam version of Timberborn 1.1.2.4. Other stores, platforms and larger groups have not been tested by this fork. Steam invites need the Steam version of the game.
- **"Post Bug Report" does not upload in this fork's builds.** The original's automatic upload needs an access token that these builds do not contain. If you hit a problem, keep the `Player.log` files from both players (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn`). **Always Use Detailed Logging** captures more but costs some performance; any diagnostic ZIPs are saved in the `BeaverBuddiesDiagnostics` folder next to the log.
- **Only one BeaverBuddies at a time.** This fork and the Workshop version use the same mod ID.
- **New text is English only.** Other languages fall back to English for the strings added by this fork.
- **Not a Workshop mod.** Update by downloading a new release and replacing the folder; there is no automatic update.

## Testing and verification

The 1.1.11 validation run passed **287 checks**: **215** in `StabilityTests` (network transport, the Steam transport against a simulated Steam network, protocol parity between direct and Steam connections, player activity, ping measurement and how it depends on frame length over a simulated Steam network, the panel and its layout, the guest catch-up rule, the mod list warning, the host's speed limit choice, pacing and frame rate easing, guarded message handlers, ending a session, the chat box, the walker trace, the entity pass's memory of which entities walk and the default colors of players), **69** in `RuntimeChecks` (the compiled mod running against the game's own assemblies: random-number scopes, water simulation, demolition, input recovery, the menu after a session ends, desync traces, the mod list), and **3** Python checks (water snapshot comparison and the walker trace comparison). Both Steam and non-Steam builds compile with no warnings.

These checks cannot start Unity or prove full multiplayer determinism, and they need the game installed locally (no proprietary game files are included in this repository). See [StabilityTests/README.md](StabilityTests/README.md) for how to run them. The maintainer's real playtests, described above, are what confirm behavior in the live game.

## Credits and license

Every change in this fork is listed in [STABILITY-CHANGELOG.md](STABILITY-CHANGELOG.md).

Thank you to the original BeaverBuddies authors and contributors, whose work this fork builds on. Their license (GPL-3.0) and authorship are preserved in [License.txt](License.txt) and the repository history.

*Below the line is the original project's developer README, kept as it was. Its badges, Workshop, mod.io, wiki and Discord links, and the clone address in "How to Build", refer to the original project, not to this fork. To build this fork, clone this repository instead.*

---

[![Last commit](https://img.shields.io/github/last-commit/thomaswp/BeaverBuddies?label=Last%20commit&color=lightgray)](https://github.com/thomaswp/BeaverBuddies/commits)
[![License](https://img.shields.io/github/license/thomaswp/BeaverBuddies?label=License&color=gray)](https://github.com/thomaswp/BeaverBuddies/blob/master/License.txt)
[![Timberborn 1.0](https://img.shields.io/badge/Timberborn_1.0-compatible-peru)](https://mechanistry.com)
[![Discord mod thread](https://img.shields.io/badge/Discord-mod_thread-mediumpurple)](https://discord.com/channels/558398674389172225/1203786573142032445)  
[![Steam Workshop](https://img.shields.io/badge/Steam_Workshop-available-royalblue)](https://steamcommunity.com/sharedfiles/filedetails/?id=3293380223)
[![mod.io](https://img.shields.io/badge/mod.io-available-limegreen)](https://mod.io/g/timberborn/m/beaverbuddies)

BeaverBuddies is a mod to allow multiplayer co-op in Timberborn.

> [!IMPORTANT]
> **If you would like to use the BeaverBuddies mod**, please see [the setup instructions in the wiki](https://github.com/thomaswp/BeaverBuddies/wiki)! This README is for developers.

## Contributing

We appreciate your help! To get started working on BeaverBuddies, see [the guide in the wiki](https://github.com/thomaswp/BeaverBuddies/wiki/Contributing).

## How to Build BeaverBuddies

1. Clone this repo `git clone git@github.com:thomaswp/BeaverBuddies`.
2. Set up DotNet C#.  
   For Windows, download & install [Visual Studio community edition](https://visualstudio.microsoft.com/vs/community).  
   For Mac, either run `brew install dotnet` or download & install [DotNet SDK](https://dotnet.microsoft.com/en-us/download).
3. Build the project.  
   For Visual Studio, open the solution & hit Ctrl+Shift+B.  
   For DotNet SDK, go to the BeaverBuddies directory & run `dotnet build`.  
   You may get a few "directory not found" errors. To fix these, open `BeaverBuddies/BeaverBuddies/env.props` and adjust the environmental variables there to point to your Timberborn installation & the necessary mods.

Building on Linux is similar to on Mac.

## How to Test Your Build

1. Make sure your project has been built with no errors.
2. Confirm that the mod files were copied to your Timberborn mods folder (e.g. `Documents/Timberborn/Mods/BeaverBuddies`.
3. Launch Timberborn and select the BeaverBuddies mod on the mod selection screen.  
   There may be multiple BeaverBuddies mod entries. The one with a "folder" icon next to it is your local build, select it.
