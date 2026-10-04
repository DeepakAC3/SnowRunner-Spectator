"""Send one timed keyboard hold to a visible SnowRunner window.

The selected window is foregrounded and receives real Win32 SendInput events,
which works with games that ignore ordinary window-posted key messages.
"""

import argparse
import ctypes
import sys
import time
from ctypes import wintypes


MAPVK_VK_TO_VSC = 0
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
SW_RESTORE = 9

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
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
user32.ShowWindow.restype = wintypes.BOOL
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.restype = wintypes.BOOL
user32.GetForegroundWindow.restype = wintypes.HWND
ULONG_PTR = ctypes.c_ulonglong if ctypes.sizeof(ctypes.c_void_p) == 8 else ctypes.c_ulong


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", wintypes.WORD),
        ("wScan", wintypes.WORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", wintypes.LONG),
        ("dy", wintypes.LONG),
        ("mouseData", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [
        ("uMsg", wintypes.DWORD),
        ("wParamL", wintypes.WORD),
        ("wParamH", wintypes.WORD),
    ]


class INPUTUNION(ctypes.Union):
    _fields_ = [
        ("mi", MOUSEINPUT),
        ("ki", KEYBDINPUT),
        ("hi", HARDWAREINPUT),
    ]


class INPUT(ctypes.Structure):
    _anonymous_ = ("data",)
    _fields_ = [
        ("type", wintypes.DWORD),
        ("data", INPUTUNION),
    ]


user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
user32.SendInput.restype = wintypes.UINT

KEYS = {
    "a": 0x41,
    "b": 0x42,
    "d": 0x44,
    "e": 0x45,
    "f": 0x46,
    "f11": 0x7A,
    "f1": 0x70,
    "f12": 0x7B,
    "i": 0x49,
    "j": 0x4A,
    "k": 0x4B,
    "l": 0x4C,
    "m": 0x4D,
    "o": 0x4F,
    "p": 0x50,
    "q": 0x51,
    "s": 0x53,
    "u": 0x55,
    "w": 0x57,
    "x": 0x58,
    "y": 0x59,
    "space": 0x20,
    "shift": 0x10,
    "ctrl": 0x11,
    "control": 0x11,
    "up": 0x26,
    "down": 0x28,
    "left": 0x25,
    "right": 0x27,
    "esc": 0x1B,
    "escape": 0x1B,
    "tab": 0x09,
    "enter": 0x0D,
    "alt": 0x12,
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


def send_input_key(vk, down):
    scan_code = user32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC)
    flags = 0 if down else KEYEVENTF_KEYUP
    event = INPUT(
        type=INPUT_KEYBOARD,
        ki=KEYBDINPUT(wVk=vk, wScan=scan_code, dwFlags=flags, time=0, dwExtraInfo=0),
    )
    if user32.SendInput(1, ctypes.byref(event), ctypes.sizeof(INPUT)) != 1:
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
    user32.ShowWindow(hwnd, SW_RESTORE)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.15)
    if user32.GetForegroundWindow() != hwnd:
        print("SnowRunner did not receive foreground; no key input sent.")
        return 1

    send_input_key(vk, True)
    try:
        time.sleep(args.seconds)
    finally:
        send_input_key(vk, False)
    print("Key released.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
