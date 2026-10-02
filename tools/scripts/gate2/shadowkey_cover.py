# SPDX-License-Identifier: 0BSD
"""Cut-2 Shadowkey cover tool: the checked-in cover manifest with SHA-256 pins, its verification, and the A-hop
expectations the Bucket-B tests compare BMT's ``ShadowkeyMeshModelReader`` and ``ShadowkeyZoneModelReader`` against.

Every byte this tool reads comes through the independent decoder of ``shadowkey_probe.py`` (same directory). Nothing
here reads, imports or consumes a result of BMT's C# code, so the expectations are a second decoding of the same bytes
(plan ``docs/design/cut2-shadowkey-reader-plan-20260928.md`` sections 3, 4, 8 and 9). The document rules restated
here (the UV vertex domain, the skin expansion, the clip keys, the terrain face rule, the placement matrix) are written
from the plan, not from the C# readers. Everything is LITTLE-endian (N-Gage, Symbian on ARM); clean room, no game code.

Subcommands (one process at a time; every one is read-only over the Shadowkey application directory):

    manifest     --census <census dir> --out <manifest.json>
                 The exact MILP joint cover of the probe's ``cover`` (every value and every observed pair of values of
                 the cell dimensions plus every tag; fewest rows, then fewest bytes) over the census the probe wrote,
                 the edge rows, the eleven empty slots and thirteen tree files as decline controls, and the synthetic
                 controls. Every row is re-read from the tree, so its size and SHA-256 are pinned from the bytes, not
                 copied from the census. Writes the manifest (schema ``cut2-shadowkey-cover/1``) and a report beside it.
    verify       --manifest <manifest.json> --out <receipt dir>
                 Re-reads every pin (the pack, every slot, every zone file, every decline control); size and SHA-256
                 must match. The built-in control alters one SHA-256 digit and must be detected.
    expectations --manifest <manifest.json> --out <expectations.jsonl>
                 One JSON line per manifest row, keyed by SHA-256 (a slot's bytes, a zone's ``.zmp``, a control's
                 bytes): the probe verdicts, the header and section layout, the coverage census, the diagnostics, the
                 document shape and the SHA-256 of every typed array under the rules of ``DIGEST_RULES``; for a zone,
                 the terrain, palette, texture, sky and placement facts, with the legacy-map control.
    selfcheck    --out <receipt dir>
                 The rules on synthetic records and a synthetic zone: each control must change exactly what it
                 targets, and the golden digests the C# default suite pins are printed.

The root resolves exactly as ``RealAssetPaths.Travels.ShadowkeyRoot`` does (see ``shadowkey_probe.resolve_root``);
``--root`` overrides. Standard library plus numpy (and scipy for ``manifest``, through the probe's MILP).
"""

import argparse
import hashlib
import json
import math
import os
import struct
import sys
import zlib

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shadowkey_probe as probe  # noqa: E402  (the independent decoder beside this file)

SCHEMA = "cut2-shadowkey-cover/1"
EXPECTATIONS_SCHEMA = "cut2-shadowkey-expectations/1"
PROBE_BYTES = probe.PROBE_BYTES
ROOT_TOKEN = ("<Sample>/Builds/The Elder Scrolls Travels - Shadowkey (2004-10-27, N-Gage - Final)/"
              "The Elder Scrolls Travels - Shadowkey/system/apps/6r51")
EMPTY_SHA256 = hashlib.sha256(b"").hexdigest()
QUARTER_TURN = 16384
TILE = 256

# The zone file families a zone row pins, in this order; ``.sta`` only where it exists.
ZONE_FAMILIES = (".zmp", ".zcp", ".zsk", ".ztx", ".zlu", ".zfg", ".ent", ".sur", ".zon", ".pth", ".stn", ".pal")

# The terrain face rule's wall directions: neighbor step, this cell's two corner slots on the shared edge, the
# neighbor's two slots for the same world corners (plan section 4.2; the corner order is the measured .zcp order,
# slot 0 = (x, y+1), 1 = (x+1, y+1), 2 = (x+1, y), 3 = (x, y)).
CORNER_OFFSETS = ((0, 1), (1, 1), (1, 0), (0, 0))
WALL_DIRECTIONS = ((1, 0, 1, 2, 0, 3), (-1, 0, 3, 0, 2, 1), (0, 1, 0, 1, 3, 2), (0, -1, 2, 3, 1, 0))
FACE_KINDS = ("floor", "ceiling", "wall", "riser", "downstand")
# The columns of one placement row of a zone expectation (m00 and m01 are null on an unresolved placement).
PLACEMENT_FIELDS = ["index", "slot", "resolved", "rawScale", "rawX", "rawY", "rawZ", "angle2", "m00", "m01"]

DIGEST_RULES = {
    "positions": "float32 little-endian x, y, z per vertex of the UV domain: vertex i is UV index i and its position "
                 "is frame 0 of the one record vertex every face corner using UV i names (exact integers)",
    "texCoords": "float32 little-endian u, v per vertex: fl32(U) / fl32(256 x width), fl32(V) / fl32(256 x height), "
                 "one correctly rounded float32 division each",
    "pointIndices": "int32 little-endian, the owner record vertex of every UV-domain vertex",
    "triangles": "int32 little-endian, the stored UV index triples of the faces, as stored",
    "targets": "float32 little-endian x, y, z, target-major: target f - 1 holds frame f of the owner vertex of every "
               "UV-domain vertex, for f = 1 .. frames - 1 (absent on a one-frame record)",
    "skins.original": "SHA-256 of the stored texel block of the skin (u16 0x0RGB little-endian, top row first)",
    "skins.rgba": "uint8 R, G, B, A per texel, top row first: each 4-bit channel times 17, A = 255",
    "clips.times": "float32 little-endian key times: fl32(i) / fl32(rate) for i = 0 .. length (the last is the derived "
                   "final key)",
    "clips.weights": "float32 little-endian, key-major one-hot weights over frames - 1 targets: key i < length selects "
                     "frame start + i, the final key repeats frame end - 1, frame 0 selects no target",
    "terrain.<kind>": "float32 little-endian x, y, z per quad corner, four per quad, in emission order: for each row y, "
                      "each column x, an open cell emits its floor (slots 3, 2, 1, 0), its ceiling (0, 1, 2, 3), then "
                      "per wall direction a full wall (blocked or off-grid neighbor), else a riser and a downstand "
                      "where the neighbor's floor is higher or its ceiling lower; corner (x + dx) x 256, (y + dy) x "
                      "256, raw 8.8 height",
    "palette.entries": "uint8 R, G, B, 255 per entry of the 768-byte .pal",
    "textures.indices": "the 16,384 index bytes of a .ztx texture, top row first (the stored rows reversed)",
    "sky.*": "the sky payload read as a mesh record with its texture-header variant, digested as a slot",
    "placements.m00/m01": "the placement matrix's first row x and y entries (float32): fl32(s cos t), fl32(s sin t) "
                          "with s = RawScale / 256 and t = -2 pi Angle2 / 65536 in binary64, exact on quarter turns; "
                          "the matrix is [[m00, m01, 0], [0, 0, s], [m01, -m00, 0]] plus the raw translation",
    "blockedVertices": "frame-0 positions of every record vertex some face names, per resolved placement, through the "
                       "float32 matrix in binary64 (x m11 + y m21 + z m31 + m41, then the y column), landing in a "
                       "blocked or off-grid cell (floor(q / 256)); legacy = the (x, z, y) map with +yaw",
    "unusedVertices.positions": "int32 little-endian, for each frame, for each record vertex no face names (index "
                                "order), x, y, z as stored (absent when every vertex is used)",
    "header.signedVolumeX6": "sum of p . (q x r) over the faces with three distinct vertex indices, frame 0, stored "
                             "(v0, v1, v2) order, exact integers; closed = every directed edge once with its reverse "
                             "once, by index or with the vertices welded by position",
}


