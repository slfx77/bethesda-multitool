# SPDX-License-Identifier: 0BSD
"""Cut-2 Shadowkey oracle: an independent decoder, the probe rules, the census, the measurements and the cover.

This probe restates the Shadowkey (N-Gage, 2004) mesh and zone layouts from the cut-2 plan
(``docs/design/cut2-shadowkey-reader-plan-20260928.md``, section 0) and reads the retail files with its own code.
It shares nothing with BMT's C# readers (``Core/Formats/Travels/Shadowkey``) and never consumes their results, so
hops A1 (fields), A2 (coverage) and A3 (geometry) compare ``mesh dump`` output against a second decoding of the
same bytes. Every multi-byte value is LITTLE-endian (Symbian on ARM); the J2ME Travels titles are big-endian and
nothing here applies to them. The only references are the retail bytes themselves: clean room, no game code.

Mesh record (one slot of ``models.huge``; the ``.zsk`` sky payload uses the same walk, see ``parse_mesh``)::

    u16 tag (7), u16 frames, u16 vertices, u16 uvs, u16 faces, u16 coordinates (3 x vertices), u16 trailer (1)
    frames x vertices x (i16 x, i16 y, i16 z)          frame-major keyframes, the second component is up
    uvs x (u16 u, u16 v)                               8.8 fixed point in texels, values past the size wrap
    faces x (u16 v0, v1, v2, u16 t0, t1, t2)           vertex and UV indices, no texture index
    u16 skins, u16 width, u16 height                   (the interior sky omits the skin count, see below)
    skins x width x height x u16                       0x0RGB 4:4:4 texels, top row first
    u16 sequences, sequences x (u16 start, u16 end, u16 rate)   [start, end) over the frames

Pack: ``models.idx`` is ``u32 count`` then ``count`` x (u32 offset, u32 size) tiling ``models.huge`` exactly;
``models.txt`` names the slots (``index flag width height file.bin``). The slot index is the identity.

Zone (one stem, beside the pack): ``.zmp .zcp .zsk .ztx .zlu .zfg`` are a u32 inflated length plus one zlib
stream; ``.ent .sur .zon .pth .stn .pal`` (and azra's ``.sta``) are plain. ``<stem>_models.txt`` masks the pack
slots the zone loads; ``entities.txt`` maps an ``.ent`` id to a slot.

Subcommands (all read-only; one process at a time):

    census    every file of the application directory: per-slot mesh records, per-zone file sets, the probe verdict
              of every file under every rule, and the composition budgets of a zone scene document
    measure   the discriminating measurements the plan relies on: the sky texel encoding, the mesh-to-zone
              orientation, the floor contact, the winding and the sequence rates, each with its control
    cover     the pairwise joint cover (exact MILP) over slots and zones, edge rows and decline controls,
              SHA-256 pinned
    verify    re-reads every row of a cover manifest; one altered digit must be detected
    dump      the A1/A3 oracle JSON for one slot (--slot N) or one zone (--zone STEM)
    selfcheck synthetic records and zone grids: round trips, every truncation, and each probe control

The root resolves exactly as ``RealAssetPaths.Travels.ShadowkeyRoot`` does: ``BETHESDA_TEST_DATA_ROOT`` (as given
and with ``Sample``), then the repository's own ``Sample`` directory, under the two build names that method tries.
``--root`` overrides.
"""

import argparse
import hashlib
import json
import math
import os
import struct
import sys
import zlib
from collections import Counter, defaultdict

ROOT_VARIABLE = "BETHESDA_TEST_DATA_ROOT"
BUILD_RELATIVE = (
    ("Builds", "The Elder Scrolls Travels - Shadowkey (N-Gage - Final)", "The Elder Scrolls Travels - Shadowkey",
     "system", "apps", "6R51"),
    ("Builds", "The Elder Scrolls Travels - Shadowkey (2004-10-27, N-Gage - Final)",
     "The Elder Scrolls Travels - Shadowkey", "system", "apps", "6r51"),
)

PROBE_BYTES = 64 * 1024
MESH_TAG = 7
MESH_TRAILER = 1
MESH_HEADER = 14
MAX_TEXTURE_SIDE = 1024
MAGENTA_444 = 0x0F0F
ZONE_HEADER = 132
ZONE_CELL = 6
BLOCKED_FLAG = 0x02
COMPRESSED_FAMILIES = (".zmp", ".zcp", ".zsk", ".ztx", ".zlu", ".zfg")
PLAIN_FAMILIES = (".ent", ".sur", ".zon", ".pth", ".stn", ".pal")
SKY_IMAGE_BYTES = 256 * 256 * 2
BINARY_TURN = 65536.0
OUTDOOR_SKY_CORNER_PAIRS = 44  # measured: 11 of the 12 outdoor domes; raiders carries 35 (census zones.json)
HUMANOID_TABLE = ((0, 1, 10), (1, 22, 10), (22, 31, 10), (31, 42, 10), (42, 66, 10), (66, 98, 10), (98, 106, 10),
                  (106, 116, 3), (116, 127, 3), (127, 138, 10), (138, 144, 10))


# ----------------------------------------------------------------------------------------------------------------
# Root resolution and small helpers


def repo_root():
    """The directory holding Directory.Build.props above this script, as RealAssetPaths walks up from its binary."""
    directory = os.path.dirname(os.path.abspath(__file__))
    while True:
        if os.path.isfile(os.path.join(directory, "Directory.Build.props")):
            return directory
        parent = os.path.dirname(directory)
        if parent == directory:
            return None
        directory = parent


def resolve_root(explicit=None):
    """Returns (root, how) for the Shadowkey application directory, or raises SystemExit naming what was tried."""
    if explicit:
        if os.path.isdir(explicit):
            return os.path.abspath(explicit), "--root"
        raise SystemExit(f"--root {explicit} is not a directory")
    tried = []
    bases = []
    variable = os.environ.get(ROOT_VARIABLE)
    if variable:
        bases.append((variable, ROOT_VARIABLE))
        bases.append((os.path.join(variable, "Sample"), ROOT_VARIABLE + "/Sample"))
    repo = repo_root()
    if repo:
        bases.append((os.path.join(repo, "Sample"), "repository Sample"))
    for relative in BUILD_RELATIVE:
        for base, how in bases:
            candidate = os.path.join(base, *relative)
            tried.append(candidate)
            if os.path.isdir(candidate):
                return os.path.abspath(candidate), how
    raise SystemExit("Shadowkey root not found; tried:\n  " + "\n  ".join(tried))


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def read(path):
    with open(path, "rb") as handle:
        return handle.read()


def write_json(path, value):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, indent=1, sort_keys=False)
        handle.write("\n")


def latin1_field(raw):
    """A fixed-width text field cut at its first NUL (the bytes after it are 0xCC/0xCD fill or stale text)."""
    end = raw.find(b"\0")
    return (raw if end < 0 else raw[:end]).decode("latin-1")


def inflate_envelope(data):
    """(payload, declared, error) for the u32-length + zlib envelope; error is None only for an exact stream."""
    if len(data) < 6 or data[4] != 0x78 or ((data[4] << 8) | data[5]) % 31:
        return None, None, "no zlib header at byte 4"
    declared = struct.unpack_from("<I", data, 0)[0]
    stream = zlib.decompressobj()
    try:
        payload = stream.decompress(data[4:]) + stream.flush()
    except zlib.error as error:
        return None, declared, f"zlib: {error}"
    if not stream.eof:
        return payload, declared, "stream did not end"
    if stream.unused_data:
        return payload, declared, f"{len(stream.unused_data)} bytes after the stream"
    if len(payload) != declared:
        return payload, declared, f"inflated {len(payload)} bytes, declared {declared}"
    return payload, declared, None


# ----------------------------------------------------------------------------------------------------------------
# Mesh record


class RecordError(ValueError):
    """A record that does not walk; the message names the byte offset and the field."""


def parse_mesh(data, texture_header="counted"):
    """Walks one record and returns its fields. ``texture_header`` is ``counted`` (u16 skins, width, height: every
    pack record and the outdoor sky) or ``uncounted`` (width, height, one implied skin: the interior sky)."""
    size = len(data)
    if size < MESH_HEADER:
        raise RecordError(f"{size} bytes, shorter than the 14-byte header")
    tag, frames, vertices, uv_count, face_count, coordinates, trailer = struct.unpack_from("<7H", data, 0)
    if tag != MESH_TAG:
        raise RecordError(f"byte 0: tag {tag}, expected 7")
    if trailer != MESH_TRAILER:
        raise RecordError(f"byte 12: trailer {trailer}, expected 1")
    if coordinates != 3 * vertices:
        raise RecordError(f"byte 10: {coordinates} coordinates for {vertices} vertices")
    if frames == 0:
        raise RecordError("byte 2: zero frames")
    offsets = {"header": 0}
    position = MESH_HEADER

    def need(length, what):
        if position + length > size:
            raise RecordError(f"byte {position}: {what} needs {length} bytes, the record has {size}")

    offsets["positions"] = position
    need(6 * frames * vertices, "the position block")
    positions = struct.unpack_from(f"<{3 * frames * vertices}h", data, position) if vertices else ()
    position += 6 * frames * vertices
    offsets["uvs"] = position
    need(4 * uv_count, "the UV block")
    uvs = struct.unpack_from(f"<{2 * uv_count}H", data, position) if uv_count else ()
    position += 4 * uv_count
    offsets["faces"] = position
    need(12 * face_count, "the face block")
    faces = struct.unpack_from(f"<{6 * face_count}H", data, position) if face_count else ()
    for face in range(face_count):
        base = 6 * face
        if max(faces[base:base + 3]) >= vertices:
            raise RecordError(f"byte {position + 12 * face}: face {face} indexes vertex {max(faces[base:base + 3])} of {vertices}")
        if max(faces[base + 3:base + 6]) >= uv_count:
            raise RecordError(f"byte {position + 12 * face}: face {face} indexes UV {max(faces[base + 3:base + 6])} of {uv_count}")
    position += 12 * face_count
    offsets["texture_header"] = position
    if texture_header == "counted":
        need(6, "the texture header")
        skins, width, height = struct.unpack_from("<3H", data, position)
        position += 6
    elif texture_header == "uncounted":
        need(4, "the uncounted texture header")
        width, height = struct.unpack_from("<2H", data, position)
        skins = 1
        position += 4
    else:
        raise ValueError(texture_header)
    offsets["texels"] = position
    texel_count = width * height
    need(2 * skins * texel_count, "the texel block")
    skin_blocks = [data[position + 2 * texel_count * k:position + 2 * texel_count * (k + 1)] for k in range(skins)]
    position += 2 * skins * texel_count
    offsets["sequence_count"] = position
    need(2, "the sequence count")
    sequence_count = struct.unpack_from("<H", data, position)[0]
    position += 2
    offsets["sequences"] = position
    need(6 * sequence_count, "the sequence table")
    raw = struct.unpack_from(f"<{3 * sequence_count}H", data, position) if sequence_count else ()
    sequences = [tuple(raw[3 * k:3 * k + 3]) for k in range(sequence_count)]
    for index, (start, end, rate) in enumerate(sequences):
        if not start < end <= frames:
            raise RecordError(f"byte {position + 6 * index}: sequence {index} is [{start}, {end}) over {frames} frames")
    position += 6 * sequence_count
    if position != size:
        raise RecordError(f"byte {position}: the sections end here but the record is {size} bytes")
    offsets["end"] = position
    return {
        "frames": frames, "vertices": vertices, "uv_count": uv_count, "face_count": face_count,
        "skins": skins, "width": width, "height": height, "sequences": sequences,
        "positions": positions, "uvs": uvs, "faces": faces, "skin_blocks": skin_blocks,
        "offsets": offsets, "texture_header": texture_header,
    }


