"""Send one timed keyboard hold to a visible SnowRunner window.

This uses the same targeted PostMessage approach as key-macro.py, but performs
only the single action requested on the command line.
"""

import argparse
import ctypes
import sys
import time
from ctypes import wintypes


WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
MAPVK_VK_TO_VSC = 0

user32 = ctypes.windll.user32
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.restype = ctypes.c_int
user32.MapVirtualKeyW.argtypes = [wintypes.UINT, wintypes.UINT]
user32.MapVirtualKeyW.restype = wintypes.UINT
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.PostMessageW.restype = wintypes.BOOL

KEYS = {
    "a": 0x41,
    "b": 0x42,
    "d": 0x44,
    "e": 0x45,
    "f": 0x46,
    "q": 0x51,
    "s": 0x53,
    "w": 0x57,
    "x": 0x58,
    "space": 0x20,
    "shift": 0x10,
    "ctrl": 0x11,
    "control": 0x11,
    "up": 0x26,
    "down": 0x28,
    "left": 0x25,
    "right": 0x27,
}


def enum_visible_windows():
    windows = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd, _):
        if user32.IsWindowVisible(hwnd):
            length = user32.GetWindowTextLengthW(hwnd)
            if length:
                title = ctypes.create_unicode_buffer(length + 1)
                user32.GetWindowTextW(hwnd, title, length + 1)
                windows.append((hwnd, title.value))
        return True

    user32.EnumWindows(callback, 0)
    return windows


def find_window(title_fragment):
    needle = title_fragment.casefold()
    matches = [(hwnd, title) for hwnd, title in enum_visible_windows()
               if needle in title.casefold()]
    if len(matches) != 1:
        print(f"Expected one visible window containing {title_fragment!r}; found {len(matches)}.")
        for hwnd, title in matches:
            print(f"  0x{hwnd:08X}: {title}")
        if not matches:
            print("Visible windows:")
            for hwnd, title in enum_visible_windows():
                print(f"  0x{hwnd:08X}: {title}")
        return None
    return matches[0]


def post_key(hwnd, vk, down):
    message = WM_KEYDOWN if down else WM_KEYUP
    scan_code = user32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC)
    lparam = 1 | (scan_code << 16)
    if not down:
        lparam |= 0xC0000000
    if not user32.PostMessageW(hwnd, message, vk, lparam):
        raise ctypes.WinError()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("key", choices=sorted(KEYS), help="key to hold")
    parser.add_argument("seconds", type=float, help="hold duration in seconds")
    parser.add_argument("--window", default="SnowRunner", help="visible window title fragment")
    args = parser.parse_args()

    if args.seconds <= 0 or args.seconds > 60:
        parser.error("seconds must be greater than 0 and no more than 60")

    match = find_window(args.window)
    if match is None:
        return 1

    hwnd, title = match
    vk = KEYS[args.key]
    print(f"Target: {title} (0x{hwnd:08X}); holding {args.key} for {args.seconds:.2f}s")
    post_key(hwnd, vk, True)
    try:
        time.sleep(args.seconds)
    finally:
        post_key(hwnd, vk, False)
    print("Key released.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
