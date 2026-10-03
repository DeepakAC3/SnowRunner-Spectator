"""Read-only SnowRunner vector snapshots with stationary/movement rejection.

Baseline scans float triples near the supplied world position, allowing axis
permutations and signs. Capture compares only retained addresses. No memory
writes are used. Input is sent only when --hold KEY SECONDS is supplied.
"""

import argparse
import array
import ctypes
import json
import math
import os
import struct
import time
from ctypes import wintypes
from pathlib import Path

import snowrunner_input as keys

try:
    import numpy as np
except ImportError:
    np = None

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
SIZE_T = ctypes.c_size_t


class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p),
                ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wintypes.DWORD),
                ("PartitionId", wintypes.WORD),
                ("RegionSize", SIZE_T), ("State", wintypes.DWORD),
                ("Protect", wintypes.DWORD), ("Type", wintypes.DWORD)]


kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                      ctypes.c_void_p, SIZE_T, ctypes.POINTER(SIZE_T)]
kernel32.ReadProcessMemory.restype = wintypes.BOOL
kernel32.VirtualQueryEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                   ctypes.POINTER(MEMORY_BASIC_INFORMATION), SIZE_T]
kernel32.VirtualQueryEx.restype = SIZE_T
kernel32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
kernel32.GetProcessTimes.restype = wintypes.BOOL
keys.user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
keys.user32.GetWindowThreadProcessId.restype = wintypes.DWORD