def probe_mesh(prefix, length, complete):
    """The ``bmt.shadowkey.mesh`` probe on a bounded prefix: (kind, confidence, evidence or reason).

    Kinds: NotAModel, Supported (Confirmed or Tentative). The rule is structural (three header constants, index
    ranges, the texture header, the length arithmetic and the sequence table), applied only to bytes inside the
    prefix; ``length`` is the declared entry length when known."""
    if len(prefix) < MESH_HEADER:
        return "NotAModel", "None", "shorter than the 14-byte header"
    tag, frames, vertices, uv_count, face_count, coordinates, trailer = struct.unpack_from("<7H", prefix, 0)
    if tag != MESH_TAG or trailer != MESH_TRAILER or coordinates != 3 * vertices:
        return "NotAModel", "None", "header constants"
    if frames == 0 or vertices == 0 or uv_count == 0 or face_count == 0:
        return "NotAModel", "None", "a zero count"
    head = MESH_HEADER + 6 * frames * vertices + 4 * uv_count + 12 * face_count
    if length is not None and head + 8 > length:
        return "NotAModel", "None", "sections past the declared length"
    face_offset = head - 12 * face_count
    for face in range(face_count):
        base = face_offset + 12 * face
        if base + 12 > len(prefix):
            break
        v0, v1, v2, t0, t1, t2 = struct.unpack_from("<6H", prefix, base)
        if max(v0, v1, v2) >= vertices or max(t0, t1, t2) >= uv_count:
            return "NotAModel", "None", "face index out of range"
    evidence = f"Shadowkey mesh record: {frames} frames, {vertices} vertices, {face_count} faces"
    if head + 6 > len(prefix):
        if complete:
            return "NotAModel", "None", "truncated before the texture header"
        return "Supported", "Tentative", evidence + " (prefix ends before the texture header)"
    skins, width, height = struct.unpack_from("<3H", prefix, head)
    if skins == 0 or not (1 <= width <= MAX_TEXTURE_SIDE and 1 <= height <= MAX_TEXTURE_SIDE):
        return "NotAModel", "None", "texture header"
    sequence_count_at = head + 6 + 2 * skins * width * height
    evidence += f", {skins} skin(s) {width}x{height}"
    if length is not None:
        rest = length - sequence_count_at - 2
        if rest < 6 or rest % 6:
            return "NotAModel", "None", "length arithmetic"
    if sequence_count_at + 2 > len(prefix):
        if complete:
            return "NotAModel", "None", "truncated before the sequence count"
        return "Supported", "Tentative", evidence + " (prefix ends before the sequence table)"
    sequence_count = struct.unpack_from("<H", prefix, sequence_count_at)[0]
    end = sequence_count_at + 2 + 6 * sequence_count
    if sequence_count == 0 or (length is not None and end != length):
        return "NotAModel", "None", "sequence count against the length"
    if end > len(prefix):
        if complete:
            return "NotAModel", "None", "truncated in the sequence table"
        return "Supported", "Tentative", evidence + " (prefix ends in the sequence table)"
    for index in range(sequence_count):
        start, stop, rate = struct.unpack_from("<3H", prefix, sequence_count_at + 2 + 6 * index)
        if not start < stop <= frames or rate == 0:
            return "NotAModel", "None", "sequence range"
    if complete and end != len(prefix):
        return "NotAModel", "None", "bytes after the sequence table"
    if not complete:
        return "Supported", "Tentative", evidence + " (prefix is not the whole entry)"
    return "Supported", "Confirmed", evidence + f", {sequence_count} sequence(s)"


def mesh_facts(record, data):
    """Everything the census, the cover and the plan quote about one parsed record."""
    frames = record["frames"]
    vertices = record["vertices"]
    positions = record["positions"]
    uvs = record["uvs"]
    faces = record["faces"]
    width, height = record["width"], record["height"]
    face_count = record["face_count"]

    def vertex(frame, index):
        base = 3 * (frame * vertices + index)
        return positions[base], positions[base + 1], positions[base + 2]

    used_vertices = set()
    used_uvs = set()
    repeated_index = 0
    repeated_position = 0
    triangle_keys = Counter()
    for face in range(face_count):
        a, b, c, t0, t1, t2 = faces[6 * face:6 * face + 6]
        used_vertices.update((a, b, c))
        used_uvs.update((t0, t1, t2))
        if len({a, b, c}) < 3:
            repeated_index += 1
        elif len({vertex(0, a), vertex(0, b), vertex(0, c)}) < 3:
            repeated_position += 1
        triangle_keys[tuple(sorted((a, b, c)))] += 1
    duplicate_triangles = sum(count - 1 for count in triangle_keys.values() if count > 1)

    bounds = [[min(positions[axis::3]), max(positions[axis::3])] for axis in range(3)]
    frame0 = [[min(positions[axis:3 * vertices:3]), max(positions[axis:3 * vertices:3])] for axis in range(3)]
    u_values = uvs[0::2]
    v_values = uvs[1::2]
    uv_beyond = sum(1 for k in range(record["uv_count"])
                    if u_values[k] > 256 * width or v_values[k] > 256 * height)
    uv_fraction_bits = sum(1 for value in uvs if value & 0x1F)

    # Winding: closed oriented manifolds (by index, then with positions welded) and their signed volume.
    directed = Counter()
    welded = Counter()
    volume = 0
    for face in range(face_count):
        a, b, c = faces[6 * face:6 * face + 3]
        if len({a, b, c}) < 3:
            continue
        p, q, r = vertex(0, a), vertex(0, b), vertex(0, c)
        volume += (p[0] * (q[1] * r[2] - q[2] * r[1]) - p[1] * (q[0] * r[2] - q[2] * r[0])
                   + p[2] * (q[0] * r[1] - q[1] * r[0]))
        for x, y in ((a, b), (b, c), (c, a)):
            directed[(x, y)] += 1
        if len({p, q, r}) == 3:
            for x, y in ((p, q), (q, r), (r, p)):
                welded[(x, y)] += 1
    closed_index = bool(directed) and all(n == 1 and directed.get((e[1], e[0])) == 1 for e, n in directed.items())
    closed_welded = bool(welded) and all(n == 1 and welded.get((e[1], e[0])) == 1 for e, n in welded.items())

    skins = []
    for block in record["skin_blocks"]:
        texels = struct.unpack(f"<{len(block) // 2}H", block)
        distinct = set(texels)
        skins.append({
            "distinct": len(distinct),
            "magenta_texels": sum(1 for t in texels if t == MAGENTA_444),
            "top_nibble_set": sum(1 for t in texels if t >> 12),
            "sha256": sha256(block),
        })

    sequences = record["sequences"]
    covered = set()
    for start, end, _ in sequences:
        covered.update(range(start, end))
    max_delta = 0
    if frames > 1:
        for frame in range(1, frames):
            for index in range(3 * vertices):
                delta = abs(positions[3 * frame * vertices + index] - positions[index])
                if delta > max_delta:
                    max_delta = delta
    return {
        "unused_vertices": vertices - len(used_vertices),
        "unused_uvs": record["uv_count"] - len(used_uvs),
        "repeated_index_triangles": repeated_index,
        "repeated_position_triangles": repeated_position,
        "duplicate_triangles": duplicate_triangles,
        "bounds_all_frames": bounds,
        "bounds_frame0": frame0,
        "uv_texel_max": [max(u_values) / 256.0, max(v_values) / 256.0],
        "uv_texel_min": [min(u_values) / 256.0, min(v_values) / 256.0],
        "uvs_beyond_texture": uv_beyond,
        "uv_values_with_low_fraction_bits": uv_fraction_bits,
        "closed_by_index": closed_index,
        "closed_welded": closed_welded,
        "signed_volume_x6": volume,
        "skins": skins,
        "frames_in_no_sequence": frames - len(covered),
        "overlapping_sequences": sum(1 for i, s in enumerate(sequences) for t in sequences[i + 1:]
                                     if s[0] < t[1] and t[0] < s[1]),
        "rate_equals_length": sum(1 for s, e, r in sequences if e - s > 1 and r == e - s),
        "multi_frame_sequences": sum(1 for s, e, r in sequences if e - s > 1),
        "rate1_multi_frame": sum(1 for s, e, r in sequences if e - s > 1 and r == 1),
        "humanoid_table": tuple(sequences) == HUMANOID_TABLE[:len(sequences)] and len(sequences) >= 9,
        "max_frame_delta": max_delta,
        "size": len(data),
    }


# ----------------------------------------------------------------------------------------------------------------
# Pack


def load_pack(root):
    index = read(os.path.join(root, "models.idx"))
    pack = read(os.path.join(root, "models.huge"))
    names_path = os.path.join(root, "models.txt")
    names = read(names_path) if os.path.isfile(names_path) else None
    count = struct.unpack_from("<I", index, 0)[0]
    if 4 + 8 * count != len(index):
        raise RecordError(f"models.idx: {count} entries need {4 + 8 * count} bytes, the file has {len(index)}")
    rows = []
    running = 0
    for slot in range(count):
        offset, size = struct.unpack_from("<II", index, 4 + 8 * slot)
        if offset != running:
            raise RecordError(f"models.idx entry {slot}: offset {offset}, the entries so far tile to {running}")
        running += size
        rows.append((offset, size))
    if running != len(pack):
        raise RecordError(f"models.idx tiles to {running}, models.huge is {len(pack)} bytes")
    lines = []
    if names is not None:
        for line in names.decode("latin-1").splitlines():
            fields = line.split()
            if fields:
                lines.append(fields)
        if len(lines) != count or any(int(fields[0]) != k for k, fields in enumerate(lines)):
            raise RecordError("models.txt is not a parallel array of the index")
    return {
        "index": index, "pack": pack, "names": names, "count": count, "rows": rows,
        "lines": lines,
        "files": {
            "models.idx": {"size": len(index), "sha256": sha256(index)},
            "models.huge": {"size": len(pack), "sha256": sha256(pack)},
            "models.txt": {"size": len(names), "sha256": sha256(names)} if names is not None else None,
        },
    }


