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
  camera keeps its current offset from the active truck chassis and
  rotates that offset with the chassis as it steers.
- `Ctrl+Shift+F12` performs a reversible one-unit camera-height nudge for
  diagnostics.
- `Ctrl+Shift+F11` opens a spectator overlay. It scans the loaded map for
  validated truck action owners, shows their exact `trucks_*` asset
  IDs and physics chassis positions, and lets you attach to a selection. The
  Attach button recenters once near the selected chassis and starts follow.

When SnowRunner is the foreground window and freecam is active, the helper
watches the `M`/`Esc` map boundary: it stops follow, disables freecam, and
restores it after returning to the world. This is a short polling guard, so the
game window must be focused before using those keys. The overlay is an external
WinForms window positioned over the SnowRunner viewport.

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

The validated source is `SnowRunner.exe+0x2A8EB90 -> [owner+0x08]`. The target
scanner resolves `SnowRunner.exe+0x2A8EB68 -> DRIVE_LOGIC`, then discovers the
current action vtable and chassis body vtable from the active owner. It scans
committed readable private memory in bounded 1 MiB chunks, capped at 40 seconds
or 8 GiB per refresh. A result must satisfy the reciprocal action-owner link,
the owner model/resource asset link, the chassis node model/definition/self
links, an exact `BoneChassis_cdt`, and the matching
`hkpRigidBody` transform vtable. This keeps inactive Fleetstar, CK1500, and
White Western Star entries visible when their physics allocations move.

Follow reads the validated chassis rigid-body basis at `owner+0x5D0+0x170`
with position at `+0x1A0` (stored Z,Y,X), preserving the full chassis rotation,
then writes only the source matrix translation. `Ctrl+F12` keeps the current
camera offset. Overlay Attach performs one guarded recenter near the selected
chassis using the camera forward vector, then starts the same owner-bound
follow worker. Selection and every follow tick revalidate the action, asset,
bone, frame links, and chassis body identity. The scanner scope is the loaded
map and build-specific offsets above; runtime game verification remains
required after a new game update.

## Runtime evidence (2026-10-04)

Release4 found the same three distinct truck chassis and visually attached to
the inactive Fleetstar without entering it. During a 7.29 m CK1500 drive over
four seconds, the camera kept the truck in frame and sampled maximum offset
error was 0.38 m. The ordinary attach/follow path with the final identity
guards is verified. Rebound and rollback-failure branches were not fault
injected, and co-op remote-player follow remains unverified.

For the camera scan, press `Ctrl+F6` once, move the free camera, then press
`Ctrl+F6` again. Continue moving the camera and pressing `Ctrl+F6`; each
press compares only the surviving addresses from the previous capture. Use
`Ctrl+F7` after the list has narrowed.
