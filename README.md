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
  It searches both module data and private heap storage using only the filtered
  candidates. Reported chains establish pointer structure; camera identity still
  requires movement checks. Heap passes stop after 60 seconds or 5000 references
  and explicitly report when results are incomplete.
- `Ctrl+F8` resets the camera scan when a movement sequence selects unrelated
  vectors.
- `Ctrl+F9` probes the rendered camera position through the newly identified chain.
- `Ctrl+F10` starts or stops read-only rendered camera position tracking.
- `Ctrl+F11` dumps the rendered camera transform and its nearby basis candidates.
- `Ctrl+F12` starts or stops validated camera follow. Enable freecam first; the
  camera keeps its current offset from the active vehicle/player anchor.
- `Ctrl+Shift+F12` performs a reversible one-unit camera-height nudge for
  diagnostics.

## TODO

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

The legacy pointer chain is not the rendered freecam position. Follow writes use
the validated view-matrix source, which avoids the character head and rig glitches
seen during early write experiments.

Reversible freecam movements identified a rendered camera transform referenced
directly by `SnowRunner.exe+0x2A97EE0`. Its first three floats store world coordinates
in Z,Y,X order; diagnostics convert these to X,Y,Z. Basis candidates are at
`+0x50`, `+0x60`, and `+0x70`. This is a render-state diagnostic; the writable
source and follow loop use the separate view-matrix chain. The former
`+0x2A99E80` rig chain remains isolated from these diagnostics.

The validated source is `SnowRunner.exe+0x2A8EB90 -> [owner+0x08]`. Follow reads
the active anchor from `SnowRunner.exe+0x2A8EB68 -> [root+0x28] -> [object+0x18]
-> object+0x1A0` (stored Z,Y,X), then writes only the source matrix translation.
The current runtime validation follows the active truck/player anchor while the
vehicle moves; multiplayer and selecting a different vehicle are not yet exposed.

For the camera scan, press `Ctrl+F6` once, move the free camera, then press
`Ctrl+F6` again. Continue moving the camera and pressing `Ctrl+F6`; each
press compares only the surviving addresses from the previous capture. Use
`Ctrl+F7` after the list has narrowed.