def slot_bytes(pack, slot):
    offset, size = pack["rows"][slot]
    return pack["pack"][offset:offset + size]


def slot_name(pack, slot):
    return pack["lines"][slot][4] if pack["lines"] else f"slot{slot}"


def probe_pack_file(path):
    """The archive-chain probe for ``models.huge``: a ``.huge`` name with a sibling ``.idx`` whose entries tile the
    file exactly. Returns (claimed, reason)."""
    if not path.lower().endswith(".huge"):
        return False, "not a .huge name"
    index_path = path[:-5] + ".idx"
    if not os.path.isfile(index_path):
        return False, "no sibling .idx"
    index = read(index_path)
    if len(index) < 4:
        return False, "index too short"
    count = struct.unpack_from("<I", index, 0)[0]
    if 4 + 8 * count != len(index):
        return False, "index length"
    running = 0
    for slot in range(count):
        offset, size = struct.unpack_from("<II", index, 4 + 8 * slot)
        if offset != running:
            return False, f"entry {slot} does not tile"
        running += size
    if running != os.path.getsize(path):
        return False, "entries do not end at the pack's end"
    return True, f"{count} slots tile the pack"


# ----------------------------------------------------------------------------------------------------------------
# Zone


def zone_stems(root):
    return sorted(name[:-4] for name in os.listdir(root) if name.lower().endswith(".zmp"))


def parse_zmp(payload):
    if len(payload) < ZONE_HEADER:
        raise RecordError(f"{len(payload)} bytes, shorter than the 132-byte header")
    width, height = struct.unpack_from("<2H", payload, 128)
    if ZONE_HEADER + ZONE_CELL * width * height != len(payload):
        raise RecordError(f"a {width}x{height} grid needs {ZONE_HEADER + ZONE_CELL * width * height} bytes, "
                          f"the payload has {len(payload)}")
    cells = [struct.unpack_from("<BBHH", payload, ZONE_HEADER + ZONE_CELL * k) for k in range(width * height)]
    return {
        "name": latin1_field(payload[0:32]), "author": latin1_field(payload[32:64]),
        "description": latin1_field(payload[64:128]), "width": width, "height": height, "cells": cells,
    }


def parse_zcp(payload):
    count = struct.unpack_from("<I", payload, 0)[0]
    if 4 + 36 * count != len(payload):
        raise RecordError(f"{count} prototypes need {4 + 36 * count} bytes, the payload has {len(payload)}")
    records = []
    for k in range(count):
        base = 4 + 36 * k
        shade, pad, ref_floor, ref_ceiling = struct.unpack_from("<bBhh", payload, base)
        floor = struct.unpack_from("<4h", payload, base + 6)
        ceiling = struct.unpack_from("<4h", payload, base + 14)
        slots = payload[base + 22:base + 30]
        edges = payload[base + 30:base + 34]
        extra = struct.unpack_from("<H", payload, base + 34)[0]
        records.append({"floor": floor, "ceiling": ceiling, "slots": tuple(slots), "edges": tuple(edges),
                        "shade": shade, "pad": pad, "ref": (ref_floor, ref_ceiling), "extra": extra})
    return records


def parse_ent(data, record_length=72):
    count = struct.unpack_from("<I", data, 0)[0]
    if 4 + record_length * count != len(data):
        raise RecordError(f"{count} records of {record_length} need {4 + record_length * count} bytes, "
                          f"the file has {len(data)}")
    rows = []
    for k in range(count):
        base = 4 + record_length * k
        x, y, z, a0, a1, a2 = struct.unpack_from("<6i", data, base)
        scale, fill, entity = struct.unpack_from("<HHI", data, base + 24)
        row = {"x": x, "y": y, "z": z, "angles": (a0, a1, a2), "scale": scale, "fill": fill, "entity": entity}
        if record_length >= 72:
            name_field = data[base + 32:base + 40]
            row["name"] = latin1_field(name_field)
            row["name_unterminated"] = b"\0" not in name_field
            row["script"] = latin1_field(data[base + 40:base + 72])
        rows.append(row)
    return rows


def parse_text_table(data):
    rows = []
    for line in data.decode("latin-1").splitlines():
        fields = line.split()
        if fields:
            rows.append(fields)
    return rows


def parse_zon(data):
    count = struct.unpack_from("<H", data, 0)[0]
    if 2 + 72 * count != len(data):
        raise RecordError(".zon tiling")
    return count


def parse_pth(data):
    count = struct.unpack_from("<H", data, 0)[0]
    position = 2
    points = 0
    for _ in range(count):
        n = struct.unpack_from("<H", data, position + 64)[0]
        position += 68 + 8 * n
        points += n
    if position != len(data):
        raise RecordError(".pth walk")
    return count, points


def parse_stn(data):
    count = struct.unpack_from("<H", data, 0)[0]
    position = 2
    for _ in range(2 * count):
        n = struct.unpack_from("<H", data, position)[0]
        position += 2 + n
    if position != len(data):
        raise RecordError(".stn walk")
    return count


def parse_sur(data):
    count = data[0]
    if 1 + 8 * count != len(data):
        raise RecordError(".sur tiling")
    return [tuple(data[1 + 8 * k:9 + 8 * k]) for k in range(count)]


def sky_variant(payload):
    """The texture-header variant of an inflated ``.zsk``: counted when the three words after the faces read
    (1, 256, 256), uncounted when two words read (256, 256) and the texels then end 8 bytes before the payload."""
    tag, frames, vertices, uv_count, face_count, coordinates, trailer = struct.unpack_from("<7H", payload, 0)
    after = MESH_HEADER + 6 * frames * vertices + 4 * uv_count + 12 * face_count
    if after + 6 + SKY_IMAGE_BYTES + 8 == len(payload):
        return "counted"
    if after + 4 + SKY_IMAGE_BYTES + 8 == len(payload):
        return "uncounted"
    return None


def terrain_census(zmp, zcp):
    """Face counts of the tile geometry under the zone builder's rules (floor and ceiling per open cell; a full wall
    toward a blocked or off-grid neighbor; a riser where an open neighbor's floor is higher at either shared
    corner; a downstand where its ceiling is lower). Corner slot k sits at (x, y) + OFFSETS[k]."""
    directions = ((1, 0, 1, 2, 0, 3), (-1, 0, 3, 0, 2, 1), (0, 1, 0, 1, 3, 2), (0, -1, 2, 3, 1, 0))
    width, height, cells = zmp["width"], zmp["height"], zmp["cells"]
    counts = Counter()
    for y in range(height):
        for x in range(width):
            flags, _, _, proto = cells[y * width + x]
            if flags & BLOCKED_FLAG:
                continue
            record = zcp[proto]
            counts["floor"] += 1
            counts["ceiling"] += 1
            for dx, dy, our_a, our_b, their_a, their_b in directions:
                nx, ny = x + dx, y + dy
                if not (0 <= nx < width and 0 <= ny < height) or cells[ny * width + nx][0] & BLOCKED_FLAG:
                    counts["wall"] += 1
                    continue
                other = zcp[cells[ny * width + nx][3]]
                if other["floor"][their_a] > record["floor"][our_a] or other["floor"][their_b] > record["floor"][our_b]:
                    counts["riser"] += 1
                if other["ceiling"][their_a] < record["ceiling"][our_a] or other["ceiling"][their_b] < record["ceiling"][our_b]:
                    counts["downstand"] += 1
    counts["quads"] = sum(counts[k] for k in ("floor", "ceiling", "wall", "riser", "downstand"))
    return dict(counts)


