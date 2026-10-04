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

Follow mode is not working yet. Find the camera controller that produces the rendered position, then validate a reversible camera movement before implementing follow. Possible next approaches are inspecting alternative coordinate layouts (double precision or strided fields) and tracing writes to the confirmed rendered position copies.

User preference: commit locally and push every tenth new commit. Last remote checkpoint was `d970357`; subsequent local commits preserve diagnostics and rendered camera tracking. Recording and replay remain README TODO items.
