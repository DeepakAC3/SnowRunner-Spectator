# Camera investigation checkpoint

## Confirmed evidence

- `SnowRunner.exe+0x2A97EE0` directly points to a rendered camera position buffer. Components are stored Z, Y, X; basis vectors are at buffer offsets `0x50`, `0x60`, and `0x70`.
- Controlled up/down, left/right, and forward/back freecam movements changed this position and reversed correctly. Stationary captures stayed unchanged, and tilt left position essentially unchanged.
- Driving the truck approximately 12 metres left the freecam position unchanged. This distinguishes the rendered camera from the truck/character rig.
- The pointer remained stable within the observed game process. Restart stability remains unverified.
- The legacy `SnowRunner.exe+0x2A99E80 -> pointer -> +0x1B8` chain belongs to the truck/character rig. Experimental camera writes in the main application remain disabled.

## Rejected writable candidate

An exact coordinate scan found 356 copies, including executable globals at offsets `0x2AEEF7C`, `0x2AEF0BC`, and `0x2AEF11C`.

The bounded diagnostic in `tools/camera_nudge_probe.py` temporarily raised the height at `SnowRunner.exe+0x2AEF120` by one unit. The game overwrote the value within approximately 47 ms, and the rendered camera never moved. The diagnostic restored and verified the original four bytes after 0.5 seconds. This candidate is an output copy, not a usable camera controller.

The live evidence is saved locally in ignored `logs/camera-nudge.json`. Other captures include `camera-exact.json`, `camera-refs.json`, `camera-global-sources.json`, and the controlled movement/truck snapshots. Captures are tied to process identity and must not be reused as current addresses after restarting the game.

## Resume point

The writable source and follow loop are now implemented. The target overlay
scans the active transform allocation, lists nonzero transform candidates, and
passes a selected object directly into the guarded follow loop. It is currently
position/address based; the scanner now also follows a bounded set of body
pointers to recover truck/trailer asset IDs when they are present. A complete
map-name mapping remains an investigation item. A foreground-gated map-transition watcher polls
SnowRunner's `M` and `Esc` boundaries, stops follow, disables freecam at the map
boundary, and restores freecam after the world view returns. The guard is
best-effort because it polls the key state while the game handles the same key.

User preference: commit locally and push every tenth new commit. Last remote checkpoint was `d970357`; subsequent local commits preserve diagnostics and rendered camera tracking. Recording and replay remain README TODO items.

## Resumed game evidence

- A resumed game observation at PID 4248 confirmed that the direct rendered-camera global at `SnowRunner.exe+0x2A97EE0` still resolves after a game restart. The module-relative global is stable; the heap pointer it contains remains process-specific.
- A contiguous double-precision scan across the full approximately 6 GB user address space found no matching camera-position representation. The resumed stride-16 scan started with 195 layouts; after controlled movement, 55 layouts moved, most of which were rendered-position copies rather than the producing state.
- The producing camera source chain is `SnowRunner.exe+0x2A8EB90 -> owner+0x08` for the view-matrix source. The freecam controller is at `owner+0x18`, and its `+0x08` transform pointer matches that source.
- The source is a row-major rigid view matrix. Inverting its translation and basis yields stored Z,Y,X camera coordinates; the cached stored position is at `source+0xB0`. To set a desired stored Z,Y,X position while preserving orientation, compute `translation = -desiredStoredZYX * basis` and write the translation at `source+0x30`. Invalidate the derived caches at `source+0x80` and `source+0xC0` with `-1` afterward.
- The live view-matrix nudge in `logs/resume-view-nudge.json` moved rendered world Y by one unit, restored the original translation exactly, and reported zero restoration errors over a 0.484-second observation window.
- The existing player-coordinate chain resolves an object through `SnowRunner.exe+0x2A8EB68 -> [root+0x28] -> [object+0x18]`. Its validated position begins at object offset `0x1A0` and is stored Z,Y,X; the old `0x1A8` read overlaps the third position component, the following scalar at `0x1AC`, and the next vector's first component. Loaded-code references at `0x7ff77272c221` read the transform vectors at `+0x170`, `+0x180`, `+0x190`, and `+0x1A0`; `0x7ff7727a5461` also consumes `+0x1A0` as a position vector.
- `logs/truck-layout-before.json` and `logs/truck-layout-after.json` show identical `+0x1A0`, `+0x1B0`, and rendered values because that earlier capture was made while freecam was active.
- A controlled runtime validation then drove the truck with freecam off, re-enabled freecam, started follow, and held forward. The anchor moved from approximately `(904.25, 115.65, 570.29)` to `(903.96, 115.65, 571.14)` while the camera target moved from `(902.26, 118.54, 558.63)` to `(901.97, 118.54, 559.48)`, preserving the approximately `(-1.99, 2.88, -11.66)` offset.
- The follow loop now reads the target’s 16-byte-strided basis vectors at `+0x170`, `+0x180`, and `+0x190`, converts the initial world offset into target-local coordinates, and rotates that offset with the target. A controlled `W+D` runtime pass changed the reported world offset from `(13.86, 4.90, 3.90)` to approximately `(-1.67, 2.86, 9.33)` as the truck turned while the camera target continued tracking it.
- A live overlay test selected a non-active candidate at approximately `(-583.14, 69.46, -115.79)` and started follow with a distinct offset, proving selection does not require entering the current truck. A separate live `M` test reproduced the black-map failure with freecam active; the rebuilt watcher then observed `M`, stopped follow, and changed the freecam flag from `1` to `0` before map navigation.

