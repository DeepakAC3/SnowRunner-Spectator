"""Inspect candidate memory and trace read-only pointer references to it.

Examples:
  camera_pointer_probe.py inspect logs/inspect.json --address 0x12345678
  camera_pointer_probe.py pointers logs/refs.json --address 0x12345678 --range 0x400
"""

import argparse
import ctypes
import json
import math
import os
import struct
import time
from ctypes import wintypes
from pathlib import Path

from camera_probe import Reader, kernel32, MEMORY_BASIC_INFORMATION, np


class MODULEENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wintypes.DWORD), ("th32ModuleID", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD), ("GlblcntUsage", wintypes.DWORD),
                ("ProccntUsage", wintypes.DWORD), ("modBaseAddr", ctypes.c_void_p),
                ("modBaseSize", wintypes.DWORD), ("hModule", wintypes.HMODULE),
                ("szModule", wintypes.WCHAR * 256), ("szExePath", wintypes.WCHAR * 260)]


kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
kernel32.Module32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(MODULEENTRY32W)]
kernel32.Module32FirstW.restype = wintypes.BOOL
kernel32.Module32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(MODULEENTRY32W)]
kernel32.Module32NextW.restype = wintypes.BOOL


def modules(pid):
    snapshot = kernel32.CreateToolhelp32Snapshot(0x18, pid)
    if snapshot == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        entry = MODULEENTRY32W()
        entry.dwSize = ctypes.sizeof(entry)
        found = kernel32.Module32FirstW(snapshot, ctypes.byref(entry))
        result = []
        while found:
            result.append({"name": entry.szModule, "path": entry.szExePath,
                           "base": entry.modBaseAddr, "size": entry.modBaseSize})
            found = kernel32.Module32NextW(snapshot, ctypes.byref(entry))
        return result
    finally:
        kernel32.CloseHandle(snapshot)


def location(address, loaded):
    for module in loaded:
        if module["base"] <= address < module["base"] + module["size"]:
            return f"{module['name']}+0x{address - module['base']:X}"
    return hex(address)


def region_info(reader, address):
    region = MEMORY_BASIC_INFORMATION()
    if not kernel32.VirtualQueryEx(reader.handle, address, ctypes.byref(region), ctypes.sizeof(region)):
        return None
    return {"base": hex(region.BaseAddress or 0), "allocation_base": hex(region.AllocationBase or 0),
            "size": region.RegionSize, "state": hex(region.State),
            "protect": hex(region.Protect), "type": hex(region.Type)}


def inspect(reader, addresses, radius, loaded):
    result = []
    for address in addresses:
        start = max(0, address - radius) & ~0xF
        data = reader.read(start, address + radius - start)
        float_rows = []
        pointers = []
        for offset in range(0, len(data) - 15, 16):
            floats = struct.unpack_from("<ffff", data, offset)
            float_rows.append({"address": hex(start + offset), "relative": start + offset - address,
                               "float4": [v if math.isfinite(v) else str(v) for v in floats]})
        for offset in range(0, len(data) - 7, 8):
            value = struct.unpack_from("<Q", data, offset)[0]
            if 0x10000 <= value < 0x800000000000:
                region = region_info(reader, value)
                if region and region["state"] == "0x1000":
                    pointers.append({"slot": hex(start + offset), "relative": start + offset - address,
                                     "value": hex(value), "location": location(value, loaded), "region": region})
        target = reader.read(address, 16)
        item = {"address": hex(address), "location": location(address, loaded),
                "region": region_info(reader, address), "dump_start": hex(start),
                "raw_hex": data.hex(), "float4_rows": float_rows, "nearby_pointers": pointers}
        if len(target) == 16:
            values = struct.unpack("<ffff", target)
            item["target_float4"] = [v if math.isfinite(v) else str(v) for v in values]
        result.append(item)
        print(f"{hex(address)}: {item.get('target_float4')}; {item['region']}", flush=True)
        for pointer in pointers[:20]:
            print(f"  {pointer['relative']:+#x}: -> {pointer['location']}", flush=True)
    return {"inspections": result}


