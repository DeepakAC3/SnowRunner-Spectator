"""Bounded, guarded height nudge for the SnowRunner freecam view matrix.

The diagnostic is intentionally fixed to the validated SnowRunner offsets.  It
checks the owner -> transform -> freecam controller chain, confirms freecam is
active, validates the row-major rigid transform, and compares the inverse
matrix position with the rendered Z,Y,X position before opening a write handle.
It then subtracts one unit of the transform's Y basis from its translation,
invalidates the two known 32-bit caches, samples for at most 0.5 seconds, and
restores the original translation.  Cache flags are invalidated again after
restoration so the game recomputes them instead of using stale values.

No addresses, delta, duration, or input keys are configurable.  The process
read handle remains read-only until all guards pass.  The only output write is
the JSON recovery/evidence file requested on the command line.
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

from camera_probe import Reader, SIZE_T, kernel32
from camera_pointer_probe import modules


# Whitelisted SnowRunner offsets.  Keep these constants local and fixed: this
# diagnostic must not turn into an arbitrary process-memory writer.
OWNER_OFFSET = 0x2A8EB90
RENDER_POINTER_OFFSET = 0x2A97EE0
FREECAM_FLAG_OFFSET = 0x2A5412C
TRANSFORM_OFFSET = 0x08
CONTROLLER_OFFSET = 0x18
CONTROLLER_TRANSFORM_OFFSET = 0x08
MATRIX_SIZE = 0x40
WORLD_POSITION_OFFSET = 0xB0
TRANSLATION_OFFSET = 0x30
CACHE_FLAG_OFFSETS = (0x80, 0xC0)
HEIGHT_DELTA = 1.0
SAMPLE_TIMES = (0.0, 0.05, 0.1, 0.25, 0.45)
SAMPLE_WINDOW = 0.5
RESTORE_WINDOW = 0.3
STABLE_ATTEMPTS = 5
STABLE_DELAY = 0.01
ORTHONORMAL_TOLERANCE = 0.02
IDENTITY_TOLERANCE = 0.02
POSITION_TOLERANCE = 0.1
RESTORE_TOLERANCE = 0.05


kernel32.WriteProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                        ctypes.c_void_p, SIZE_T,
                                        ctypes.POINTER(SIZE_T)]
kernel32.WriteProcessMemory.restype = wintypes.BOOL


def _finite(values):
    return all(math.isfinite(float(value)) for value in values)


def _max_error(first, second):
    return max(abs(float(a) - float(b)) for a, b in zip(first, second))


def _valid_pointer(value):
    return 0x10000 <= int(value) < 0x800000000000


def read_exact(reader, address, size):
    data = reader.read(address, size)
    if len(data) != size:
        raise RuntimeError(f"Short read at {hex(address)}: {len(data)} of {size} bytes")
    return data


def read_u64(reader, address, label):
    value = struct.unpack("<Q", read_exact(reader, address, 8))[0]
    if not _valid_pointer(value):
        raise RuntimeError(f"{label} pointer is invalid: {hex(value)}")
    return value


def matrix_values(data):
    values = struct.unpack("<16f", data)
    if not _finite(values):
        raise RuntimeError("View matrix contains a nonfinite value")
    return list(values)


def validate_matrix(matrix):
    """Validate the 3x3 basis and homogeneous row of a rigid row-major matrix."""
    rows = [matrix[0:3], matrix[4:7], matrix[8:11]]
    for row in rows:
        length = math.sqrt(sum(value * value for value in row))
        if abs(length - 1.0) > ORTHONORMAL_TOLERANCE:
            raise RuntimeError(f"View basis is not unit length: {length:.6f}")
    for first, second in ((rows[0], rows[1]), (rows[0], rows[2]), (rows[1], rows[2])):
        dot = sum(a * b for a, b in zip(first, second))
        if abs(dot) > ORTHONORMAL_TOLERANCE:
            raise RuntimeError(f"View basis is not orthogonal: dot={dot:.6f}")
    for offset in (3, 7, 11):
        if abs(matrix[offset]) > IDENTITY_TOLERANCE:
            raise RuntimeError(f"View matrix homogeneous value is not zero at {offset * 4:#x}")
    if abs(matrix[15] - 1.0) > IDENTITY_TOLERANCE:
        raise RuntimeError("View matrix homogeneous value is not one at 0x3c")


def reconstruct_zyx(matrix):
    """Invert a row-vector rigid transform, yielding stored Z,Y,X order."""
    translation = matrix[12:15]
    rows = (matrix[0:3], matrix[4:7], matrix[8:11])
    return [-sum(translation[i] * rows[axis][i] for i in range(3))
            for axis in range(3)]


def read_rendered(reader, module_base):
    pointer = read_u64(reader, module_base + RENDER_POINTER_OFFSET, "Rendered position")
    stored_zyx = struct.unpack("<fff", read_exact(reader, pointer, 12))
    if not _finite(stored_zyx):
        raise RuntimeError("Rendered position contains a nonfinite value")
    return {"address": hex(pointer), "stored_zyx": list(stored_zyx),
            "world_xyz": [stored_zyx[2], stored_zyx[1], stored_zyx[0]]}


def read_stable_matrix(reader, transform):
    """Return two identical consecutive matrix reads within a short bound."""
    previous = None
    for attempt in range(STABLE_ATTEMPTS):
        current = read_exact(reader, transform, MATRIX_SIZE)
        if previous == current:
            return matrix_values(current), attempt + 1
        previous = current
        if attempt + 1 < STABLE_ATTEMPTS:
            time.sleep(STABLE_DELAY)
    raise RuntimeError("View matrix did not remain stable during guard reads")


def read_observation(reader, module_base, transform, require_stable=True):
    if require_stable:
        matrix, attempts = read_stable_matrix(reader, transform)
    else:
        matrix = matrix_values(read_exact(reader, transform, MATRIX_SIZE))
        attempts = 1
    validate_matrix(matrix)
    reconstructed_zyx = reconstruct_zyx(matrix)
    reconstructed_xyz = [reconstructed_zyx[2], reconstructed_zyx[1], reconstructed_zyx[0]]
    cached_zyx = list(struct.unpack("<fff", read_exact(reader, transform + WORLD_POSITION_OFFSET, 12)))
    if not _finite(cached_zyx):
        raise RuntimeError("Cached source position contains a nonfinite value")
    rendered = read_rendered(reader, module_base)
    reconstruction_error = _max_error(reconstructed_zyx, rendered["stored_zyx"])
    if require_stable and reconstruction_error > POSITION_TOLERANCE:
        raise RuntimeError(f"Matrix/rendered position mismatch: {reconstruction_error:.6f}")
    cache_error = _max_error(reconstructed_zyx, cached_zyx)
    if require_stable and cache_error > POSITION_TOLERANCE:
        raise RuntimeError(f"Matrix/source position mismatch: {cache_error:.6f}")
    return {"matrix": matrix, "reconstructed_xyz": reconstructed_xyz,
            "reconstructed_zyx": reconstructed_zyx, "cached_zyx": cached_zyx,
            "rendered": rendered, "reconstruction_error": reconstruction_error,
            "cache_error": cache_error, "stable_reads": attempts}


def validate_chain(reader, module_base):
    flag = read_exact(reader, module_base + FREECAM_FLAG_OFFSET, 1)[0]
    if flag != 1:
        raise RuntimeError(f"Freecam flag must equal 1; found {flag}")
    owner = read_u64(reader, module_base + OWNER_OFFSET, "Camera owner")
    transform = read_u64(reader, owner + TRANSFORM_OFFSET, "Transform")
    controller = read_u64(reader, owner + CONTROLLER_OFFSET, "Freecam controller")
    controller_transform = read_u64(reader, controller + CONTROLLER_TRANSFORM_OFFSET,
                                    "Controller transform")
    if controller_transform != transform:
        raise RuntimeError("Freecam controller does not reference the camera transform")
    return {"freecam_flag": flag, "owner": hex(owner), "transform": hex(transform),
            "controller": hex(controller), "controller_transform": hex(controller_transform)}


def write_exact(handle, address, data):
    buffer = ctypes.create_string_buffer(data)
    written = SIZE_T()
    if (not kernel32.WriteProcessMemory(handle, address, buffer, len(data),
                                        ctypes.byref(written)) or written.value != len(data)):
        raise RuntimeError(f"Write failed at {hex(address)} ({written.value}/{len(data)} bytes; "
                           f"Win32 {ctypes.get_last_error()})")


def persist(path, result):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(result, indent=2, allow_nan=False))
    os.replace(temporary, path)


def restore(reader, write_handle, transform, original_translation, result):
    errors = []
    translation_readback = None
    try:
        write_exact(write_handle, transform + TRANSLATION_OFFSET, original_translation)
        translation_readback = read_exact(reader, transform + TRANSLATION_OFFSET,
                                          len(original_translation))
        result["restore_translation_readback"] = translation_readback.hex()
        result["restore_translation_exact"] = translation_readback == original_translation
        if translation_readback != original_translation:
            errors.append("translation readback differed before cache invalidation")
    except Exception as error:  # Continue to invalidate both flags after a partial restore.
        errors.append(f"translation: {error}")
    invalidated = []
    for offset in CACHE_FLAG_OFFSETS:
        try:
            write_exact(write_handle, transform + offset, struct.pack("<i", -1))
            invalidated.append(hex(offset))
        except Exception as error:
            errors.append(f"cache {offset:#x}: {error}")
    result["restore_cache_invalidated"] = invalidated
    if errors:
        result["restore_errors"] = errors
        return False
    return True


def restoration_check(reader, module_base, transform, before):
    deadline = time.monotonic() + RESTORE_WINDOW
    latest = None
    while time.monotonic() < deadline:
        try:
            latest = read_observation(reader, module_base, transform, require_stable=False)
            errors = {
                "source": _max_error(latest["cached_zyx"], before["cached_zyx"]),
                "reconstructed": _max_error(latest["reconstructed_zyx"], before["reconstructed_zyx"]),
                "rendered": _max_error(latest["rendered"]["stored_zyx"], before["rendered"]["stored_zyx"]),
            }
            if all(value <= RESTORE_TOLERANCE for value in errors.values()):
                return True, latest, errors
        except Exception:
            pass
        time.sleep(0.01)
    return False, latest, ({"source": None, "reconstructed": None, "rendered": None}
                           if latest is None else errors)


def probe(reader, module_base, output):
    result = {"pid": reader.pid, "process_created": reader.created, "time": time.time(),
              "module_base": hex(module_base), "status": "guarding", "write_attempted": False,
              "restored": False, "samples": []}
    write_handle = None
    transform = None
    original_translation = None
    write_started = None
    try:
        chain = validate_chain(reader, module_base)
        transform = int(chain["transform"], 16)
        result["chain"] = chain
        before = read_observation(reader, module_base, transform, require_stable=True)
        result["before"] = before
        original_translation = struct.pack("<fff", *before["matrix"][12:15])
        original_flags = {hex(offset): read_exact(reader, transform + offset, 4).hex()
                          for offset in CACHE_FLAG_OFFSETS}
        result["original_write_bytes"] = {
            "translation_address": hex(transform + TRANSLATION_OFFSET),
            "translation": original_translation.hex(),
            "cache_flags": original_flags,
        }
        # Persist recovery bytes before acquiring a write handle or attempting a write.
        persist(output, result)
        if read_exact(reader, module_base + FREECAM_FLAG_OFFSET, 1) != b"\x01":
            raise RuntimeError("Freecam flag changed immediately before write")
        chain_now = validate_chain(reader, module_base)
        if chain_now["transform"] != chain["transform"]:
            raise RuntimeError("Camera transform changed immediately before write")
        prewrite = read_observation(reader, module_base, transform, require_stable=True)
        if _max_error(prewrite["matrix"], before["matrix"]) > 0.001:
            raise RuntimeError("Camera matrix changed immediately before write")
        result["prewrite_guard"] = prewrite
        write_handle = kernel32.OpenProcess(0x438, False, reader.pid)
        if not write_handle:
            raise ctypes.WinError(ctypes.get_last_error())
        matrix = before["matrix"]
        row_y = matrix[4:7]
        target_translation = [matrix[12 + index] - row_y[index] * HEIGHT_DELTA
                              for index in range(3)]
        target_bytes = struct.pack("<fff", *target_translation)
        result.update({"delta_height": HEIGHT_DELTA, "target_translation": target_translation,
                       "target_translation_bytes": target_bytes.hex()})
        persist(output, result)
        write_started = time.monotonic()
        result["write_attempted"] = True
        write_exact(write_handle, transform + TRANSLATION_OFFSET, target_bytes)
        for offset in CACHE_FLAG_OFFSETS:
            write_exact(write_handle, transform + offset, struct.pack("<i", -1))
        for sample_time in SAMPLE_TIMES:
            delay = write_started + sample_time - time.monotonic()
            if delay > 0:
                time.sleep(delay)
            if time.monotonic() - write_started >= SAMPLE_WINDOW:
                result["sampling_cutoff"] = True
                break
            sample = read_observation(reader, module_base, transform, require_stable=False)
            sample["elapsed_seconds"] = time.monotonic() - write_started
            result["samples"].append(sample)
        result["status"] = "sampled"
    except Exception as error:
        result["status"] = "failed"
        result["error"] = str(error)
    finally:
        if result["write_attempted"] and write_handle and transform and original_translation:
            try:
                restored_write = restore(reader, write_handle, transform, original_translation, result)
                result["restored_write"] = restored_write
                if restored_write:
                    ok, latest, errors = restoration_check(reader, module_base, transform, result["before"])
                    result["restoration_check"] = {"ok": ok, "errors": errors, "observation": latest}
                    result["restored"] = ok
                else:
                    result["restored"] = False
            except Exception as error:
                result["restore_error"] = str(error)
                result["restored"] = False
            result["write_window_seconds"] = time.monotonic() - (write_started or time.monotonic())
        if write_handle:
            kernel32.CloseHandle(write_handle)
        persist(output, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if ctypes.sizeof(ctypes.c_void_p) != 8:
        parser.error("Use 64-bit Python for SnowRunner's 64-bit process")
    reader = Reader("SnowRunner")
    try:
        main_module = next((module for module in modules(reader.pid)
                            if module["name"].casefold() == "snowrunner.exe"), None)
        required = max(OWNER_OFFSET, RENDER_POINTER_OFFSET, FREECAM_FLAG_OFFSET) + 8
        if main_module is None or main_module["size"] <= required:
            raise RuntimeError("SnowRunner executable does not contain the whitelisted offsets")
        result = probe(reader, main_module["base"], args.output)
        print(json.dumps(result, indent=2, allow_nan=False), flush=True)
        return 0 if result["status"] == "sampled" and result["restored"] else 1
    finally:
        reader.close()


if __name__ == "__main__":
    raise SystemExit(main())
