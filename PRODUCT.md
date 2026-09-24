# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Pairs (and small groups) of Timberborn players who want to build **one colony together** in real time. Mostly
non-technical. Usually one person, the future host, finds the mod (through the timbermods org site, the MultiColony
site, a friend, a forum or Discord post, or a search for "Timberborn multiplayer") and decides whether it is worth
setting up. Then they send a link or the zip to a friend, who arrives cold and needs only the install steps. Many of
them already know the original BeaverBuddies from the Steam Workshop and want to know what this fork does differently,
and whether it can be installed alongside the original (it can't).

Returning visitors come back with a problem: a join refused for a build mismatch, a mod-list warning, a desync, a
Steam invite that does nothing, lag at a high speed, or a connection panel they can't find. The Troubleshooting page
and the FAQ are for them. Some arrive from the GitHub issue template, which asks whether they read the website first.

## Product Purpose

The website for the **BeaverBuddies Stability Fork** (repo and releases:
https://github.com/timbermods/BeaverBuddies-Stability-Fork), a multiplayer co-op mod for Timberborn. It is an
independent fork of thomaswp's original **BeaverBuddies**. It keeps everything the original does: players build one
shared colony at the same time, each with their own camera and interface; multi-start maps; map pings; hosting and
joining from the in-game menus. On top of that it adds:

- **Steam friend invites** on Valve's current networking API, so no port forwarding and no Hamachi are needed.
- **An in-game connection panel with a chat box.** It shows players, ping, sync status, tick rate and speed, and the
  host's pacing. At the top of the chat there is a speed boost.
- **Player cursors, selection outlines and Viewing / Editing labels**, each player's cursor styled in the Player cursors
  dialog.
- **A long, documented list of crash and desync fixes**, each marked Confirmed or Tested.
- **Clear failures:** a different build is refused before the save is sent, differing mod lists are flagged, and a
  failure ends with a plain-language reason.

How it works, in one line: every player runs an identical copy of the game from the same save; the host decides which
tick each action happens on. That is why every player needs the exact same download.

Success, in order:
1. **Understand it:** one shared colony with friends, what this fork adds over the original, and that the players'
   builds must be identical.
2. **Install it right:** every player installs the same zip and the same game version, with Harmony and Mod Settings.
   Only one BeaverBuddies is installed (the Workshop copy is unsubscribed).
3. **Use it:** host, invite or join, read the connection panel, recover from a desync (Save and Rehost, then Reconnect
   (wait for Rehost)).
4. **Report problems well:** an issue on this repo (not the original's) with both players' `Player.log`, the versions,
   Steam or direct IP, and what they were doing.

## Positioning

- **Versus the base game:** Timberborn is single player. This mod makes it co-op on one shared colony.
- **Versus the original BeaverBuddies** (Steam Workshop and mod.io, by thomaswp and contributors): same design and the
  same shared colony. The fork adds working Steam invites on the current API, the connection panel and chat, player
  activity, the speed boost and the specific, documented stability fixes. The original's `v1.1` branch has not moved
  since the fork branched (commit `a13b1f2`, 24 August 2026). The two share a mod ID: never both at once, and a
  session can't mix them. All credit for the multiplayer design stays with the original.
- **Versus BeaverBuddies MultiColony** (https://github.com/timbermods/BeaverBuddies-MultiColony, same maintainer): built
  on this fork and includes everything it does. With separate colonies off it plays one shared colony, and it can also
  give each player a colony of their own. It is still in beta and gets the new features. The Stability Fork is the
  **stable, feature-complete** choice for a shared colony. Never enable both.
- **With MixedStorage** (a sibling timbermods mod): works in co-op; every player needs the same MixedStorage version.

## Operating Context

- **Current release: 1.1.15** (tag `v1.1.15`, 2026-09-23), a full release marked **Latest** on GitHub, not a
  pre-release. It has no new features: bug fixes taken from MultiColony, on top of 1.1.14's fixes from a review of
  MultiColony's shared-colony code. **1.1.13 was the final feature release.** The fork is feature-complete: it will still be updated for new Timberborn versions and
  for bugs, and new features go into MultiColony.
- **Game:** built and tested against Timberborn **1.1.2.4**; the manifest requires at least 1.1.0.0. Tested only on
  **Windows with the Steam version**, with **two players**. Other stores, macOS, Linux and larger groups are untested.
  Steam invites need the Steam version.
- **Requirements:** the **Harmony** and **Mod Settings** mods (the manifest's `eMka.ModSettings` 1.1.0.0 or newer).
  Distributed through **GitHub Releases only**. The Workshop and mod.io pages belong to the original project.
- **Install facts:**
  - Download `BeaverBuddies-Stability-Fork-<version>.zip` under **Assets** (not "Source code"). The release also has
    `<version>-SHA256SUMS.txt`.
  - Close Timberborn, then copy the `BeaverBuddies-Stability-Fork` folder into `Documents\Timberborn\Mods`. It should
    end up as `Mods\BeaverBuddies-Stability-Fork\version-1.1\manifest.json`.
  - Enable **BeaverBuddies - Stability Fork** in the mod list.
  - Unsubscribe from or remove the Workshop BeaverBuddies and any other copy. They share the mod ID `beaverbuddies`.
  - Check it loaded: the main menu has **Join co-op game**, and `Player.log` contains `BeaverBuddies v1.1.15 is loaded!`.
  - Updating: everyone updates together; there is no automatic update.
  - Upgrade fact to keep: delete an old `BeaverBuddies-StabilityPreview` folder from an earlier download.
- **What players meet in game** (names exactly as in the game):
  - **Host co-op game** → **Invite Friends** (Steam) or share an IP (port **25565**) → **Start Game**. Guests accept the
    Steam invite, or use **Join Game** from Steam's friends list, or **Join co-op game** on the main menu for IP.
    Nobody can join after Start Game, or once something was placed, marked or changed while the game waited.
  - After a desync: the host chooses **Save and Rehost**, then guests choose **Reconnect (wait for Rehost)**.
  - **The connection panel** (top left by default). Its statuses: In sync, Catching up, Waiting for host, Connection
    unstable, Out of sync, Disconnected. Ping is normal text at 80 ms or less, yellow up to 160, red above. It shows
    Behind host (guests) and the host's pacing lines with **Ease off below** (Off / 20 / 30 / 45 / 60 fps). Below it,
    the chat and the speed boost (**-** / **+** in steps of 0.5, from -6.5 to +23; the game runs between 0.5x and
    30x).
  - **Options → Player cursors:** a card per player (Color, Size 50–300%, Transparency 0–90%) and **You, in the chat**.
  - **Mod Settings → BeaverBuddies:** Enable Steam Networking (On), Allow Friends to Join Directly via Steam (On),
    Player activity indicators (On), Ping Display Name, Ping Color (Yellow), Connection panel (Expanded / Collapsed /
    Hidden), Connection panel position, Ease off when a guest drops below this frame rate (Off), Remove the large
    colony speed limit (Off), Reduce the number of forced pauses (Off / Menu only / Never auto-pause), Port (25565),
    Client Connection Address, Always Use Detailed Logging (Off).
  - **Options → Bindings → BeaverBuddies:** Ping Location, Toggle connection panel, Chat: start typing, all unbound by
    default.
- **Reporting:** issues at https://github.com/timbermods/BeaverBuddies-Stability-Fork/issues (templates: bug report,
  feature request, question), not the original project.
  - Attach both players' `Player.log`, and `Player-prev.log` for an earlier session. They are in
    `%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn`.
  - After a desync with detailed logging on, also send the `BeaverBuddiesDiagnostics` folder next to the logs.
  - **Post Bug Report** uploads nothing in this fork's builds (they have no upload token).

## Capabilities and Constraints

**Stack and hosting:**
- The site is plain static HTML, CSS and a little vanilla JS in `docs/` on `main`, with no build step. Four pages:
  `index.html` (features), `install.html`, `troubleshooting.html`, `faq.html`. Assets are `assets/style.css`,
  `release.js`, `site.js` (the optional Copy buttons; install page only), `favicon.svg`, `connection-panel.png`, the
  procedural `log-round.webp` and `log-round-mark.webp` (made by `make_log.py`) and the Zilla Slab fonts.
- The site is dark only (blued-steel ground). It self-hosts Zilla Slab (OFL, `assets/fonts/`) for headings and loads no
  external fonts, scripts or images. There is no `404.html`.
- GitHub Pages ("legacy" build) serves `main:/docs` at https://timbermods.github.io/BeaverBuddies-Stability-Fork/, and
  it goes live about a minute after a merge to `main`. `docs/.nojekyll` must stay.
- Every change goes through a **PR → merge** on `main`; nothing is pushed straight to `main`. It is one of the
  timbermods sites (MultiColony's and MixedStorage's are siblings) and must stay fast, light and mobile-friendly.

**Site tests and CI: none check the site.** The only workflow, `.github/workflows/tests.yml` ("Stability tests"), runs
`StabilityTests` (C#) on every push and PR. Nothing in `StabilityTests/`, `RuntimeChecks/` or the workflow reads
`docs/`. So these conventions are held by hand and by the release checklist, not by a test:
- **Every page loads `assets/release.js`** with the same attributes. `data-repo` is
  `timbermods/BeaverBuddies-Stability-Fork` and `data-asset` is `^BeaverBuddies-Stability-Fork-[\d.]+\.zip$`.
- **The HTML always holds working values**: buttons and links to `/releases/latest`, and the version last written by
  hand (`1.1.15`). `release.js` then replaces `data-release="version" | "tag" | "asset-name"`. The page must still
  work with no script, no network or a rate-limited GitHub API. Link to `/releases/latest` (this repo has a real
  Latest release), never to a fixed tag.
- **`data-release-pinned="<version>"`** marks text written for one version. `release.js` adds a "written for X, the
  newest release is Y" note when a newer release exists. It is on the home page's status plate.
- The Download buttons use `data-release-href="download"`; their fallback `href` stays `/releases/latest`.
- **`docs/assets/connection-panel.png` is embedded by the repo README.** Renaming or moving it breaks the README. The
  issue template `question.md` links the site's root URL.
- **Per release** the release checklist edits: the home page's status plate (`div.status[data-release-pinned]`), its
  two Download buttons and the stability scorecard (items and the Confirmed / Tested counts); the version fallbacks on
  install and troubleshooting (the FAQ states no version number); and the README status note.
- **`docs/assets/release.js` is shared across timbermods sites** (byte-identical to MixedStorage's copy). **Replace it
  with a newer shared copy, never edit it.**

**Terminology** (exact, as in game and README):
- The mod is **BeaverBuddies Stability Fork**; in the mod list it is **BeaverBuddies - Stability Fork**. The original
  is **BeaverBuddies** (by thomaswp); the sibling is **BeaverBuddies MultiColony**.
- Players are the **host** and **guests**; the colony is **one shared colony**.
- Use the in-game names: **Host co-op game, Invite Friends, Start Game, Join co-op game, Save and Rehost, Reconnect
  (wait for Rehost), connection panel, Ease off below, speed boost, Player cursors, You, in the chat, Player activity
  indicators, Viewing / Editing**. Setting names as listed in Operating Context.
- Label each fix **Confirmed** (verified in a real multiplayer playtest) or **Tested** (automated checks only).
  "Played" means the maintainer played it in the real game.

**Honest status (what has and hasn't been played):**
- 1.1.10 was played extensively in multiplayer: over an hour over Steam invites in 300+ beaver colonies, with no
  desyncs recorded. The 1.1.11 color change was played and works. The maintainer played 1.1.13 (which includes
  1.1.12's changes) and reports it stable and working; the situations the 1.1.12 fixes target were not set up on
  purpose.
- **1.1.15 (which includes 1.1.14's fixes) has been played and works** (the maintainer, 2026-09-24). Its fixes are also
  covered by automated checks (464 pass: 276 StabilityTests, 185 RuntimeChecks, 3 Python). The particular situations
  those fixes target were not all set up on purpose (see "Tested only" below).
- Confirmed in play: Steam invites, the water frame-rate fix (the "badtide" desync), the animation crash, the low ping
  at a true speed 7, the controls working after a disconnect, the panel and chat colors, player activity, the speed
  boost and own chat color, the large colony speed limit (played in 1.0.8).
- Tested only: the current Ease off rule, the demolition-selection fix, the Wonder timing, the fuller desync check,
  the network reader, the direct-guest send lanes, planting across layer views, Tick once off in co-op, dev mode's
  Ctrl keys, joining closing on the first action and before the first tick is sent, the shared pump, valve and dev
  generator controls, unlocks checked when played, the detailed-logging trace cap, a guest leaving over an unreadable
  action while the others play on.
- **Sources of truth:** `README.md`, `STABILITY-CHANGELOG.md`, `CONNECTION-PANEL.md`, `STEAM-INVITES.md`,
  `PLAYER-ACTIVITY.md`, the in-game strings (`BeaverBuddies/Localizations/enUS_BeaverBuddie.csv`) and the release
  notes. Where the site and these disagree, flag it; don't guess.

## Brand Commitments

- **Voice:** a fellow player explaining a useful mod: clear, exact, practical, never hype. It says what is confirmed
  and what isn't in the same calm tone, and asks for backups and reports without alarm.
- **No official Timberborn logos or key art.** The game's own item and goods icons are allowed where a UI replica uses
  them, credited as Timberborn's.
- **The original project's art is not this fork's brand.** `Media/` (the BEAVERBUDDIES wordmark, `logo.jpg`,
  `Icon-full.png`, `thumbnail-large.png`) is thomaswp's, committed by him. If any of it is shown, it is credited to the
  original project. The site's own mark is a log round with a two-person saw across it (`favicon.svg`, and inline in the header).
- **License:** GPL-3.0 (`License.txt`). The original's authorship and license are preserved.
- **Required credits, clearly and on every page:**
  - An independent fork of **BeaverBuddies** (https://github.com/thomaswp/BeaverBuddies) by **thomaswp and
    contributors**, whose multiplayer design this builds on. All credit for the multiplayer design belongs to them.
  - Maintained by **Timbermods** (https://github.com/timbermods).
  - An **unofficial community mod** for Timberborn, not affiliated with or endorsed by Mechanistry.
  - Report problems with this fork to this repo, not the original.

## Evidence on Hand

- `docs/assets/connection-panel.png` (383×558): a **real in-game screenshot** of the connection panel as the host sees
  it during a Steam session. It shows In sync, a guest at 19 ms, 11.7 ticks/s, speed 7x, the pacing lines and two chat
  lines. It was taken on 1.1.10, when the whole chat line took the player's color (now only the name does). It
  predates the speed boost row, so it does not show it.
- `Media/`: the original project's promotional art (thomaswp's; see Brand Commitments). `Icon-full.png` and
  `thumbnail-large.png` show two monitors with in-game views and the captions "Now with Steam networking!" and "Now
  with Update 7 Support!" (the latter is out of date). `logo.jpg` is the wordmark over an illustration of three
  beavers on a raft. `IconBG.png` and `Icon.pdn` are the icon sources. The release zip also carries a `thumbnail.png`.
- Written evidence the site can cite: the playtest record (1.1.10 for over an hour over Steam invites in 300+
  colonies; the ping under 100 ms at a true speed 7, down from 200–300 ms; the badtide desync resolved), the 464
  automated checks, and the diff since the original (196 files, about 28,100 lines, by 1.1.15).
- **Does not exist, and must not be faked:**
  - Screenshots of player cursors or Viewing / Editing labels, the Player cursors dialog, the speed boost row, Mod
    Settings, the mod-list warning or a desync dialog.
  - Gameplay clips, a current panel screenshot showing the speed boost, and anything showing more than two players.
  - Testimonials, quotes, player counts, download numbers or press.
  - Leave marked slots for the maintainer's own shots instead.

## Product Principles

1. **Same build, one BeaverBuddies.** Every page helps all players end up on the exact same download and game
   version, with the Workshop copy removed. Those steps are never buried.
2. **Stable and finished, and says so.** This is the feature-complete choice for a shared colony. Point to MultiColony
   for new features without selling against this mod.
3. **Confirmed or Tested, never blurred.** Each claim carries how it is known. Say plainly what has not been played,
   without scaring people off, and describe the mod as it is now; version history lives in the changelog.
4. **Credit the original first.** thomaswp's BeaverBuddies is the foundation; the fork adds to it and says exactly
   what it adds.
5. **Recovery is part of the product.** Desyncs, refused joins and dropped connections have a clear next step, and a
   good report (both players' logs) is easy to send and welcome.
