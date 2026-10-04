"""Read-only scanner for alternate SnowRunner camera coordinate layouts.

The confirmed render position is a float32 Z,Y,X vector.  This diagnostic looks
for the same world position in other representations: contiguous float64 and
float32/float64 triples whose components are separated by 8, 16, or 32 bytes.
Every candidate records the axis permutation and signs that map its raw values
to canonical X,Y,Z.  A later capture can retain only layouts that stayed still,
moved, or reversed direction.

This module never sends input and never opens a process with VM_WRITE access.
The only output write is the JSON snapshot requested on the command line.
"""

import argparse
import ctypes
import itertools
import json
import math
import os
import struct
import time
from pathlib import Path

from camera_probe import Reader, np

try:
    from camera_pointer_probe import modules, location
except ImportError:  # pragma: no cover - useful when helpers are embedded
    modules = None
    location = None


RENDER_POINTER_OFFSET = 0x2A97EE0
CHUNK_BYTES = 1024 * 1024
STRIDES = (8, 16, 32)
DTYPES = ("f4", "f8")
PERMUTATIONS = tuple(itertools.permutations(range(3)))
SIGNS = tuple(itertools.product((-1, 1), repeat=3))


def _finite(values):
    return all(math.isfinite(float(value)) for value in values)


def _mapping_name(permutation, signs):
    axes = "XYZ"
    return "".join(("-" if sign < 0 else "") + axes[index]
                   for index, sign in zip(permutation, signs))


def _mapped(raw, permutation, signs):
    return [float(signs[i] * raw[permutation[i]]) for i in range(3)]


def _matching_mappings(raw, center, radius):
    """Return all sign/permutation mappings matching canonical *center*."""
    if not _finite(raw):
        return []
    matches = []
    for permutation in PERMUTATIONS:
        for signs in SIGNS:
            mapped = _mapped(raw, permutation, signs)
            if all(abs(value - target) <= radius
                   for value, target in zip(mapped, center)):
                matches.append({"raw_indices": list(permutation),
                                "signs": list(signs),
                                "name": _mapping_name(permutation, signs)})
    return matches


def _candidate(address, dtype, stride, raw, mapping):
    canonical = _mapped(raw, mapping["raw_indices"], mapping["signs"])
    return {"address": hex(address), "dtype": dtype, "stride": stride,
            "mapping": mapping, "raw_values": list(raw),
            "values": [canonical]}