# --------------------------------------------------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------------------------------------------------

def sha(data):
    return hashlib.sha256(data).hexdigest()


def f32_bytes(values):
    return np.asarray(values, dtype=np.float32).astype("<f4").tobytes()


def i32_bytes(values):
    return np.asarray(values, dtype=np.int64).astype("<i4").tobytes()


def f32_divide(numerators, denominator):
    """One correctly rounded float32 division per value (IEEE float32 on both operands)."""
    return (np.asarray(numerators, dtype=np.float32) / np.float32(denominator)).astype(np.float32)


def complete_for(size):
    """Shared's candidate helper sees EOF inside its 64 KiB budget only for a stream shorter than the budget."""
    return size < PROBE_BYTES


# --------------------------------------------------------------------------------------------------------------------
# Mesh document rules (plan section 3)
# --------------------------------------------------------------------------------------------------------------------

class DocumentError(ValueError):
    """A record the reader must refuse (a UV used by no face, or by two vertices)."""


def uv_owners(record):
    """The owner record vertex of every UV index, from the faces in stored order (plan decision D2)."""
    faces = record["faces"]
    owners = [None] * record["uv_count"]
    for face in range(record["face_count"]):
        corners = faces[6 * face:6 * face + 6]
        for k in range(3):
            vertex, uv = corners[k], corners[3 + k]
            if owners[uv] is None:
                owners[uv] = vertex
            elif owners[uv] != vertex:
                raise DocumentError(f"UV {uv} is used by vertices {owners[uv]} and {vertex}")
    for uv, owner in enumerate(owners):
        if owner is None:
            raise DocumentError(f"UV {uv} is used by no face")
    return owners


def frame_positions(record, frame, owners):
    vertices = record["vertices"]
    positions = record["positions"]
    values = []
    for owner in owners:
        base = 3 * (frame * vertices + owner)
        values.extend(positions[base:base + 3])
    return values


def texcoords(record):
    uvs = record["uvs"]
    u = f32_divide(uvs[0::2], 256 * record["width"])
    v = f32_divide(uvs[1::2], 256 * record["height"])
    out = np.empty(2 * len(u), dtype=np.float32)
    out[0::2] = u
    out[1::2] = v
    return out


def skin_rgba(block):
    texels = struct.unpack(f"<{len(block) // 2}H", block)
    out = bytearray()
    for texel in texels:
        out += bytes((((texel >> 8) & 15) * 17, ((texel >> 4) & 15) * 17, (texel & 15) * 17, 255))
    return bytes(out)


def clip_arrays(record, sequence):
    start, end, rate = sequence
    length = end - start
    targets = record["frames"] - 1
    times = f32_divide(list(range(length + 1)), rate)
    weights = np.zeros((length + 1) * targets, dtype=np.float32)
    for key in range(length + 1):
        frame = start + key if key < length else end - 1
        if frame > 0:
            weights[key * targets + frame - 1] = 1.0
    return times, weights


def is_humanoid(sequences):
    return len(sequences) >= 9 and tuple(tuple(s) for s in sequences) == probe.HUMANOID_TABLE[:len(sequences)]


def mesh_sections(record):
    """The record's section layout as (name, offset, length), the names the reader's header row reports."""
    offsets = record["offsets"]
    order = (("header", "header"), ("positions", "positions"), ("uvs", "uvs"), ("faces", "faces"),
             ("texture-header", "texture_header"), ("texels", "texels"), ("sequence-count", "sequence_count"),
             ("sequences", "sequences"))
    ends = [offsets[key] for _, key in order[1:]] + [offsets["end"]]
    return [[name, offsets[key], ends[k] - offsets[key]] for k, (name, key) in enumerate(order)]


def unused_vertices(record):
    """The record vertices no face names, in index order (plan section 3.2; review finding 3)."""
    faces = record["faces"]
    used = {faces[6 * f + k] for f in range(record["face_count"]) for k in range(3)}
    return [v for v in range(record["vertices"]) if v not in used]


def winding(record):
    """(closed, 6 x signed volume) of frame 0: the plan's measurement rule (section 0.2, winding 75 of 79)."""
    faces = record["faces"]
    positions = record["positions"]
    directed = {}
    welded = {}
    volume = 0
    for f in range(record["face_count"]):
        a, b, c = faces[6 * f:6 * f + 3]
        if len({a, b, c}) < 3:
            continue
        p, q, r = (tuple(positions[3 * v:3 * v + 3]) for v in (a, b, c))
        volume += (p[0] * (q[1] * r[2] - q[2] * r[1]) - p[1] * (q[0] * r[2] - q[2] * r[0])
                   + p[2] * (q[0] * r[1] - q[1] * r[0]))
        for edge in ((a, b), (b, c), (c, a)):
            directed[edge] = directed.get(edge, 0) + 1
        if len({p, q, r}) == 3:
            for edge in ((p, q), (q, r), (r, p)):
                welded[edge] = welded.get(edge, 0) + 1

    def closed(edges):
        return bool(edges) and all(n == 1 and edges.get((e[1], e[0])) == 1 for e, n in edges.items())

    return closed(directed) or closed(welded), volume


def mesh_coverage(record):
    rows = [["header", "Typed"]]
    rows += [[f"positions:{f}", "Typed"] for f in range(record["frames"])]
    if unused_vertices(record):
        rows.append(["unused-vertices", "NativeOnly"])
    rows += [["uvs", "Typed"], ["faces", "Typed"], ["texture-header", "Typed"]]
    rows += [[f"skin:{k}", "Typed"] for k in range(record["skins"])]
    animated = record["frames"] > 1
    rows += [[f"sequence:{k}", "Typed" if animated else "NativeOnly"] for k in range(len(record["sequences"]))]
    return rows


