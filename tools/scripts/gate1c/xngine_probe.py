# SPDX-License-Identifier: 0BSD
"""Gate-1c hop A6 oracle: independent XnGine ``.3D`` field extraction.

This probe restates the on-disk layouts from the cut-1c plan
(``docs/design/cut1c-xngine-reader-plan-20260925.md`` sections 0.2, 3.2, 6.1 and 9) and the
planning census receipts (``TestOutput/cut1c-prep-20260925/census_xngine.py`` and
``plan_fix_triangulation.py.txt``). It shares no code with BMT's C# readers; where the plan cites
reference behavior (the legacy decomposer's corner test and fan, the packed-UV gate), the rule is
re-implemented here from the plan's written description so hop A6 can compare ``mesh dump`` output
against a second, independent decoding of the same bytes.

What it extracts per record: the 64-byte header's named fields, the point and normal lists, every
plane's count, unknown byte, texture key (u16 archive/record split for the Daggerfall layout, u32
base-40 dword for the Battlespire layout), header tail, and the STORED per-corner u/v values (raw
int16, no unfold, no accumulation - the ``xngine.uv16`` stream), plus the plane-data area SHA-256
and the reference triangulation with its omission rule (plan D5):

- a 3-corner plane is its own triangle;
- an n-gon keeps the corners the 0.001 rad test keeps, in the order c1..c(n-1), c0, and fans from
  the FIRST KEPT corner (not c0);
- a fan triangle covering no area is omitted when the plane's authored normal is zero;
- a plane left with no triangle is a no-triangle plane, and a texture key left with no plane that
  yields a triangle is an emptied key.

Containers: numbered XnGine BSA (Daggerfall ARCH3D), name-record XnGine BSA with per-entry LZSS
(Battlespire 3D.BSA / 3D.BS6), Redguard .ROB segments, and loose files.
"""

import hashlib
import struct

import numpy as np

HEADER_LENGTH = 64
POINT_LENGTH = 12
PLANE_POINT_LENGTH = 8
PLANE_DATA_LENGTH = 24
DAGGERFALL_PLANE_HEADER = 8
BATTLESPIRE_PLANE_HEADER = 10
PACKED_UV_OBJECT_ID_LIMIT = 905
CORNER_TEST_RADIANS = 0.001
MESH_TAGS = (b"v2.5", b"v2.6", b"v2.7")
FXART_TAGS = (b"v4.0", b"v5.0")


class ProbeError(ValueError):
    """A container or record that violates the stated layout."""


def sha256_hex(data):
    return hashlib.sha256(data).hexdigest()


def _i32(b, o):
    return struct.unpack_from("<i", b, o)[0]


def _u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def _i16(b, o):
    return struct.unpack_from("<h", b, o)[0]


# --------------------------------------------------------------------------- containers

def parse_xngine_bsa(data):
    """An XnGine BSA: u16 count + u16 directory type at 0, payloads from 4, directory at EOF.

    Type 0x0100 is an 18-byte name record (12-byte name, u16 compression flag, i32 size); type
    0x0200 an 8-byte number record (i32 id, i32 size). The payload sizes must tile exactly from
    offset 4 to the directory start.
    """
    count, rtype = struct.unpack_from("<HH", data, 0)
    record_length = {0x0100: 18, 0x0200: 8}.get(rtype)
    if not count or record_length is None:
        raise ProbeError("not an XnGine BSA directory word")
    directory = len(data) - count * record_length
    position = 4
    entries = []
    for index in range(count):
        record = data[directory + index * record_length: directory + (index + 1) * record_length]
        if rtype == 0x0100:
            name = record[:12].split(b"\0", 1)[0].decode("ascii", "replace")
            flag = struct.unpack_from("<H", record, 12)[0]
            size = struct.unpack_from("<i", record, 14)[0]
            entries.append({"index": index, "name": name, "id": None, "offset": position,
                            "size": size, "compressed": flag == 0x0100})
        else:
            ident, size = struct.unpack_from("<Ii", record, 0)
            entries.append({"index": index, "name": str(ident), "id": ident, "offset": position,
                            "size": size, "compressed": False})
        position += size
    if position != directory:
        raise ProbeError(f"payloads end at {position}, directory at {directory}")
    return rtype, entries


