# Player cursors

See your friends at work: their cursors, what they've selected, and which buildings they're changing.

## What you see

- **Cursors** in each player's color, with their name. They follow the map, whatever your camera does. A cursor
  over the game's interface, outside the window or off the map is hidden.
- **Selections**: what another player selects gets the game's selection outline in their color. Your own selection
  color wins on your screen.
- **Viewing / Editing**: a selected building shows **Viewing: name**. For three seconds after someone changes it
  (recipes, workers, names, priorities, water or automation settings), it shows **Editing: name**. It's a notice,
  not a lock: both players can still change the building.
- **Pings** mark a spot on everyone's map, in your **Ping Color**. Bind **Ping Location** under Options → Bindings →
  BeaverBuddies first.

## Restyling cursors

In a co-op game, press Esc → **Player cursors**. Each connected player who shares activity gets a card:

| Control | Range | Default |
| --- | --- | --- |
| Color | **Their color**, 10 presets, or Red/Green/Blue sliders | Their color |
| Size | 50%–300% | 100% |
| Transparency | 0%–90% | 50% |

The color also applies to their name label, selection outline and chat name. **Reset** returns a card to the
defaults. The first card, **You, in the chat**, sets the color you see your own name in, in the chat.

These choices are yours alone: nothing is sent, so each player can style others differently. They're remembered by
display name, so a friend keeps their look next time.

## Settings

In the mod's settings (**Mods**, then the settings button beside **BeaverBuddies - Stability Fork**):

- **Player activity indicators** (on): share your cursor, selection and edits, and show others'.
- **Ping Display Name** ("Player") and **Ping Color** (yellow): what others see for you. Left on yellow, you get a
  color by player number (the host orange, then blue, green, pink, purple, teal, red and lime), so players who never
  change it still look different. Your pings stay yellow.

A player who turns sharing off disappears from your screen until they turn it back on.

How cursors are sent without touching the game: [DEVELOPING.md](DEVELOPING.md#connection-panel-chat-and-player-cursors).