def load_zone(root, stem, pack=None, pack_facts=None, entities=None):
    """Parses one zone's file set and returns (facts, parsed) where parsed holds the decoded structures."""
    facts = {"stem": stem, "files": {}, "errors": []}
    parsed = {}
    for family in COMPRESSED_FAMILIES + PLAIN_FAMILIES + (".sta",):
        path = os.path.join(root, stem + family)
        if not os.path.isfile(path):
            continue
        data = read(path)
        entry = {"size": len(data), "sha256": sha256(data)}
        if family in COMPRESSED_FAMILIES:
            payload, declared, error = inflate_envelope(data)
            entry["inflated"] = len(payload) if payload is not None else None
            entry["declared"] = declared
            if error:
                facts["errors"].append(f"{stem}{family}: {error}")
            parsed[family] = payload
        else:
            parsed[family] = data
        facts["files"][stem + family] = entry
    models_name = stem + "_models.txt"
    models_path = os.path.join(root, models_name)
    if os.path.isfile(models_path):
        data = read(models_path)
        facts["files"][models_name] = {"size": len(data), "sha256": sha256(data)}
        parsed["models"] = parse_text_table(data)

    zmp = parse_zmp(parsed[".zmp"])
    zcp = parse_zcp(parsed[".zcp"])
    parsed["zmp"], parsed["zcp"] = zmp, zcp
    cells = zmp["cells"]
    referenced = Counter(cell[3] for cell in cells)
    facts["grid"] = {
        "name": zmp["name"], "author": zmp["author"], "description": zmp["description"],
        "width": zmp["width"], "height": zmp["height"],
        "blocked": sum(1 for cell in cells if cell[0] & BLOCKED_FLAG),
        "open": sum(1 for cell in cells if not cell[0] & BLOCKED_FLAG),
        "name_matches_stem": zmp["name"].lower() == stem.lower(),
        "raw_low_bits_clear": all((cell[2] & 0x3F) == 0 for cell in cells),
    }
    facts["prototypes"] = {
        "count": len(zcp),
        "max_index": max(referenced),
        "all_referenced": len(referenced) == len(zcp),
        "slot_values_outside_surfaces": None,
    }
    surfaces = parse_sur(parsed[".sur"])
    parsed["surfaces"] = surfaces
    facts["prototypes"]["slot_values_outside_surfaces"] = sum(
        1 for record in zcp for value in record["slots"] if value != 0xFF and value >= len(surfaces))
    texture_count = parsed[".ztx"][0]
    facts["prototypes"]["slot_values_at_or_above_texture_count"] = sum(
        1 for record in zcp for value in record["slots"] if value != 0xFF and value >= texture_count)
    facts["textures"] = {"count": texture_count,
                         "tiles_exactly": 1 + texture_count * 128 * 128 == len(parsed[".ztx"]),
                         "surface_rows": len(surfaces),
                         "max_surface_texture_index": max(row[7] for row in surfaces) if surfaces else None}
    palette = parsed[".pal"]
    magenta = [k for k in range(256) if palette[3 * k:3 * k + 3] == b"\xff\x00\xff"]
    facts["palette"] = {"size": len(palette), "max_component": max(palette), "magenta_indices": magenta,
                        "sha256": sha256(palette)}
    fog = parsed[".zfg"]
    fog_entry = struct.unpack_from("<H", fog, 2 * (15 * 4096 + 0xFFF))[0] if len(fog) == 131072 else None
    facts["light_fog"] = {"zlu_bytes": len(parsed[".zlu"]), "zfg_bytes": len(fog),
                          "fog_rgb444": [(fog_entry >> 8) & 15, (fog_entry >> 4) & 15, fog_entry & 15]
                          if fog_entry is not None else None,
                          "zlu_sha256": sha256(parsed[".zlu"]), "zfg_sha256": sha256(fog)}

    sky = parsed[".zsk"]
    variant = sky_variant(sky)
    sky_facts = {"inflated": len(sky), "variant": variant}
    if variant:
        record = parse_mesh(sky, variant)
        block = record["skin_blocks"][0]
        texels = struct.unpack(f"<{len(block) // 2}H", block)
        corner_pairs = set(zip(record["uvs"][0::2], record["uvs"][1::2]))
        sky_facts.update({
            "vertices": record["vertices"], "corners": record["uv_count"], "faces": record["face_count"],
            "texture": [record["width"], record["height"]], "sequences": record["sequences"],
            "painted": any(texels), "top_nibble_set": sum(1 for t in texels if t >> 12),
            "distinct_texels": len(set(texels)), "distinct_corner_pairs": len(corner_pairs),
            "corner_low_bytes_nonzero": sum(1 for value in record["uvs"] if value & 0xFF),
            "image_sha256": sha256(block),
            "mesh_probe_on_payload": list(probe_mesh(sky[:PROBE_BYTES], len(sky), len(sky) <= PROBE_BYTES)[:2]),
        })
        parsed["sky"] = record
    facts["sky"] = sky_facts

    placements = parse_ent(parsed[".ent"])
    parsed["placements"] = placements
    unresolved = 0
    not_resident = 0
    outside = 0
    on_blocked = 0
    animated = 0
    animated_slots = Counter()
    slots = Counter()
    fills = Counter(row["fill"] for row in placements)
    unterminated = sum(1 for row in placements if row["name_unterminated"])
    angle_nonzero = [sum(1 for row in placements if row["angles"][k]) for k in range(3)]
    for row in placements:
        definition = entities.get(row["entity"]) if entities else None
        if definition is None:
            unresolved += 1
            continue
        slot = int(definition[1])
        zone_row = parsed.get("models", [])[slot] if slot < len(parsed.get("models", [])) else None
        if zone_row is None or zone_row[4].lower() == "null.bin":
            not_resident += 1
            continue
        slots[slot] += 1
        cx, cy = row["x"] // 256, row["y"] // 256
        if not (0 <= cx < zmp["width"] and 0 <= cy < zmp["height"]):
            outside += 1
        elif cells[cy * zmp["width"] + cx][0] & BLOCKED_FLAG:
            on_blocked += 1
        if pack_facts and pack_facts[slot]["frames"] > 1:
            animated += 1
            animated_slots[slot] += 1
    facts["placements"] = {
        "count": len(placements), "unresolved": unresolved, "not_resident": not_resident,
        "outside_grid": outside, "on_blocked_cell": on_blocked, "animated": animated,
        "distinct_slots": len(slots), "distinct_animated_slots": len(animated_slots),
        "fill_values": sorted(fills), "unterminated_names": unterminated, "angle_nonzero": angle_nonzero,
        "scale_not_unity": sum(1 for row in placements if row["scale"] != 256),
    }
    parsed["placed_slots"] = slots
    if ".sta" in parsed:
        facts["sta_records"] = len(parse_ent(parsed[".sta"], 32))
    facts["triggers"] = parse_zon(parsed[".zon"])
    facts["paths"] = parse_pth(parsed[".pth"])
    facts["locks"] = parse_stn(parsed[".stn"])
    facts["terrain"] = terrain_census(zmp, zcp)
    if parsed.get("models"):
        facts["resident_models"] = sum(1 for row in parsed["models"] if row[4].lower() != "null.bin")
        facts["model_rows"] = len(parsed["models"])
    return facts, parsed


def probe_zone(prefix, length, complete, file_name):
    """The ``bmt.shadowkey.zone`` probe on a ``.zmp`` candidate: the envelope, the inflated header's
    NUL-terminated name, the grid arithmetic against the declared inflated length and, for a complete file, the
    exact stream. ``file_name`` only enriches the evidence (the name field equals the stem on retail)."""
    if len(prefix) < 6 or prefix[4] != 0x78 or ((prefix[4] << 8) | prefix[5]) % 31:
        return "NotAModel", "None", "no zlib envelope"
    declared = struct.unpack_from("<I", prefix, 0)[0]
    stream = zlib.decompressobj()
    try:
        head = stream.decompress(prefix[4:], ZONE_HEADER)
    except zlib.error:
        return "NotAModel", "None", "zlib stream"
    if len(head) < ZONE_HEADER:
        return "NotAModel", "None", "inflated header too short"
    name = head[0:32]
    end = name.find(b"\0")
    if end <= 0 or any(b < 0x20 or b > 0x7E for b in name[:end]):
        return "NotAModel", "None", "name field"
    width, height = struct.unpack_from("<2H", head, 128)
    if width == 0 or height == 0 or width > 4096 or height > 4096:
        return "NotAModel", "None", "grid size"
    if ZONE_HEADER + ZONE_CELL * width * height != declared:
        return "NotAModel", "None", "grid arithmetic against the declared length"
    text = name[:end].decode("latin-1")
    evidence = f"Shadowkey zone grid '{text}' {width}x{height}"
    if file_name and os.path.splitext(os.path.basename(file_name))[0].lower() == text.lower():
        evidence += " (name equals the file stem)"
    if not complete:
        return "Supported", "Tentative", evidence + " (prefix is not the whole file)"
    payload, _, error = inflate_envelope(prefix)
    if error:
        return "NotAModel", "None", error
    return "Supported", "Confirmed", evidence


# ----------------------------------------------------------------------------------------------------------------
# Census


def load_entities(root):
    rows = parse_text_table(read(os.path.join(root, "entities.txt")))
    return {int(fields[0]): fields for fields in rows}


def census(root, out):
    pack = load_pack(root)
    slots = []
    pack_facts = {}
    payload_owners = defaultdict(list)
    for slot in range(pack["count"]):
        data = slot_bytes(pack, slot)
        row = {"slot": slot, "name": slot_name(pack, slot), "offset": pack["rows"][slot][0], "size": len(data),
               "sha256": sha256(data), "line": pack["lines"][slot][:4] if pack["lines"] else None}
        kind, confidence, text = probe_mesh(data[:PROBE_BYTES], len(data), len(data) <= PROBE_BYTES)
        row["probe"] = {"kind": kind, "confidence": confidence, "text": text}
        if data:
            record = parse_mesh(data)
            payload_owners[row["sha256"]].append(slot)
            row.update({key: record[key] for key in ("frames", "vertices", "uv_count", "face_count", "skins",
                                                     "width", "height")})
            row["sequences"] = record["sequences"]
            row["facts"] = mesh_facts(record, data)
            pack_facts[slot] = row
        slots.append(row)
    names = Counter(row["name"].lower() for row in slots if row["size"])
    for row in slots:
        if row["size"]:
            row["duplicate_payload_slots"] = [s for s in payload_owners[row["sha256"]] if s != row["slot"]]
            row["duplicate_name"] = names[row["name"].lower()] > 1

    entities = load_entities(root)
    zones = []
    for stem in zone_stems(root):
        facts, parsed = load_zone(root, stem, pack, pack_facts, entities)
        zones.append(facts)

    # Every file of the tree under every probe rule (the controls of the census).
    files = []
    for directory, _, names_here in os.walk(root):
        for name in sorted(names_here):
            path = os.path.join(directory, name)
            data = read(path)
            relative = os.path.relpath(path, root).replace("\\", "/")
            prefix = data[:PROBE_BYTES]
            complete = len(data) <= PROBE_BYTES
            mesh = probe_mesh(prefix, len(data), complete)
            zone = probe_zone(prefix, len(data), complete, name)
            claimed, why = probe_pack_file(path)
            files.append({"path": relative, "size": len(data), "sha256": sha256(data),
                          "extension": os.path.splitext(name)[1].lower(),
                          "mesh_probe": mesh[:2], "mesh_reason": mesh[2] if mesh[0] == "NotAModel" else None,
                          "zone_probe": zone[:2], "zone_reason": zone[2] if zone[0] == "NotAModel" else None,
                          "pack_probe": claimed})

    summary = summarize(pack, slots, zones, files)
    summary["root"] = root
    write_json(os.path.join(out, "pack-slots.json"), {"files": pack["files"], "slots": slots})
    write_json(os.path.join(out, "zones.json"), zones)
    write_json(os.path.join(out, "files.json"), files)
    write_json(os.path.join(out, "census-summary.json"), summary)
    print(json.dumps(summary, indent=1))


