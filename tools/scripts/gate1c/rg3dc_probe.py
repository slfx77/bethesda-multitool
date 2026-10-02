# SPDX-License-Identifier: 0BSD
"""Gate-1c hop A7 oracle: independent Redguard ``.3DC`` decoding.

Restates the layout from the cut-1c plan (sections 0.2, 4 and 6.1) and the planning census
receipts; shares no code with BMT's C# readers. A ``.3DC`` is a 64-byte ``.3D``-shaped header, a
frame block at header +20 whose dword 0 is the frame-table offset and dword 2 the length of the
one unaccounted region, a frame table of ``frame_count`` records of 3 dwords (narrow) or 4 (wide),
the plane list at header +60, and per frame a points block, a normals block and a plane-data
block. Acceptance is EXACT TILING: for exactly one width, the declared blocks plus the header,
frame block, frame table and plane list cover the file with at most one gap of exactly the
declared unaccounted length.

Poses: frame 0 (the keyframe) is int32 triples; later frames are int32 triples on wide files and
int16 DELTAS ADDED TO THE KEYFRAME on narrow files, never accumulated frame to frame. The kept
corner rule for the reader is pose independent: a corner is kept when the reference corner test
keeps it in at least one pose.

The trap this oracle also demonstrates (plan slice 2): a ``.3DC`` walks as a static ``.3D``
without error, but the header's +48/+52/+24 offsets are frame 1's on wide files and point at no
frame on narrow files, so the points such a walk returns are WRONG.
"""

import struct

from xngine_probe import (ProbeError, kept_corners, read_header, sha256_hex, walk_planes, _i32)

NARROW_RECORD_DWORDS = 3
WIDE_RECORD_DWORDS = 4
PROBE_PREFIX = 64 * 1024


def shape_test(b, prefix_length=PROBE_PREFIX):
    """The plan 6.1 shape test S(b) over a probe prefix: header +16 > 0, the +20 frame block
    inside the prefix, the table offset at or before the plane-list offset, and the table span a
    whole number of 3- or 4-dword records per frame. True on 147 of 147 retail .3DC files and on
    no static mesh."""
    if len(b) < 64:
        return False
    fc = _i32(b, 16)
    fbo = _i32(b, 20)
    plo = _i32(b, 60)
    if fc <= 0 or fbo <= 0 or fbo + 24 > min(len(b), prefix_length):
        return False
    table_offset = _i32(b, fbo)
    if table_offset < 0 or table_offset > plo or plo > len(b):
        return False
    span = plo - table_offset
    if span <= 0 or span % (4 * fc):
        return False
    return span // (4 * fc) in (NARROW_RECORD_DWORDS, WIDE_RECORD_DWORDS)


def _tiling(blocks, length, unaccounted):
    """Exact-tiling check: blocks must fit, must not overlap, and must leave at most one gap of
    exactly the declared unaccounted length."""
    for start, end in blocks:
        if start < 0 or end > length:
            return {"ok": False, "why": f"block {start}..{end} outside {length}"}
    ordered = sorted(blocks)
    cursor = 0
    gaps = []
    for start, end in ordered:
        if start > cursor:
            gaps.append((cursor, start - cursor))
        elif start < cursor:
            return {"ok": False, "why": f"overlap at {start} (covered to {cursor})"}
        cursor = max(cursor, end)
    if cursor < length:
        gaps.append((cursor, length - cursor))
    total = sum(size for _, size in gaps)
    if len(gaps) > 1 or total != unaccounted:
        return {"ok": False, "why": f"{total} bytes in {len(gaps)} gaps vs declared {unaccounted}"}
    return {"ok": True, "gap": gaps[0] if gaps else None}


