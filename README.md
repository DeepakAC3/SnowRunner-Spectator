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
- `Ctrl+F6` captures or compares freecam vector candidates near the truck.

Use the tracker in single-player first. It should print lines in this form while
you drive:

```text
LOCAL: X Y Z
```

For the camera scan, press `Ctrl+F6`, move the free camera, then press
`Ctrl+F6` again. The tool will print the addresses whose three-float values
changed between the two captures.