def pointer_scan(reader, args, loaded):
    if np is None:
        raise RuntimeError("Pointer scan requires NumPy; use the bundled Codex Python")
    started = time.monotonic()
    next_log = started + 5
    scanned = 0
    refs = []
    stop_reason = "complete"
    regions = list(reader.regions())
    main = next((m for m in loaded if m["name"].casefold() == "snowrunner.exe"), None)
    if main:
        # Search executable globals before heaps when a budget is needed.
        regions.sort(key=lambda r: (not main["base"] <= r[0] < main["base"] + main["size"], r[0]))
    for base, size in regions:
        for offset in range(0, size, 1024 * 1024):
            if time.monotonic() - started >= args.max_seconds:
                stop_reason = "time limit"
                break
            if scanned >= args.max_mib * 1048576:
                stop_reason = "byte limit"
                break
            data = reader.read(base + offset, min(1024 * 1024 + 8, size - offset))
            scanned += len(data)
            for phase in range(0, 8, args.alignment):
                available = (len(data) - phase) // 8 * 8
                if available <= 0:
                    continue
                values = np.frombuffer(data, dtype="<u8", count=available // 8, offset=phase)
                values = values[:(1024 * 1024 - phase + 7) // 8]
                for target in args.address:
                    mask = (values >= max(0, target - args.range)) & (values <= target)
                    # Object offsets examined by the C# scanner are four-byte aligned.
                    mask &= ((np.uint64(target) - values) & np.uint64(3)) == 0
                    for index in np.flatnonzero(mask):
                        slot = base + offset + phase + int(index) * 8
                        value = int(values[index])
                        refs.append({"slot": hex(slot), "location": location(slot, loaded),
                                     "pointer": hex(value), "target": hex(target), "delta": target - value})
                        if len(refs) >= args.max_references:
                            stop_reason = "reference limit"
                            break
                    if stop_reason != "complete":
                        break
                if stop_reason != "complete":
                    break
            if time.monotonic() >= next_log:
                print(f"Pointer scan: {scanned / 1048576:.0f} MiB, {len(refs)} references, {time.monotonic() - started:.0f}s", flush=True)
                next_log = time.monotonic() + 5
            if stop_reason != "complete":
                break
        if stop_reason != "complete":
            break
    print(f"Pointer scan {stop_reason}: {len(refs)} references", flush=True)
    for reference in refs[:40]:
        print(f"{reference['location']} -> {reference['pointer']} + 0x{reference['delta']:X} = {reference['target']}", flush=True)
    return {"references": refs, "stats": {"complete": stop_reason == "complete", "stop_reason": stop_reason,
                                           "scanned_bytes": scanned, "elapsed_seconds": time.monotonic() - started}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("inspect", "pointers"))
    parser.add_argument("output", type=Path)
    parser.add_argument("--address", type=lambda x: int(x, 0), action="append", required=True)
    parser.add_argument("--window", default="SnowRunner")
    parser.add_argument("--range", type=lambda x: int(x, 0), default=0x400)
    parser.add_argument("--dump-radius", type=lambda x: int(x, 0), default=0x100)
    parser.add_argument("--alignment", type=int, choices=(4, 8), default=8)
    parser.add_argument("--max-seconds", type=float, default=90)
    parser.add_argument("--max-mib", type=int, default=8192)
    parser.add_argument("--max-references", type=int, default=10000)
    args = parser.parse_args()
    if ctypes.sizeof(ctypes.c_void_p) != 8:
        parser.error("Use 64-bit Python")
    if (not all(0x10000 <= a < 0x800000000000 for a in args.address) or
            not 0 <= args.range <= 0x10000 or not 16 <= args.dump_radius <= 0x10000 or
            args.max_seconds <= 0 or args.max_mib <= 0 or args.max_references <= 0):
        parser.error("Invalid address, radius, or scan limits")
    reader = Reader(args.window)
    try:
        loaded = modules(reader.pid)
        result = inspect(reader, args.address, args.dump_radius, loaded) if args.action == "inspect" else pointer_scan(reader, args, loaded)
        result.update({"pid": reader.pid, "process_created": reader.created, "time": time.time(),
                       "modules": loaded, "settings": {k: str(v) if isinstance(v, Path) else v for k, v in vars(args).items()}})
        args.output.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.output.with_suffix(args.output.suffix + ".tmp")
        temporary.write_text(json.dumps(result, separators=(",", ":"), allow_nan=False))
        os.replace(temporary, args.output)
        print(f"Saved {args.output}", flush=True)
    finally:
        reader.close()


if __name__ == "__main__":
    main()