def summarize(pack, slots, zones, files):
    filled = [row for row in slots if row["size"]]
    animated = [row for row in filled if row["frames"] > 1]
    sequences = [seq for row in filled for seq in row["sequences"]]
    animated_sequences = [seq for row in animated for seq in row["sequences"]]
    probes = Counter((row["probe"]["kind"], row["probe"]["confidence"]) for row in slots)
    mesh_file_claims = Counter(tuple(f["mesh_probe"]) for f in files)
    zone_file_claims = Counter((f["extension"], tuple(f["zone_probe"])) for f in files if f["zone_probe"][0] != "NotAModel")
    zone_rejections = Counter(f["zone_reason"] for f in files if f["extension"] in COMPRESSED_FAMILIES and f["zone_probe"][0] == "NotAModel")
    mesh_rejections = Counter(f["mesh_reason"] for f in files if f["mesh_probe"][0] == "NotAModel")
    closed = [row for row in filled if row["facts"]["closed_by_index"] or row["facts"]["closed_welded"]]
    return {
        "pack": {
            "slots": pack["count"], "empty": sum(1 for row in slots if not row["size"]),
            "empty_slots": [row["slot"] for row in slots if not row["size"]],
            "records": len(filled), "static": sum(1 for row in filled if row["frames"] == 1),
            "animated": len(animated), "animated_slots": [row["slot"] for row in animated],
            "multi_skin": sum(1 for row in filled if row["skins"] > 1), "skins_total": sum(row["skins"] for row in filled),
            "max_skins": max(row["skins"] for row in filled),
            "frames_max": max(row["frames"] for row in filled), "frames_total": sum(row["frames"] for row in filled),
            "texture_sizes": dict(Counter(f"{row['width']}x{row['height']}" for row in filled)),
            "size_min": min(row["size"] for row in filled), "size_max": max(row["size"] for row in filled),
            "over_probe_budget": sum(1 for row in filled if row["size"] > PROBE_BYTES),
            "sequences_total": len(sequences), "sequences_in_animated": len(animated_sequences),
            "rates_all": dict(sorted(Counter(r for _, _, r in sequences).items())),
            "rates_animated_multi_frame": dict(sorted(Counter(r for s, e, r in animated_sequences if e - s > 1).items())),
            "multi_frame_sequences": sum(1 for s, e, _ in animated_sequences if e - s > 1),
            "single_frame_sequences_in_animated": sum(1 for s, e, _ in animated_sequences if e - s == 1),
            "rate_equals_length": sum(row["facts"]["rate_equals_length"] for row in animated),
            "rate1_multi_frame": sum(row["facts"]["rate1_multi_frame"] for row in animated),
            "static_sequence_shapes": dict(Counter(str(row["sequences"]) for row in filled if row["frames"] == 1)),
            "frames_in_no_sequence": sum(row["facts"]["frames_in_no_sequence"] for row in filled),
            "overlapping_sequences": sum(row["facts"]["overlapping_sequences"] for row in filled),
            "humanoid_table_records": [row["name"] for row in animated if row["facts"]["humanoid_table"]],
            "records_with_unused_vertices": sum(1 for row in filled if row["facts"]["unused_vertices"]),
            "records_with_unused_uvs": sum(1 for row in filled if row["facts"]["unused_uvs"]),
            "records_with_repeated_index_triangles": sum(1 for row in filled if row["facts"]["repeated_index_triangles"]),
            "records_with_repeated_position_triangles": sum(1 for row in filled if row["facts"]["repeated_position_triangles"]),
            "records_with_duplicate_triangles": sum(1 for row in filled if row["facts"]["duplicate_triangles"]),
            "records_with_uv_beyond_texture": sum(1 for row in filled if row["facts"]["uvs_beyond_texture"]),
            "uv_values_with_low_fraction_bits": sum(row["facts"]["uv_values_with_low_fraction_bits"] for row in filled),
            "max_uv_texel_ratio": max(max(row["facts"]["uv_texel_max"][0] / row["width"],
                                          row["facts"]["uv_texel_max"][1] / row["height"]) for row in filled),
            "records_with_magenta": sum(1 for row in filled if any(s["magenta_texels"] for s in row["facts"]["skins"])),
            "max_distinct_colors": max(s["distinct"] for row in filled for s in row["facts"]["skins"]),
            "texels_with_top_nibble": sum(s["top_nibble_set"] for row in filled for s in row["facts"]["skins"]),
            "closed_records": len(closed),
            "closed_positive_volume": sum(1 for row in closed if row["facts"]["signed_volume_x6"] > 0),
            "closed_negative_volume": [row["name"] for row in closed if row["facts"]["signed_volume_x6"] < 0],
            "duplicate_payload_groups": sum(1 for row in filled if row["duplicate_payload_slots"]) ,
            "duplicate_names": sorted({row["name"] for row in filled if row["duplicate_name"]}),
            "max_frame_delta": max(row["facts"]["max_frame_delta"] for row in filled),
            "max_abs_coordinate": max(max(abs(v) for pair in row["facts"]["bounds_all_frames"] for v in pair) for row in filled),
            "probe": {f"{k[0]}/{k[1]}": v for k, v in sorted(probes.items())},
        },
        "zones": {
            "count": len(zones),
            "errors": [e for z in zones for e in z["errors"]],
            "grid_sizes": dict(Counter(f"{z['grid']['width']}x{z['grid']['height']}" for z in zones)),
            "names_match_stem": sum(1 for z in zones if z["grid"]["name_matches_stem"]),
            "prototypes_min_max": [min(z["prototypes"]["count"] for z in zones), max(z["prototypes"]["count"] for z in zones)],
            "prototypes_all_referenced": sum(1 for z in zones if z["prototypes"]["all_referenced"]),
            "slot_values_outside_surfaces": sum(z["prototypes"]["slot_values_outside_surfaces"] for z in zones),
            "textures_total": sum(z["textures"]["count"] for z in zones),
            "textures_tile": sum(1 for z in zones if z["textures"]["tiles_exactly"]),
            "surface_texture_index_is_last": sum(1 for z in zones if z["textures"]["max_surface_texture_index"] == z["textures"]["count"] - 1),
            "palettes_with_magenta": sum(1 for z in zones if z["palette"]["magenta_indices"]),
            "distinct_palettes": len({z["palette"]["sha256"] for z in zones}),
            "distinct_light_tables": len({z["light_fog"]["zlu_sha256"] for z in zones}),
            "distinct_fog_tables": len({z["light_fog"]["zfg_sha256"] for z in zones}),
            "fog_colors": dict(Counter(str(z["light_fog"]["fog_rgb444"]) for z in zones)),
            "sky_variants": dict(Counter(z["sky"]["variant"] for z in zones)),
            "sky_painted": sum(1 for z in zones if z["sky"].get("painted")),
            "sky_top_nibble_set": sum(z["sky"].get("top_nibble_set", 0) for z in zones),
            "sky_corner_pairs": dict(Counter(z["sky"].get("distinct_corner_pairs") for z in zones)),
            "sky_rates": dict(Counter(str(z["sky"].get("sequences")) for z in zones)),
            "distinct_sky_images": len({z["sky"].get("image_sha256") for z in zones}),
            "placements": sum(z["placements"]["count"] for z in zones),
            "placements_max": max(z["placements"]["count"] for z in zones),
            "placements_min": min(z["placements"]["count"] for z in zones),
            "unresolved": sum(z["placements"]["unresolved"] for z in zones),
            "not_resident": sum(z["placements"]["not_resident"] for z in zones),
            "outside_grid": sum(z["placements"]["outside_grid"] for z in zones),
            "on_blocked": sum(z["placements"]["on_blocked_cell"] for z in zones),
            "animated_placements": sum(z["placements"]["animated"] for z in zones),
            "animated_placements_max": max(z["placements"]["animated"] for z in zones),
            "angle_nonzero": [sum(z["placements"]["angle_nonzero"][k] for z in zones) for k in range(3)],
            "scale_not_unity": sum(z["placements"]["scale_not_unity"] for z in zones),
            "unterminated_names": sum(z["placements"]["unterminated_names"] for z in zones),
            "fill_values": sorted({v for z in zones for v in z["placements"]["fill_values"]}),
            "triggers": sum(z["triggers"] for z in zones),
            "paths": sum(z["paths"][0] for z in zones), "path_points": sum(z["paths"][1] for z in zones),
            "locks": sum(z["locks"] for z in zones),
            "sta": {z["stem"]: z["sta_records"] for z in zones if "sta_records" in z},
            "terrain_quads_max": max(z["terrain"]["quads"] for z in zones),
            "terrain_quads_total": sum(z["terrain"]["quads"] for z in zones),
        },
        "files": {
            "count": len(files),
            "extensions": dict(sorted(Counter(f["extension"] for f in files).items())),
            "mesh_probe_on_files": {f"{k[0]}/{k[1]}": v for k, v in mesh_file_claims.items()},
            "mesh_probe_rejections": dict(mesh_rejections.most_common()),
            "zone_probe_claims": {f"{k[0]} {k[1][0]}/{k[1][1]}": v for k, v in zone_file_claims.items()},
            "zone_probe_rejections_on_compressed_families": dict(zone_rejections.most_common()),
            "pack_probe_claims": [f["path"] for f in files if f["pack_probe"]],
        },
    }


# ----------------------------------------------------------------------------------------------------------------
# Measurements


