# Steam invites

Invite a Steam friend into your game from Steam's overlay: no port forwarding, no Hamachi. Joining by IP works
alongside it.

## Host

1. Load game → pick a save → **Host co-op game**.
2. Choose **Invite Friends** and pick your friend in Steam's overlay. If nothing opens right after you host, wait a
   moment and click again: the Steam lobby is still being set up.
3. When your friend appears in the list of connected players, choose **Start Game**.

**Enable Steam Networking** must be on in Mod Settings. It is by default.

## Friend

- Accept the invite from Steam's notification or overlay. If Timberborn is closed, Steam starts it and joins for you.
- With the host's **Allow Friends to Join Directly via Steam** on (the default), **Join Game** on the host's entry in
  your Steam friends list works too.
- An invite to a game that has started says so. Ask the host to save, host that save again and send a new invite.

## Good to know

- Everyone must be online in Steam and own Timberborn there, with the same mod download and game version.
- **Join before the host unpauses.** After that, or once anything is built or marked, nobody else can join.
- **Only Steam friends get in.** The host accepts players from its friends-only Steam lobby only, so a stranger who
  knows your Steam ID can't connect.
- Steam connects players directly when it can, and relays through its network otherwise. Valve documents that relaying
  keeps players' IP addresses hidden from each other.
- **After a desync**, the host chooses **Save and Rehost** and guests choose **Reconnect (wait for Rehost)**. A Steam
  guest rejoins the host's new game when Steam shows it; otherwise, accept the host's new invite.
- **If Steam won't connect**, the message says why, with Steam's own reason; so does `Player.log`. Host by direct IP or
  Hamachi instead: Steam problems never stop direct-IP hosting.
- Text this mod adds is English only.

How the Steam connection is built, and how it was tested: [DEVELOPING.md](DEVELOPING.md#steam-networking).
