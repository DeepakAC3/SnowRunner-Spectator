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
- `Ctrl+F11` reads one camera-object snapshot and dumps nearby normalized vectors for orientation discovery.
- `Ctrl+F12` is reserved for camera follow while the writable camera state is being identified.
- `Ctrl+Shift+F12` performs a one-unit camera-position nudge and restores it on the next press.

## TODO

- Identify and validate the writable camera state for positioning and follow mode.
- Record and replay camera paths.

Use the tracker in single-player first. It should print lines in this form while
you drive:

```text
LOCAL: X Y Z
```

For a targeted held-key input, use the helper instead of the random macro:

```text
python tools/snowrunner_input.py --window SnowRunner d 1.5
```

It sends one key-down, holds it for the requested number of seconds, and always
sends the matching key-up.

The freecam movement keys currently used by the helper are:

```text
i = front       k = back
j = left        l = right
o = up          u = down
y = tilt clockwise
p = tilt anticlockwise
```

The helper only sends input to the selected SnowRunner window. It does not write
camera or vehicle memory.

For the camera scan, press `Ctrl+F6` once, move the free camera, then press
`Ctrl+F6` again. Continue moving the camera and pressing `Ctrl+F6`; each
press compares only the surviving addresses from the previous capture. Use
`Ctrl+F7` after the list has narrowed.
