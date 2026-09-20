# Connection panel

A small panel in the corner of your screen during a multiplayer game. It shows who is
connected, how good each connection is, whether you are in sync, and how fast the
simulation is running, and below that it has a chat box for the players in the game. It can be
collapsed to a single line or hidden completely.

## What it shows

**Collapsed** (one line): a colored dot, the number of players, and one ping.

```
o  3 players  42 ms                                   +
```

**Expanded:**

```
o  Multiplayer                              Host     -
o  In sync
-------------------------------------------------------
o  Kyler                                 You / Host
o  Sarah                                       42 ms
o  Bob                                        190 ms
-------------------------------------------------------
Tick rate   1.7 ticks/s
Speed       1x
Connection  Direct
```

| Item | Meaning |
| --- | --- |
| **Status** | *In sync* is normal. *Catching up* (guests): this game is a few ticks behind the host. *Waiting for host* (guests): nothing has arrived from the host for a moment. *Connection unstable*: someone has stopped responding for five seconds. *Out of sync*: a desync was detected. *Disconnected*: the session has ended. |
| **Players** | Everyone in the session, host first. **You** marks your own row. |
| **Ping** | Round-trip time to the host in milliseconds. Green dot: 80 ms or less. Yellow: up to 160 ms. Red: more, or **No response**. Grey `...`: not measured yet. |
| **Tick rate** | Simulation ticks per second right now, averaged over about three seconds. Around 1.7 at normal speed; it rises with game speed and drops to 0 when paused. |
| **Speed** | The current game speed, or Paused. |
| **Behind host** | Guests only: how many ticks behind the host this game is. Should sit at 0 or 1. |
| **Guest behind** | Host only: how many ticks behind the slowest guest was at its last report, about once a second. Shown once a guest running 1.0.4 or newer has reported. |
| **Easing off** | Host only, and only while it applies: the share of the chosen speed the host is running at because a guest cannot keep up, such as "75% of speed", or "75% (frame rate)" when it is a guest's frame rate that is holding it back. It returns to full speed by itself. Reads **waiting for a guest** while the host stands still for a guest more than 60 ticks behind (1.0.6). |
| **Guest fps** | Host only: the lowest frame rate any guest reported, about once a second. A guest reports nothing while its game window is in the background. |
| **Ease off below** | Host only. Click it to choose a guest frame rate floor: Off, 20, 30, 45 or 60 fps. While a guest stays below the floor the host slows the game a little, and speeds back up by itself. The same choice is in the mod settings. |
| **Connection** | How players are connected: Direct (IP, including Hamachi or port forwarding) or Steam. |

The host sees every guest's ping. A guest sees their own ping in the pill and the other
players' pings **to the host**, which is the connection that matters for keeping in sync.

## Showing, collapsing and hiding

- **Click the title** to collapse or expand the panel. Your choice is remembered.
- **Mod Settings -> BeaverBuddies -> Connection panel:** Expanded, Collapsed or Hidden.
- **Mod Settings -> BeaverBuddies -> Connection panel position:** top left (default), top
  right, bottom left or bottom right.
- **Options -> Bindings -> BeaverBuddies -> Toggle connection panel:** an optional key to
  hide and show the panel. It is unbound until you choose a key.
- **Options -> Bindings -> BeaverBuddies -> Chat: start typing:** an optional key that shows
  the panel if it was collapsed or hidden and puts the cursor in the chat box. It is unbound
  until you choose a key; clicking the box always works.

Collapsing the panel hides the chat with it. While it is collapsed, the header shows a yellow
**N new** for messages other players sent that you have not seen; expanding the panel clears it.

The panel is docked into the game's own interface, so it scales with your UI scale and
does not overlap other panels in the same corner. It appears only in multiplayer games.

Its width is the width of the game's own beaver counters (the population panel) above it,
measured when the panel is shown, so it lines up with them and follows your UI scale. In a
corner without those counters it follows the nearest panel above it, and if there is none it is
as wide as its text needs (between 210 and 300). The width it followed is written to `Player.log`.