def measure(root, out):
    pack = load_pack(root)
    entities = load_entities(root)
    records = {}
    for slot in range(pack["count"]):
        data = slot_bytes(pack, slot)
        if data:
            records[slot] = parse_mesh(data)
    results = {}

    # 1. Sky texel encoding: 256x256 u16 0x0RGB (the mesh skin encoding) against 512x256 u8 through the zone .pal.
    sky_rows = []
    ztx_controls = []
    for stem in zone_stems(root):
        payload, _, _ = inflate_envelope(read(os.path.join(root, stem + ".zsk")))
        image = payload[len(payload) - 8 - SKY_IMAGE_BYTES:len(payload) - 8]
        palette = read(os.path.join(root, stem + ".pal"))
        texels = struct.unpack(f"<{SKY_IMAGE_BYTES // 2}H", image)
        row = {"zone": stem, "painted": any(image), "top_nibble_set": sum(1 for t in texels if t >> 12),
               "odd_byte_max": max(image[1::2]), "even_byte_max": max(image[0::2])}
        if row["painted"]:
            rgb444 = [(((t >> 8) & 15) * 17, ((t >> 4) & 15) * 17, (t & 15) * 17) for t in texels]
            rgb_pal = [(palette[3 * i], palette[3 * i + 1], palette[3 * i + 2]) for i in image]
            row["neighbor_difference_444"] = neighbor_difference(rgb444, 256, 256)
            row["neighbor_difference_palette"] = neighbor_difference(rgb_pal, 512, 256)
        sky_rows.append(row)
        ztx, _, _ = inflate_envelope(read(os.path.join(root, stem + ".ztx")))
        body = ztx[1:1 + 2 * ((len(ztx) - 1) // 2)]
        words = struct.unpack(f"<{len(body) // 2}H", body)
        ztx_controls.append({"zone": stem, "u16_top_nibble_clear_fraction": sum(1 for w in words if not w >> 12) / len(words)})
    results["sky_encoding"] = {"skies": sky_rows, "control_ztx_read_as_u16": ztx_controls}

    # 2. Mesh-to-zone orientation: 2 basis maps x 2 yaw signs x 2 angle units, plus no yaw; the score is the number
    #    of frame-0 vertices landing in blocked or off-grid cells.
    hypotheses = []
    for basis in ("swap", "rot"):
        for sign in (1, -1):
            for unit in ("binary", "degrees256"):
                hypotheses.append((basis, sign, unit))
    hypotheses.append(("swap", 0, "binary"))
    hypotheses.append(("rot", 0, "binary"))
    totals = Counter()
    zero_blocked = Counter()
    best = Counter()
    discriminating = 0
    placements = 0
    quarter_turn = 0
    for stem in zone_stems(root):
        zmp = parse_zmp(inflate_envelope(read(os.path.join(root, stem + ".zmp")))[0])
        width, height, cells = zmp["width"], zmp["height"], zmp["cells"]
        for row in parse_ent(read(os.path.join(root, stem + ".ent"))):
            slot = int(entities[row["entity"]][1])
            record = records[slot]
            positions = record["positions"]
            vertices = record["vertices"]
            scale = row["scale"] / 256.0
            angle = row["angles"][2]
            placements += 1
            if angle % 16384 == 0:
                quarter_turn += 1
            scores = {}
            for basis, sign, unit in hypotheses:
                theta = sign * angle * (2 * math.pi / BINARY_TURN if unit == "binary" else math.pi / (180 * 256))
                c, s = math.cos(theta), math.sin(theta)
                blocked = 0
                for k in range(vertices):
                    vx, vz = positions[3 * k], positions[3 * k + 2]
                    px, py = vx * scale, (vz if basis == "swap" else -vz) * scale
                    qx, qy = c * px - s * py + row["x"], s * px + c * py + row["y"]
                    ix, iy = math.floor(qx / 256), math.floor(qy / 256)
                    if not (0 <= ix < width and 0 <= iy < height) or cells[iy * width + ix][0] & BLOCKED_FLAG:
                        blocked += 1
                key = f"{basis}{'+' if sign > 0 else '-' if sign < 0 else '0'}/{unit}"
                scores[key] = blocked
                totals[key] += blocked
                if blocked == 0:
                    zero_blocked[key] += 1
            candidates = {k: v for k, v in scores.items() if k.endswith("/binary") and k[len(k.split("/")[0]) - 1] in "+-"}
            if len(set(candidates.values())) > 1:
                discriminating += 1
                low = min(candidates.values())
                for k, v in candidates.items():
                    if v == low:
                        best[k] += 1
    results["orientation"] = {
        "placements": placements, "quarter_turn_angles": quarter_turn,
        "blocked_vertices": dict(sorted(totals.items(), key=lambda t: t[1])),
        "placements_with_zero_blocked_vertices": dict(sorted(zero_blocked.items(), key=lambda t: -t[1])),
        "discriminating_placements": discriminating,
        "co_best_on_discriminating": dict(sorted(best.items(), key=lambda t: -t[1])),
        "maps": {"swap": "(x, y, z) -> (x, z, y), determinant -1 (the legacy viewer's ZUp)",
                 "rot": "(x, y, z) -> (x, -z, y), determinant +1"},
    }

    # 3. Floor contact: the mesh base (lowest point along the candidate up axis, times the unit factor and the
    #    placement scale, plus the placement height) against the cell's floor band, within 1/8 tile.
    contact = {}
    zone_cache = {}
    for stem in zone_stems(root):
        zmp = parse_zmp(inflate_envelope(read(os.path.join(root, stem + ".zmp")))[0])
        zcp = parse_zcp(inflate_envelope(read(os.path.join(root, stem + ".zcp")))[0])
        zone_cache[stem] = (zmp, zcp, parse_ent(read(os.path.join(root, stem + ".ent"))))
    inside = 0
    for factor in (0.5, 1.0, 2.0):
        for up in (1, -1):
            within = 0
            inside = 0
            for stem, (zmp, zcp, rows) in zone_cache.items():
                for row in rows:
                    cx, cy = row["x"] // 256, row["y"] // 256
                    if not (0 <= cx < zmp["width"] and 0 <= cy < zmp["height"]):
                        continue
                    inside += 1
                    floor = zcp[zmp["cells"][cy * zmp["width"] + cx][3]]["floor"]
                    record = records[int(entities[row["entity"]][1])]
                    ys = record["positions"][1:3 * record["vertices"]:3]
                    extreme = min(ys) if up > 0 else -max(ys)
                    base = row["z"] + factor * row["scale"] / 256.0 * extreme
                    gap = base - min(floor) if base < min(floor) else base - max(floor) if base > max(floor) else 0
                    if abs(gap) <= 32:
                        within += 1
            contact[f"x{factor} up{'+' if up > 0 else '-'}"] = within
    results["floor_contact"] = {"placements_inside_grid": inside, "within_one_eighth_tile": contact}

    # 4. Winding: closed oriented records and the sign of their signed volume under a right-handed reading.
    closed = []
    for slot, record in records.items():
        facts = mesh_facts(record, slot_bytes(pack, slot))
        if facts["closed_by_index"] or facts["closed_welded"]:
            closed.append({"slot": slot, "name": slot_name(pack, slot), "positive": facts["signed_volume_x6"] > 0,
                           "by_index": facts["closed_by_index"]})
    results["winding"] = {"closed": len(closed), "positive": sum(1 for c in closed if c["positive"]),
                          "negative": [c["name"] for c in closed if not c["positive"]]}

    # 5. Sequence rates: rate equal to the sequence length (one second under frames per second; length^2 ticks under
    #    ticks per frame) on the multi-frame sequences of animated records.
    rows = []
    for slot, record in records.items():
        if record["frames"] > 1:
            for start, end, rate in record["sequences"]:
                rows.append({"slot": slot, "name": slot_name(pack, slot), "start": start, "end": end, "rate": rate,
                             "length": end - start, "seconds_if_fps": (end - start) / rate})
    multi = [r for r in rows if r["length"] > 1]
    results["sequence_rates"] = {
        "animated_sequences": len(rows), "multi_frame": len(multi),
        "rate_equals_length": sum(1 for r in multi if r["rate"] == r["length"]),
        "rate_equals_length_records": sorted({r["name"] for r in multi if r["rate"] == r["length"]}),
        "seconds_if_fps": dict(sorted(Counter(round(r["seconds_if_fps"], 2) for r in multi).items())),
        "rate1_multi_frame": [f"{r['name']} [{r['start']}, {r['end']})" for r in multi if r["rate"] == 1],
        "rows": rows,
    }

    # 6. Weapons against bodies: do the weapon records share the body's frame space (lockstep keyframes)?
    lockstep = []
    for slot, record in records.items():
        if record["frames"] == 144 and record["vertices"] < 40:
            lockstep.append({"slot": slot, "name": slot_name(pack, slot), "sequences_equal_humanoid":
                             tuple(record["sequences"]) == HUMANOID_TABLE})
    results["weapon_records"] = lockstep
    write_json(os.path.join(out, "measurements.json"), results)
    brief = {k: v for k, v in results.items() if k != "sequence_rates"}
    brief["sequence_rates"] = {k: v for k, v in results["sequence_rates"].items() if k != "rows"}
    print(json.dumps(brief, indent=1))


def neighbor_difference(rgb, width, height):
    horizontal = vertical = 0
    for y in range(height):
        row = rgb[y * width:(y + 1) * width]
        for x in range(width - 1):
            a, b = row[x], row[x + 1]
            horizontal += abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2])
    for y in range(height - 1):
        for x in range(width):
            a, b = rgb[y * width + x], rgb[(y + 1) * width + x]
            vertical += abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2])
    return [round(horizontal / ((width - 1) * height * 3), 3), round(vertical / (width * (height - 1) * 3), 3)]


# ----------------------------------------------------------------------------------------------------------------
# Cover


def frames_class(frames):
    return "1" if frames == 1 else "2-15" if frames < 16 else "16-127" if frames < 128 else "128+"


def count_class(count):
    return "1" if count == 1 else "2-4" if count <= 4 else "5+"


def slot_cells(row):
    facts = row["facts"]
    dims = {"frames": frames_class(row["frames"]), "skins": count_class(row["skins"]),
            "texture": f"{row['width']}x{row['height']}", "sequences": count_class(len(row["sequences"]))}
    tags = {
        "magenta": any(s["magenta_texels"] for s in facts["skins"]),
        "uv-beyond-texture": facts["uvs_beyond_texture"] > 0,
        "repeated-position-triangle": facts["repeated_position_triangles"] > 0,
        "repeated-index-triangle": facts["repeated_index_triangles"] > 0,
        "duplicate-triangle": facts["duplicate_triangles"] > 0,
        "unused-vertices": facts["unused_vertices"] > 0,
        "unused-uvs": facts["unused_uvs"] > 0,
        "closed": facts["closed_by_index"] or facts["closed_welded"],
        "closed-negative-volume": (facts["closed_by_index"] or facts["closed_welded"]) and facts["signed_volume_x6"] < 0,
        "rate-equals-length": facts["rate_equals_length"] > 0,
        "rate1-multi-frame": facts["rate1_multi_frame"] > 0,
        "humanoid-table": facts["humanoid_table"],
        "probe-tentative": row["size"] > PROBE_BYTES,
        "duplicate-payload": bool(row["duplicate_payload_slots"]),
        "duplicate-name": row["duplicate_name"],
        "non-square-texture": row["width"] != row["height"],
        "static-rate-10": row["frames"] == 1 and row["sequences"][0][2] == 10,
        "frames-outside-sequences": facts["frames_in_no_sequence"] > 0,
    }
    return dims, {k for k, v in tags.items() if v}


def zone_cells(zone):
    sky = zone["sky"]
    dims = {"sky": sky["variant"], "painted": str(bool(sky.get("painted"))),
            "palette-key": "magenta" if zone["palette"]["magenta_indices"] else "none",
            "fog": "black" if zone["light_fog"]["fog_rgb444"] == [0, 0, 0] else "colored",
            "grid": f"{zone['grid']['width']}x{zone['grid']['height']}"}
    tags = {
        "sky-rate-10": bool(sky.get("sequences")) and sky["sequences"][0][2] == 10,
        "sky-uv-variant": sky["variant"] == "counted" and sky.get("distinct_corner_pairs") != OUTDOOR_SKY_CORNER_PAIRS,
        "sta": "sta_records" in zone,
        "paths": zone["paths"][0] > 0,
        "locks": zone["locks"] > 0,
        "outside-grid": zone["placements"]["outside_grid"] > 0,
        "on-blocked": zone["placements"]["on_blocked_cell"] > 0,
        "unterminated-name": zone["placements"]["unterminated_names"] > 0,
        "author-not-default": zone["grid"]["author"] != "No Auth",
        "description-not-default": zone["grid"]["description"] != "Nondescript",
        "pitch-roll-angles": zone["placements"]["angle_nonzero"][0] > 0 or zone["placements"]["angle_nonzero"][1] > 0,
        "slot-at-or-above-texture-count": zone["prototypes"]["slot_values_at_or_above_texture_count"] > 0,
    }
    return dims, {k for k, v in tags.items() if v}