## 2026-10-05: truck class scanning and freecamera toggle

Fix `42a1764` was deployed from `bin/Spectator` with game PID 24392 and helper PID 7316. Scanning now obtains the chassis body from `owner+0x5D0`; the first physics body reached through `DRIVE_LOGIC+0x28` can differ from the chassis. Candidate truck validation remains intact. The Freecamera button toggles freecam, and delayed map restoration checks and enables freecam under the same camera lock as the toggle.

Each Refresh returned nine validated trucks while occupying the following vehicles. Occupancy and vehicle names were independently observed on the external monitor through the game map and menu; the log records scan counts but does not itself establish the vehicle class. The first list entry matched the occupied truck.

| Occupied truck | Class | Refresh observation (UTC) |
| --- | --- | --- |
| Fleetstar | Heavy Duty | 03:28:09.7932542 |
| Transtar | Highway | 03:30:18.1214143 |
| Bandit | Offroad | 03:34:08.8141526 |
| Scout 800 | Scout | 03:36:35.0126596 |
| Azov 670963N Atom | Heavy | 03:39:40.7790039 |

The actual Freecamera button was clicked on at 03:30:45.9978 UTC; patch success and the On label were observed. While occupying Transtar, Spectate attached to Fleetstar: the camera was approximately `(-707.68, 28.27, 799.28)` and the target truck `(-713.62, 23.02, 809.18)`. Clicking Freecamera off at 03:32:25.9119 UTC stopped follow and restored the game camera. No Ctrl+F1 shortcut was used.

Opening the map with M while freecam was on disabled it at 03:35:15.3474 UTC. Switching to Scout 800 through map navigation returned to a visible Scout, and freecam restored at 03:35:56.0245 UTC. This guard still infers transitions from keys rather than detecting actual map visibility.

The scan and transition events are recorded in `bin/Spectator/logs/spectator-20261005-085312.log`. Release helper and native builds passed with existing warnings. Co-op was not tested in this round.

### Resource asset names stored inline

A read-only inspection while occupying GMC 9500 found a valid chassis owner
whose resource name at `resource+0x10` was inline `trucks_gmc_9500` rather than
a pointer. The following fields were count 15 and capacity 15, with the count
including the trailing NUL. CK1500 used a heap pointer, count 23, and capacity
31 for `trucks_chevrolet_ck1500`. The asset reader now handles this observed
16-byte inline/pointer union and validates bounded count, capacity, terminator,
and printable ASCII for both representations. The bone-name reader and truck
structure checks are unchanged. Runtime verification of this reader is pending.