## Chat

Below the connection panel, inside the same rectangle, is a chat box: the messages, and a box to
type in. It has a fixed, compact height (about five lines and the box), so it does not grow with
the rest of the panel, and it appears whenever the panel is expanded, in a multiplayer game only.

```
o  Multiplayer                              Host     -
o  In sync
-------------------------------------------------------
o  Kyler                                 You / Host
o  Sarah                                       42 ms
-------------------------------------------------------
Tick rate   1.7 ticks/s
Speed       1x
-------------------------------------------------------
Sarah: anyone want to build a second dam?
Kyler: yes, upstream of the farm
Sarah: on it
[ Type a message...                                   ]
```

- **Send:** click the box, type, press **Enter**. Enter sends and leaves the cursor in the box
  so you can keep talking. **Enter on an empty box, Esc, or a click on the game itself** gives
  the keyboard back to the game.
- **Typing does not play the game.** While the cursor is in the box the game's own hotkeys are
  switched off (the game does this for its own text boxes), so a typed W does not move the camera.
- **In front of the game's alerts.** The game draws its alerts (for example "Nothing to do in
  range") at the bottom of the screen, and a tall panel can reach them. While the cursor is in
  the chat box, the panel is drawn in front of them so they cannot cover what you are typing, and
  it goes back when the cursor leaves. This only changes what is drawn on top.
- **Who said what:** each line reads `Name: message`, the name in that player's **Ping Color**
  (a very dark color is lightened so it can be read on the dark panel) and using the same
  **Ping Display Name** as cursors and pings. Chat lines have no "(Host)" or "(P2)" tag, so two
  players who both keep the default name and color look alike: set your own **Ping Display Name**
  and **Ping Color** in Mod Settings.
- **One order for everyone.** The host numbers every message and sends it to every player,
  the sender included, so everyone sees the same conversation in the same order. Your own
  message appears when the host has it, normally at once.
- **Full history.** The host keeps the whole conversation and sends all of it to a player who
  joins later, so they see what they missed. A session that goes past 2,000 messages drops the
  oldest ones for everybody.
- **Per session.** Chat is not saved with the game. Reloading a save or rehosting starts with
  an empty chat.
- **Scrolling:** the log follows new messages, unless you scroll up to read older ones. Use the
  mouse wheel over it.
- **Plain text, one line, up to 200 characters.** Line breaks and control characters are
  removed, and so are `<` and `>` so nobody can put formatting into anyone else's screen.
- **No flooding.** The host allows a player a burst of six messages and then two a second;
  anything faster is dropped. The box also waits a moment between your own messages.

The chat is laid out over the space below the panel instead of inside it, so a long message
wraps to the panel's width and can never make the panel wider.

## How ping is measured

Once a second the host sends each guest a tiny probe, and the guest answers on its network
thread, not its game thread. The reply also carries the guest's current tick and frame rate,
which is where **Guest behind** and **Guest fps** come from. Over Steam, data still only moves
while a player's game thread is serving Steam, so the number is the network plus a short wait
at each end. That wait is the gap between two pumps: at most a millisecond during the
simulation, and the length of the non-simulation part of a frame outside it. Serving Steam
only once per frame made it grow with the game speed, because at a high speed most of a frame
is simulation; a direct connection has no such wait. The host smooths the results (so a
single spike does not jump around) and publishes a short roster that every guest receives. Names come from the same **Ping Display Name**
players already use for cursors and pings; if a player has activity indicators turned off,
they appear as "Player N".

## It cannot affect the game

Probes, the roster and chat all use the same separate lane as cursor activity. They are never
part of the replay script or the desync hash, are handled before they can reach the game's event
queue, are never sent to a guest who is still joining (a joining guest gets its save and state
first, then the chat history), and are validated on arrival; a malformed frame is ignored and
never ends the session. The panel only reads, and chat sends no gameplay event. If the panel
ever fails, it disables itself and the game continues; if only the chat fails, the rest of the
panel carries on.