def mesh_diagnostics(record, shape):
    codes = []
    magenta = any(probe.MAGENTA_444 in struct.unpack(f"<{len(b) // 2}H", b) for b in record["skin_blocks"])
    if shape == "placement":
        return codes
    if magenta:
        codes.append("bmt.shadowkey.mesh.magenta-opaque")
    if shape == "sky":
        return codes
    if record["skins"] > 1:
        codes.append("bmt.shadowkey.mesh.skin-selection")
    if record["frames"] > 1:
        codes.append("bmt.shadowkey.mesh.step-interpolation")
        if any(e - s > 1 and r == 1 for s, e, r in record["sequences"]):
            codes.append("bmt.shadowkey.mesh.rate-suspect")
    if is_humanoid(record["sequences"]):
        codes.append("bmt.shadowkey.mesh.actor-assembly")
    closed, volume = winding(record)
    if closed and volume < 0:
        codes.append("bmt.shadowkey.mesh.reversed-winding")
    return sorted(codes)


def mesh_expectation(record, shape="standalone"):
    """What the reader's document of one record must hold (plan section 3; the sky shape has no targets or clips)."""
    owners = uv_owners(record)
    uv_count = record["uv_count"]
    skins = record["skins"] if shape != "placement" else 1
    animated = record["frames"] > 1 and shape == "standalone"
    digests = {
        "positions": sha(f32_bytes(frame_positions(record, 0, owners))),
        "texCoords": sha(texcoords(record).astype("<f4").tobytes()),
        "pointIndices": sha(i32_bytes(owners)),
        "pointCount": record["vertices"],
        "triangles": sha(i32_bytes([record["faces"][6 * f + 3 + k] for f in range(record["face_count"])
                                    for k in range(3)])),
        "targets": None,
        "skins": [],
        "clips": [],
    }
    if animated:
        values = []
        for frame in range(1, record["frames"]):
            values.extend(frame_positions(record, frame, owners))
        digests["targets"] = sha(f32_bytes(values))
        for index, sequence in enumerate(record["sequences"]):
            times, weights = clip_arrays(record, sequence)
            digests["clips"].append({
                "name": f"seq{index:02d}", "keys": len(times), "rate": sequence[2], "tracks": 1,
                "times": sha(times.astype("<f4").tobytes()), "weights": sha(weights.astype("<f4").tobytes()),
            })
    for block in record["skin_blocks"][:skins]:
        texels = struct.unpack(f"<{len(block) // 2}H", block)
        digests["skins"].append({"original": sha(block), "rgba": sha(skin_rgba(block)), "colors": len(set(texels))})
    unused = unused_vertices(record)
    stream = [record["positions"][3 * (frame * record["vertices"] + v) + k]
              for frame in range(record["frames"]) for v in unused for k in range(3)]
    digests["unusedVertices"] = {"indices": unused, "positions": sha(i32_bytes(stream)) if stream else None}
    closed, volume = winding(record)
    return {
        "header": {
            "frames": record["frames"], "vertices": record["vertices"], "uvCount": uv_count,
            "faceCount": record["face_count"], "skins": record["skins"], "width": record["width"],
            "height": record["height"],
            "textureHeader": "Counted" if record["texture_header"] == "counted" else "Uncounted",
            "sequences": [list(s) for s in record["sequences"]],
            "closed": closed, "signedVolumeX6": volume,
        },
        "sections": mesh_sections(record),
        "coverage": mesh_coverage(record) if shape == "standalone" else None,
        "document": {
            "nodes": skins, "meshes": skins, "layerSets": 0 if skins == 1 else skins,
            "animations": len(record["sequences"]) if animated else 0, "images": skins, "materials": skins,
            "targets": record["frames"] - 1 if animated else 0, "vertexCount": uv_count,
            "unrolledVertexCount": 3 * record["face_count"],
        },
        "digests": digests,
        "diagnostics": mesh_diagnostics(record, shape),
    }


# --------------------------------------------------------------------------------------------------------------------
# Zone document rules (plan section 4)
# --------------------------------------------------------------------------------------------------------------------

def terrain_quads(zmp, zcp):
    """Every terrain quad as (kind, [(x, y, z) x 4]) in emission order (plan section 4.2), mesh units."""
    width, height, cells = zmp["width"], zmp["height"], zmp["cells"]

    def corner(x, y, slot, z):
        dx, dy = CORNER_OFFSETS[slot]
        return ((x + dx) * TILE, (y + dy) * TILE, z)

    def blocked(x, y):
        return not (0 <= x < width and 0 <= y < height) or cells[y * width + x][0] & probe.BLOCKED_FLAG

    quads = []
    for y in range(height):
        for x in range(width):
            flags, _, _, proto = cells[y * width + x]
            if flags & probe.BLOCKED_FLAG:
                continue
            record = zcp[proto]
            floor, ceiling = record["floor"], record["ceiling"]
            quads.append(("floor", [corner(x, y, s, floor[s]) for s in (3, 2, 1, 0)]))
            quads.append(("ceiling", [corner(x, y, s, ceiling[s]) for s in (0, 1, 2, 3)]))
            for dx, dy, a, b, ta, tb in WALL_DIRECTIONS:
                ca, cb = corner(x, y, a, floor[a]), corner(x, y, b, floor[b])
                if blocked(x + dx, y + dy):
                    quads.append(("wall", [ca, cb, cb[:2] + (ceiling[b],), ca[:2] + (ceiling[a],)]))
                    continue
                other = zcp[cells[(y + dy) * width + x + dx][3]]
                if other["floor"][ta] > floor[a] or other["floor"][tb] > floor[b]:
                    quads.append(("riser", [ca, cb, cb[:2] + (max(floor[b], other["floor"][tb]),),
                                            ca[:2] + (max(floor[a], other["floor"][ta]),)]))
                if other["ceiling"][ta] < ceiling[a] or other["ceiling"][tb] < ceiling[b]:
                    quads.append(("downstand", [ca[:2] + (min(ceiling[a], other["ceiling"][ta]),),
                                                cb[:2] + (min(ceiling[b], other["ceiling"][tb]),),
                                                cb[:2] + (ceiling[b],), ca[:2] + (ceiling[a],)]))
    return quads


def terrain_expectation(zmp, zcp):
    quads = terrain_quads(zmp, zcp)
    result = {}
    for kind in FACE_KINDS:
        values = [c for k, corners in quads if k == kind for p in corners for c in p]
        count = sum(1 for k, _ in quads if k == kind)
        result[kind] = {"quads": count, "positions": sha(f32_bytes(values)) if count else None}
    return result