def _numpy_hits(data, base_address, core_end, dtype, stride, center, radius, alignment):
    """Yield candidates from one memory chunk using NumPy.

    *core_end* is an offset into data.  Starts at or beyond it belong to the
    next chunk, which prevents duplicate candidates while the overlap keeps
    layouts crossing a chunk boundary visible.
    """
    item_size = 4 if dtype == "f4" else 8
    step = stride // item_size
    if len(data) < item_size or stride % item_size:
        return
    target_abs = np.abs(np.asarray(center, dtype=float))
    # A phase is relative to this chunk's address, so account for the region's
    # actual base when applying the requested global address alignment.  The
    # wider bound matters for f32 values in an 8-byte aligned scan.
    for phase in range(max(item_size, alignment)):
        if (base_address + phase) % alignment:
            continue
        available = (len(data) - phase) // item_size
        if available < 1 + 2 * step:
            continue
        values = np.frombuffer(data, dtype="<" + dtype, count=available,
                               offset=phase)
        first_count = max(0, (min(core_end, len(data)) - phase + item_size - 1) // item_size)
        count = min(first_count, len(values) - 2 * step)
        if count <= 0:
            continue
        starts = values[:count]
        # The first component can be any canonical axis, so reject a chunk
        # position unless it is close to one of the three target magnitudes.
        # Only the surviving indices need strided gathers and mapping checks.
        with np.errstate(invalid="ignore", over="ignore"):
            magnitudes = np.abs(starts)
            coarse = np.isfinite(starts)
            coarse &= np.logical_or.reduce(
                [(np.abs(magnitudes - target) <= radius) for target in target_abs])
        indices = np.flatnonzero(coarse)
        index_step = alignment // math.gcd(alignment, item_size)
        if index_step > 1:
            indices = indices[indices % index_step == 0]
        for index in indices:
            index = int(index)
            raw = [float(starts[index]), float(values[index + step]),
                   float(values[index + 2 * step])]
            address = base_address + phase + index * item_size
            for mapping in _matching_mappings(raw, center, radius):
                yield _candidate(address, dtype, stride, raw, mapping)


def _python_hits(data, base_address, core_end, dtype, stride, center, radius, alignment):
    """Small-memory fallback for environments without NumPy."""
    item_size = 4 if dtype == "f4" else 8
    format_code = "f" if dtype == "f4" else "d"
    limit = min(core_end, len(data))
    target_abs = [abs(value) for value in center]
    first_start = (-base_address) % alignment
    for start in range(first_start, max(0, limit), alignment):
        if start + 2 * stride + item_size > len(data):
            break
        try:
            first = struct.unpack_from("<" + format_code, data, start)[0]
        except struct.error:
            continue
        if not math.isfinite(first) or not any(abs(abs(first) - target) <= radius
                                               for target in target_abs):
            continue
        try:
            raw = [first] + [struct.unpack_from("<" + format_code, data, start + n * stride)[0]
                             for n in (1, 2)]
        except struct.error:
            continue
        if not _finite(raw):
            continue
        for mapping in _matching_mappings(raw, center, radius):
            yield _candidate(base_address + start, dtype, stride, raw, mapping)


def _iter_hits(data, base_address, core_end, dtype, stride, center, radius, alignment):
    if np is not None:
        yield from _numpy_hits(data, base_address, core_end, dtype, stride, center, radius, alignment)
    else:
        yield from _python_hits(data, base_address, core_end, dtype, stride, center, radius, alignment)


def baseline(reader, args):
    """Scan readable regions for exact coordinate representations."""
    started = time.monotonic()
    scanned = 0
    layouts = []
    seen = set()
    stop_reason = "complete"
    next_log = started + 5
    overlap = max(STRIDES) * 2 + 8
    for base, size in reader.regions():
        for offset in range(0, size, CHUNK_BYTES):
            if time.monotonic() - started >= args.max_seconds:
                stop_reason = "time limit"
                break
            if scanned >= args.max_mib * 1048576:
                stop_reason = "byte limit"
                break
            remaining = args.max_mib * 1048576 - scanned
            requested = min(CHUNK_BYTES + overlap, size - offset, remaining)
            data = reader.read(base + offset, requested)
            scanned += len(data)
            if not data:
                continue
            core_end = min(CHUNK_BYTES, size - offset, len(data))
            for dtype in args.dtype:
                for stride in args.stride:
                    for row in _iter_hits(data, base + offset, core_end,
                                          dtype, stride, args.center, args.radius,
                                          args.alignment):
                        key = (row["address"], row["dtype"], row["stride"],
                               tuple(row["mapping"]["raw_indices"]),
                               tuple(row["mapping"]["signs"]))
                        if key in seen:
                            continue
                        seen.add(key)
                        layouts.append(row)
                        if len(layouts) >= args.max_candidates:
                            stop_reason = "candidate limit"
                            break
                    if stop_reason != "complete":
                        break
                if stop_reason != "complete":
                    break
            if time.monotonic() >= started + args.max_seconds:
                stop_reason = "time limit"
            if time.monotonic() >= next_log:
                print(f"Scanned {scanned / 1048576:.0f} MiB; {len(layouts)} layouts",
                      flush=True)
                next_log = time.monotonic() + 5
            if stop_reason != "complete":
                break
        if stop_reason != "complete":
            break
    return layouts, {"scanned_bytes": scanned, "layout_count": len(layouts),
                     "scan_complete": stop_reason == "complete",
                     "stop_reason": stop_reason,
                     "elapsed_seconds": time.monotonic() - started}


def _read_layout(reader, row):
    address = int(row["address"], 16)
    item_size = 4 if row["dtype"] == "f4" else 8
    fmt = "f" if row["dtype"] == "f4" else "d"
    stride = int(row["stride"])
    data = reader.read(address, 2 * stride + item_size)
    if len(data) != 2 * stride + item_size:
        return None
    raw = [struct.unpack_from("<" + fmt, data, n * stride)[0] for n in range(3)]
    if not _finite(raw):
        return None
    mapping = row["mapping"]
    return raw, _mapped(raw, mapping["raw_indices"], mapping["signs"])


def capture(reader, snapshot, args):
    """Read only prior candidates and retain rows matching capture mode."""
    source = snapshot.get("layouts", snapshot.get("candidates", []))
    layouts = []
    failed = 0
    read_bytes = 0
    started = time.monotonic()
    stop_reason = "complete"
    for row in source:
        if time.monotonic() - started >= args.max_seconds:
            stop_reason = "time limit"
            break
        if read_bytes >= args.max_mib * 1048576:
            stop_reason = "byte limit"
            break
        extent = 2 * int(row["stride"]) + (4 if row["dtype"] == "f4" else 8)
        if read_bytes + extent > args.max_mib * 1048576:
            stop_reason = "byte limit"
            break
        try:
            result = _read_layout(reader, row)
            read_bytes += extent
        except (KeyError, TypeError, ValueError, struct.error):
            result = None
        if result is None:
            failed += 1
            continue
        raw, value = result
        prior_values = row.get("values") or [row.get("canonical_values")]
        if not prior_values or prior_values[-1] is None:
            failed += 1
            continue
        prior = [float(x) for x in prior_values[-1]]
        length = math.sqrt(sum((a - b) ** 2 for a, b in zip(value, prior)))
        keep = (length <= args.max_delta if args.mode == "stationary"
                else args.min_delta <= length <= args.max_delta)
        if keep and args.mode == "reverse":
            if len(prior_values) < 2:
                raise ValueError("Reverse capture needs a preceding movement capture")
            old = [a - b for a, b in zip(prior_values[-1], prior_values[-2])]
            old_length = math.sqrt(sum(x * x for x in old))
            new_length = length
            keep = old_length > 0 and new_length > 0 and (
                sum(a * b for a, b in zip(old, [a - b for a, b in zip(value, prior)])) /
                (old_length * new_length) <= -args.reverse_cosine and
                0.25 <= new_length / old_length <= 4)
        if keep:
            saved = dict(row)
            saved["raw_values"] = list(raw)
            saved["values"] = list(prior_values) + [list(value)]
            layouts.append(saved)
            if len(layouts) >= args.max_candidates:
                stop_reason = "candidate limit"
                break
    return layouts, {"input_layouts": len(source), "layout_count": len(layouts),
                     "failed_reads": failed, "read_bytes": read_bytes,
                     "capture_mode": args.mode, "scan_complete": stop_reason == "complete",
                     "stop_reason": stop_reason,
                     "elapsed_seconds": time.monotonic() - started}


def _known_render_pointer(reader, loaded):
    """Return the known render pointer for context when it can be read."""
    main = next((m for m in loaded or [] if m["name"].casefold() == "snowrunner.exe"), None)
    if not main:
        return None
    data = reader.read(main["base"] + RENDER_POINTER_OFFSET, 8)
    if len(data) != 8:
        return None
    pointer = struct.unpack("<Q", data)[0]
    if not 0x10000 <= pointer < 0x800000000000:
        return None
    result = {"address": hex(pointer),
              "location": location(pointer, loaded) if location else hex(pointer),
              "module_offset": hex(RENDER_POINTER_OFFSET)}
    position = reader.read(pointer, 12)
    if len(position) == 12:
        stored_zyx = struct.unpack("<fff", position)
        if _finite(stored_zyx):
            result["stored_zyx"] = list(stored_zyx)
            result["world_xyz"] = [stored_zyx[2], stored_zyx[1], stored_zyx[0]]
    return result


def _persist(path, result):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(result, separators=(",", ":"), allow_nan=False))
    os.replace(temporary, path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("baseline", "capture"))
    parser.add_argument("output", type=Path)
    parser.add_argument("--input", type=Path, help="previous layout snapshot for capture")
    parser.add_argument("--window", default="SnowRunner")
    parser.add_argument("--center", nargs=3, type=float, default=(894, 60, 513),
                        metavar=("X", "Y", "Z"), help="canonical X Y Z position")
    parser.add_argument("--radius", type=float, default=0.02,
                        help="per-component absolute match tolerance")
    parser.add_argument("--alignment", type=int, choices=(1, 4, 8), default=4,
                        help="global candidate start alignment (default: 4; use 1 for exhaustive)")
    parser.add_argument("--dtype", choices=DTYPES, action="append", default=None,
                        help="value width to scan; repeat (default: f4 and f8)")
    parser.add_argument("--stride", type=int, choices=STRIDES, action="append", default=None,
                        help="component byte stride; repeat (default: 8, 16, 32)")
    parser.add_argument("--max-seconds", type=float, default=120)
    parser.add_argument("--max-mib", type=int, default=2048)
    parser.add_argument("--max-candidates", type=int, default=500000)
    parser.add_argument("--mode", choices=("moved", "stationary", "reverse"), default="moved")
    parser.add_argument("--min-delta", type=float, default=0.05)
    parser.add_argument("--max-delta", type=float, default=200)
    parser.add_argument("--reverse-cosine", type=float, default=0.9)
    args = parser.parse_args()
    args.dtype = tuple(args.dtype or DTYPES)
    args.stride = tuple(args.stride or STRIDES)
    if ctypes.sizeof(ctypes.c_void_p) != 8:
        parser.error("Use 64-bit Python for SnowRunner's 64-bit process")
    if args.action == "capture" and not args.input:
        parser.error("capture requires --input")
    if args.mode == "stationary" and args.max_delta == 200:
        args.max_delta = 0.05
    if not (all(math.isfinite(value) for value in args.center) and
            math.isfinite(args.radius) and args.radius >= 0 and
            math.isfinite(args.max_seconds) and args.max_seconds > 0 and args.max_mib > 0 and
            args.max_candidates > 0 and 0 <= args.min_delta <= args.max_delta and
            math.isfinite(args.min_delta) and math.isfinite(args.max_delta) and
            0 <= args.reverse_cosine <= 1 and math.isfinite(args.reverse_cosine)):
        parser.error("Invalid bounds, thresholds, or time limits")
    snapshot = json.loads(args.input.read_text()) if args.input else None
    reader = Reader(args.window)
    try:
        if snapshot and (snapshot.get("pid") != reader.pid or
                         snapshot.get("process_created") != reader.created):
            raise RuntimeError("Snapshot belongs to a different SnowRunner process; take a new baseline")
        loaded = modules(reader.pid) if modules else []
        print(f"{args.action}: {reader.title}; PID {reader.pid}", flush=True)
        rows, stats = (baseline(reader, args) if args.action == "baseline"
                       else capture(reader, snapshot, args))
        result = {"pid": reader.pid, "process_created": reader.created, "time": time.time(),
                  "source": str(args.input) if args.input else None,
                  "known_render_pointer": _known_render_pointer(reader, loaded),
                  "modules": loaded,
                  "settings": {k: str(v) if isinstance(v, Path) else v
                               for k, v in vars(args).items()},
                  "stats": stats, "layouts": rows, "candidates": rows}
        _persist(args.output, result)
        print(f"Saved {len(rows)} layouts to {args.output}; {stats}", flush=True)
        for row in rows[:20]:
            print(f"{row['address']} {row['dtype']} stride={row['stride']} "
                  f"{row['mapping']['name']}: {row['values'][-1]}", flush=True)
        return 0
    finally:
        reader.close()


if __name__ == "__main__":
    raise SystemExit(main())