## Validation

`dotnet run --project StabilityTests` (199 checks) covers:

- the round-trip tracker: smoothing, jitter, ignored duplicate, unknown and expired
  replies, and silence measured from the last reply;
- the wire format, including rejection of every malformed roster and probe frame;
- real host and guest sessions: pings measured for each guest, a guest with 80 ms of
  injected delay reading slower than a prompt one, every guest receiving the roster with
  its own id, status traffic changing neither the hash, the event script nor tick progress,
  bad frames being ignored, a departed guest leaving the roster, and no status traffic
  before a joining guest has its save, state and init frames;
- the panel's wording and states without a game: ping colors and boundaries, the tick-rate
  window, host and guest views, the priority between statuses, silent players, placeholders,
  numbers formatted the same in every culture, and that every string the panel asks for
  exists in the English file.

The ping over Steam has checks for the between-ticks pump (once a millisecond at most, only
for a connection that is up, never for one being closed), the timing line, and the ping as a
function of both players' frame length over a fake Steam network, with and without that pump
(`dotnet run --project StabilityTests -- --ping-report` prints the table). The host's frame rate
easing has checks for the frame rate meter, the reply format and its limits, the rule step by
step, how it combines with the lag easing, a real host and guest session in which the host
reads the guest's frame rate and forgets it when the guest stops reporting, and the panel's
lines.

The chat adds checks (1.0.7) for:

- the wire format and cleaning: control and direction-changing characters, markup, length,
  surrogate pairs, and every kind of malformed message or history frame;
- the history log (order, duplicates, the 2,000 message cap) and the host's rate limit;
- real host and guest sessions: the host ignoring a guest's claimed id and number, everyone
  seeing one order, no change to the hash, the event script or tick progress, gameplay events
  keeping their order under a chat flood, a guest that floods being limited, a guest that
  leaves, and a guest that joins receiving the whole history in order after its save, state and
  init event, with a message sent during the join arriving exactly once, also while two guests
  join during a burst of messages;
- how a line is written (only the name is colored, no message can add markup, dark colors are
  lightened), the English strings and the chat key binding's blueprint.

The panel's sizing adds checks for the width it follows (the
population panel first, then the nearest panel above, never a width that is not believable, and
its own text width when there is nothing to follow), for the chat's height, and that every label
and every pacing text is short enough for its column (the old ones were not).

**Seen so far:** a screenshot from before the sizing changes showed the panel and chat drawing
(an empty log and the box to type in). It also showed the chat as tall as the top of the panel,
the alerts covering its text box, and the pacing text pushing the panel wider than the game's own
counters; the sizing checks above and the current layout are the response.

**Not verified: how it looks and feels.** The fixes themselves have not been seen in the running
game: that the panel now matches the counters' width (and to what), that the chat clears the
alerts, and that the panel really is drawn in front of them while you type. The panel's layout,
colors, spacing and where it sits in each corner other than the top left have not been seen
either, nor have the controls (click to collapse, the settings, the optional keys). For the chat
that also means: how the messages look, that
the box takes and gives back the keyboard as described (Enter, Esc, a click on the game, the
optional key), that the game's hotkeys really stay off while you type and come back after, how a
long message wraps, whether the log follows new messages and lets you scroll up, and that the
mouse wheel over the chat scrolls it without also zooming the camera (the game skips zooming
while the pointer is over its interface, which this relies on).

## Known limits

- Other languages show the English text for the new strings. Chat itself carries any text
  players type, but the game's font decides which characters can be drawn.
- Chat is text only: no emoji picker, no private messages, no commands, no message editing.
- Chat is not saved: it lasts as long as the multiplayer session, and starts empty after a reload.
- Several alerts at once can still reach the chat, because the alerts grow upward from the bottom
  of the screen. The chat is drawn in front of them while you type, but not otherwise.
- Ping is measured about once a second, so it lags a sudden change slightly.
- The panel does not show packet loss or bandwidth.