def lzss_expand(data):
    """Battlespire's LZSS: 4096-byte ring seeded with 0x20, 18-byte parking spot, flag-bit stream."""
    window = bytearray(b"\x20" * (4096 - 18) + b"\x00" * 18)
    write = 4096 - 18
    out = bytearray()
    position = 0
    length = len(data)
    bits = 0
    mask = 0
    while position < length:
        if bits == 0:
            mask = data[position]
            position += 1
            bits = 8
            continue
        bits -= 1
        literal = mask & 1
        mask >>= 1
        if literal:
            if position >= length:
                break
            value = data[position]
            position += 1
            out.append(value)
            window[write] = value
            write = (write + 1) & 0xFFF
        else:
            if position + 2 > length:
                break
            low = data[position]
            high = data[position + 1]
            position += 2
            offset = low | ((high & 0xF0) << 4)
            run = (high & 0x0F) + 3
            for k in range(run):
                value = window[(offset + k) & 0xFFF]
                out.append(value)
                window[write] = value
                write = (write + 1) & 0xFFF
    return bytes(out)


def parse_rob(data):
    """A Redguard .ROB: OARC(4)=count, OARD(len), 80-byte segment headers + payloads, END."""
    if data[0:4] != b"OARC" or data[12:16] != b"OARD":
        raise ProbeError("not a ROB")
    if struct.unpack_from(">I", data, 4)[0] != 4:
        raise ProbeError("OARC length is not 4")
    count = _u32(data, 8)
    data_length = struct.unpack_from(">I", data, 16)[0]
    if 20 + data_length + 4 != len(data):
        raise ProbeError("OARD length does not tile")
    position = 20
    end = 20 + data_length
    segments = []
    for index in range(count):
        step = _u32(data, position)
        name = data[position + 4: position + 12].split(b"\0", 1)[0].decode("ascii", "replace")
        segment_type = _u32(data, position + 12)
        size = _u32(data, position + 76)
        if step != 80 + size:
            raise ProbeError(f"segment {index} next pointer {step} != 80 + {size}")
        segments.append({"index": index, "name": name, "type": segment_type,
                         "offset": position + 80, "size": size})
        position += 80 + size
    if position != end or data[end:end + 4] != b"END ":
        raise ProbeError("segments do not tile to END")
    return segments


# --------------------------------------------------------------------------- .3D record

def read_header(b):
    """The 64-byte header's named fields (plan section 3.4 and the census layout)."""
    if len(b) < HEADER_LENGTH:
        raise ProbeError(f"{len(b)} bytes is shorter than the 64-byte header")
    h = {"tag": b[0:4].split(b"\0", 1)[0].decode("ascii", "replace")}
    (h["point_count"], h["plane_count"], h["radius"], h["h16"], h["h20"], h["plane_data_offset"],
     h["object_data_offset"], h["object_data_count"], h["h36"], h["h40"], h["h44"],
     h["point_list_offset"], h["normal_list_offset"], h["h56"],
     h["plane_list_offset"]) = struct.unpack_from("<iiIiiiiiIIIiiIi", b, 4)
    return h


def read_points(b, h):
    o = h["point_list_offset"]
    return [(_i32(b, o + 12 * i), _i32(b, o + 12 * i + 4), _i32(b, o + 12 * i + 8))
            for i in range(h["point_count"])]


def read_normals(b, h):
    o = h["normal_list_offset"]
    return [(_i32(b, o + 12 * i), _i32(b, o + 12 * i + 4), _i32(b, o + 12 * i + 8))
            for i in range(h["plane_count"])]


