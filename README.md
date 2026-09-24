# BeaverBuddies Stability Fork

Multiplayer co-op for Timberborn: build one colony together in real time. With **Steam friend invites**, an
**in-game connection panel** and a long list of crash and desync fixes.

[![Latest release](https://img.shields.io/github/v/release/timbermods/BeaverBuddies-Stability-Fork?label=latest&labelColor=172620&color=e0812f&style=flat-square)](https://github.com/timbermods/BeaverBuddies-Stability-Fork/releases/latest) ![Timberborn 1.1.2.4](https://img.shields.io/badge/Timberborn-1.1.2.4-2a4034?labelColor=172620&style=flat-square) ![Tested on Windows with the Steam version](https://img.shields.io/badge/tested_on-Windows_%2B_Steam-2a4034?labelColor=172620&style=flat-square) [![GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-2a4034?labelColor=172620&style=flat-square)](License.txt)

**[Download](https://github.com/timbermods/BeaverBuddies-Stability-Fork/releases/latest)** · [Install](#install) · [Website](https://timbermods.github.io/BeaverBuddies-Stability-Fork/) · [Changelog](STABILITY-CHANGELOG.md) · [More mods from Timbermods](https://timbermods.github.io/)

> [!NOTE]
> **Stable and feature-complete.** 1.1.15 has been played and works. The fork still gets fixes and updates for new
> Timberborn versions, but no new features: those go into
> [Timber Together](https://github.com/timbermods/TimberTogether), which is built on this fork and
> can also give each player a colony of their own. Install one or the other, never both.

An independent fork of [BeaverBuddies](https://github.com/thomaswp/BeaverBuddies) by thomaswp and contributors, the
original multiplayer mod. Everything the original does still works: one shared colony, each player with their own
camera, multi-start maps, map pings, and hosting and joining from the game's menus. Please report problems with this
fork [here](https://github.com/timbermods/BeaverBuddies-Stability-Fork/issues), not to the original project.

## What you get

- **Steam invites.** Invite a friend from Steam's overlay: no port forwarding, no Hamachi. Direct IP works too.
- **A connection panel.** Who is connected, each ping, whether you're in sync and the tick rate, with a chat box.
- **Teammates' cursors.** See where your friends point and what they're editing, in colors you choose.
- **Fewer crashes and desyncs.** Fixes for water, animation, random numbers, saving, demolition, Wonders, input and
  the network. A desync can still happen.
- **A low ping at high speed.** Over Steam the ping stays low even at speed 7.
- **Easing off for a slow guest.** The host can slow the game a little while a guest falls behind.
- **Full speed in a large colony**, if the host chooses it (off by default).
- **A speed boost in the chat**, which adds to the game speed for everyone.
- **Clear errors.** A different build is refused before the save is sent, differing mods are flagged, and a failed
  connection says why.

## Install

**You need:** Timberborn **1.1.2.4**, with the **Harmony** and **Mod Settings** mods enabled.

1. Download `BeaverBuddies-Stability-Fork-1.1.15.zip` under **Assets** on the [latest release](https://github.com/timbermods/BeaverBuddies-Stability-Fork/releases/latest) (not the "Source code" archives). <!-- latest -->
2. **Close Timberborn.**
3. Extract the zip and copy the `BeaverBuddies-Stability-Fork` folder into `Documents\Timberborn\Mods`.
4. **Keep only one BeaverBuddies.** Unsubscribe from the Workshop BeaverBuddies and delete any other copy, including an
   old `BeaverBuddies-StabilityPreview` folder. They share a mod ID and conflict.
5. Start Timberborn and enable **BeaverBuddies - Stability Fork** (version 1.1.15) in the mod list. <!-- latest -->
6. **Every player installs the same download and runs the same game version.** A mismatch is the most common cause of
   trouble.

To update, replace the folder with the new download; every player updates together. This fork is on GitHub Releases
only, not the Workshop.

**Settings:** open **Mods** (on the main menu, or Esc in a game) and click the settings button on
**BeaverBuddies - Stability Fork**. The [install guide](https://timbermods.github.io/BeaverBuddies-Stability-Fork/install.html#settings)
lists every setting.

## Host and join

**Host:** Load game → pick a save → **Host co-op game**. Choose **Invite Friends** to invite over Steam, or give
friends your IP address (port **25565**, forwarded on your router, or a VPN such as Hamachi). When everyone is in the
list, choose **Start Game**.

**Join:** accept the Steam invite (Steam starts the game if it's closed), or choose **Join co-op game** on the main
menu and enter the host's IP address.

**Join before the host unpauses.** After that, or once anything is built or marked, nobody else can join: the host
saves and hosts again.

**If a desync happens,** the host chooses **Save and Rehost**, then the others choose **Reconnect (wait for
Rehost)**. Keep backups of your saves.

Steam needs every player online in Steam and owning Timberborn there. More in [STEAM-INVITES.md](STEAM-INVITES.md).

## The connection panel

<img src="docs/assets/connection-panel.png" width="280" alt="The connection panel as the host sees it: In sync, the players with their pings, the tick rate and speed, the host's pacing lines, a Steam connection and a short chat.">

It sits top left during a multiplayer game: players, pings, sync status and speed, with the chat and speed boost
below. Click its title to collapse it. Details: [CONNECTION-PANEL.md](CONNECTION-PANEL.md). Teammates' cursors and
how to restyle them: [PLAYER-ACTIVITY.md](PLAYER-ACTIVITY.md).

## Good to know

- **Other mods should match.** You're warned when they don't. A mod that changes the simulation makes the games drift
  apart.
- **Settings that affect the simulation should match too**, such as **Reduce the number of forced pauses**.
- **Dev mode is for single player.** Most of its tools change only your game and desync a co-op game.
- **Tested with two players** on Windows, with the Steam version of Timberborn. Other setups haven't been tried.
- **Text this mod adds is English only.**

## Reporting a problem

Open an [issue](https://github.com/timbermods/BeaverBuddies-Stability-Fork/issues) with what you were doing and the
`Player.log` from **every** player. It's in `%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn`. This fork's
builds can't upload bug reports from the game, so attach the files yourself.

## For developers

Building, the checks and how the networking works: [DEVELOPING.md](DEVELOPING.md). Every change, with how it was
checked: [STABILITY-CHANGELOG.md](STABILITY-CHANGELOG.md).

## Credits and license

Built on [BeaverBuddies](https://github.com/thomaswp/BeaverBuddies) by thomaswp and contributors, who designed the
multiplayer this all rests on. GPL-3.0 ([License.txt](License.txt)); authorship is preserved in the repository
history. Maintained by [Timbermods](https://github.com/timbermods).

An unofficial community mod for Timberborn. Not affiliated with or endorsed by Mechanistry.
