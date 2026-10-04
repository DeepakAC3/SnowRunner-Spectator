"""Temporarily raise the whitelisted freecam source height by one unit.

This diagnostic requires active freecam and matching source/render coordinates.
It writes only four bytes at SnowRunner.exe+0x2AEF120, samples rendered state for
half a second, then restores the original bytes in a finally block. No input is
sent and neither target addresses nor displacement are configurable.
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

from camera_probe import Reader, kernel32, SIZE_T
from camera_pointer_probe import modules

SOURCE_OFFSET = 0x2AEF11C
RENDER_POINTER_OFFSET = 0x2A97EE0
FREECAM_FLAG_OFFSET = 0x2A5412C
SAMPLE_TIMES = (0.0, 0.05, 0.1, 0.25, 0.5)

kernel32.WriteProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                       ctypes.c_void_p, SIZE_T, ctypes.POINTER(SIZE_T)]
kernel32.WriteProcessMemory.restype = wintypes.BOOL


def read_exact(reader, address, size):
    data = reader.read(address, size)
    if len(data) != size:
        raise RuntimeError(f"Short read at {hex(address)}: {len(data)} of {size} bytes")
    return data


def read_vector(reader, address):
    value = list(struct.unpack("<fff", read_exact(reader, address, 12)))
    if not all(math.isfinite(v) for v in value):
        raise RuntimeError(f"Nonfinite position at {hex(address)}")
    return value


def rendered(reader, module_base):
    pointer = struct.unpack("<Q", read_exact(reader, module_base + RENDER_POINTER_OFFSET, 8))[0]
    if not 0x10000 <= pointer < 0x800000000000:
        raise RuntimeError("Rendered camera pointer is invalid")
    return {"address": hex(pointer), "stored_zyx": read_vector(reader, pointer)}


def persist(path, result):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(result, indent=2, allow_nan=False))
    os.replace(temporary, path)


def write_height(handle, address, data):
    if len(data) != 4:
        raise ValueError("The height diagnostic accepts exactly four bytes")
    buffer = ctypes.create_string_buffer(data)
    written = SIZE_T()
    if not kernel32.WriteProcessMemory(handle, address, buffer, 4, ctypes.byref(written)) or written.value != 4:
        raise RuntimeError(f"Height write failed at {hex(address)} (bytes written: {written.value}, Win32: {ctypes.get_last_error()})")


def probe(reader, module_base, output):
    source_address = module_base + SOURCE_OFFSET
    height_address = source_address + 4
    result = {"pid": reader.pid, "process_created": reader.created, "time": time.time(),
              "module_base": hex(module_base), "source_address": hex(source_address),
              "height_address": hex(height_address), "delta_height": 1.0,
              "write_attempted": False, "restored": False, "samples": [], "status": "guarding"}
    write_handle = None
    original_bytes = None
    started = None
    try:
        flag = read_exact(reader, module_base + FREECAM_FLAG_OFFSET, 1)[0]
        result["freecam_flag"] = flag
        if flag != 1:
            raise RuntimeError("Freecam flag must equal 1; diagnostic refused")
        source = read_vector(reader, source_address)
        before = rendered(reader, module_base)
        result.update({"source_before_zyx": source, "rendered_before": before})
        if any(abs(a - b) > 0.1 for a, b in zip(source, before["stored_zyx"])):
            raise RuntimeError("Source and rendered positions differ by more than 0.1; diagnostic refused")
        original_bytes = read_exact(reader, height_address, 4)
        original_height = struct.unpack("<f", original_bytes)[0]
        # Ensure the original byte read agrees with the guarded vector snapshot.
        if not math.isfinite(original_height) or abs(original_height - source[1]) > 0.001:
            raise RuntimeError("Source height changed during guard reads; diagnostic refused")
        target_bytes = struct.pack("<f", original_height + 1.0)
        result.update({"original_height_bytes": original_bytes.hex(), "original_height": original_height,
                       "target_height": struct.unpack("<f", target_bytes)[0]})
        write_handle = kernel32.OpenProcess(0x438, False, reader.pid)
        if not write_handle:
            raise ctypes.WinError(ctypes.get_last_error())
        # Persist recovery data before any write is attempted.
        persist(output, result)
        if read_exact(reader, module_base + FREECAM_FLAG_OFFSET, 1) != b"\x01":
            raise RuntimeError("Freecam flag changed immediately before write; diagnostic refused")
        if read_exact(reader, height_address, 4) != original_bytes:
            raise RuntimeError("Source height changed immediately before write; diagnostic refused")
        result["write_attempted"] = True
        started = time.monotonic()
        write_height(write_handle, height_address, target_bytes)
        for sample_time in SAMPLE_TIMES:
            delay = started + sample_time - time.monotonic()
            if delay > 0:
                time.sleep(delay)
            if time.monotonic() - started >= 0.75:
                result["sampling_cutoff"] = True
                break
            sample = {"elapsed_seconds": time.monotonic() - started,
                      "source_zyx": read_vector(reader, source_address),
                      "rendered": rendered(reader, module_base)}
            result["samples"].append(sample)
        result["status"] = "sampled"
    except Exception as error:
        result["status"] = "failed"
        result["error"] = str(error)
    finally:
        if result["write_attempted"]:
            try:
                # Restore even when the initial write or a subsequent read failed.
                write_height(write_handle, height_address, original_bytes)
                restored_bytes = read_exact(reader, height_address, 4)
                restored_height = struct.unpack("<f", restored_bytes)[0]
                result["restored_height"] = restored_height
                result["restored_height_bytes"] = restored_bytes.hex()
                result["restored"] = math.isfinite(restored_height) and abs(restored_height - original_height) <= 0.001
                if not result["restored"]:
                    raise RuntimeError("Restored height did not match the original")
            except Exception as error:
                result["status"] = "restore_failed"
                result["restore_error"] = str(error)
            if started is not None:
                result["write_window_seconds"] = time.monotonic() - started
        if write_handle:
            kernel32.CloseHandle(write_handle)
        persist(output, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if ctypes.sizeof(ctypes.c_void_p) != 8:
        parser.error("Use 64-bit Python")
    reader = Reader("SnowRunner")
    try:
        main_module = next((m for m in modules(reader.pid) if m["name"].casefold() == "snowrunner.exe"), None)
        if main_module is None or main_module["size"] <= max(SOURCE_OFFSET + 12, FREECAM_FLAG_OFFSET + 1, RENDER_POINTER_OFFSET + 8):
            raise RuntimeError("SnowRunner executable does not contain the whitelisted offsets")
        result = probe(reader, main_module["base"], args.output)
        print(json.dumps(result, indent=2, allow_nan=False), flush=True)
        return 0 if result["status"] == "sampled" and result["restored"] else 1
    finally:
        reader.close()


if __name__ == "__main__":
    raise SystemExit(main())