def walk_planes(b, h, plane_header):
    """The plane list with the acceptance checks the census states.

    Returns (planes, end_offset). Each plane: point count, unknown byte, texture key per layout
    (u16 at +2 for the 8-byte header, u32 at +2 for the 10-byte header), the header tail beyond the
    key, and per-corner (point_index, stored_u, stored_v) with the RAW int16 values.
    Raises ProbeError when a list does not fit or a corner does not address a point.
    """
    n = len(b)
    pc, plc = h["point_count"], h["plane_count"]
    if pc < 0 or h["point_list_offset"] < 0 or h["point_list_offset"] + pc * POINT_LENGTH > n:
        raise ProbeError("point list does not fit")
    if plc < 0 or h["normal_list_offset"] < 0 or h["normal_list_offset"] + plc * POINT_LENGTH > n:
        raise ProbeError("normal list does not fit")
    position = h["plane_list_offset"]
    if position < 0 or position > n:
        raise ProbeError("plane list offset outside the record")
    v25 = h["tag"] == "v2.5"
    planes = []
    for k in range(plc):
        if position + plane_header > n:
            raise ProbeError(f"plane {k} header past the end")
        count = b[position]
        unknown1 = b[position + 1]
        if plane_header == BATTLESPIRE_PLANE_HEADER:
            key = _u32(b, position + 2)
            tail = b[position + 6: position + plane_header]
        else:
            key = struct.unpack_from("<H", b, position + 2)[0]
            tail = b[position + 4: position + plane_header]
        position += plane_header
        corners = []
        for q in range(count):
            if position + PLANE_POINT_LENGTH > n:
                raise ProbeError(f"plane {k} corner {q} past the end")
            offset = _i32(b, position)
            u = _i16(b, position + 4)
            v = _i16(b, position + 6)
            position += PLANE_POINT_LENGTH
            byte_offset = offset * 3 if v25 else offset
            if byte_offset < 0 or byte_offset % POINT_LENGTH or byte_offset // POINT_LENGTH >= pc:
                raise ProbeError(f"plane {k} corner {q} offset {offset} does not address a point")
            corners.append((byte_offset // POINT_LENGTH, u, v))
        planes.append({"index": k, "count": count, "unknown1": unknown1, "key": key,
                       "tail": tail.hex(), "corners": corners})
    return planes, position


def texture_key_parts(key, plane_header):
    """Daggerfall/Redguard split (archive = bits >> 7, record = bits & 0x7F) or the raw u32."""
    if plane_header == DAGGERFALL_PLANE_HEADER:
        return key >> 7, key & 0x7F
    return None, None


# --------------------------------------------------------------------------- reference rule

def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def newell(points):
    nx = ny = nz = 0.0
    n = len(points)
    for q in range(n):
        a, b = points[q], points[(q + 1) % n]
        nx += (a[1] - b[1]) * (a[2] + b[2])
        ny += (a[2] - b[2]) * (a[0] + b[0])
        nz += (a[0] - b[0]) * (a[1] + b[1])
    return (nx, ny, nz)


def kept_corners(points):
    """The reference corner test: kept source-corner indices in the order c1..c(n-1), c0.

    Corner i is tested with its two successors (wrapping), and kept when the angle between the two
    edges exceeds 0.001 rad; the kept index recorded for slot i is i+1 (or 0 at the last slot),
    which is what produces the reference order.

    PRECISION IS PART OF THE RULE: the reference (XnGineMeshDecomposer.CornerIndices) runs the
    whole chain in System.Numerics.Vector3 float32 - int-to-float conversion, edge subtraction,
    the two squared-length dots, their square roots, the length product, the edge dot, the
    division and the clamp - and only the final Math.Acos in double. This function simulates that
    chain op for op in numpy float32 (additions left to right, as Vector3.Dot associates). A
    float64 chain flips 32 verdicts on 28 retail planes whose angles sit within ~6e-5 rad of the
    threshold (the float32-representable cosines quantize the angle to ~0.000977 and ~0.001036
    there); the measurement is receipts/float32_corner_divergence.json beside the slice-2 runner.
    """
    n = len(points)
    if n == 0:
        return []
    a = np.asarray(points, dtype=np.float32)
    v1 = np.roll(a, -1, axis=0)
    v2 = np.roll(a, -2, axis=0)
    e0 = v1 - a
    e1 = v2 - a
    lengths = np.sqrt(_dot32(e0, e0)) * np.sqrt(_dot32(e1, e1))
    with np.errstate(divide="ignore", invalid="ignore"):
        cosine = np.clip(_dot32(e0, e1) / lengths, np.float32(-1.0), np.float32(1.0))
    angle = np.arccos(cosine.astype(np.float64))
    keep = (lengths > 0) & (angle > CORNER_TEST_RADIANS)
    corner = np.roll(np.arange(n), -1)
    return [int(c) for c, k in zip(corner, keep) if k]


def _dot32(a, b):
    """Row-wise float32 dot with the reference's association: (x*x + y*y) + z*z."""
    return (a[:, 0] * b[:, 0] + a[:, 1] * b[:, 1]) + a[:, 2] * b[:, 2]


def reference_triangles(points):
    """The reference triangle stream (decomposer corner order): fan from the first kept corner.

    Returns (triangles, kept_count). A 3-corner plane is its own triangle; an n-gon with fewer
    than 3 kept corners yields no triangle. Triangle corners are source-corner indices in the
    decomposer's order (K0, K(i), K(i+1)); the legacy exporter then swaps the second and third.
    """
    n = len(points)
    if n == 3:
        return [(0, 1, 2)], 3
    kept = kept_corners(points)
    if len(kept) < 3:
        return [], len(kept)
    return [(kept[0], kept[i], kept[i + 1]) for i in range(1, len(kept) - 1)], len(kept)


def naive_c0_triangles(points):
    """The withdrawn draft reading, kept as the discriminating control: the SAME kept-corner set
    reordered c0..c(n-1) and fanned from its first element. Must disagree with the reference
    stream on retail n-gons; if it did not, the apex rule would not be measurable."""
    n = len(points)
    if n == 3:
        return [(0, 1, 2)]
    kept = sorted(kept_corners(points))
    if len(kept) < 3:
        return []
    return [(kept[0], kept[i], kept[i + 1]) for i in range(1, len(kept) - 1)]


def triangulate_plane(points, authored_normal):
    """Applies plan D5 to one plane. Returns a dict with the kept triangle stream, counts of
    omitted zero-area triangles, folded triangles (against Newell), and whether the plane yields
    no triangle."""
    reference, kept_count = reference_triangles(points)
    zero_normal = authored_normal == (0, 0, 0)
    plane_normal = newell(points)
    kept = []
    omitted_zero_area = 0
    zero_area = 0
    folded = 0
    for triangle in reference:
        cross = _cross(_sub(points[triangle[1]], points[triangle[0]]),
                       _sub(points[triangle[2]], points[triangle[0]]))
        if cross == (0, 0, 0):
            zero_area += 1
            if zero_normal:
                omitted_zero_area += 1
                continue
        elif len(points) > 3 and _dot(cross, plane_normal) < 0:
            folded += 1
        kept.append(triangle)
    return {"triangles": kept, "reference_stream": reference, "kept_corner_count": kept_count,
            "zero_area_triangles": zero_area, "omitted_zero_area_triangles": omitted_zero_area,
            "folded_triangles": folded, "zero_normal": zero_normal,
            "no_triangle": len(kept) == 0}


# --------------------------------------------------------------------------- whole record

def probe_mesh(b, plane_header, object_id=None):
    """Extracts one record's A6 field set plus the D5 triangulation summary."""
    h = read_header(b)
    planes, plane_list_end = walk_planes(b, h, plane_header)
    points = read_points(b, h)
    normals = read_normals(b, h)
    per_key = {}
    no_triangle_planes = []
    totals = {"triangles": 0, "reference_triangles": 0, "zero_area": 0, "omitted_zero_area": 0,
              "folded": 0, "zero_normal_planes": 0, "ngons_fewer_than_3_kept": 0}
    for plane in planes:
        ps = [points[ci] for ci, _, _ in plane["corners"]]
        result = triangulate_plane(ps, normals[plane["index"]])
        plane["triangulation"] = result
        totals["triangles"] += len(result["triangles"])
        totals["reference_triangles"] += len(result["reference_stream"])
        totals["zero_area"] += result["zero_area_triangles"]
        totals["omitted_zero_area"] += result["omitted_zero_area_triangles"]
        totals["folded"] += result["folded_triangles"]
        if result["zero_normal"]:
            totals["zero_normal_planes"] += 1
        if len(ps) > 3 and result["kept_corner_count"] < 3:
            totals["ngons_fewer_than_3_kept"] += 1
        slot = per_key.setdefault(plane["key"], [0, 0])
        slot[0] += 1
        if not result["no_triangle"]:
            slot[1] += 1
        else:
            no_triangle_planes.append({"plane": plane["index"], "key": plane["key"],
                                       "zero_normal": result["zero_normal"],
                                       "corners": len(ps)})
    emptied_keys = [k for k, (total, kept) in per_key.items() if kept == 0]
    plane_data = None
    if h["plane_data_offset"] > 0:
        start = h["plane_data_offset"]
        size = h["plane_count"] * PLANE_DATA_LENGTH
        if start + size <= len(b):
            plane_data = {"offset": start, "size": size, "sha256": sha256_hex(b[start:start + size])}
    return {"header": h, "points": points, "normals": normals, "planes": planes,
            "plane_list_end": plane_list_end, "texture_keys_first_use": list(dict.fromkeys(
                p["key"] for p in planes)),
            "per_key_planes": {k: v[0] for k, v in per_key.items()},
            "per_key_kept": {k: v[1] for k, v in per_key.items()},
            "emptied_keys": emptied_keys, "no_triangle_planes": no_triangle_planes,
            "totals": totals, "plane_data": plane_data, "object_id": object_id,
            "payload_sha256": sha256_hex(b)}


def uv_field_stream(planes):
    """The stored-UV stream in Faces corner order, for byte-flip detection: a list of
    (plane, corner, point_index, u, v) plus its SHA-256."""
    rows = []
    for plane in planes:
        for q, (ci, u, v) in enumerate(plane["corners"]):
            rows.append((plane["index"], q, ci, u, v))
    digest = hashlib.sha256()
    for row in rows:
        digest.update(struct.pack("<iiihh", *row))
    return rows, digest.hexdigest()


# --------------------------------------------------------------------------- M-T area tiling

def area_tiling(b, h, plane_list_end):
    """M-T: tiles the record's declared byte areas and reports every uncovered range.

    Areas: the 64-byte header, the point list, the normal list, the walked plane list, and the
    plane-data area (plane_count x 24 at +24 when the offset is positive and the area fits).
    Returns the gap list plus whether a gap starts at the object-data offset and the implied
    object-data record stride when a single gap starts there.
    """
    n = len(b)
    areas = [("header", 0, HEADER_LENGTH),
             ("points", h["point_list_offset"], h["point_list_offset"] + h["point_count"] * POINT_LENGTH),
             ("normals", h["normal_list_offset"], h["normal_list_offset"] + h["plane_count"] * POINT_LENGTH),
             ("planes", h["plane_list_offset"], plane_list_end)]
    plane_data_size = h["plane_count"] * PLANE_DATA_LENGTH
    plane_data_state = "absent"
    if h["plane_data_offset"] > 0:
        if h["plane_data_offset"] + plane_data_size <= n:
            areas.append(("plane-data", h["plane_data_offset"], h["plane_data_offset"] + plane_data_size))
            plane_data_state = "fits"
        else:
            plane_data_state = "declared-but-does-not-fit"
    ordered = sorted(areas, key=lambda a: a[1])
    overlaps = []
    gaps = []
    cursor = 0
    for name, start, end in ordered:
        if start < cursor:
            overlaps.append((name, start, cursor))
        elif start > cursor:
            gaps.append((cursor, start))
        cursor = max(cursor, end)
    if cursor < n:
        gaps.append((cursor, n))
    odo, odc = h["object_data_offset"], h["object_data_count"]
    gap_at_object_data = any(start == odo for start, _ in gaps) if odc > 0 else False
    object_stride = None
    if odc > 0:
        for start, end in gaps:
            if start == odo and (end - start) % odc == 0:
                object_stride = (end - start) // odc
                break
    return {"gaps": gaps, "overlaps": overlaps, "unclaimed_bytes": sum(e - s for s, e in gaps),
            "plane_data_state": plane_data_state, "gap_at_object_data_offset": gap_at_object_data,
            "object_data_stride": object_stride, "tiles_exactly": not gaps and not overlaps}
