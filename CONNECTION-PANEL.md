# Connection panel and chat

A small panel in a corner of your screen during a multiplayer game. It shows who is connected, each ping, whether
you're in sync, and how fast the game runs. Below it are a speed boost and a chat box.

## What it shows

**Collapsed**, one line: the sync dot, the number of players and one ping.

```
o  3 players  42 ms                                 [+]
```

**Expanded:**

```
Multiplayer                                 Host   [-]
o  In sync
-------------------------------------------------------
Player 1                                           -
Player 2                                       42 ms
Player 3                                      190 ms
-------------------------------------------------------
Tick rate   1.7 ticks/s
Speed       1x
Connection  Direct
```

Your own row is in bold, with a dash instead of a ping.

| Line | Meaning |
| --- | --- |
| **Status** | *In sync* is normal. *Catching up* (guests): three or more ticks behind the host. *Waiting for host* (guests): nothing has arrived from the host for a moment. *Connection unstable*: someone hasn't responded for five seconds. *Out of sync*: a desync; see the [troubleshooting page](https://timbermods.github.io/BeaverBuddies-Stability-Fork/troubleshooting.html#desync). *Disconnected*: the session has ended. The dot is green in sync, yellow while catching up or waiting, red otherwise. |
| **Players** | Everyone in the game, host first. |
| **Ping** | Round trip to that player, in milliseconds. Normal text up to 80 ms, yellow up to 160 ms, red above or **No response**. `...` means not measured yet. A guest sees the other guests' pings to the host. |
| **Tick rate** | Simulation ticks per second: about 1.7 at normal speed, about 11.7 at the fastest button, 0 when paused. |
| **Speed** | The speed the game runs at, including any speed boost, or Paused. |
| **Behind host** | Guests: how many ticks behind the host you are. Should be 0 or 1. |
| **Guest behind**, **Guest fps** | Host: the slowest guest's lag in ticks, and the lowest guest frame rate. A guest whose game window is in the background reports no frame rate. |
| **Easing off** | Host: the share of the chosen speed the game runs at while a guest catches up, such as *75% of speed*, or *75% (frame rate)* when a guest's frame rate is the reason. *waiting for a guest* means the game waits for a guest far behind. It returns to full speed by itself. |
| **Ease off below** | Host: click to choose a guest frame rate floor (Off, 20, 30, 45 or 60 fps). While a guest stays below it, the game slows a little. |
| **Connection** | Direct (IP, Hamachi or port forwarding) or Steam. |

## Showing and hiding

- **Click the title** to collapse or expand it. Your choice is remembered. Collapsed, it shows **N new** for unread
  chat messages.
- **Mod Settings → Connection panel**: Expanded, Collapsed or Hidden.
- **Mod Settings → Connection panel position**: top left (default), top right, bottom left or bottom right.
- **Options → Bindings → BeaverBuddies → Toggle connection panel**: a key to hide and show it (none by default).

It appears only in multiplayer games, and scales with your UI scale.

## Chat

```
Speed boost [-] [+0.5] [+]   = 1.5x
Player 2: anyone want to build a second dam?
Player 1: yes, upstream of the farm
Player 2: on it
[ Type a message...                                   ]
```

- **Send:** click the box, type and press **Enter**. Enter on an empty box, **Esc** or a click on the game gives the
  keyboard back. **Chat: start typing** (Options → Bindings → BeaverBuddies) opens the box with a key.
- **Typing doesn't play the game:** the game's hotkeys are off while you type.
- **Names** are each player's **Ping Display Name**, in their cursor color. A player who kept the default yellow gets
  a color by player number, so no two start the same. Pick the color you see your own name in under Esc →
  **Player cursors**.
- **Everyone sees the same conversation**, in the same order. A player who joins later gets the history.
- **Chat isn't saved**: it starts empty after a reload or a rehost. Messages are one line, up to 200 characters.
  Scroll up with the mouse wheel to read older ones.

**Speed boost.** The row at the top of the chat adds to the speed picked at the top right. **-** and **+** step by
0.5, or type a number and press **Enter**. With +0.5, the fastest button runs at 7.5x. It goes from -6.5 to +23, and
the game stays between 0.5x and 30x. Any player can change it, for everyone. It resets to 0 in a new session. The
slowest computer still sets the real pace: the **Tick rate** line shows what's achieved.

## Known limits

- Text this mod adds is English only. Chat carries any text, but the game's font decides what can be drawn.
- Chat is text only: no private messages, no emoji picker, no editing.
- Ping is measured about once a second. The panel doesn't show packet loss or bandwidth.
- Several game alerts at once can reach the chat box. While you type, the chat is drawn in front of them.

The panel, chat, speed boost and chat colors have been played in multiplayer over Steam. A few details haven't been
checked one by one, such as the other corners and the optional keys. How ping is measured, why the panel can't affect
the game, and the full play record: [DEVELOPING.md](DEVELOPING.md#connection-panel-chat-and-player-cursors).