def milp_cover(items, weights):
    """items: list of (key, set_of_requirements). Minimum-count, then minimum-weight exact cover of every
    requirement. Returns the chosen keys and the solver statuses."""
    import numpy as np
    from scipy.optimize import LinearConstraint, milp, Bounds
    requirements = sorted({r for _, reqs in items for r in reqs})
    index = {r: k for k, r in enumerate(requirements)}
    matrix = np.zeros((len(requirements), len(items)))
    for column, (_, reqs) in enumerate(items):
        for r in reqs:
            matrix[index[r], column] = 1
    bounds = Bounds(0, 1)
    integrality = np.ones(len(items))
    first = milp(c=np.ones(len(items)), constraints=[LinearConstraint(matrix, lb=1)], integrality=integrality,
                 bounds=bounds)
    count = int(round(first.fun))
    second = milp(c=np.array(weights, dtype=float),
                  constraints=[LinearConstraint(matrix, lb=1), LinearConstraint(np.ones((1, len(items))), lb=count, ub=count)],
                  integrality=integrality, bounds=bounds)
    chosen = [items[k][0] for k in range(len(items)) if second.x[k] > 0.5]
    return chosen, {"requirements": len(requirements), "stage1": first.message, "stage2": second.message,
                    "count": count}


def requirements_for(dims, tags):
    reqs = {f"{k}={v}" for k, v in dims.items()}
    names = sorted(dims)
    for a in range(len(names)):
        for b in range(a + 1, len(names)):
            reqs.add(f"{names[a]}={dims[names[a]]}&{names[b]}={dims[names[b]]}")
    reqs.update(f"tag:{t}" for t in tags)
    return reqs