def parse(b):
    """Full parse with the exact-tiling acceptance. Returns header facts, the frame-table records
    (all dwords, the 4th kept when present), the width, the plane list, the keyframe and every
    rebuilt integer pose."""
    n = len(b)
    h = read_header(b)
    pc, plc = h["point_count"], h["plane_count"]
    fc, fbo = h["h16"], h["h20"]
    plo = h["plane_list_offset"]
    if pc <= 0 or plc <= 0 or fc <= 0:
        raise ProbeError(f"counts {pc}/{plc}/{fc} are not all positive")
    if fbo < 0 or fbo + 24 > n:
        raise ProbeError("frame block outside the file")
    preamble = [_i32(b, fbo + 4 * i) for i in range(6)]
    table_offset = preamble[0]
    unaccounted = preamble[2]
    if table_offset < 0 or table_offset > plo or plo > n:
        raise ProbeError("frame table does not precede the plane list")
    span = plo - table_offset
    if span % (4 * fc):
        raise ProbeError("frame table is not whole records")
    record_dwords = span // (4 * fc)
    if record_dwords not in (NARROW_RECORD_DWORDS, WIDE_RECORD_DWORDS):
        raise ProbeError(f"record of {record_dwords} dwords")
    table = []
    for i in range(fc):
        o = table_offset + i * record_dwords * 4
        table.append([_i32(b, o + 4 * d) for d in range(record_dwords)])
    planes, plane_list_end = walk_planes(b, h, 8)
    results = {}
    for wide in (True, False):
        blocks = [(0, 64), (fbo, plo), (plo, plane_list_end)]
        for i, record in enumerate(table):
            points_offset, normals_offset, plane_data_offset = record[0], record[1], record[2]
            point_bytes = 12 if (i == 0 or wide) else 6
            blocks.append((points_offset, points_offset + pc * point_bytes))
            blocks.append((normals_offset, normals_offset + plc * (12 if wide else 4)))
            blocks.append((plane_data_offset, plane_data_offset + plc * (24 if wide else 12)))
        results["wide" if wide else "narrow"] = _tiling(blocks, n, unaccounted)
    tiled = [k for k, v in results.items() if v["ok"]]
    if len(tiled) != 1:
        raise ProbeError("tiling accepts " + (" and ".join(tiled) if tiled else "neither width")
                         + ": " + "; ".join(f"{k}: {v.get('why', 'ok')}" for k, v in results.items()))
    width = tiled[0]
    declared = {"wide": WIDE_RECORD_DWORDS, "narrow": NARROW_RECORD_DWORDS}[width]
    if record_dwords != declared:
        raise ProbeError(f"{record_dwords}-dword records but {width} tiling")
    keyframe = _read_points(b, table[0][0], pc)
    poses = [keyframe]
    for i in range(1, fc):
        if width == "wide":
            poses.append(_read_points(b, table[i][0], pc))
        else:
            o = table[i][0]
            poses.append([(keyframe[j][0] + struct.unpack_from("<h", b, o + 6 * j)[0],
                           keyframe[j][1] + struct.unpack_from("<h", b, o + 6 * j + 2)[0],
                           keyframe[j][2] + struct.unpack_from("<h", b, o + 6 * j + 4)[0])
                          for j in range(pc)])
    gap = results[width]["gap"]
    return {"header": h, "preamble": preamble, "table": table, "record_dwords": record_dwords,
            "width": width, "frame_count": fc, "point_count": pc, "plane_count": plc,
            "planes": planes, "plane_list_end": plane_list_end, "poses": poses,
            "unaccounted": unaccounted, "gap": gap, "payload_sha256": sha256_hex(b)}


def _read_points(b, offset, count):
    return [(_i32(b, offset + 12 * i), _i32(b, offset + 12 * i + 4), _i32(b, offset + 12 * i + 8))
            for i in range(count)]


def narrow_deltas(b, parsed, frame):
    """The raw int16 delta triples of one narrow frame (frame >= 1), for bit-flip detection."""
    if parsed["width"] != "narrow":
        raise ProbeError("not a narrow file")
    o = parsed["table"][frame][0]
    pc = parsed["point_count"]
    return [struct.unpack_from("<hhh", b, o + 6 * j) for j in range(pc)]


def pose_independent_kept(parsed):
    """Plan section 4: per n-gon, the reader's corner rule - a corner is kept when ANY pose keeps
    it. Returns per-plane kept lists (reference order, union over poses) plus which n-gons keep a
    different list in some pose than in the keyframe."""
    planes = parsed["planes"]
    poses = parsed["poses"]
    union = {}
    keyframe_lists = {}
    differing = []
    for plane in planes:
        if plane["count"] <= 3:
            continue
        corners = [ci for ci, _, _ in plane["corners"]]
        keyframe_kept = kept_corners([poses[0][ci] for ci in corners])
        keyframe_lists[plane["index"]] = keyframe_kept
        kept_any = set(keyframe_kept)
        differs = False
        for pose in poses[1:]:
            kept = kept_corners([pose[ci] for ci in corners])
            if kept != keyframe_kept:
                differs = True
            kept_any.update(kept)
        # Reference order over the union: c1..c(n-1), c0 restricted to the kept set.
        order = list(range(1, plane["count"])) + [0]
        union[plane["index"]] = [c for c in order if c in kept_any]
        if differs:
            differing.append(plane["index"])
    return {"union_kept": union, "keyframe_kept": keyframe_lists, "pose_dependent_ngons": differing}


def walk_as_3d_trap(b, parsed):
    """The .3DC-as-.3D trap: read the point list a static .3D walk would use (header +48) and
    compare it with the keyframe from the frame table. On wide files the header offsets are frame
    1's; on narrow files they point at no frame. Returns the mismatch census; a correct trap
    demonstration has wrong_points > 0."""
    h = parsed["header"]
    pc = parsed["point_count"]
    n = len(b)
    in_range = h["point_list_offset"] >= 0 and h["point_list_offset"] + pc * 12 <= n
    walked = _read_points(b, h["point_list_offset"], pc) if in_range else None
    keyframe = parsed["poses"][0]
    result = {"header_point_list_offset": h["point_list_offset"],
              "keyframe_offset": parsed["table"][0][0],
              "header_offset_in_range": in_range,
              "matches_a_frame": None, "wrong_points": None}
    if walked is None:
        return result
    result["wrong_points"] = sum(1 for a, b2 in zip(walked, keyframe) if a != b2)
    for i, pose in enumerate(parsed["poses"]):
        if walked == pose:
            result["matches_a_frame"] = i
            break
    return result