def placement_entries(angle, scale_raw):
    """(m00, m01) of the placement matrix as float32 values (see DIGEST_RULES)."""
    scale = scale_raw / 256.0
    if angle % QUARTER_TURN == 0:
        quarter = (-(angle // QUARTER_TURN)) % 4
        cosine, sine = ((1.0, 0.0), (0.0, 1.0), (-1.0, 0.0), (0.0, -1.0))[quarter]
    else:
        theta = -math.tau * angle / 65536.0
        cosine, sine = math.cos(theta), math.sin(theta)
    return float(np.float32(scale * cosine)), float(np.float32(scale * sine))


def matrix_rows(m00, m01, scale_raw, x, y, z, legacy=False):
    """The row-vector matrix rows (x column, y column only) the blocked count uses: [(m11, m12), (m21, m22),
    (m31, m32), (m41, m42)]. Legacy: (x, y, z) -> (x, z, y) and +yaw, i.e. rows (m00, -m01), (0, 0), (m01, m00)."""
    scale = float(np.float32(scale_raw / 256.0))
    if legacy:
        return [(m00, -m01), (0.0, 0.0), (m01, m00), (float(x), float(y))]
    return [(m00, m01), (0.0, 0.0), (m01, -m00), (float(x), float(y))]


def blocked_count(points, rows, zmp):
    width, height, cells = zmp["width"], zmp["height"], zmp["cells"]
    count = 0
    for vx, vy, vz in points:
        qx = vx * rows[0][0] + vy * rows[1][0] + vz * rows[2][0] + rows[3][0]
        qy = vx * rows[0][1] + vy * rows[1][1] + vz * rows[2][1] + rows[3][1]
        ix, iy = math.floor(qx / TILE), math.floor(qy / TILE)
        if not (0 <= ix < width and 0 <= iy < height) or cells[iy * width + ix][0] & probe.BLOCKED_FLAG:
            count += 1
    return count


def zone_expectation(root, stem, pack, records, entities):
    files = {}
    raw = {}
    inflated = {}
    for family in ZONE_FAMILIES + (".sta",):
        path = os.path.join(root, stem + family)
        if not os.path.isfile(path):
            continue
        data = probe.read(path)
        raw[family] = data
        if family in probe.COMPRESSED_FAMILIES:
            payload, _, error = probe.inflate_envelope(data)
            if error:
                raise probe.RecordError(f"{stem}{family}: {error}")
            inflated[family] = payload
    zmp = probe.parse_zmp(inflated[".zmp"])
    zcp = probe.parse_zcp(inflated[".zcp"])
    palette = raw[".pal"]
    zlu = inflated[".zlu"]
    zone_models = probe.parse_text_table(probe.read(os.path.join(root, stem + "_models.txt")))

    # Palette key: the first magenta entry, declared only when the light table pins it to 0x0F0F everywhere.
    magenta = [k for k in range(256) if palette[3 * k:3 * k + 3] == b"\xff\x00\xff"]
    transparent = []
    if magenta:
        key = magenta[0]
        entries = struct.unpack("<65536H", zlu)
        if all(entries[(bank * 64 + level) * 256 + key] == 0x0F0F for bank in range(4) for level in range(64)):
            transparent = [key]
    entries = bytearray()
    for k in range(256):
        entries += palette[3 * k:3 * k + 3] + b"\xff"

    ztx = inflated[".ztx"]
    textures = []
    for n in range(ztx[0]):
        stored = ztx[1 + 16384 * n:1 + 16384 * (n + 1)]
        rows = [stored[128 * r:128 * (r + 1)] for r in range(128)]
        textures.append({"original": sha(stored), "indices": sha(b"".join(reversed(rows)))})

    sky_payload = inflated[".zsk"]
    variant = probe.sky_variant(sky_payload)
    sky_record = probe.parse_mesh(sky_payload, variant)
    sky = mesh_expectation(sky_record, "sky")
    sky["variant"] = variant

    placements = probe.parse_ent(raw[".ent"])
    rows = []
    resolved = 0
    blocked_doc = 0
    blocked_legacy = 0
    animated = 0
    pitch_roll = 0
    placed_multi_skin = set()
    placed_magenta = set()
    placed_reversed = set()
    for index, row in enumerate(placements):
        entity = entities.get(row["entity"])
        slot = int(entity[1]) if entity else None
        zone_row = zone_models[slot] if slot is not None and 0 <= slot < len(zone_models) else None
        record = records.get(slot) if slot is not None else None
        if zone_row is None or zone_row[4].lower() == "null.bin" or record is None:
            rows.append([index, slot, 0, row["scale"], row["x"], row["y"], row["z"], row["angles"][2], None, None])
            continue
        resolved += 1
        m00, m01 = placement_entries(row["angles"][2], row["scale"])
        rows.append([index, slot, 1, row["scale"], row["x"], row["y"], row["z"], row["angles"][2], m00, m01])
        if row["angles"][0] or row["angles"][1]:
            pitch_roll += 1
        if record["frames"] > 1:
            animated += 1
        if record["skins"] > 1:
            placed_multi_skin.add(slot)
        if probe.MAGENTA_444 in struct.unpack(f"<{len(record['skin_blocks'][0]) // 2}H", record["skin_blocks"][0]):
            placed_magenta.add(slot)
        closed, volume = winding(record)
        if closed and volume < 0:
            placed_reversed.add(slot)
        used = sorted({record["faces"][6 * f + k] for f in range(record["face_count"]) for k in range(3)})
        points = [tuple(record["positions"][3 * v:3 * v + 3]) for v in used]
        blocked_doc += blocked_count(points, matrix_rows(m00, m01, row["scale"], row["x"], row["y"], row["z"]), zmp)
        blocked_legacy += blocked_count(
            points, matrix_rows(m00, m01, row["scale"], row["x"], row["y"], row["z"], legacy=True), zmp)

    # Prototypes no open cell uses: the terrain draws none of their corner heights (review finding 4).
    placed_prototypes = {cell[3] for cell in zmp["cells"] if not cell[0] & probe.BLOCKED_FLAG}
    unplaced = len(zcp) - len(placed_prototypes)
    coverage = [["zmp:header", "NativeOnly"], ["zmp:cells", "Typed"], ["zcp:heights", "Typed"]]
    if unplaced:
        coverage.append(["zcp:heights-unplaced", "NativeOnly"])
    coverage += [["zcp:slots-edges-extra-shade", "NativeOnly"], ["sur", "NativeOnly"]]
    coverage += [[f"ztx:{n}", "Typed"] for n in range(ztx[0])]
    coverage += [["pal", "Typed"], ["zlu", "Typed"], ["zfg", "NativeOnly"], ["zsk", "Typed"]]
    coverage += [[f"ent:{r[0]}", "Typed" if r[2] else "NativeOnly"] for r in rows]
    if placements:
        coverage.append(["ent:angles-0-1", "NativeOnly"])
    coverage += [["zon", "NativeOnly"], ["pth", "NativeOnly"], ["stn", "NativeOnly"]]
    if ".sta" in raw:
        coverage.append(["sta", "NativeOnly"])
    coverage.append(["models-list", "Typed"])

    diagnostics = {"bmt.shadowkey.zone.tile-texturing-unresolved"}
    diagnostics.update(sky["diagnostics"])
    if resolved:
        diagnostics.add("bmt.shadowkey.zone.chirality-assumed")
    if pitch_roll:
        diagnostics.add("bmt.shadowkey.zone.pitch-roll-unapplied")
    if animated:
        diagnostics.add("bmt.shadowkey.zone.static-placements")
    if variant == "uncounted":
        diagnostics.add("bmt.shadowkey.zone.sky-texture-header")
    if placed_multi_skin:
        diagnostics.add("bmt.shadowkey.zone.placed-skin-default")
    if placed_magenta:
        diagnostics.add("bmt.shadowkey.zone.placed-magenta-opaque")
    if placed_reversed:
        diagnostics.add("bmt.shadowkey.zone.placed-reversed-winding")
    if resolved != len(placements):
        diagnostics.add("bmt.shadowkey.zone.placement-unresolved")

    zmp_bytes = raw[".zmp"]
    kind, confidence, text = probe.probe_zone(zmp_bytes[:PROBE_BYTES], len(zmp_bytes), complete_for(len(zmp_bytes)),
                                              stem + ".zmp")
    return {
        "stem": stem,
        "probe": {"kind": kind, "confidence": confidence, "complete": complete_for(len(zmp_bytes)),
                  "evidence": text if kind != "NotAModel" else None},
        "grid": {"name": zmp["name"], "author": zmp["author"], "description": zmp["description"],
                 "width": zmp["width"], "height": zmp["height"], "prototypes": len(zcp)},
        "terrain": terrain_expectation(zmp, zcp),
        "palette": {"original": sha(palette), "entries": sha(bytes(entries)), "transparent": transparent,
                    "zlu": sha(zlu)},
        "textures": textures,
        "sky": sky,
        "placements": {"count": len(placements), "resolved": resolved, "animated": animated,
                       "pitchRoll": pitch_roll, "rowFields": PLACEMENT_FIELDS, "rows": rows},
        "blockedVertices": {"document": blocked_doc, "legacy": blocked_legacy},
        "placedMultiSkinSlots": sorted(placed_multi_skin),
        "placedMagentaSlots": sorted(placed_magenta),
        "placedReversedSlots": sorted(placed_reversed),
        "unplacedPrototypes": unplaced,
        "coverage": coverage,
        "diagnostics": sorted(diagnostics),
    }


# --------------------------------------------------------------------------------------------------------------------
# Manifest
# --------------------------------------------------------------------------------------------------------------------

def entry_name(slot, name):
    return f"{slot:03d}_{name}"


def cell_text(dims):
    return "|".join(f"{key}={dims[key]}" for key in sorted(dims))


def manifest(root, census_dir, out):
    with open(os.path.join(census_dir, "pack-slots.json"), encoding="utf-8") as handle:
        pack_census = json.load(handle)
    with open(os.path.join(census_dir, "zones.json"), encoding="utf-8") as handle:
        zones = json.load(handle)
    with open(os.path.join(census_dir, "files.json"), encoding="utf-8") as handle:
        files = json.load(handle)
    pack = probe.load_pack(root)
    slots = [row for row in pack_census["slots"] if row["size"]]

    items, weights, seen = [], [], set()
    for row in slots:
        if row["sha256"] in seen:
            continue
        seen.add(row["sha256"])
        dims, tags = probe.slot_cells(row)
        items.append((row["slot"], probe.requirements_for(dims, tags)))
        weights.append(row["size"])
    chosen_slots, slot_status = probe.milp_cover(items, weights)
    covered = set()
    for key, reqs in items:
        if key in chosen_slots:
            covered |= reqs
    uncovered = sorted({r for _, reqs in items for r in reqs} - covered)

    zone_items, zone_weights = [], []
    for zone in zones:
        dims, tags = probe.zone_cells(zone)
        zone_items.append((zone["stem"], probe.requirements_for(dims, tags)))
        zone_weights.append(sum(f["size"] for f in zone["files"].values()))
    chosen_zones, zone_status = probe.milp_cover(zone_items, zone_weights)
    zone_covered = set()
    for key, reqs in zone_items:
        if key in chosen_zones:
            zone_covered |= reqs
    uncovered += sorted({r for _, reqs in zone_items for r in reqs} - zone_covered)

    by_slot = {row["slot"]: row for row in pack_census["slots"]}
    edges = {
        "smallest": min(slots, key=lambda r: r["size"])["slot"],
        "largest": max(slots, key=lambda r: r["size"])["slot"],
        "most-frames": max(slots, key=lambda r: r["frames"])["slot"],
        "most-skins": max(slots, key=lambda r: r["skins"])["slot"],
        "most-vertices": max(slots, key=lambda r: r["vertices"])["slot"],
        "most-faces": max(slots, key=lambda r: r["face_count"])["slot"],
        "widest-uv-wrap": max(slots, key=lambda r: max(r["facts"]["uv_texel_max"][0] / r["width"],
                                                       r["facts"]["uv_texel_max"][1] / r["height"]))["slot"],
        "most-sequences": max(slots, key=lambda r: (len(r["sequences"]), r["frames"]))["slot"],
        "largest-frame-delta": max(slots, key=lambda r: r["facts"]["max_frame_delta"])["slot"],
    }
    slot_rows = []
    order = list(chosen_slots) + [s for s in edges.values() if s not in chosen_slots]
    for slot in dict.fromkeys(order):
        census = by_slot[slot]
        data = probe.slot_bytes(pack, slot)
        dims, tags = probe.slot_cells(census)
        slot_rows.append({
            "role": "cover" if slot in chosen_slots else "edge",
            "name": f"slot {slot} {census['name']}",
            "slot": slot,
            "entry": entry_name(slot, census["name"]),
            "offset": pack["rows"][slot][0],
            "size": len(data),
            "sha256": sha(data),
            "alsoInSlots": census["duplicate_payload_slots"],
            "cell": cell_text(dims),
            "tags": sorted(tags),
            "edges": [label for label, s in edges.items() if s == slot],
        })

    zone_by_stem = {z["stem"]: z for z in zones}
    zone_edges = {
        "most-placements": max(zones, key=lambda z: z["placements"]["count"])["stem"],
        "fewest-placements": min(zones, key=lambda z: z["placements"]["count"])["stem"],
        "most-prototypes": max(zones, key=lambda z: z["prototypes"]["count"])["stem"],
        "fewest-prototypes": min(zones, key=lambda z: z["prototypes"]["count"])["stem"],
        "most-terrain": max(zones, key=lambda z: z["terrain"]["quads"])["stem"],
        "most-animated": max(zones, key=lambda z: z["placements"]["animated"])["stem"],
    }
    zone_rows = []
    zone_order = list(chosen_zones) + [s for s in zone_edges.values() if s not in chosen_zones]
    for stem in dict.fromkeys(zone_order):
        zone = zone_by_stem[stem]
        dims, tags = probe.zone_cells(zone)
        pins = {}
        for family in ZONE_FAMILIES + (".sta",):
            path = os.path.join(root, stem + family)
            if os.path.isfile(path):
                data = probe.read(path)
                pins[stem + family] = {"size": len(data), "sha256": sha(data)}
        models = probe.read(os.path.join(root, stem + "_models.txt"))
        pins[stem + "_models.txt"] = {"size": len(models), "sha256": sha(models)}
        zone_rows.append({
            "role": "cover" if stem in chosen_zones else "edge",
            "name": f"zone {stem}",
            "stem": stem,
            "sha256": pins[stem + ".zmp"]["sha256"],
            "size": pins[stem + ".zmp"]["size"],
            "cell": cell_text(dims),
            "tags": sorted(tags),
            "edges": [label for label, s in zone_edges.items() if s == stem],
            "files": pins,
        })

    declines = []
    for row in pack_census["slots"]:
        if not row["size"]:
            declines.append({"name": f"empty slot {row['slot']}", "kind": "empty-slot", "slot": row["slot"],
                             "entry": entry_name(row["slot"], row["name"]), "size": 0, "sha256": EMPTY_SHA256,
                             "expect": "NotAModel"})
    wanted = {".s": 1, ".wav": 1, ".ogg": 1, ".txt": 1, ".app": 1, ".zcp": 1, ".zsk": 1, ".ztx": 1, ".zlu": 1,
              ".zfg": 1, ".ent": 1, ".spr": 1, ".idx": 1}
    for f in sorted(files, key=lambda f: (f["extension"], f["size"], f["path"])):
        if wanted.get(f["extension"], 0) > 0 and f["size"] > 0:
            wanted[f["extension"]] -= 1
            data = probe.read(os.path.join(root, f["path"]))
            declines.append({"name": f"file {f['path']}", "kind": "file", "path": f["path"], "size": len(data),
                             "sha256": sha(data), "expect": "NotAModel",
                             "meshReason": f["mesh_reason"], "zoneReason": f["zone_reason"]})

    pins = {}
    for name in ("models.idx", "models.huge", "models.txt", "entities.txt"):
        data = probe.read(os.path.join(root, name))
        pins[name] = {"size": len(data), "sha256": sha(data)}
    document = {
        "schema": SCHEMA,
        "generator": "tools/scripts/gate2/shadowkey_cover.py manifest",
        "plan": "docs/design/cut2-shadowkey-reader-plan-20260928.md, section 9",
        "root": ROOT_TOKEN,
        "rule": "exact MILP joint cover over every value and every observed pair of values of the slot cells (frames, "
                "skins, texture, sequences) and zone cells (sky, painted, palette key, fog, grid) plus every tag; "
                "fewest rows, then fewest bytes; byte-identical slot payloads collapse to one representative; then "
                "the edge rows",
        "solver": {"slots": slot_status, "zones": zone_status},
        "uncoveredItems": uncovered,
        "pack": pins,
        "slots": slot_rows,
        "zones": zone_rows,
        "declineControls": declines,
        "syntheticControls": [
            "every truncation of a builder record is never Supported and Confirmed", "one trailing byte after the "
            "sequence table", "tag 6", "trailer 2", "coordinate count 3V + 1", "a face vertex index equal to the "
            "vertex count", "a face UV index equal to the UV count", "a sequence end past the frame count",
            "a zero sequence rate", "a .zmp whose declared length is one cell short", "a .zmp name field without a "
            "NUL", "an interior sky payload read with the counted header",
        ],
    }
    probe.write_json(out, document)
    report = [
        f"slot cover: {len(chosen_slots)} records over {slot_status['requirements']} requirements "
        f"({slot_status['stage1']}; {slot_status['stage2']})",
        f"  cover + edges: {len(slot_rows)} rows, {sum(r['size'] for r in slot_rows):,} bytes",
        f"zone cover: {len(chosen_zones)} zones over {zone_status['requirements']} requirements",
        f"  cover + edges: {len(zone_rows)} zones: {', '.join(r['stem'] for r in zone_rows)}",
        f"decline controls: {sum(1 for d in declines if d['kind'] == 'empty-slot')} empty slots, "
        f"{sum(1 for d in declines if d['kind'] == 'file')} tree files",
        f"uncovered items: {len(uncovered)}",
    ]
    report += [f"  {r['name']:32} {r['role']:5} {r['size']:7} {r['sha256'][:12]} {','.join(r['edges'])}"
               for r in slot_rows]
    report += [f"  {r['name']:32} {r['role']:5} {','.join(r['edges'])} {r['cell']}" for r in zone_rows]
    with open(os.path.splitext(out)[0] + "-report.txt", "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(report) + "\n")
    print("\n".join(report))


# --------------------------------------------------------------------------------------------------------------------
# Verify
# --------------------------------------------------------------------------------------------------------------------

def verify(root, manifest_path, out):
    with open(manifest_path, encoding="utf-8") as handle:
        document = json.load(handle)
    pack = probe.load_pack(root)
    failures = []
    checked = 0

    def check(label, data, pin):
        nonlocal checked
        checked += 1
        if len(data) != pin["size"] or sha(data) != pin["sha256"]:
            failures.append(label)

    for name, pin in document["pack"].items():
        check(name, probe.read(os.path.join(root, name)), pin)
    for row in document["slots"]:
        check(row["name"], probe.slot_bytes(pack, row["slot"]), row)
        if pack["rows"][row["slot"]][0] != row["offset"]:
            failures.append(row["name"] + " offset")
        for other in row["alsoInSlots"]:
            check(f"{row['name']} (slot {other})", probe.slot_bytes(pack, other), row)
    for row in document["zones"]:
        for name, pin in row["files"].items():
            check(name, probe.read(os.path.join(root, name)), pin)
    for row in document["declineControls"]:
        data = probe.slot_bytes(pack, row["slot"]) if row["kind"] == "empty-slot" else probe.read(
            os.path.join(root, row["path"]))
        check(row["name"], data, row)
    altered = dict(document["slots"][0])
    altered["sha256"] = ("1" if altered["sha256"][0] != "1" else "2") + altered["sha256"][1:]
    detected = sha(probe.slot_bytes(pack, altered["slot"])) != altered["sha256"]
    result = {"checked": checked, "failures": failures, "alteredDigitDetected": detected}
    probe.write_json(os.path.join(out, "verify.json"), result)
    print(json.dumps(result, indent=1))
    if failures or not detected:
        sys.exit(1)


# --------------------------------------------------------------------------------------------------------------------
# Expectations
# --------------------------------------------------------------------------------------------------------------------

def slot_expectation(pack, row):
    data = probe.slot_bytes(pack, row["slot"])
    kind, confidence, text = probe.probe_mesh(data[:PROBE_BYTES], len(data), complete_for(len(data)))
    record = probe.parse_mesh(data)
    result = mesh_expectation(record, "standalone")
    result["probe"] = {"kind": kind, "confidence": confidence, "complete": complete_for(len(data)),
                       "evidence": text if kind != "NotAModel" else None}
    return result


def decline_expectation(pack, root, row):
    data = probe.slot_bytes(pack, row["slot"]) if row["kind"] == "empty-slot" else probe.read(
        os.path.join(root, row["path"]))
    complete = complete_for(len(data))
    name = row.get("path") or row["entry"]
    mesh = probe.probe_mesh(data[:PROBE_BYTES], len(data), complete)
    zone = probe.probe_zone(data[:PROBE_BYTES], len(data), complete, name)
    return {"probe": {"kind": mesh[0], "confidence": mesh[1], "complete": complete,
                      "reason": mesh[2] if mesh[0] == "NotAModel" else None},
            "zoneProbe": {"kind": zone[0], "confidence": zone[1],
                          "reason": zone[2] if zone[0] == "NotAModel" else None}}


def expectations(root, manifest_path, out):
    with open(manifest_path, encoding="utf-8") as handle:
        document = json.load(handle)
    pack = probe.load_pack(root)
    records = {}
    for slot in range(pack["count"]):
        data = probe.slot_bytes(pack, slot)
        if data:
            records[slot] = probe.parse_mesh(data)
    entities = probe.load_entities(root)
    lines = [json.dumps({"schema": EXPECTATIONS_SCHEMA, "generator": "tools/scripts/gate2/shadowkey_cover.py "
                                                                     "expectations",
                         "rules": DIGEST_RULES}, sort_keys=False)]
    for row in document["slots"]:
        record = {"schema": EXPECTATIONS_SCHEMA, "kind": "slot", "name": row["name"], "sha256": row["sha256"],
                  "slot": row["slot"], "size": row["size"]}
        record.update(slot_expectation(pack, row))
        lines.append(json.dumps(record, separators=(",", ":")))
    for row in document["zones"]:
        record = {"schema": EXPECTATIONS_SCHEMA, "kind": "zone", "name": row["name"], "sha256": row["sha256"],
                  "size": row["size"]}
        record.update(zone_expectation(root, row["stem"], pack, records, entities))
        lines.append(json.dumps(record, separators=(",", ":")))
    for row in document["declineControls"]:
        if row["kind"] == "empty-slot" and row["slot"] != document["declineControls"][0]["slot"]:
            continue  # every empty slot has the same (empty) bytes; one record serves all eleven
        record = {"schema": EXPECTATIONS_SCHEMA, "kind": "decline", "name": row["name"], "sha256": row["sha256"],
                  "size": row["size"]}
        record.update(decline_expectation(pack, root, row))
        lines.append(json.dumps(record, separators=(",", ":")))
    with open(out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")
    print(f"{out}: {len(lines) - 1} records")


# --------------------------------------------------------------------------------------------------------------------
# Self-check and the golden synthetic records the C# default suite pins
# --------------------------------------------------------------------------------------------------------------------

GOLDEN_FRAME0 = [0, 0, 0, 256, 0, 0, 256, 512, 0, 0, 512, -128]
GOLDEN_OFFSET = (3, -7, 11)
GOLDEN_UVS = [0, 0, 512, 0, 512, 512, 0, 512, 1024, 256]
GOLDEN_FACES = [0, 1, 2, 0, 1, 2, 0, 2, 3, 4, 2, 3]
GOLDEN_SKIN0 = [0x0F00, 0x00F0, 0x000F, 0x0F0F]
GOLDEN_SKIN1 = [0x0123, 0x0456, 0x0789, 0x0ABC]


def golden_animated():
    """Two frames, four vertices, five UVs (vertex 0 owns UVs 0 and 4), two faces, two 2x2 skins (the first holds
    magenta), sequences (0, 1, 10) and (1, 2, 3). The C# builder writes the same bytes (its test pins this SHA-256)."""
    frame1 = [value + GOLDEN_OFFSET[k % 3] for k, value in enumerate(GOLDEN_FRAME0)]
    return probe.build_mesh(2, GOLDEN_FRAME0 + frame1, GOLDEN_UVS, GOLDEN_FACES, skins=2, width=2, height=2,
                            texels=GOLDEN_SKIN0 + GOLDEN_SKIN1, sequences=[(0, 1, 10), (1, 2, 3)])


def golden_static():
    """One frame of the same geometry, one 2x2 skin without magenta, the static sequence (0, 1, 1)."""
    return probe.build_mesh(1, GOLDEN_FRAME0, GOLDEN_UVS, GOLDEN_FACES, skins=1, width=2, height=2,
                            texels=GOLDEN_SKIN1, sequences=[(0, 1, 1)])


def golden_zone():
    """A 3x2 grid: cell (1, 0) blocked; prototypes 0 (flat, floor 0, ceiling 0x0400), 1 (floor sloped 0, 64, 128, 0,
    ceiling 0x0400) and 2 (floor 0x0100, ceiling 0x0300). Cells row-major: [0, 2(blocked), 1], [1, 0, 2]."""
    cells = [(0, 0, 0, 0), (probe.BLOCKED_FLAG, 0, 0, 2), (0, 0, 0, 1), (0, 0, 0, 1), (0, 0, 0, 0), (0, 0, 0, 2)]
    zmp = {"width": 3, "height": 2, "cells": cells}
    zcp = [
        {"floor": (0, 0, 0, 0), "ceiling": (0x0400,) * 4},
        {"floor": (0, 64, 128, 0), "ceiling": (0x0400,) * 4},
        {"floor": (0x0100,) * 4, "ceiling": (0x0300,) * 4},
    ]
    return zmp, zcp


def selfcheck(out):
    checks = []

    def check(name, condition):
        checks.append({"check": name, "passed": bool(condition)})

    animated = probe.parse_mesh(golden_animated())
    static = probe.parse_mesh(golden_static())
    owners = uv_owners(animated)
    check("the UV domain splits vertex 0 over UVs 0 and 4", owners == [0, 1, 2, 3, 0])
    expectation = mesh_expectation(animated)
    check("animated golden: 5 vertices, 1 target, 2 clips of 2 keys",
          expectation["document"]["vertexCount"] == 5 and expectation["document"]["targets"] == 1 and
          [c["keys"] for c in expectation["digests"]["clips"]] == [2, 2])
    check("animated golden: two skins in an exclusive group, each its own node",
          expectation["document"]["layerSets"] == 2 and expectation["document"]["nodes"] == 2)
    check("animated golden: one track per clip", [c["tracks"] for c in expectation["digests"]["clips"]] == [1, 1])
    check("animated golden diagnostics", expectation["diagnostics"] == [
        "bmt.shadowkey.mesh.magenta-opaque", "bmt.shadowkey.mesh.skin-selection",
        "bmt.shadowkey.mesh.step-interpolation"])
    # Control: a UV owned by two vertices must be refused.
    shared = probe.build_mesh(1, GOLDEN_FRAME0, GOLDEN_UVS, [0, 1, 2, 0, 1, 2, 0, 2, 3, 1, 2, 3], width=2, height=2,
                              texels=GOLDEN_SKIN1, sequences=[(0, 1, 1)])
    try:
        uv_owners(probe.parse_mesh(shared))
        check("a UV owned by two vertices is refused", False)
    except DocumentError:
        check("a UV owned by two vertices is refused", True)
    # Winding: a closed tetrahedron wound outward is positive; wound inward it is negative and diagnosed; the open
    # golden record is not closed (review finding 5).
    tetra = [0, 0, 0, 256, 0, 0, 0, 256, 0, 0, 0, 256]
    outward = [0, 2, 1, 0, 2, 1, 0, 1, 3, 0, 1, 3, 0, 3, 2, 0, 3, 2, 1, 2, 3, 1, 2, 3]
    inward = [0, 1, 2, 0, 1, 2, 0, 3, 1, 0, 3, 1, 0, 2, 3, 0, 2, 3, 1, 3, 2, 1, 3, 2]
    tetra_uvs = [0, 0, 512, 0, 0, 512, 512, 512]
    out_record = probe.parse_mesh(probe.build_mesh(1, tetra, tetra_uvs, outward, width=2, height=2,
                                                   texels=GOLDEN_SKIN1, sequences=[(0, 1, 1)]))
    in_record = probe.parse_mesh(probe.build_mesh(1, tetra, tetra_uvs, inward, width=2, height=2,
                                                  texels=GOLDEN_SKIN1, sequences=[(0, 1, 1)]))
    check("an outward tetrahedron is closed with 6V = 256^3", winding(out_record) == (True, 16777216))
    check("an inward tetrahedron is closed with 6V = -256^3 and diagnosed",
          winding(in_record) == (True, -16777216) and
          "bmt.shadowkey.mesh.reversed-winding" in mesh_diagnostics(in_record, "standalone"))
    check("the open golden record is not closed", not winding(static)[0])
    # Unused vertices: a fifth vertex no face names gets its own NativeOnly census element (review finding 3).
    extra = probe.parse_mesh(probe.build_mesh(2, GOLDEN_FRAME0 + [9, 8, 7] + [v + GOLDEN_OFFSET[k % 3] for k, v in
                                                                             enumerate(GOLDEN_FRAME0)] + [19, 18, 17],
                                              GOLDEN_UVS, GOLDEN_FACES, width=2, height=2, texels=GOLDEN_SKIN1,
                                              sequences=[(0, 2, 5)]))
    check("an unused vertex is its own NativeOnly element",
          ["unused-vertices", "NativeOnly"] in mesh_coverage(extra) and unused_vertices(extra) == [4] and
          mesh_expectation(extra)["digests"]["unusedVertices"]["positions"] == sha(i32_bytes([9, 8, 7, 19, 18, 17])))
    check("a record with every vertex used has no such element", unused_vertices(animated) == [])
    # Control: the unrolled corner domain has 6 vertices, not the UV domain's 5.
    check("the unrolled domain differs from the UV domain", expectation["document"]["unrolledVertexCount"] == 6)
    # Control: x16 instead of x17 changes the RGBA digest.
    block = animated["skin_blocks"][0]
    x16 = bytearray()
    for texel in struct.unpack("<4H", block):
        x16 += bytes((((texel >> 8) & 15) * 16, ((texel >> 4) & 15) * 16, (texel & 15) * 16, 255))
    check("x16 expansion changes the skin digest", sha(bytes(x16)) != expectation["digests"]["skins"][0]["rgba"])
    # Clip keys: seq01 selects target 0 (frame 1) at both keys; seq00 selects none.
    times, weights = clip_arrays(animated, (1, 2, 3))
    check("seq01 keys 0 and 1/3, one-hot on target 0", list(times) == [np.float32(0), np.float32(1) / np.float32(3)]
          and list(weights) == [1.0, 1.0])
    times, weights = clip_arrays(animated, (0, 1, 10))
    check("seq00 keys 0 and 0.1, no target", list(weights) == [0.0, 0.0])

    # Placement entries: exact on quarter turns, the yaw sign negated.
    check("angle 0 is the identity rotation", placement_entries(0, 256) == (1.0, 0.0))
    check("a quarter turn (16384) gives cos 0, sin -1", placement_entries(16384, 256) == (0.0, -1.0))
    check("a half turn gives cos -1", placement_entries(-32768, 512) == (-2.0, 0.0))
    m00, m01 = placement_entries(8192, 256)
    check("an eighth turn is rounded once", m00 == float(np.float32(math.cos(-math.pi / 4))) and
          m01 == float(np.float32(math.sin(-math.pi / 4))))
    check("the +yaw control differs", placement_entries(-16384, 256) != placement_entries(16384, 256))

    # Terrain rule on the golden grid, with the rotated-corner control.
    zmp, zcp = golden_zone()
    quads = terrain_quads(zmp, zcp)
    counts = {kind: sum(1 for k, _ in quads if k == kind) for kind in FACE_KINDS}
    check("golden terrain counts", counts == {"floor": 5, "ceiling": 5, "wall": 12, "riser": 4, "downstand": 2})
    terrain = terrain_expectation(zmp, zcp)
    rotated = [{"floor": tuple(r["floor"][1:]) + (r["floor"][0],), "ceiling": r["ceiling"]} for r in zcp]
    check("rotated corner order changes the floor digest",
          terrain_expectation(zmp, rotated)["floor"]["positions"] != terrain["floor"]["positions"])

    golden = {
        "animated": {"sha256": sha(golden_animated()), "size": len(golden_animated()),
                     "digests": expectation["digests"], "sections": expectation["sections"]},
        "static": {"sha256": sha(golden_static()), "size": len(golden_static()),
                   "digests": mesh_expectation(static)["digests"]},
        "terrain": terrain,
    }
    result = {"checks": checks, "failed": [c["check"] for c in checks if not c["passed"]], "golden": golden}
    probe.write_json(os.path.join(out, "selfcheck.json"), result)
    print(json.dumps(result, indent=1))
    if result["failed"]:
        sys.exit(1)


# --------------------------------------------------------------------------------------------------------------------

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--root", help="the Shadowkey application directory (default: RealAssetPaths resolution)")
    commands = parser.add_subparsers(dest="command", required=True)
    sub = commands.add_parser("manifest")
    sub.add_argument("--census", required=True)
    sub.add_argument("--out", required=True)
    sub = commands.add_parser("verify")
    sub.add_argument("--manifest", required=True)
    sub.add_argument("--out", required=True)
    sub = commands.add_parser("expectations")
    sub.add_argument("--manifest", required=True)
    sub.add_argument("--out", required=True)
    sub = commands.add_parser("selfcheck")
    sub.add_argument("--out", required=True)
    args = parser.parse_args(argv)
    if args.command == "selfcheck":
        selfcheck(args.out)
        return
    root, how = probe.resolve_root(args.root)
    sys.stderr.write(f"root: {root} (via {how})\n")
    if args.command == "manifest":
        manifest(root, args.census, args.out)
    elif args.command == "verify":
        verify(root, args.manifest, args.out)
    elif args.command == "expectations":
        expectations(root, args.manifest, args.out)


if __name__ == "__main__":
    main()