def distance(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


class Reader:
    def __init__(self, window):
        match = keys.find_window(window)
        if match is None:
            raise RuntimeError("Could not select SnowRunner window")
        self.hwnd, self.title = match
        pid = wintypes.DWORD()
        keys.user32.GetWindowThreadProcessId(self.hwnd, ctypes.byref(pid))
        self.pid = pid.value
        # PROCESS_QUERY_INFORMATION | PROCESS_VM_READ; deliberately no VM_WRITE.
        self.handle = kernel32.OpenProcess(0x410, False, self.pid)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        times = [wintypes.FILETIME() for _ in range(4)]
        if not kernel32.GetProcessTimes(self.handle, *[ctypes.byref(t) for t in times]):
            self.close()
            raise ctypes.WinError(ctypes.get_last_error())
        self.created = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime

    def close(self):
        if self.handle:
            kernel32.CloseHandle(self.handle)
            self.handle = None

    def read(self, address, size):
        buffer = ctypes.create_string_buffer(size)
        received = SIZE_T()
        kernel32.ReadProcessMemory(self.handle, address, buffer, size, ctypes.byref(received))
        return buffer.raw[:received.value]

    def regions(self):
        address = 0
        region = MEMORY_BASIC_INFORMATION()
        while kernel32.VirtualQueryEx(self.handle, address, ctypes.byref(region), ctypes.sizeof(region)):
            base, size = region.BaseAddress or 0, region.RegionSize
            if not size or base + size <= address:
                break
            # Private state and readable image data; skip file/GPU mappings.
            if (region.State == 0x1000 and region.Type in (0x20000, 0x1000000) and
                    not region.Protect & (0x100 | 0x01) and
                    region.Protect & 0xFF in (0x02, 0x04, 0x08, 0x20, 0x40, 0x80)):
                yield base, size
            address = base + size

    def hold(self, key, seconds):
        keys.user32.ShowWindow(self.hwnd, keys.SW_RESTORE)
        if not keys.user32.SetForegroundWindow(self.hwnd):
            raise RuntimeError("Could not foreground SnowRunner")
        print(f"Holding freecam {key} for {seconds:.2f}s", flush=True)
        try:
            keys.send_input_key(keys.KEYS[key], True)
            time.sleep(seconds)
        finally:
            keys.send_input_key(keys.KEYS[key], False)


def baseline(reader, args):
    expected = sorted(abs(value) for value in args.center)
    rows = []
    scanned = 0
    started = time.monotonic()
    next_log = started + 5
    stop_reason = "complete"
    for base, size in reader.regions():
        for offset in range(0, size, 1024 * 1024):
            if time.monotonic() - started >= args.max_seconds:
                stop_reason = "time limit"
                break
            if scanned >= args.max_mib * 1024 * 1024:
                stop_reason = "byte limit"
                break
            # Eight bytes overlap makes triples spanning chunk boundaries visible.
            chunk = reader.read(base + offset, min(1024 * 1024 + 8, size - offset))
            scanned += len(chunk)
            aligned = chunk[:len(chunk) // 4 * 4]
            if np is not None and len(aligned) >= 12:
                floats = np.frombuffer(aligned, dtype="<f4")
                limit = min(len(floats) - 2, 1024 * 1024 // 4)
                if args.exact:
                    # Tight coordinate matches are rare. Reject almost all offsets
                    # using only the first float before building/sorting triples.
                    first = np.abs(floats[:limit])
                    mask = np.zeros(limit, dtype=bool)
                    for target in expected:
                        mask |= (first >= target - args.radius) & (first <= target + args.radius)
                    coarse = np.flatnonzero(mask)
                    if len(coarse):
                        triples = np.stack((floats[coarse], floats[coarse + 1], floats[coarse + 2]), axis=1)
                        values = np.sort(np.abs(triples), axis=1)
                        keep = np.isfinite(values).all(axis=1) & (np.abs(values - expected) <= args.radius).all(axis=1)
                        indices = coarse[keep]
                    else:
                        indices = coarse
                else:
                    triples = np.lib.stride_tricks.sliding_window_view(floats, 3)[:limit]
                    values = np.sort(np.abs(triples), axis=1)
                    mask = np.isfinite(values).all(axis=1)
                    if args.relative:
                        mask &= (values[:, 2] >= args.min_coordinate) & (values[:, 2] <= args.radius)
                    else:
                        mask &= (np.abs(values - expected) <= args.radius).all(axis=1)
                    indices = np.flatnonzero(mask)
            else:
                floats = array.array("f")
                floats.frombytes(aligned)
                indices = []
                limit = min(len(floats) - 2, 1024 * 1024 // 4)
                for i in range(max(0, limit)):
                    triple = (floats[i], floats[i + 1], floats[i + 2])
                    if not all(math.isfinite(x) for x in triple):
                        continue
                    values = sorted(abs(x) for x in triple)
                    if args.relative:
                        keep = args.min_coordinate <= values[2] <= args.radius
                    else:
                        keep = all(abs(value - target) <= args.radius for value, target in zip(values, expected))
                    if keep:
                        indices.append(i)
            for i in indices:
                a, b, c = (float(floats[i]), float(floats[i + 1]), float(floats[i + 2]))
                rows.append({"address": hex(base + offset + i * 4), "values": [[a, b, c]]})
                if len(rows) >= args.max_candidates:
                    stop_reason = "candidate limit"
                    break
            if time.monotonic() >= next_log:
                print(f"Scanned {scanned / 1048576:.0f} MiB; {len(rows)} candidates", flush=True)
                next_log = time.monotonic() + 5
            if stop_reason != "complete":
                break
        if stop_reason != "complete":
            break
    return rows, {"scanned_bytes": scanned, "scan_complete": stop_reason == "complete",
                  "stop_reason": stop_reason, "elapsed_seconds": time.monotonic() - started}


def capture(reader, snapshot, args):
    rows = []
    pages = {}
    for row in snapshot["candidates"]:
        address = int(row["address"], 16)
        page = address & ~0xFFF
        pages.setdefault(page, []).append((address, row))
    failed = 0
    for page, members in pages.items():
        # Extra bytes accommodate triples across a page boundary. A short read
        # still allows triples entirely within the accessible part of the page.
        data = reader.read(page, 4096 + 8)
        for address, row in members:
            offset = address - page
            if offset + 12 > len(data):
                data = reader.read(page, 4096)
            if offset + 12 > len(data):
                failed += 1
                continue
            value = struct.unpack_from("<fff", data, offset)
            if not all(math.isfinite(x) for x in value):
                continue
            prior = row["values"][-1]
            length = distance(value, prior)
            keep = length <= args.max_delta if args.mode == "stationary" else args.min_delta <= length <= args.max_delta
            if keep and args.mode == "reverse":
                if len(row["values"]) < 2:
                    raise ValueError("Reverse capture needs a preceding movement capture")
                old = [x - y for x, y in zip(prior, row["values"][-2])]
                new = [x - y for x, y in zip(value, prior)]
                old_length = math.sqrt(sum(x * x for x in old))
                keep = old_length > 0 and length > 0 and (
                    sum(x * y for x, y in zip(old, new)) / (old_length * length) <= -args.reverse_cosine
                    and 0.25 <= length / old_length <= 4)
            if keep:
                rows.append({"address": row["address"], "values": row["values"] + [list(value)]})
    return rows, {"input_candidates": len(snapshot["candidates"]), "failed_reads": failed,
                  "capture_mode": args.mode}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("baseline", "capture"))
    parser.add_argument("output", type=Path)
    parser.add_argument("--input", type=Path, help="previous snapshot for capture")
    parser.add_argument("--window", default="SnowRunner")
    parser.add_argument("--center", nargs=3, type=float, default=(894, 60, 513))
    parser.add_argument("--radius", type=float, default=600)
    parser.add_argument("--relative", action="store_true", help="scan bounded local triples instead of world position")
    parser.add_argument("--exact", action="store_true", help="accelerate tight signed/permuted matches; supply --radius such as 0.02")
    parser.add_argument("--min-coordinate", type=float, default=2)
    parser.add_argument("--max-seconds", type=float, default=120)
    parser.add_argument("--max-mib", type=int, default=2048)
    parser.add_argument("--max-candidates", type=int, default=500000)
    parser.add_argument("--mode", choices=("moved", "stationary", "reverse"), default="moved")
    parser.add_argument("--min-delta", type=float, default=0.05)
    parser.add_argument("--max-delta", type=float, default=200)
    parser.add_argument("--reverse-cosine", type=float, default=0.9)
    parser.add_argument("--hold", nargs=2, metavar=("KEY", "SECONDS"))
    parser.add_argument("--settle", type=float, default=0.3)
    args = parser.parse_args()
    if ctypes.sizeof(ctypes.c_void_p) != 8:
        parser.error("Use 64-bit Python for SnowRunner's 64-bit memory layout")
    if args.action == "capture" and not args.input:
        parser.error("capture requires --input")
    if args.exact and args.relative:
        parser.error("--exact and --relative cannot be combined")
    if args.mode == "stationary" and args.max_delta == 200:
        args.max_delta = 0.05
    if not (args.radius > 0 and args.max_seconds > 0 and args.max_mib > 0 and args.max_candidates > 0
            and 0 <= args.min_delta <= args.max_delta and 0 <= args.reverse_cosine <= 1 and args.settle >= 0):
        parser.error("Invalid bounds, thresholds, or time limits")
    snapshot = json.loads(args.input.read_text()) if args.input else None
    if args.hold:
        key, seconds = args.hold
        seconds = float(seconds)
        if key not in "ikjlouyp" or len(key) != 1 or not 0 < seconds <= 10:
            parser.error("--hold accepts a freecam key and a duration between 0 and 10 seconds")
    reader = Reader(args.window)
    try:
        if snapshot and (snapshot["pid"] != reader.pid or snapshot["process_created"] != reader.created):
            raise RuntimeError("Snapshot belongs to a different SnowRunner process; take a new baseline")
        if args.hold:
            reader.hold(key, seconds)
            time.sleep(args.settle)
        print(f"{args.action}: {reader.title}; PID {reader.pid}", flush=True)
        rows, stats = baseline(reader, args) if args.action == "baseline" else capture(reader, snapshot, args)
        result = {"pid": reader.pid, "process_created": reader.created, "time": time.time(),
                  "source": str(args.input) if args.input else None,
                  "settings": {k: str(v) if isinstance(v, Path) else v for k, v in vars(args).items()},
                  "stats": stats, "candidates": rows}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.output.with_suffix(args.output.suffix + ".tmp")
        temporary.write_text(json.dumps(result, separators=(",", ":")))
        os.replace(temporary, args.output)
        print(f"Saved {len(rows)} candidates to {args.output}; {stats}", flush=True)
        for row in rows[:20]:
            print(f"{row['address']}: {row['values'][-1]}", flush=True)
        return 0
    finally:
        reader.close()


if __name__ == "__main__":
    raise SystemExit(main())
