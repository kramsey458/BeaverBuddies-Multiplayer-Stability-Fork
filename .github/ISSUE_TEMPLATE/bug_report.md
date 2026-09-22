---
name: Bug report
about: Report a desync, crash or other problem with the Stability Fork
title: ''
labels: ''
assignees: ''

---

**Important**: Collect the logs before you play again. Each time Timberborn starts, it replaces `Player.log` and keeps the previous session's log as `Player-prev.log`, so starting the game twice loses the log of the session with the problem. You can close the game, but do not start it again until you have submitted this report.

**Describe the bug**
* What happened: [Desync / Crash / Unexpected behavior]
* What were you doing right before this happened? [Were you doing anything you had not done before, for example building a specific type of building or using a particular part of the interface?]
* How were you connected? [Steam invite / Direct IP / Hamachi or another virtual network]

**Save file**: On the host's computer, [find the save file](https://timberborn.fandom.com/wiki/Game_Save_File#Location) for the game that desynced or crashed. Rename the file from `xxx.timber` to `xxx.zip` and upload it here (for example, by drag-and-drop).
* This is important. It's almost impossible to reproduce errors without the save.

**Logs**: On both the host's and the guest's computer:
1. Open `%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn` in File Explorer (paste it into the address bar).
2. Rename `Player.log` to `host.txt` (on the host) or `guest.txt` (on the guest). If the game has been started again since the problem happened, use `Player-prev.log` instead.
3. Upload these files to this issue. If there is a `BeaverBuddiesDiagnostics` folder, zip it and upload it too.

Logs contain file paths that include your Windows user name. Look through them before you upload if that matters to you.

**To reproduce**: If you have been able to reproduce this bug more than once, describe the steps (or delete this section)
1. Go to '...'
2. Click on '....'
3. Scroll down to '....'
4. See error

**Screenshots**
If this is unexpected behavior, or there is something unique about your map that caused the bug, add screenshots to help explain your problem.

**Setup (please fill this in for both the host and the guest):**
 - OS: [for example, Windows 11]
 - Timberborn version: [shown in the bottom-left corner of the main menu]
 - BeaverBuddies Stability Fork version: [shown in the mod list, for example 1.1.12]
 - Other mods enabled: [list them, or "none"]