def cover(root, census_dir, out):
    with open(os.path.join(census_dir, "pack-slots.json"), encoding="utf-8") as handle:
        pack_census = json.load(handle)
    with open(os.path.join(census_dir, "zones.json"), encoding="utf-8") as handle:
        zones = json.load(handle)
    slots = [row for row in pack_census["slots"] if row["size"]]
    # Group byte-identical payloads: a representative stands for its duplicates.
    items = []
    weights = []
    seen = set()
    for row in slots:
        if row["sha256"] in seen:
            continue
        seen.add(row["sha256"])
        dims, tags = slot_cells(row)
        items.append((row["slot"], requirements_for(dims, tags)))
        weights.append(row["size"])
    chosen_slots, slot_status = milp_cover(items, weights)
    zone_items = []
    zone_weights = []
    for zone in zones:
        dims, tags = zone_cells(zone)
        zone_items.append((zone["stem"], requirements_for(dims, tags)))
        zone_weights.append(sum(f["size"] for f in zone["files"].values()))
    chosen_zones, zone_status = milp_cover(zone_items, zone_weights)

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
    rows = []
    for slot in chosen_slots:
        rows.append(slot_row(by_slot[slot], "cover"))
    for label, slot in edges.items():
        if slot in chosen_slots:
            for row in rows:
                if row["slot"] == slot:
                    row.setdefault("edges", []).append(label)
            continue
        row = slot_row(by_slot[slot], "edge")
        row["edges"] = [label]
        if not any(r["slot"] == slot for r in rows):
            rows.append(row)
        else:
            for r in rows:
                if r["slot"] == slot:
                    r["edges"].append(label)
    empty = [slot_row(row, "decline-control") for row in pack_census["slots"] if not row["size"]]
    zone_rows = []
    zone_by_stem = {z["stem"]: z for z in zones}
    for stem in chosen_zones:
        zone = zone_by_stem[stem]
        zone_rows.append({"zone": stem, "role": "cover", "cells": zone_cells(zone)[0],
                          "tags": sorted(zone_cells(zone)[1]),
                          "files": {name: {"size": f["size"], "sha256": f["sha256"]} for name, f in zone["files"].items()},
                          "placements": zone["placements"]["count"], "terrain_quads": zone["terrain"]["quads"]})
    zone_edges = {
        "most-placements": max(zones, key=lambda z: z["placements"]["count"])["stem"],
        "fewest-placements": min(zones, key=lambda z: z["placements"]["count"])["stem"],
        "most-prototypes": max(zones, key=lambda z: z["prototypes"]["count"])["stem"],
        "fewest-prototypes": min(zones, key=lambda z: z["prototypes"]["count"])["stem"],
        "most-terrain": max(zones, key=lambda z: z["terrain"]["quads"])["stem"],
        "most-animated": max(zones, key=lambda z: z["placements"]["animated"])["stem"],
    }
    for label, stem in zone_edges.items():
        existing = [r for r in zone_rows if r["zone"] == stem]
        if existing:
            existing[0].setdefault("edges", []).append(label)
            continue
        zone = zone_by_stem[stem]
        zone_rows.append({"zone": stem, "role": "edge", "edges": [label], "cells": zone_cells(zone)[0],
                          "tags": sorted(zone_cells(zone)[1]),
                          "files": {name: {"size": f["size"], "sha256": f["sha256"]} for name, f in zone["files"].items()},
                          "placements": zone["placements"]["count"], "terrain_quads": zone["terrain"]["quads"]})
    with open(os.path.join(census_dir, "files.json"), encoding="utf-8") as handle:
        files = json.load(handle)
    controls = []
    wanted = {".s": 1, ".wav": 1, ".ogg": 1, ".txt": 1, ".app": 1, ".zcp": 1, ".zsk": 1, ".ztx": 1, ".zlu": 1,
              ".zfg": 1, ".ent": 1, ".spr": 1, ".idx": 1}
    for f in sorted(files, key=lambda f: (f["extension"], f["size"])):
        if wanted.get(f["extension"], 0) > 0:
            wanted[f["extension"]] -= 1
            controls.append({"path": f["path"], "size": f["size"], "sha256": f["sha256"], "expect": "NotAModel",
                             "mesh_reason": f["mesh_reason"], "zone_reason": f["zone_reason"]})
    pins = dict(pack_census["files"])
    entities_bytes = read(os.path.join(root, "entities.txt"))
    pins["entities.txt"] = {"size": len(entities_bytes), "sha256": sha256(entities_bytes)}
    manifest = {
        "schema": "bmt.cut2.shadowkey.cover/1",
        "root": "<Sample>/Builds/The Elder Scrolls Travels - Shadowkey (2004-10-27, N-Gage - Final)/"
                "The Elder Scrolls Travels - Shadowkey/system/apps/6r51",
        "pack": pins,
        "slots": rows,
        "empty_slot_controls": empty,
        "zones": zone_rows,
        "file_controls": controls,
        "synthetic_controls": [
            "every truncation of the three smallest cover records (never Supported, Confirmed)",
            "one trailing byte after the sequence table", "tag 6", "trailer 2", "coordinate count 3V + 1",
            "a face index equal to the vertex count", "a UV index equal to the UV count",
            "a sequence end past the frame count", "a zero sequence rate",
            "a .zmp whose declared length is one cell short", "a .zmp name field without a NUL",
            "an interior sky payload read with the counted header (must not walk)",
        ],
        "status": {"slots": slot_status, "zones": zone_status},
    }
    write_json(os.path.join(out, "cover-manifest.json"), manifest)
    report = [
        f"slot cover: {len(chosen_slots)} records over {slot_status['requirements']} requirements "
        f"({slot_status['stage1']}; {slot_status['stage2']})",
        f"  distinct payloads considered: {len(items)} of {len(slots)} records",
        f"  cover + edges: {len(rows)} rows, {sum(r['size'] for r in rows):,} bytes",
        f"zone cover: {len(chosen_zones)} zones over {zone_status['requirements']} requirements "
        f"({zone_status['stage1']}; {zone_status['stage2']})",
        f"  cover + edges: {len(zone_rows)} zones: {', '.join(r['zone'] for r in zone_rows)}",
        f"decline controls: {len(empty)} empty slots, {len(controls)} tree files, "
        f"{len(manifest['synthetic_controls'])} synthetic",
        "rows:",
    ]
    for row in rows:
        report.append(f"  slot {row['slot']:3} {row['name']:28} {row['size']:7} {row['sha256'][:12]} "
                      f"{row['role']} {','.join(row.get('edges', []))}")
    for row in zone_rows:
        report.append(f"  zone {row['zone']:14} {row['role']} {','.join(row.get('edges', []))} "
                      f"cells={row['cells']} tags={row['tags']}")
    with open(os.path.join(out, "cover-report.txt"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(report) + "\n")
    print("\n".join(report))


def slot_row(row, role):
    result = {"slot": row["slot"], "name": row["name"], "role": role, "offset": row["offset"], "size": row["size"],
              "sha256": row["sha256"], "probe": row["probe"]}
    if row["size"]:
        dims, tags = slot_cells(row)
        result.update({"cells": dims, "tags": sorted(tags), "frames": row["frames"], "skins": row["skins"],
                       "sequences": len(row["sequences"]), "also_in_slots": row["duplicate_payload_slots"]})
    return result


# ----------------------------------------------------------------------------------------------------------------
# Verify


def verify(root, manifest_path, out):
    with open(manifest_path, encoding="utf-8") as handle:
        manifest = json.load(handle)
    pack = load_pack(root)
    failures = []
    checked = 0
    for name, pin in manifest["pack"].items():
        if pin is None:
            continue
        data = read(os.path.join(root, name))
        checked += 1
        if len(data) != pin["size"] or sha256(data) != pin["sha256"]:
            failures.append(name)
    for row in manifest["slots"] + manifest["empty_slot_controls"]:
        data = slot_bytes(pack, row["slot"])
        checked += 1
        if pack["rows"][row["slot"]][0] != row["offset"] or len(data) != row["size"] or sha256(data) != row["sha256"]:
            failures.append(f"slot {row['slot']}")
    for zone in manifest["zones"]:
        for name, pin in zone["files"].items():
            data = read(os.path.join(root, name))
            checked += 1
            if len(data) != pin["size"] or sha256(data) != pin["sha256"]:
                failures.append(name)
    for control in manifest["file_controls"]:
        data = read(os.path.join(root, control["path"]))
        checked += 1
        if len(data) != control["size"] or sha256(data) != control["sha256"]:
            failures.append(control["path"])
    # Control: one altered hex digit in a copy of the first slot pin must be detected.
    altered = dict(manifest["slots"][0])
    digit = altered["sha256"][0]
    altered["sha256"] = ("1" if digit != "1" else "2") + altered["sha256"][1:]
    detected = sha256(slot_bytes(pack, altered["slot"])) != altered["sha256"]
    result = {"checked": checked, "failures": failures, "altered_digit_detected": detected}
    write_json(os.path.join(out, "verify.json"), result)
    print(json.dumps(result, indent=1))
    if failures or not detected:
        sys.exit(1)


# ----------------------------------------------------------------------------------------------------------------
# Dump (A1/A3 oracle)


def dump_slot(root, slot, arrays):
    pack = load_pack(root)
    data = slot_bytes(pack, slot)
    result = {"slot": slot, "name": slot_name(pack, slot), "size": len(data), "sha256": sha256(data)}
    if not data:
        result["empty"] = True
        return result
    record = parse_mesh(data)
    result.update({k: record[k] for k in ("frames", "vertices", "uv_count", "face_count", "skins", "width", "height",
                                          "sequences", "offsets")})
    result["facts"] = mesh_facts(record, data)
    result["skin_sha256"] = [sha256(block) for block in record["skin_blocks"]]
    if arrays:
        result["positions"] = list(record["positions"])
        result["uvs"] = list(record["uvs"])
        result["faces"] = list(record["faces"])
        # Portable forms the reader must reproduce: normalized UV = texel / size, exact in binary64 then float32.
        result["normalized_uvs"] = [[record["uvs"][2 * k] / 256.0 / record["width"],
                                     record["uvs"][2 * k + 1] / 256.0 / record["height"]]
                                    for k in range(record["uv_count"])]
    return result


def dump_zone(root, stem, arrays):
    pack = load_pack(root)
    pack_facts = {}
    for slot in range(pack["count"]):
        data = slot_bytes(pack, slot)
        if data:
            record = parse_mesh(data)
            pack_facts[slot] = {"frames": record["frames"]}
    facts, parsed = load_zone(root, stem, pack, pack_facts, load_entities(root))
    if arrays:
        facts["placements_rows"] = parsed["placements"]
        facts["surfaces"] = parsed["surfaces"]
    return facts


# ----------------------------------------------------------------------------------------------------------------
# Self-check


def build_mesh(frames=1, positions=None, uvs=None, faces=None, skins=1, width=2, height=2, texels=None,
               sequences=None, texture_header="counted"):
    vertices = len(positions) // (3 * frames)
    out = struct.pack("<7H", MESH_TAG, frames, vertices, len(uvs) // 2, len(faces) // 6, 3 * vertices, MESH_TRAILER)
    out += struct.pack(f"<{len(positions)}h", *positions)
    out += struct.pack(f"<{len(uvs)}H", *uvs)
    out += struct.pack(f"<{len(faces)}H", *faces)
    if texture_header == "counted":
        out += struct.pack("<3H", skins, width, height)
    else:
        out += struct.pack("<2H", width, height)
    texels = texels if texels is not None else [0x0F00 + k for k in range(skins * width * height)]
    out += struct.pack(f"<{len(texels)}H", *texels)
    sequences = sequences if sequences is not None else [(0, frames, frames)]
    out += struct.pack("<H", len(sequences))
    for seq in sequences:
        out += struct.pack("<3H", *seq)
    return out


def selfcheck(out):
    checks = []

    def check(name, condition):
        checks.append({"check": name, "passed": bool(condition)})

    quad = [0, 0, 0, 256, 0, 0, 256, 0, 256, 0, 0, 256]
    uvs = [0, 0, 512, 0, 512, 512, 0, 512]
    faces = [0, 1, 2, 0, 1, 2, 0, 2, 3, 0, 2, 3]
    static = build_mesh(1, quad, uvs, faces, sequences=[(0, 1, 1)])
    record = parse_mesh(static)
    check("static round trip", record["positions"] == tuple(quad) and record["sequences"] == [(0, 1, 1)])
    check("static probe Confirmed", probe_mesh(static, len(static), True)[:2] == ("Supported", "Confirmed"))
    animated = build_mesh(3, quad + [p + 1 for p in quad] + [p + 2 for p in quad], uvs, faces, skins=2,
                          sequences=[(0, 1, 10), (1, 3, 2)])
    record = parse_mesh(animated)
    check("animated round trip", record["frames"] == 3 and record["skins"] == 2 and len(record["skin_blocks"]) == 2)
    truncations_bad = 0
    for cut in range(len(animated)):
        prefix = animated[:cut]
        verdict = probe_mesh(prefix, cut, True)
        if verdict[:2] == ("Supported", "Confirmed"):
            truncations_bad += 1
        try:
            parse_mesh(prefix)
            truncations_bad += 1
        except RecordError:
            pass
    check("every truncation refused by probe and parser", truncations_bad == 0)
    check("trailing byte refused", probe_mesh(animated + b"\0", len(animated) + 1, True)[0] == "NotAModel")
    bad_tag = struct.pack("<H", 6) + animated[2:]
    check("tag 6 refused", probe_mesh(bad_tag, len(bad_tag), True)[0] == "NotAModel")
    bad_trailer = animated[:12] + struct.pack("<H", 2) + animated[14:]
    check("trailer 2 refused", probe_mesh(bad_trailer, len(bad_trailer), True)[0] == "NotAModel")
    bad_coordinates = animated[:10] + struct.pack("<H", 13) + animated[12:]
    check("coordinate count refused", probe_mesh(bad_coordinates, len(bad_coordinates), True)[0] == "NotAModel")
    bad_face = build_mesh(1, quad, uvs, [0, 1, 4, 0, 1, 2, 0, 2, 3, 0, 2, 3], sequences=[(0, 1, 1)])
    check("vertex index = count refused", probe_mesh(bad_face, len(bad_face), True)[0] == "NotAModel")
    bad_uv = build_mesh(1, quad, uvs, [0, 1, 2, 0, 1, 4, 0, 2, 3, 0, 2, 3], sequences=[(0, 1, 1)])
    check("UV index = count refused", probe_mesh(bad_uv, len(bad_uv), True)[0] == "NotAModel")
    bad_sequence = build_mesh(1, quad, uvs, faces, sequences=[(0, 2, 1)])
    check("sequence past frames refused", probe_mesh(bad_sequence, len(bad_sequence), True)[0] == "NotAModel")
    zero_rate = build_mesh(1, quad, uvs, faces, sequences=[(0, 1, 0)])
    check("zero rate refused", probe_mesh(zero_rate, len(zero_rate), True)[0] == "NotAModel")
    tentative = probe_mesh(animated[:40], len(animated), False)
    check("prefix of a longer entry is Tentative", tentative[:2] == ("Supported", "Tentative"))
    sky_interior = build_mesh(1, quad, uvs, faces, width=2, height=2, sequences=[(0, 1, 1)], texture_header="uncounted")
    check("uncounted sky layout parses with its variant", parse_mesh(sky_interior, "uncounted")["skins"] == 1)
    try:
        parse_mesh(sky_interior)
        check("uncounted sky layout refused by the counted walk", False)
    except RecordError:
        check("uncounted sky layout refused by the counted walk", True)

    grid = bytearray(ZONE_HEADER + ZONE_CELL * 4)
    grid[0:4] = b"test"
    struct.pack_into("<2H", grid, 128, 2, 2)
    envelope = struct.pack("<I", len(grid)) + zlib.compress(bytes(grid))
    check("zone probe Confirmed", probe_zone(envelope, len(envelope), True, "test.zmp")[:2] == ("Supported", "Confirmed"))
    short = struct.pack("<I", len(grid) - ZONE_CELL) + zlib.compress(bytes(grid))
    check("zone declared length one cell short refused", probe_zone(short, len(short), True, "test.zmp")[0] == "NotAModel")
    unterminated = bytearray(grid)
    unterminated[0:32] = b"x" * 32
    bad_name = struct.pack("<I", len(unterminated)) + zlib.compress(bytes(unterminated))
    check("zone name without NUL refused", probe_zone(bad_name, len(bad_name), True, "test.zmp")[0] == "NotAModel")
    zone_bad = 0
    for cut in range(len(envelope)):
        if probe_zone(envelope[:cut], cut, True, "test.zmp")[:2] == ("Supported", "Confirmed"):
            zone_bad += 1
    check("every zone truncation refused", zone_bad == 0)
    result = {"checks": checks, "failed": [c["check"] for c in checks if not c["passed"]]}
    write_json(os.path.join(out, "selfcheck.json"), result)
    print(json.dumps(result, indent=1))
    if result["failed"]:
        sys.exit(1)


# ----------------------------------------------------------------------------------------------------------------


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--root", help="the Shadowkey application directory (default: RealAssetPaths resolution)")
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("census", "measure"):
        sub = commands.add_parser(name)
        sub.add_argument("--out", required=True)
    sub = commands.add_parser("cover")
    sub.add_argument("--census", required=True)
    sub.add_argument("--out", required=True)
    sub = commands.add_parser("verify")
    sub.add_argument("--manifest", required=True)
    sub.add_argument("--out", required=True)
    sub = commands.add_parser("dump")
    sub.add_argument("--slot", type=int)
    sub.add_argument("--zone")
    sub.add_argument("--arrays", action="store_true")
    sub = commands.add_parser("selfcheck")
    sub.add_argument("--out", required=True)
    args = parser.parse_args(argv)
    if args.command == "selfcheck":
        selfcheck(args.out)
        return
    root, how = resolve_root(args.root)
    sys.stderr.write(f"root: {root} (via {how})\n")
    if args.command == "census":
        census(root, args.out)
    elif args.command == "measure":
        measure(root, args.out)
    elif args.command == "cover":
        cover(root, args.census, args.out)
    elif args.command == "verify":
        verify(root, args.manifest, args.out)
    elif args.command == "dump":
        if (args.slot is None) == (args.zone is None):
            raise SystemExit("dump needs exactly one of --slot or --zone")
        value = dump_slot(root, args.slot, args.arrays) if args.slot is not None else dump_zone(root, args.zone, args.arrays)
        json.dump(value, sys.stdout, indent=1)
        sys.stdout.write("\n")


if __name__ == "__main__":
    main()
