## SnowFlyer2

A minimal trainer to enable free camera mode in Snowrunner

Tested on version 43.0 (Merc Pack 2 Update),  Steam and Epic version only.

## Usage

Simply unzip to a folder of your choice, then run Snowflyer2.
When Snowrunner is running, follow the prompts to toggle free camera mode.

## Config

The mod creates a config file `keybinds.json` for power users to configure the hotkeys.

## Spectator prototype

This branch adds a first reverse-engineering aid for the spectator project:

- `Ctrl+F4` prints the local player position once.
- `Ctrl+F5` starts or stops continuous local position tracking.
- `Ctrl+F6` performs the initial freecam vector scan, then narrows the same
  candidate set after each camera movement.
- `Ctrl+F7` searches pointer references after the candidate set is narrow.
- `Ctrl+F8` resets the camera scan when a movement sequence selects unrelated
  vectors.
- `Ctrl+F9` probes the currently known fast camera pointer chain.
- `Ctrl+F10` starts or stops continuous camera position tracking through the fast pointer chain.

Use the tracker in single-player first. It should print lines in this form while
you drive:

```text
LOCAL: X Y Z
```

For the camera scan, press `Ctrl+F6` once, move the free camera, then press
`Ctrl+F6` again. Continue moving the camera and pressing `Ctrl+F6`; each
press compares only the surviving addresses from the previous capture. Use
`Ctrl+F7` after the list has narrowed.
