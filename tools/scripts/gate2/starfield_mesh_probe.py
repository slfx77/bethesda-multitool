# SPDX-License-Identifier: 0BSD
"""Cut-2 Starfield ``.mesh`` oracle: an independent decoder, the bounded probe rule, the corpus census and the cover.

This probe restates the ``.mesh`` layout from the cut-2 plan
(``docs/design/cut2-starfield-mesh-reader-plan-20260928.md``, section 0) and reads Starfield's GNRL BA2
archives with its own reader. It shares no code with BMT's C# ``StarfieldMeshFile`` or its BA2 backend and
never consumes their results, so hops A1 (fields) and A3 (geometry) compare ``mesh dump`` output against a
second decoding of the same bytes.

Layout (all little-endian, count-prefixed, no magic):

    u32 version (0, 1 or 2)
    u32 indexCount, u16 index[indexCount]
    f32 scale (finite and positive)
    u32 weightsPerVertex
    u32 vertexCount, then per vertex three int16 (x, y, z), 6 bytes, position = q * scale / 32767
    u32 count, then count x (half u, half v)                 UV set 0
    u32 count, then count x (half u, half v)                 UV set 1
    u32 count, then count x (u8 B, G, R, A)                  vertex colors
    u32 count, then count x u32 (10/10/10/2, v / 511.5 - 1)  normals
    u32 count, then count x u32 (10/10/10/2)                 tangents, 2-bit W
    u32 count, then count x (u16 bone, u16 weight)           skin weights
    if version != 0: u32 lodCount, then per LOD u32 indexCount and u16 index[indexCount]
    u32 count, then count x 4 u32 (vertexCount, vertexOffset, triangleCount, triangleOffset)   meshlets
    u32 count, then count x 6 f32 (center xyz, extent xyz)   cull records

The meshlet and cull sections form an optional tail: a stream may end exactly after the LOD section (so the
smallest valid stream holds, after its index list, 46 bytes for version 1 or 2 and 42 for version 0: ten or nine
dwords and one vertex, with every other stream empty). The first normal's 2-bit W says which (1 with the tail, 0 without it; a two-way equality over all 720,957 retail files), so the
probe refuses a stream whose normals say 1 and that ends at the tail boundary (a truncation the walk alone cannot
see). The walk must end exactly at the last byte. Retail ships only version 2.

Decoding rules the oracle states (compare after rounding once to float32): position = q * scale / 32767 in binary64;
a Dec4 channel = (2v - 1023) / 1023 in binary64 (v / 511.5 - 1); a UV = the half value exactly. A meshlet's
triangleOffset is the byte offset of its 3-byte local triangles, each meshlet's block padded to 4 bytes; its
vertexOffset is the running sum of vertexCount.

Subcommands (one process at a time; every one is read-only over the Starfield install):

    census   --data <Starfield Data dir> --out <receipt dir> [--limit N]
             Every .mesh entry of every GNRL BA2 that holds geometries\\ is decoded in full and probed on its
             64 KiB prefix; every other entry of those archives, and up to 1,000 evenly spaced entries of every
             other GNRL archive, is probed as a NotAModel control. Writes census-records.jsonl.gz,
             census-summary.json, controls.jsonl.gz, controls-summary.json.
    cover    --records <census-records.jsonl.gz> --out <receipt dir>
             Exact MILP joint cover (minimum file count, then minimum bytes) over every value and every observed
             pair of values of (version, weightsPerVertex, lodCount, colors, UV1, meshlet-count bucket) plus every
             file-level tag (secondary_tags), then the edge files and the decline controls. Writes
             cover-manifest.json and cover-report.txt.
    verify   --manifest <cover-manifest.json> --data <Starfield Data dir> --out <receipt dir>
             Re-reads every manifest row from its archive and checks size and SHA-256; the built-in control
             alters one SHA-256 digit and must fail.
    selfcheck --out <receipt dir> [--manifest <cover-manifest.json> --data <dir>]
             Synthetic writer round trips for versions 0, 1 and 2, every truncation point, trailing bytes,
             version 3, an out-of-range index; with a manifest, every truncation of the smallest cover files.
    frames   --records <census-records.jsonl.gz> --data <dir> --out <receipt dir> [--every N]
             On every Nth file: the stored tangent against the UV-derived frame (both W readings scored against
             +dP/dv of the raw UVs, the engine's DirectX frame; glTF's bitangent runs along -dP/dv, so its w is
             the winning reading negated), the
             one-rounding positions against the legacy two-rounding product; on every file with a near-zero
             normal or tangent, the raw Dec4 codes. Writes frames.json.
    dump     --data <dir> --archive <name> --entry <path> [--arrays]
             One entry's fields as JSON (the A1/A3 oracle payload).

Standard library plus numpy only.
"""

import argparse
import gzip
import hashlib
import json
import math
import os
import struct
import sys
import time
import zlib

import numpy as np

PROBE_BYTES = 64 * 1024
SNORM = 32767.0
DEC10 = 511.5
MAX_VERSION = 2
MAX_VERTICES = 65536
BACKSLASH = chr(92)
CONTROL_SAMPLE_PER_ARCHIVE = 1000
STEAM_SOURCE = "<SteamLibrary>/Starfield/Data/"


# --------------------------------------------------------------------------------------------------------------------
# GNRL BA2 reader (independent of BMT's backend)
# --------------------------------------------------------------------------------------------------------------------

class Ba2Entry:
    """One GNRL file record plus its name-table path."""

    __slots__ = ("index", "path", "offset", "packed", "size")

    def __init__(self, index, path, offset, packed, size):
        self.index = index
        self.path = path
        self.offset = offset
        self.packed = packed
        self.size = size


class Ba2Archive:
    """A BTDX archive: header, GNRL records (36 bytes each) and the u16-length name table."""

    def __init__(self, path):
        self.path = path
        self.name = os.path.basename(path)
        with open(path, "rb") as handle:
            head = handle.read(36)
            magic, version, kind, count, names_at = struct.unpack_from("<4sI4sIQ", head, 0)
            if magic != b"BTDX":
                raise ValueError(f"{path}: not a BTDX archive")
            self.version = version
            self.kind = kind.decode("latin1")
            self.count = count
            self.entries = []
            if self.kind != "GNRL":
                return
            # Version 1 has a 24-byte header; versions 2 and 3 add a u64, version 3 a further u32 compression method.
            header = 24 + (8 if version >= 2 else 0) + (4 if version >= 3 else 0)
            self.compression = struct.unpack_from("<I", head, 32)[0] if version >= 3 else 0
            handle.seek(header)
            records = handle.read(36 * count)
            handle.seek(names_at)
            table = handle.read()
        cursor = 0
        for index in range(count):
            _, _, _, _, offset, packed, size, _ = struct.unpack_from("<I4sIIQIII", records, index * 36)
            length = struct.unpack_from("<H", table, cursor)[0]
            name = table[cursor + 2:cursor + 2 + length].decode("latin1")
            cursor += 2 + length
            self.entries.append(Ba2Entry(index, name, offset, packed, size))

    def read(self, handle, entry):
        """The entry's decoded bytes (zlib when packed is non-zero, stored otherwise)."""
        handle.seek(entry.offset)
        if entry.packed == 0:
            data = handle.read(entry.size)
        else:
            if self.compression not in (0,):
                raise ValueError(f"{self.name}: compression method {self.compression} not supported")
            data = zlib.decompress(handle.read(entry.packed))
        if len(data) != entry.size:
            raise ValueError(f"{self.name}:{entry.path}: decoded {len(data)} bytes, record says {entry.size}")
        return data

    def read_prefix(self, handle, entry, limit=PROBE_BYTES):
        """At most limit decoded bytes and whether they are the whole entry (the probe's candidate)."""
        handle.seek(entry.offset)
        if entry.packed == 0:
            data = handle.read(min(limit, entry.size))
            return data, len(data) == entry.size
        inflater = zlib.decompressobj()
        out = bytearray()
        remaining = entry.packed
        while remaining > 0 and len(out) < limit:
            chunk = handle.read(min(remaining, 256 * 1024))
            remaining -= len(chunk)
            out += inflater.decompress(chunk, limit - len(out))
            while inflater.unconsumed_tail and len(out) < limit:
                out += inflater.decompress(inflater.unconsumed_tail, limit - len(out))
        data = bytes(out[:limit])
        return data, entry.size <= limit and len(data) == entry.size


def normalized(path):
    return path.lower().replace("/", BACKSLASH)


def gnrl_archives(data_dir):
    """Every GNRL archive in the Data folder, in name order, with a flag for whether it holds geometries\\."""
    result = []
    for name in sorted(os.listdir(data_dir)):
        if not name.lower().endswith(".ba2"):
            continue
        archive = Ba2Archive(os.path.join(data_dir, name))
        if archive.kind != "GNRL":
            continue
        holds = any(normalized(e.path).startswith("geometries" + BACKSLASH) for e in archive.entries)
        result.append((archive, holds))
    return result


# --------------------------------------------------------------------------------------------------------------------
# .mesh decoder
# --------------------------------------------------------------------------------------------------------------------

class MeshError(Exception):
    """A structural failure at a byte offset."""

    def __init__(self, offset, field, reason):
        super().__init__(f"{field} at 0x{offset:X}: {reason}")
        self.offset = offset
        self.field = field
        self.reason = reason


class Walker:
    """A bounded little-endian cursor that raises MeshError when a field does not fit."""

    def __init__(self, data):
        self.data = data
        self.pos = 0

    def u32(self, field):
        if self.pos + 4 > len(self.data):
            raise MeshError(self.pos, field, "runs past the end")
        value = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return value

    def f32(self, field):
        if self.pos + 4 > len(self.data):
            raise MeshError(self.pos, field, "runs past the end")
        value = struct.unpack_from("<f", self.data, self.pos)[0]
        self.pos += 4
        return value

    def array(self, dtype, count, field):
        size = np.dtype(dtype).itemsize * count
        if self.pos + size > len(self.data):
            raise MeshError(self.pos, field, f"{count} elements run past the end")
        value = np.frombuffer(self.data, dtype=dtype, count=count, offset=self.pos)
        self.pos += size
        return value


def decode(data):
    """Decodes a whole .mesh into raw arrays and offsets; raises MeshError on any structural failure."""
    w = Walker(data)
    m = {"offsets": {}}
    off = m["offsets"]
    off["version"] = w.pos
    m["version"] = w.u32("version")
    if m["version"] > MAX_VERSION:
        raise MeshError(0, "version", f"{m['version']} is above {MAX_VERSION}")
    off["indices"] = w.pos
    m["indexCount"] = w.u32("indexCount")
    m["indices"] = w.array("<u2", m["indexCount"], "indices")
    off["scale"] = w.pos
    m["scale"] = w.f32("scale")
    if not (math.isfinite(m["scale"]) and m["scale"] > 0):
        raise MeshError(off["scale"], "scale", f"{m['scale']!r} is not finite and positive")
    off["weightsPerVertex"] = w.pos
    m["weightsPerVertex"] = w.u32("weightsPerVertex")
    off["positions"] = w.pos
    m["vertexCount"] = w.u32("vertexCount")
    m["positions"] = w.array("<i2", 3 * m["vertexCount"], "positions").reshape(-1, 3)
    for key, dtype, width in (("uv0", "<u2", 2), ("uv1", "<u2", 2), ("colors", "<u1", 4),
                              ("normals", "<u4", 1), ("tangents", "<u4", 1), ("weights", "<u2", 2)):
        off[key] = w.pos
        count = w.u32(key + "Count")
        values = w.array(dtype, width * count, key)
        m[key] = values.reshape(-1, width) if width > 1 else values
        m[key + "Count"] = count
    m["lods"] = []
    if m["version"] != 0:
        off["lods"] = w.pos
        lod_count = w.u32("lodCount")
        for index in range(lod_count):
            count = w.u32(f"lod[{index}].indexCount")
            m["lods"].append(w.array("<u2", count, f"lod[{index}].indices"))
    off["meshlets"] = w.pos
    # The meshlet + cull tail is absent on some retail files: the stream then ends exactly here.
    m["tail"] = w.pos != len(data)
    if m["tail"]:
        m["meshletCount"] = w.u32("meshletCount")
        m["meshlets"] = w.array("<u4", 4 * m["meshletCount"], "meshlets").reshape(-1, 4)
        off["cull"] = w.pos
        m["cullCount"] = w.u32("cullCount")
        m["cull"] = w.array("<f4", 6 * m["cullCount"], "cull").reshape(-1, 6)
    else:
        m["meshletCount"] = m["cullCount"] = 0
        m["meshlets"] = np.zeros((0, 4), dtype="<u4")
        m["cull"] = np.zeros((0, 6), dtype="<f4")
    off["end"] = w.pos
    m["consumed"] = w.pos
    m["trailing"] = len(data) - w.pos
    return m


def half_to_float(bits):
    return bits.astype(np.uint16).view(np.float16).astype(np.float32)


def dec4(values):
    """10/10/10/2 unsigned channels mapped by v / 511.5 - 1, plus the raw 2-bit W."""
    v = values.astype(np.uint32)
    xyz = np.stack([(v & 0x3FF), (v >> 10) & 0x3FF, (v >> 20) & 0x3FF], axis=1).astype(np.float64) / DEC10 - 1.0
    return xyz, (v >> 30).astype(np.uint8)


def positions_metres(m):
    return m["positions"].astype(np.float64) * (float(np.float32(m["scale"])) / SNORM)


def measure(m):
    """Flat per-file facts for the census (every value is JSON-serializable)."""
    r = {}
    nv = m["vertexCount"]
    ic = m["indexCount"]
    idx = m["indices"]
    r["version"] = m["version"]
    r["indexCount"] = ic
    r["indexMod3"] = ic % 3
    r["vertexCount"] = nv
    r["scale"] = float(np.float32(m["scale"]))
    r["weightsPerVertex"] = m["weightsPerVertex"]
    for key in ("uv0", "uv1", "colors", "normals", "tangents", "weights"):
        r[key + "Count"] = m[key + "Count"]
    r["lodCount"] = len(m["lods"])
    r["lodIndexCounts"] = [int(len(x)) for x in m["lods"][:16]]
    r["meshletCount"] = m["meshletCount"]
    r["cullCount"] = m["cullCount"]
    r["consumed"] = m["consumed"]
    r["trailing"] = m["trailing"]
    r["exact"] = m["trailing"] == 0
    # Indices.
    r["indexMax"] = int(idx.max()) if ic else -1
    r["indexOutOfRange"] = int(np.count_nonzero(idx >= nv)) if ic else 0
    whole = ic - ic % 3
    tris = idx[:whole].reshape(-1, 3)
    if len(tris):
        rep = (tris[:, 0] == tris[:, 1]) | (tris[:, 1] == tris[:, 2]) | (tris[:, 0] == tris[:, 2])
        r["degenerateTriangles"] = int(np.count_nonzero(rep))
        used = np.zeros(nv, dtype=bool)
        inside = tris[tris.max(axis=1) < nv] if nv else tris[:0]
        used[inside.ravel()] = True
        r["unusedVertices"] = int(nv - np.count_nonzero(used)) if nv else 0
    else:
        r["degenerateTriangles"] = 0
        r["unusedVertices"] = nv
    # Positions.
    q = m["positions"]
    r["qMaxAbs"] = int(np.abs(q.astype(np.int32)).max()) if nv else 0
    r["qHasMinus32768"] = bool(nv and np.any(q == -32768))
    if nv:
        p = positions_metres(m)
        r["boundsMin"] = [round(float(x), 6) for x in p.min(axis=0)]
        r["boundsMax"] = [round(float(x), 6) for x in p.max(axis=0)]
    # UV sets (half bits).
    for key in ("uv0", "uv1"):
        if m[key + "Count"]:
            bits = m[key].astype(np.uint16)
            exponent = (bits >> 10) & 0x1F
            r[key + "NonFinite"] = int(np.count_nonzero(exponent == 0x1F))
            values = half_to_float(bits)
            finite = values[np.isfinite(values)]
            if finite.size:
                r[key + "Range"] = [round(float(finite.min()), 4), round(float(finite.max()), 4)]
    # Colors (byte order B, G, R, A).
    if m["colorsCount"]:
        c = m["colors"]
        r["colorAlphaAll255"] = bool(np.all(c[:, 3] == 255))
        r["colorAlphaMin"] = int(c[:, 3].min())
        r["colorAllWhite"] = bool(np.all(c == 255))
        r["colorSaturatedFraction"] = round(float(np.mean(np.all((c[:, :3] == 0) | (c[:, :3] == 255), axis=1))), 4)
        r["colorDistinct"] = int(np.unique(c.view("<u4")).size)
    # Normals and tangents.
    normals = tangents = None
    if m["normalsCount"]:
        normals, nw = dec4(m["normals"])
        length = np.linalg.norm(normals, axis=1)
        r["normalW"] = [int(x) for x in np.bincount(nw, minlength=4)]
        r["normalLengthDeviation"] = round(float(np.abs(length - 1).max()), 6)
        r["normalNearZero"] = int(np.count_nonzero(length < 0.01))
    if m["tangentsCount"]:
        tangents, tw = dec4(m["tangents"])
        length = np.linalg.norm(tangents, axis=1)
        r["tangentW"] = [int(x) for x in np.bincount(tw, minlength=4)]
        r["tangentLengthDeviation"] = round(float(np.abs(length - 1).max()), 6)
        r["tangentNearZero"] = int(np.count_nonzero(length < 0.01))
        if normals is not None and len(normals) == len(tangents):
            dots = np.abs(np.einsum("ij,ij->i", normals, tangents))
            r["normalTangentMaxDot"] = round(float(dots.max()), 6)
    # Skin weights: (u16 bone, u16 weight).
    wpv = m["weightsPerVertex"]
    if m["weightsCount"]:
        r["weightsShapeMatches"] = bool(wpv > 0 and m["weightsCount"] == nv * wpv)
        pairs = m["weights"]
        r["boneMax"] = int(pairs[:, 0].max())
        r["zeroWeightSlots"] = int(np.count_nonzero(pairs[:, 1] == 0))
        if r["weightsShapeMatches"]:
            sums = pairs[:, 1].astype(np.int64).reshape(nv, wpv).sum(axis=1)
            r["weightSumExact65535"] = int(np.count_nonzero(sums == 65535))
            r["weightSumMaxDeviation"] = int(np.abs(sums - 65535).max())
            bones = pairs[:, 0].reshape(nv, wpv).astype(np.int64)
            nonzero = pairs[:, 1].reshape(nv, wpv) > 0
            dup = 0
            for a in range(wpv):
                for b in range(a + 1, wpv):
                    dup += int(np.count_nonzero((bones[:, a] == bones[:, b]) & nonzero[:, a] & nonzero[:, b]))
            r["duplicateNonzeroBonePairs"] = dup
            # The same sums read with the fields swapped: the order control.
            swapped = pairs[:, 0].astype(np.int64).reshape(nv, wpv).sum(axis=1)
            r["weightSumExact65535IfSwapped"] = int(np.count_nonzero(swapped == 65535))
    # LOD index lists.
    if m["lods"]:
        r["lodMod3"] = [int(len(x) % 3) for x in m["lods"][:16]]
        r["lodOutOfRange"] = int(sum(int(np.count_nonzero(x >= nv)) for x in m["lods"]))
        r["lodDecreasing"] = bool(all(len(m["lods"][i]) >= len(m["lods"][i + 1]) for i in range(len(m["lods"]) - 1)))
        r["lodFirstBelowMain"] = bool(len(m["lods"][0]) <= ic)
    # Meshlets and cull records.
    r["tail"] = m["tail"]
    ml = m["meshlets"].astype(np.int64)
    if len(ml):
        vc, vo, tc, to = ml[:, 0], ml[:, 1], ml[:, 2], ml[:, 3]
        r["meshletTriangleSum"] = int(tc.sum())
        r["meshletVertexSum"] = int(vc.sum())
        r["meshletVertexSumMinusCount"] = int(vc.sum()) - nv
        r["meshletMaxVertices"] = int(vc.max())
        r["meshletMaxTriangles"] = int(tc.max())
        r["meshletEmpty"] = int(np.count_nonzero(tc == 0))
        r["meshletTrianglesEqualMain"] = bool(r["meshletTriangleSum"] * 3 == ic)
        vertex_starts = np.concatenate([[0], np.cumsum(vc)[:-1]])
        r["meshletVertexOffsetsCumulative"] = int(np.count_nonzero(vo == vertex_starts))
        # triangleOffset: the byte offset of 3-byte local triangles, each meshlet's block aligned to 4 bytes.
        aligned = np.concatenate([[0], np.cumsum((3 * tc + 3) // 4 * 4)[:-1]])
        r["meshletTriangleOffsetsAligned3"] = int(np.count_nonzero(to == aligned))
        triangle_starts = np.concatenate([[0], np.cumsum(tc)[:-1]])
        # The index-unit reading of the same field: the control.
        r["meshletTriangleOffsetsIndexUnits"] = int(np.count_nonzero(to == 3 * triangle_starts))
        if r["meshletTrianglesEqualMain"] and len(tris):
            owner = np.repeat(np.arange(len(ml)), tc)
            lo = vo[owner]
            hi = lo + vc[owner]
            inside = np.all((tris >= lo[:, None]) & (tris < hi[:, None]), axis=1)
            r["meshletTrianglesInOwnVertexRange"] = int(np.count_nonzero(inside))
            if m["cullCount"] == len(ml) and nv and r["indexOutOfRange"] == 0:
                p = positions_metres(m)
                corners = p[tris]
                tmin = corners.min(axis=1)
                tmax = corners.max(axis=1)
                keep = tc > 0
                mmin = np.minimum.reduceat(tmin, triangle_starts[keep], axis=0)
                mmax = np.maximum.reduceat(tmax, triangle_starts[keep], axis=0)
                cull = m["cull"].astype(np.float64)[keep]
                center, extent = cull[:, :3], cull[:, 3:]
                tolerance = 1e-4 * max(1.0, float(np.abs(p).max()))
                contained = np.all((mmin >= center - extent - tolerance) & (mmax <= center + extent + tolerance), axis=1)
                r["cullChecked"] = int(np.count_nonzero(keep))
                r["cullContainsMeshlet"] = int(np.count_nonzero(contained))
                r["cullTightness"] = round(float(np.abs(np.concatenate([mmin - (center - extent),
                                                                        (center + extent) - mmax])).max()), 6)
                # The min/max reading of the same six floats: the semantics control.
                as_box = np.all((mmin >= cull[:, :3] - tolerance) & (mmax <= cull[:, 3:] + tolerance), axis=1)
                r["cullContainsIfMinMax"] = int(np.count_nonzero(as_box))
    r["meshletBucket"] = meshlet_bucket(m["meshletCount"]) if m["tail"] else "absent"
    return r


def meshlet_bucket(count):
    if count == 0:
        return "0"
    if count == 1:
        return "1"
    if count <= 4:
        return "2-4"
    if count <= 16:
        return "5-16"
    if count <= 64:
        return "17-64"
    if count <= 256:
        return "65-256"
    return "257+"


# --------------------------------------------------------------------------------------------------------------------
# The bounded probe rule (what BMT's StarfieldMeshModelProbe mirrors)
# --------------------------------------------------------------------------------------------------------------------

class OutOfPrefix(Exception):
    """The walk reached the end of the bounded content."""


class PrefixWalker(Walker):
    """The probe's cursor: running out of content is OutOfPrefix, never a structural failure."""

    def u32(self, field):
        if self.pos + 4 > len(self.data):
            self.pos = len(self.data)
            raise OutOfPrefix()
        return super().u32(field)

    def f32(self, field):
        if self.pos + 4 > len(self.data):
            self.pos = len(self.data)
            raise OutOfPrefix()
        return super().f32(field)


def probe(content, complete, declared_length=None, max_weights=32, max_lods=32, tail_rule=True):
    """Classifies a bounded prefix: NotAModel, Supported (Confirmed or Tentative) or Unsupported truncated.

    Every check applies only to bytes inside the prefix. A violated check is NotAModel (the content is not a
    .mesh). A complete file whose walk ends exactly at EOF is Supported and Confirmed. A complete file whose walk
    runs out after the vertex count validated is Unsupported (truncated); before it, NotAModel. A complete file
    with bytes after the walk is NotAModel. An incomplete prefix that validated the vertex count is Supported and
    Tentative (the read decides); one that ran out before it is Supported and Tentative with the stage recorded.
    """
    w = PrefixWalker(content)
    stage = "start"
    facts = {}
    try:
        if len(content) < 8:
            # Too short for the version and index-count dwords: never a .mesh, complete or not (plan 4.1 step 1).
            return result("NotAModel", reason="short", stage="start", examined=len(content))
        version = w.u32("version")
        if version > MAX_VERSION:
            return result("NotAModel", reason="version", stage="version")
        facts["version"] = version
        count = w.u32("indexCount")
        stage = "indexCount"
        if count % 3:
            return result("NotAModel", reason="indexCount%3", stage=stage)
        # The smallest valid layout after the index list: scale, weightsPerVertex, vertexCount, one vertex, the
        # five stream counts, the weight count and (versions 1 and 2) the LOD count, with no meshlet tail. A stream
        # with the tail is 8 bytes longer, so this bound admits every stream the reader accepts.
        if declared_length is not None and 8 + 2 * count + min_bytes_after_indices(version) > declared_length:
            return result("NotAModel", reason="indexCount>length", stage=stage)
        available = (len(content) - w.pos) // 2
        indices = np.frombuffer(content, dtype="<u2", count=min(count, available), offset=w.pos)
        index_max = int(indices.max()) if len(indices) else -1
        if available < count:
            w.pos += 2 * available
            raise OutOfPrefix()
        w.pos += 2 * count
        stage = "indices"
        scale = w.f32("scale")
        if not (math.isfinite(scale) and scale > 0):
            return result("NotAModel", reason="scale", stage="scale")
        weights = w.u32("weightsPerVertex")
        if weights > max_weights:
            return result("NotAModel", reason="weightsPerVertex", stage="weightsPerVertex")
        vertices = w.u32("vertexCount")
        if vertices == 0 or vertices > MAX_VERTICES:
            return result("NotAModel", reason="vertexCount", stage="vertexCount")
        if index_max >= vertices:
            return result("NotAModel", reason="index>=vertexCount", stage="vertexCount")
        if declared_length is not None and w.pos + 6 * vertices > declared_length:
            return result("NotAModel", reason="vertices>length", stage="vertexCount")
        stage = "vertexCount"
        facts["vertexCount"] = vertices
        skip(w, 6 * vertices)
        stage = "positions"
        normal_w = None
        for key, width in (("uv0", 4), ("uv1", 4), ("colors", 4), ("normals", 4), ("tangents", 4)):
            n = w.u32(key)
            if n not in (0, vertices):
                return result("NotAModel", reason=key + "Count", stage=key)
            if key == "normals" and n and w.pos + 4 <= len(content):
                # The first normal's 2-bit W: 1 on every file with the meshlet tail, 0 on every file without it
                # (a two-way equality over the whole corpus), so it says whether a tail must follow.
                normal_w = struct.unpack_from("<I", content, w.pos)[0] >> 30
            skip(w, width * n)
            stage = key
        n = w.u32("weights")
        if n != (vertices * weights):
            return result("NotAModel", reason="weightsCount", stage="weights")
        skip(w, 4 * n)
        stage = "weights"
        if version != 0:
            lods = w.u32("lodCount")
            if lods > max_lods:
                return result("NotAModel", reason="lodCount", stage="lodCount")
            for _ in range(lods):
                n = w.u32("lodIndexCount")
                if n % 3:
                    return result("NotAModel", reason="lodIndexCount%3", stage="lods")
                have = (len(content) - w.pos) // 2
                lod = np.frombuffer(content, dtype="<u2", count=min(n, have), offset=w.pos)
                if len(lod) and int(lod.max()) >= vertices:
                    return result("NotAModel", reason="lodIndex>=vertexCount", stage="lods")
                skip(w, 2 * n)
            stage = "lods"
        if complete and w.pos == len(content):
            if tail_rule and normal_w == 1:
                return result("Unsupported", "Tentative", reason="truncated", stage="lods", examined=w.pos)
            return result("Supported", "Confirmed", reason="no meshlet tail", stage="end", examined=w.pos)
        meshlets = w.u32("meshletCount")
        skip(w, 16 * meshlets)
        stage = "meshlets"
        cull = w.u32("cullCount")
        if cull != meshlets:
            return result("NotAModel", reason="cullCount!=meshletCount", stage="cullCount")
        skip(w, 24 * cull)
        stage = "end"
    except OutOfPrefix:
        if complete:
            if stage in ("start", "indexCount"):
                return result("NotAModel", reason="short", stage=stage, examined=len(content))
            if stage == "indices":
                return result("NotAModel", reason="short-before-vertexCount", stage=stage, examined=len(content))
            return result("Unsupported", "Tentative", reason="truncated", stage=stage, examined=len(content))
        return result("Supported", "Tentative", stage=stage, examined=len(content))
    if complete:
        if w.pos != len(content):
            return result("NotAModel", reason="trailing", stage="end", examined=w.pos)
        return result("Supported", "Confirmed", stage="end", examined=w.pos)
    return result("Supported", "Tentative", stage="end", examined=w.pos)


def min_bytes_after_indices(version):
    """The bytes the smallest valid tail-less stream holds after its index list: 46, or 42 for version 0."""
    return 4 * 9 + 6 if version == 0 else 4 * 10 + 6


def minimal_stream(version, vertices=1):
    """The smallest valid stream: indices [0, 0, 0], scale 1, no weights, `vertices` zero vertices, every stream and
    LOD count 0, no meshlet tail (60 bytes for version 2 with one vertex, 62 for version 0 with two)."""
    b = struct.pack("<II3H", version, 3, 0, 0, 0) + struct.pack("<fII", 1.0, 0, vertices) + bytes(6 * vertices)
    b += struct.pack("<6I", 0, 0, 0, 0, 0, 0)
    if version != 0:
        b += struct.pack("<I", 0)
    return b


def skip(walker, size):
    if walker.pos + size > len(walker.data):
        walker.pos = len(walker.data)
        raise OutOfPrefix()
    walker.pos += size


def result(kind, confidence=None, reason=None, stage=None, examined=None):
    return {"kind": kind, "confidence": confidence, "reason": reason, "stage": stage, "examined": examined}


# --------------------------------------------------------------------------------------------------------------------
# census
# --------------------------------------------------------------------------------------------------------------------

def census(args):
    out = args.out
    os.makedirs(out, exist_ok=True)
    started = time.time()
    archives = gnrl_archives(args.data)
    records = gzip.open(os.path.join(out, "census-records.jsonl.gz"), "wt", encoding="utf-8")
    controls = gzip.open(os.path.join(out, "controls.jsonl.gz"), "wt", encoding="utf-8")
    summary = {"data": args.data, "archives": {}, "started": time.strftime("%Y-%m-%dT%H:%M:%S")}
    control_summary = {"byArchive": {}, "byExtension": {}, "supported": []}
    seen_paths = {}
    total = 0
    for archive, holds in archives:
        mesh_entries = [e for e in archive.entries if e.path.lower().endswith(".mesh")] if holds else []
        others = [e for e in archive.entries if not e.path.lower().endswith(".mesh")]
        if not holds and len(others) > CONTROL_SAMPLE_PER_ARCHIVE:
            step = len(others) / CONTROL_SAMPLE_PER_ARCHIVE
            others = [others[int(i * step)] for i in range(CONTROL_SAMPLE_PER_ARCHIVE)]
        info = {"entries": archive.count, "holdsGeometries": holds, "mesh": len(mesh_entries),
                "controlsProbed": len(others), "decodeFailures": 0}
        summary["archives"][archive.name] = info
        with open(archive.path, "rb") as handle:
            for entry in sorted(mesh_entries, key=lambda e: e.offset):
                if args.limit and total >= args.limit:
                    break
                total += 1
                data = archive.read(handle, entry)
                row = {"archive": archive.name, "index": entry.index, "path": entry.path, "size": entry.size,
                       "packed": entry.packed, "sha256": hashlib.sha256(data).hexdigest()}
                key = normalized(entry.path)
                previous = seen_paths.get(key)
                if previous is not None:
                    row["pathAlsoIn"] = previous[0]
                    row["pathSameBytes"] = previous[1] == row["sha256"]
                seen_paths[key] = (archive.name, row["sha256"])
                try:
                    row.update(measure(decode(data)))
                except MeshError as error:
                    row["decodeError"] = {"field": error.field, "offset": error.offset, "reason": error.reason}
                    info["decodeFailures"] += 1
                prefix = data[:PROBE_BYTES]
                row["probe"] = probe(prefix, len(data) <= PROBE_BYTES, entry.size)
                records.write(json.dumps(row, separators=(",", ":")) + "\n")
                if total % 20000 == 0:
                    print(f"{time.time() - started:8.0f}s {total} meshes ({archive.name})", flush=True)
            for entry in sorted(others, key=lambda e: e.offset):
                prefix, complete = archive.read_prefix(handle, entry)
                verdict = probe(prefix, complete, entry.size)
                strict = probe(prefix, complete, entry.size, max_weights=8, max_lods=8)
                ext = entry.path.rsplit(".", 1)[-1].lower() if "." in entry.path else ""
                crow = {"archive": archive.name, "index": entry.index, "path": entry.path, "size": entry.size,
                        "ext": ext, "probe": verdict, "probeStrict": strict["kind"]}
                controls.write(json.dumps(crow, separators=(",", ":")) + "\n")
                by_ext = control_summary["byExtension"].setdefault(ext, {})
                by_ext[verdict["kind"]] = by_ext.get(verdict["kind"], 0) + 1
                by_arc = control_summary["byArchive"].setdefault(archive.name, {})
                by_arc[verdict["kind"]] = by_arc.get(verdict["kind"], 0) + 1
                if verdict["kind"] != "NotAModel":
                    control_summary["supported"].append(crow)
        print(f"{time.time() - started:8.0f}s done {archive.name}: {len(mesh_entries)} mesh, {len(others)} controls",
              flush=True)
    records.close()
    controls.close()
    summary["seconds"] = round(time.time() - started, 1)
    summary["meshTotal"] = total
    with open(os.path.join(out, "controls-summary.json"), "w", encoding="utf-8") as handle:
        json.dump(control_summary, handle, indent=1)
    summarize(os.path.join(out, "census-records.jsonl.gz"), summary)
    with open(os.path.join(out, "census-summary.json"), "w", encoding="utf-8") as handle:
        json.dump(summary, handle, indent=1)
    print(f"census: {total} meshes in {summary['seconds']}s", flush=True)


def iter_records(path):
    with gzip.open(path, "rt", encoding="utf-8") as handle:
        for line in handle:
            yield json.loads(line)


def bump(table, key, amount=1):
    key = str(key)
    table[key] = table.get(key, 0) + amount


def summarize(path, summary):
    """Aggregates the per-file records (a second streaming pass)."""
    s = {}
    unique = set()
    cells = {}
    maxima = {}
    for r in iter_records(path):
        bump(s.setdefault("records", {}), "all")
        unique.add(r["sha256"])
        if "pathAlsoIn" in r:
            bump(s.setdefault("pathOverrides", {}), "sameBytes" if r["pathSameBytes"] else "differentBytes")
        p = r["probe"]
        bump(s.setdefault("probe", {}), f"{p['kind']}/{p['confidence']}/{p['stage']}/{p['reason']}")
        if "decodeError" in r:
            bump(s.setdefault("decodeError", {}), r["decodeError"]["field"] + ": " + r["decodeError"]["reason"][:40])
            continue
        bump(s.setdefault("exact", {}), r["exact"])
        bump(s.setdefault("version", {}), r["version"])
        bump(s.setdefault("indexMod3", {}), r["indexMod3"])
        bump(s.setdefault("weightsPerVertex", {}), r["weightsPerVertex"])
        bump(s.setdefault("lodCount", {}), f"v{r['version']}:{r['lodCount']}")
        bump(s.setdefault("meshletBucket", {}), r["meshletBucket"])
        bump(s.setdefault("cullEqualsMeshlet", {}), r["cullCount"] == r["meshletCount"])
        for key in ("uv0", "uv1", "colors", "normals", "tangents"):
            c = r[key + "Count"]
            bump(s.setdefault(key, {}), "absent" if c == 0 else ("=vertexCount" if c == r["vertexCount"] else "other"))
        wc = r["weightsCount"]
        bump(s.setdefault("weights", {}), "absent" if wc == 0 else
             ("=vertexCount*wpv" if r.get("weightsShapeMatches") else "other"))
        bump(s.setdefault("indexOutOfRange", {}), r["indexOutOfRange"] > 0)
        bump(s.setdefault("degenerateTriangles", {}), r["degenerateTriangles"] > 0)
        bump(s.setdefault("unusedVertices", {}), r["unusedVertices"] > 0)
        bump(s.setdefault("qMaxAbsIs32767", {}), r["qMaxAbs"] == 32767)
        bump(s.setdefault("qHasMinus32768", {}), r["qHasMinus32768"])
        bump(s.setdefault("sizeOverProbe", {}), r["size"] > PROBE_BYTES)
        bump(s.setdefault("vertexCountIsMax", {}), r["vertexCount"] == MAX_VERTICES)
        for key in ("uv0NonFinite", "uv1NonFinite"):
            if key in r:
                bump(s.setdefault(key, {}), r[key] > 0)
        if "normalW" in r:
            bump(s.setdefault("normalWValuesUsed", {}), "".join(str(i) for i in range(4) if r["normalW"][i]))
            bump(s.setdefault("normalNearZero", {}), r["normalNearZero"] > 0)
        if "tangentW" in r:
            bump(s.setdefault("tangentWValuesUsed", {}), "".join(str(i) for i in range(4) if r["tangentW"][i]))
            bump(s.setdefault("tangentNearZero", {}), r["tangentNearZero"] > 0)
        if "colorAlphaAll255" in r:
            bump(s.setdefault("colorAlphaAll255", {}), r["colorAlphaAll255"])
            bump(s.setdefault("colorAllWhite", {}), r["colorAllWhite"])
            bump(s.setdefault("colorSaturatedFraction", {}), "1.0" if r["colorSaturatedFraction"] == 1 else
                 (">=0.5" if r["colorSaturatedFraction"] >= 0.5 else "<0.5"))
        if r["weightsCount"] and r.get("weightsShapeMatches"):
            bump(s.setdefault("weightSumAll65535", {}), r["weightSumExact65535"] == r["vertexCount"])
            bump(s.setdefault("weightSumAll65535IfSwapped", {}), r["weightSumExact65535IfSwapped"] == r["vertexCount"])
            bump(s.setdefault("duplicateNonzeroBonePairs", {}), r["duplicateNonzeroBonePairs"] > 0)
        if r["lodCount"]:
            bump(s.setdefault("lodMod3AllZero", {}), all(x == 0 for x in r["lodMod3"]))
            bump(s.setdefault("lodOutOfRange", {}), r["lodOutOfRange"] > 0)
            bump(s.setdefault("lodDecreasing", {}), r["lodDecreasing"])
            bump(s.setdefault("lodFirstBelowMain", {}), r["lodFirstBelowMain"])
        bump(s.setdefault("tail", {}), f"v{r['version']}:{'present' if r['tail'] else 'absent'}")
        bump(s.setdefault("tailByArchive", {}), f"{r['archive']}:{'present' if r['tail'] else 'absent'}")
        if r["meshletCount"]:
            n = r["meshletCount"]
            bump(s.setdefault("meshletTrianglesEqualMain", {}), r["meshletTrianglesEqualMain"])
            bump(s.setdefault("meshletVertexOffsetsCumulativeAll", {}), r["meshletVertexOffsetsCumulative"] == n)
            bump(s.setdefault("meshletTriangleOffsetsAligned3All", {}), r["meshletTriangleOffsetsAligned3"] == n)
            bump(s.setdefault("meshletTriangleOffsetsIndexUnitsAll", {}), r["meshletTriangleOffsetsIndexUnits"] == n)
            d = r["meshletVertexSumMinusCount"]
            bump(s.setdefault("meshletVertexSumMinusCount", {}), "0" if d == 0 else ("<0" if d < 0 else ">0"))
            bump(s.setdefault("meshletHasEmpty", {}), r["meshletEmpty"] > 0)
            if "meshletTrianglesInOwnVertexRange" in r:
                ntri = r["indexCount"] // 3
                bump(s.setdefault("meshletTrianglesAllInOwnRange", {}), r["meshletTrianglesInOwnVertexRange"] == ntri)
            if "cullContainsMeshlet" in r:
                kept = r["cullChecked"]
                bump(s.setdefault("cullContainsAllMeshlets", {}), r["cullContainsMeshlet"] == kept)
                bump(s.setdefault("cullContainsAllIfMinMax", {}), r["cullContainsIfMinMax"] == kept)
        cell = bucket_key(r)
        cells[cell] = cells.get(cell, 0) + 1
        for key in ("vertexCount", "indexCount", "size", "meshletCount", "lodCount", "weightsPerVertex", "boneMax",
                    "qMaxAbs", "normalLengthDeviation", "tangentLengthDeviation", "normalTangentMaxDot",
                    "weightSumMaxDeviation", "cullTightness", "meshletMaxVertices", "meshletMaxTriangles"):
            if key in r and (key not in maxima or r[key] > maxima[key][0]):
                maxima[key] = (r[key], r["archive"], r["path"])
        for key, lo in (("scale", True), ("size", True), ("vertexCount", True)):
            name = key + "Min"
            if key in r and (name not in maxima or r[key] < maxima[name][0]):
                maxima[name] = (r[key], r["archive"], r["path"])
    s["uniquePayloads"] = len(unique)
    s["cells"] = dict(sorted(cells.items(), key=lambda kv: -kv[1]))
    s["cellCount"] = len(cells)
    s["extremes"] = {k: {"value": v[0], "archive": v[1], "path": v[2]} for k, v in maxima.items()}
    summary["aggregate"] = s


def bucket_key(r):
    """The cover cell: version x weights x LODs x colors x UV1 x meshlet-count bucket."""
    return "|".join([f"v{r['version']}", f"w{r['weightsPerVertex']}", f"lod{r['lodCount']}",
                     "col" if r["colorsCount"] else "nocol", "uv1" if r["uv1Count"] else "nouv1",
                     "m" + r["meshletBucket"]])


def secondary_tags(r):
    """File-level values the cover also reaches, beyond the cell (each is one cover item)."""
    tags = set()
    for key in ("uv0", "normals", "tangents"):
        if r[key + "Count"] == 0:
            tags.add(key + ":absent")
    for key in ("normalW", "tangentW"):
        if key in r:
            tags.add(key + ":" + "".join(str(i) for i in range(4) if r[key][i]))
    if r["degenerateTriangles"]:
        tags.add("degenerateTriangles")
    if r["unusedVertices"]:
        tags.add("unusedVertices")
    if r.get("colorAlphaAll255") is False:
        tags.add("colorAlpha<255")
    if r["colorsCount"] and r.get("colorSaturatedFraction", 1) < 1:
        tags.add("colorNotAllSaturated")
    if r.get("uv0NonFinite"):
        tags.add("uv0NonFinite")
    if r.get("uv1NonFinite"):
        tags.add("uv1NonFinite")
    if r["qMaxAbs"] == 32767:
        tags.add("qMaxAbs=32767")
    scale = r["scale"]
    if scale <= 0 or 2 ** round(math.log2(scale)) != scale:
        tags.add("scaleNotPowerOfTwo")
    if r["probe"]["confidence"] == "Tentative":
        early = r["probe"]["stage"] in ("indexCount", "indices")
        tags.add("probe:Tentative:" + ("beforeVertexCount" if early else "afterVertexCount"))
    if r.get("normalNearZero"):
        tags.add("normalSentinel")
    if r.get("tangentNearZero"):
        tags.add("tangentSentinel")
    if r["weightsCount"] and r.get("weightsShapeMatches") and r["weightSumExact65535"] != r["vertexCount"]:
        tags.add("weightSum!=65535")
    if r.get("duplicateNonzeroBonePairs"):
        tags.add("duplicateNonzeroBone")
    if "cullContainsMeshlet" in r and r["cullContainsMeshlet"] != r["cullChecked"]:
        tags.add("cullOvershoot")
    return tags


def cell_items(cell):
    """Every value of the six cell dimensions and every observed pair of them (the pairwise joint cover)."""
    parts = cell.split("|")
    items = {f"{i}:{p}" for i, p in enumerate(parts)}
    for a in range(len(parts)):
        for b in range(a + 1, len(parts)):
            items.add(f"{a}:{parts[a]}&{b}:{parts[b]}")
    return items


def solve_cover(groups, universe):
    """Exact set cover by MILP (HiGHS): minimum file count first, then minimum bytes among minimum-count covers.

    groups: list of (items, size, sha); returns the chosen group indices and the solver statuses.
    """
    from scipy.optimize import Bounds, LinearConstraint, milp
    index = {item: n for n, item in enumerate(sorted(universe))}
    matrix = np.zeros((len(index), len(groups)))
    for column, (items, _, _) in enumerate(groups):
        for item in items:
            matrix[index[item], column] = 1
    cover_rows = LinearConstraint(matrix, lb=1, ub=np.inf)
    integrality = np.ones(len(groups))
    bounds = Bounds(0, 1)
    first = milp(c=np.ones(len(groups)), constraints=[cover_rows], integrality=integrality, bounds=bounds,
                 options={"time_limit": 300})
    count = int(round(first.fun))
    sizes = np.array([size for _, size, _ in groups], dtype=np.float64)
    second = milp(c=sizes, constraints=[cover_rows, LinearConstraint(np.ones((1, len(groups))), lb=0, ub=count)],
                  integrality=integrality, bounds=bounds, options={"time_limit": 300})
    solution = second.x if second.success else first.x
    chosen = sorted((n for n in range(len(groups)) if solution[n] > 0.5), key=lambda n: (groups[n][1], groups[n][2]))
    return chosen, {"countStage": first.message, "bytesStage": second.message, "count": count}


# --------------------------------------------------------------------------------------------------------------------
# cover
# --------------------------------------------------------------------------------------------------------------------

def cover(args):
    out = args.out
    os.makedirs(out, exist_ok=True)
    groups = {}
    first_source = {}
    cells = {}
    edge = {}
    decode_errors = []
    for r in iter_records(args.records):
        sha = r["sha256"]
        known = first_source.get(sha)
        if known is None or (not known["archive"].startswith("Starfield - ") and r["archive"].startswith("Starfield - ")):
            first_source[sha] = r
        if "decodeError" in r:
            decode_errors.append(r)
            continue
        cell = bucket_key(r)
        cells[cell] = cells.get(cell, 0) + 1
        key = (cell, frozenset(secondary_tags(r)))
        candidate = (r["size"], sha)
        if key not in groups or candidate < groups[key][0]:
            groups[key] = (candidate, r)
        for name, pick in (("largestFile", lambda x: x["size"]), ("mostVertices", lambda x: x["vertexCount"]),
                           ("mostIndices", lambda x: x["indexCount"]), ("mostMeshlets", lambda x: x["meshletCount"]),
                           ("mostLods", lambda x: x["lodCount"]), ("largestBoneIndex", lambda x: x.get("boneMax", -1)),
                           ("loosestCull", lambda x: x.get("cullTightness", -1))):
            value = pick(r)
            if name not in edge or (value, -r["size"]) > (edge[name][0], -edge[name][1]["size"]):
                edge[name] = (value, r)
        if "smallestFile" not in edge or candidate < (edge["smallestFile"][0], edge["smallestFile"][1]["sha256"]):
            edge["smallestFile"] = (r["size"], r)
    group_list = []
    universe = set()
    for (cell, tags), ((size, sha), r) in sorted(groups.items(), key=lambda kv: kv[1][0]):
        items = cell_items(cell) | set(tags)
        universe |= items
        group_list.append((items, size, sha, r))
    chosen, statuses = solve_cover([(g[0], g[1], g[2]) for g in group_list], universe)
    rows = []
    selected = set()
    covered = set()
    for n in chosen:
        items, _, sha, r = group_list[n]
        rows.append(manifest_row(first_source[sha], "cover"))
        selected.add(sha)
        covered |= items
    for key in sorted(edge):
        _, r = edge[key]
        if r["sha256"] in selected:
            for row in rows:
                if row["sha256"] == r["sha256"]:
                    row.setdefault("edges", []).append(key)
            continue
        rows.append(manifest_row(first_source[r["sha256"]], "edge", edge=key))
        selected.add(r["sha256"])
    # Second pass: every other occurrence of each chosen payload (a patch archive repeating a path, a DLC repeating
    # base-game bytes), so a resolver can fall back and the manifest says where else the bytes live.
    by_sha = {row["sha256"]: row for row in rows}
    for r in iter_records(args.records):
        row = by_sha.get(r["sha256"])
        if row is None or (r["archive"] == row["archive"] and r["path"] == row["entry"]):
            continue
        row.setdefault("alsoIn", []).append({"source": STEAM_SOURCE + r["archive"], "entry": r["path"],
                                             "index": r["index"]})
    manifest = {"schema": "cut2-starfield-mesh-cover/1", "records": args.records,
                "rule": "exact MILP joint cover over every value and every observed pair of values of (version, "
                        "weightsPerVertex, lodCount, colors, UV1, meshlet-count bucket) plus every file-level tag; "
                        "minimum file count, then minimum bytes; representatives are the smallest file (then "
                        "SHA-256 order) of each (cell, tag set) group",
                "solver": statuses, "cellCount": len(cells), "cells": cells, "groupCount": len(group_list),
                "itemCount": len(universe), "uncoveredItems": sorted(universe - covered),
                "decodeErrors": len(decode_errors),
                "files": rows,
                "declineControls": decline_controls(args)}
    with open(os.path.join(out, "cover-manifest.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=1)
    with open(os.path.join(out, "cover-report.txt"), "w", encoding="utf-8") as handle:
        handle.write(f"cells {len(cells)}, groups {len(group_list)}, items {len(universe)}, files {len(rows)}\n")
        for row in rows:
            handle.write(f"{row['role']:16s} {row['size']:>9} {row['sha256'][:16]} {row['archive']} :: {row['entry']}"
                         f"  [{row.get('cell') or row.get('tag') or row.get('edge')}]\n")
        for control in manifest["declineControls"]:
            handle.write(f"control          {control['kind']}: {control.get('archive')} :: {control.get('entry')} "
                         f"-> {control['expect']}\n")
    print(f"cover: {len(cells)} cells, {len(group_list)} groups, {len(universe)} items, {len(rows)} files, "
          f"{statuses}", flush=True)


def manifest_row(r, role, edge=None):
    row = {"role": role, "source": STEAM_SOURCE + r["archive"], "archive": r["archive"], "entry": r["path"],
           "index": r["index"], "size": r["size"],
           "sha256": r["sha256"], "version": r["version"], "vertexCount": r["vertexCount"],
           "indexCount": r["indexCount"], "weightsPerVertex": r["weightsPerVertex"], "lodCount": r["lodCount"],
           "meshletCount": r["meshletCount"], "cell": bucket_key(r), "tags": sorted(secondary_tags(r)),
           "probe": r["probe"]}
    if edge:
        row["reason"] = "edge " + edge
    return row


def decline_controls(args):
    """Named decline controls, resolved from the controls census when present."""
    controls_path = os.path.join(os.path.dirname(args.records), "controls.jsonl.gz")
    picks = {}
    deep = {}
    if os.path.exists(controls_path):
        for c in iter_records(controls_path):
            ext = c["ext"]
            if c["probe"]["kind"] != "NotAModel":
                continue
            if ext in ("nif", "mat", "cdb", "af", "rig", "btd", "dat") and ext not in picks:
                picks[ext] = c
            # The controls that pass the version dword: each is rejected only by a deeper structural check, so each
            # pins that check (a probe without it would claim the entry).
            reason = c["probe"]["reason"]
            if reason != "version" and reason not in deep:
                deep[reason] = c
    result = [{"kind": "not-a-model:" + ext, "archive": c["archive"], "entry": c["path"], "size": c["size"],
               "expect": "NotAModel", "probeReason": c["probe"]["reason"]} for ext, c in sorted(picks.items())]
    result += [{"kind": "not-a-model:passes-version:" + reason, "archive": c["archive"], "entry": c["path"],
                "size": c["size"], "expect": "NotAModel", "probeReason": reason}
               for reason, c in sorted(deep.items())]
    result.append({"kind": "synthetic:truncated", "expect": "Unsupported (truncated) or NotAModel; read throws",
                   "rule": "every truncation point of the smallest cover file (selfcheck)"})
    result.append({"kind": "synthetic:version3", "expect": "NotAModel", "rule": "version dword set to 3"})
    result.append({"kind": "synthetic:trailing-byte", "expect": "NotAModel; read throws", "rule": "one byte appended"})
    result.append({"kind": "synthetic:index-out-of-range", "expect": "NotAModel; read throws",
                   "rule": "one index set to vertexCount"})
    return result


# --------------------------------------------------------------------------------------------------------------------
# verify
# --------------------------------------------------------------------------------------------------------------------

def verify(args):
    with open(args.manifest, encoding="utf-8") as handle:
        manifest = json.load(handle)
    archives = {}
    report = {"rows": [], "failures": 0}
    for row in manifest["files"] + [c for c in manifest["declineControls"] if c.get("archive")]:
        name = row["archive"]
        if name not in archives:
            archives[name] = Ba2Archive(os.path.join(args.data, name))
        archive = archives[name]
        matches = [e for e in archive.entries if e.path == row["entry"]]
        status = {"archive": name, "entry": row["entry"]}
        if len(matches) != 1:
            status["error"] = f"{len(matches)} entries with that path"
            report["failures"] += 1
        else:
            with open(archive.path, "rb") as handle:
                data = archive.read(handle, matches[0])
            sha = hashlib.sha256(data).hexdigest()
            status["size"] = len(data)
            status["sha256"] = sha
            if "sha256" in row:
                status["ok"] = sha == row["sha256"] and len(data) == row["size"]
                if not status["ok"]:
                    report["failures"] += 1
                if row is manifest["files"][0]:
                    altered = row["sha256"][:-1] + ("0" if row["sha256"][-1] != "0" else "1")
                    report["control"] = {"alteredDigitDetected": altered != sha, "entry": row["entry"]}
        report["rows"].append(status)
    report["passed"] = report["failures"] == 0 and report.get("control", {}).get("alteredDigitDetected", False)
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "verify.json"), "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=1)
    print(f"verify: {len(report['rows'])} rows, {report['failures']} failures, control "
          f"{report.get('control')}, passed={report['passed']}", flush=True)
    return 0 if report["passed"] else 1


# --------------------------------------------------------------------------------------------------------------------
# selfcheck (synthetic writer, truncations, controls)
# --------------------------------------------------------------------------------------------------------------------

def write_mesh(version, indices, scale, positions, uv0=None, uv1=None, colors=None, normals=None, tangents=None,
               weights_per_vertex=0, weights=None, lods=None, meshlets=None, cull=None):
    """An independent writer of the layout, for round trips (positions are int16 triples)."""
    b = bytearray()
    b += struct.pack("<II", version, len(indices)) + struct.pack(f"<{len(indices)}H", *indices)
    b += struct.pack("<fII", scale, weights_per_vertex, len(positions))
    for x, y, z in positions:
        b += struct.pack("<hhh", x, y, z)
    for arr in (uv0, uv1):
        arr = arr or []
        b += struct.pack("<I", len(arr)) + b"".join(struct.pack("<HH", u, v) for u, v in arr)
    arr = colors or []
    b += struct.pack("<I", len(arr)) + b"".join(bytes(c) for c in arr)
    for arr in (normals, tangents):
        arr = arr or []
        b += struct.pack("<I", len(arr)) + b"".join(struct.pack("<I", v) for v in arr)
    arr = weights or []
    b += struct.pack("<I", len(arr)) + b"".join(struct.pack("<HH", bone, w) for bone, w in arr)
    if version != 0:
        lods = lods or []
        b += struct.pack("<I", len(lods))
        for lod in lods:
            b += struct.pack("<I", len(lod)) + struct.pack(f"<{len(lod)}H", *lod)
    arr = meshlets or []
    b += struct.pack("<I", len(arr)) + b"".join(struct.pack("<4I", *m) for m in arr)
    arr = cull or []
    b += struct.pack("<I", len(arr)) + b"".join(struct.pack("<6f", *c) for c in arr)
    return bytes(b)


def pack_dec4(x, y, z, w):
    q = [int(round((c + 1.0) * DEC10)) for c in (x, y, z)]
    return q[0] | (q[1] << 10) | (q[2] << 20) | (w << 30)


def synthetic_samples():
    positions = [(0, 0, 0), (32767, 0, 0), (0, 32767, 0), (0, 0, -32767)]
    half_one = 0x3C00
    common = dict(indices=[0, 1, 2, 0, 2, 3], scale=2.5, positions=positions,
                  uv0=[(0, 0), (half_one, 0), (0, half_one), (half_one, half_one)],
                  colors=[(10, 20, 30, 255)] * 4, normals=[pack_dec4(0, 0, 1, 1)] * 4,
                  tangents=[pack_dec4(1, 0, 0, 3), pack_dec4(1, 0, 0, 0), pack_dec4(1, 0, 0, 3), pack_dec4(1, 0, 0, 0)],
                  meshlets=[(4, 0, 2, 0)], cull=[(1.25, 1.25, -1.25, 1.25, 1.25, 1.25)])
    # Retail pairs the meshlet tail with normal W 1 and its absence with normal W 0 (two-way, whole corpus).
    tailless = write_mesh(2, lods=[], **dict(common, normals=[pack_dec4(0, 0, 1, 0)] * 4))
    tailless = tailless[:len(tailless) - 8 - 16 - 24]
    return {
        "v2-no-tail": tailless,
        "v0": write_mesh(0, **common),
        "v1-lod": write_mesh(1, lods=[[0, 1, 2]], **common),
        "v2-weights-uv1": write_mesh(2, uv1=[(0, 0)] * 4, weights_per_vertex=2,
                                     weights=[(0, 65535), (1, 0)] * 4, lods=[], **common),
    }


def selfcheck(args):
    checks = []

    def expect(name, condition, detail=None):
        checks.append({"check": name, "passed": bool(condition), "detail": detail})

    for name, data in synthetic_samples().items():
        m = decode(data)
        expect(f"{name}: exact consumption", m["trailing"] == 0)
        p = positions_metres(m)
        expect(f"{name}: position scale", abs(p[1, 0] - 2.5) < 1e-6 and abs(p[3, 2] + 2.5) < 1e-6,
               [p[1, 0], p[3, 2]])
        n, _ = dec4(m["normals"])
        expect(f"{name}: dec4 unit normal", abs(np.linalg.norm(n[0]) - 1) < 0.002, float(np.linalg.norm(n[0])))
        _, tw = dec4(m["tangents"])
        expect(f"{name}: tangent W kept raw", list(tw) == [3, 0, 3, 0], [int(x) for x in tw])
        expect(f"{name}: probe Confirmed", probe(data, True, len(data))["confidence"] == "Confirmed",
               probe(data, True, len(data)))
        if name == "v2-weights-uv1":
            r = measure(m)
            expect(f"{name}: weight sums 65535 and the swapped reading fails",
                   r["weightSumExact65535"] == 4 and r["weightSumExact65535IfSwapped"] == 0,
                   [r["weightSumExact65535"], r["weightSumExact65535IfSwapped"]])
        classes = {}
        boundary = m["offsets"]["meshlets"]
        for cut in range(len(data)):
            verdict = probe(data[:cut], True, cut)
            label = verdict["kind"] + ("/" + verdict["reason"] if verdict["reason"] else "")
            classes[label] = classes.get(label, 0) + 1
            try:
                decode(data[:cut])
                refused = False
            except MeshError:
                refused = True
            if cut == boundary and m["tail"]:
                # A cut exactly where the tail starts walks as a tail-less file; only the normal-W rule refuses it,
                # so the rule's control (the probe without it) must read the same cut as Supported.
                without = probe(data[:cut], True, cut, tail_rule=False)
                expect(f"{name}: a cut at the tail boundary ({cut}) is refused by the normal-W rule and read as "
                       f"tail-less without it (control)",
                       verdict["kind"] == "Unsupported" and without["kind"] == "Supported", [verdict, without])
                continue
            if not refused or verdict["kind"] == "Supported":
                expect(f"{name}: truncation at {cut} refused", False, verdict)
        expect(f"{name}: every other truncation refused by probe and decode", True, classes)
        padded = data + bytes(1)
        expect(f"{name}: trailing byte refused by the probe", probe(padded, True, len(padded))["kind"] != "Supported",
               probe(padded, True, len(padded)))
        try:
            refused = decode(padded)["trailing"] == 1
        except MeshError:
            refused = True  # on a tail-less file the extra byte is a truncated meshlet count
        expect(f"{name}: trailing byte refused by the decoder", refused)
        v3 = struct.pack("<I", 3) + data[4:]
        expect(f"{name}: version 3 NotAModel", probe(v3, True, len(v3))["kind"] == "NotAModel")
        bad = bytearray(data)
        struct.pack_into("<H", bad, 8, 4)
        expect(f"{name}: index = vertexCount NotAModel", probe(bytes(bad), True, len(bad))["kind"] == "NotAModel")
    # The smallest valid tail-less streams are admitted by the length bound, and one byte less is not (the bound
    # is exactly the smallest layout: version 2 with one vertex is 60 bytes, version 0 with two is 62).
    for version, vertices, size in ((2, 1, 60), (1, 1, 60), (0, 2, 62)):
        data = minimal_stream(version, vertices)
        verdict = probe(data, True, len(data))
        decoded = decode(data)
        cut = probe(data[:-1], True, len(data) - 1)
        expect(f"the smallest v{version} stream ({size} B) decodes and is Supported, Confirmed, no meshlet tail; its "
               f"{size - 1}-byte cut is not Supported",
               len(data) == size and decoded["trailing"] == 0 and not decoded["tail"]
               and verdict["kind"] == "Supported" and verdict["confidence"] == "Confirmed"
               and verdict["reason"] == "no meshlet tail" and cut["kind"] != "Supported", [len(data), verdict, cut])
        at_bound = probe(data, True, 8 + 6 + min_bytes_after_indices(version))
        below = probe(data, True, 8 + 6 + min_bytes_after_indices(version) - 1)
        expect(f"v{version}: a declared length at the bound is admitted and one below it is NotAModel at the bound",
               at_bound["kind"] == "Supported" and below["reason"] == "indexCount>length", [at_bound, below])
    # The min/max reading of cull data must fail on the synthetic record the center+extent reading passes.
    m = decode(synthetic_samples()["v0"])
    r = measure(m)
    expect("cull: center+extent contains the meshlet, min/max does not",
           r.get("cullContainsMeshlet") == 1 and r.get("cullContainsIfMinMax") == 0,
           [r.get("cullContainsMeshlet"), r.get("cullContainsIfMinMax")])
    if args.manifest and args.data:
        with open(args.manifest, encoding="utf-8") as handle:
            manifest = json.load(handle)
        smallest = sorted(manifest["files"], key=lambda row: row["size"])[:3]
        for row in smallest:
            archive = Ba2Archive(os.path.join(args.data, row["archive"]))
            entry = [e for e in archive.entries if e.path == row["entry"]][0]
            with open(archive.path, "rb") as handle:
                data = archive.read(handle, entry)
            classes = {}
            leaked = []
            decoded = decode(data)
            boundary = decoded["offsets"]["meshlets"]
            for cut in range(len(data)):
                verdict = probe(data[:cut], True, cut)
                label = verdict["kind"] + ("/" + verdict["reason"] if verdict["reason"] else "")
                classes[label] = classes.get(label, 0) + 1
                if verdict["kind"] == "Supported":
                    leaked.append(cut)
            control = probe(data[:boundary], True, boundary, tail_rule=False)["kind"] if decoded["tail"] else None
            expect(f"retail truncations of {row['entry']} ({row['size']} B) never Supported; the tail-boundary cut "
                   f"{boundary} reads Supported without the normal-W rule (control)",
                   not leaked and (control is None or control == "Supported"),
                   {"classes": classes, "leaked": leaked[:10], "boundaryWithoutRule": control})
    report = {"checks": checks, "passed": all(c["passed"] for c in checks)}
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "selfcheck.json"), "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=1, default=str)
    failed = [c for c in checks if not c["passed"]]
    print(f"selfcheck: {len(checks)} checks, {len(failed)} failed", flush=True)
    for c in failed:
        print("  FAILED", c["check"], c["detail"])
    return 0 if report["passed"] else 1


# --------------------------------------------------------------------------------------------------------------------
# dump
# --------------------------------------------------------------------------------------------------------------------

def dump(args):
    archive = Ba2Archive(os.path.join(args.data, args.archive))
    entry = [e for e in archive.entries if e.path == args.entry][0]
    with open(archive.path, "rb") as handle:
        data = archive.read(handle, entry)
    m = decode(data)
    out = {"archive": archive.name, "entry": entry.path, "size": len(data),
           "sha256": hashlib.sha256(data).hexdigest(), "offsets": m["offsets"], "facts": measure(m)}
    if args.arrays:
        normals = dec4(m["normals"]) if m["normalsCount"] else (np.zeros((0, 3)), np.zeros(0))
        tangents = dec4(m["tangents"]) if m["tangentsCount"] else (np.zeros((0, 3)), np.zeros(0))
        out["arrays"] = {
            "indices": m["indices"].tolist(),
            "positionsQ": m["positions"].tolist(),
            "positionsMetres": positions_metres(m).tolist(),
            "uv0": half_to_float(m["uv0"]).tolist() if m["uv0Count"] else [],
            "uv1": half_to_float(m["uv1"]).tolist() if m["uv1Count"] else [],
            "colorsBGRA": m["colors"].tolist(),
            "normals": normals[0].tolist(), "normalW": normals[1].tolist(),
            "tangents": tangents[0].tolist(), "tangentW": tangents[1].tolist(),
            "weights": m["weights"].tolist(),
            "lods": [x.tolist() for x in m["lods"]],
            "meshlets": m["meshlets"].tolist(),
            "cull": m["cull"].tolist(),
        }
    json.dump(out, sys.stdout, indent=1)
    sys.stdout.write("\n")


def frames(args):
    """Measures the tangent frame against the UV-derived frame, and the raw codes of near-zero Dec4 vectors.

    Per sampled file with UV0, normals and tangents: per-vertex UV tangents and bitangents are accumulated from the
    triangles (raw UVs, no V flip); a stored tangent AGREES in direction when dot(T_stored, T_uv) > 0, and its W
    reading agrees when sign(dot(cross(N, T_stored), B_uv)) equals the mapped W. Both W mappings are scored, so the
    wrong one is the control. Vertices whose UV frame is degenerate are counted separately.
    """
    report = {"files": 0, "vertices": 0, "degenerate": 0, "directionAgrees": 0, "wMaps": {},
              "nearZeroCodes": {}, "normalNearZeroCodes": {}, "sample": args.every}
    maps = {"W3=+1,W0=-1": {3: 1.0, 0: -1.0}, "W0=+1,W3=-1": {3: -1.0, 0: 1.0}}
    for name in maps:
        report["wMaps"][name] = 0
    archives = {}
    count = 0
    for r in iter_records(args.records):
        count += 1
        if "decodeError" in r:
            continue
        near = r.get("tangentNearZero", 0) or r.get("normalNearZero", 0)
        if count % args.every and not near:
            continue
        if not (r["uv0Count"] and r["normalsCount"] and r["tangentsCount"]):
            continue
        name = r["archive"]
        if name not in archives:
            archives[name] = (Ba2Archive(os.path.join(args.data, name)), None)
            archives[name] = (archives[name][0], {e.index: e for e in archives[name][0].entries})
        archive, index = archives[name]
        with open(archive.path, "rb") as handle:
            data = archive.read(handle, index[r["index"]])
        m = decode(data)
        normals, _ = dec4(m["normals"])
        tangents, tw = dec4(m["tangents"])
        raw_t = m["tangents"].astype(np.uint32)
        for label, values, raw, table in (("t", tangents, raw_t, "nearZeroCodes"),
                                          ("n", normals, m["normals"].astype(np.uint32), "normalNearZeroCodes")):
            small = np.linalg.norm(values, axis=1) < 0.01
            for code in raw[small][:64]:
                key = f"{code & 0x3FF},{(code >> 10) & 0x3FF},{(code >> 20) & 0x3FF},w{code >> 30}"
                report[table][key] = report[table].get(key, 0) + 1
        if count % args.every:
            continue
        report["files"] += 1
        p = positions_metres(m)
        # One rounding (the exact product rounded once to float32) against the legacy renderer's two roundings
        # (float32 scale / 32767, then the float32 product): the discriminating control for hop A1's position pin.
        once = p.astype(np.float32)
        step = np.float32(np.float32(m["scale"]) / np.float32(SNORM))
        twice = (m["positions"].astype(np.float32) * step).astype(np.float32)
        differs = np.count_nonzero(once != twice)
        report["positionComponents"] = report.get("positionComponents", 0) + int(once.size)
        report["positionComponentsTwoRoundingsDiffer"] = report.get("positionComponentsTwoRoundingsDiffer", 0) + \
            int(differs)
        recovered = np.rint(once.astype(np.float64) * SNORM / float(np.float32(m["scale"]))).astype(np.int64)
        report["positionComponentsNotInvertible"] = report.get("positionComponentsNotInvertible", 0) + \
            int(np.count_nonzero(recovered != m["positions"].astype(np.int64)))
        uv = half_to_float(m["uv0"]).astype(np.float64)
        tris = m["indices"][:len(m["indices"]) - len(m["indices"]) % 3].reshape(-1, 3).astype(np.int64)
        if not len(tris):
            continue
        p0, p1, p2 = p[tris[:, 0]], p[tris[:, 1]], p[tris[:, 2]]
        w0, w1, w2 = uv[tris[:, 0]], uv[tris[:, 1]], uv[tris[:, 2]]
        e1, e2 = p1 - p0, p2 - p0
        d1, d2 = w1 - w0, w2 - w0
        det = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
        ok = np.abs(det) > 1e-12
        inv = np.where(ok, 1.0 / np.where(ok, det, 1.0), 0.0)
        tu = (e1 * d2[:, 1:2] - e2 * d1[:, 1:2]) * inv[:, None]
        bv = (e2 * d1[:, 0:1] - e1 * d2[:, 0:1]) * inv[:, None]
        nv = m["vertexCount"]
        acc_t = np.zeros((nv, 3))
        acc_b = np.zeros((nv, 3))
        for corner in range(3):
            np.add.at(acc_t, tris[:, corner], tu)
            np.add.at(acc_b, tris[:, corner], bv)
        valid = (np.linalg.norm(acc_t, axis=1) > 1e-9) & (np.linalg.norm(acc_b, axis=1) > 1e-9) & \
                (np.linalg.norm(tangents, axis=1) > 0.01) & (np.linalg.norm(normals, axis=1) > 0.01)
        report["vertices"] += int(nv)
        report["degenerate"] += int(nv - np.count_nonzero(valid))
        report["directionAgrees"] += int(np.count_nonzero(np.einsum("ij,ij->i", tangents, acc_t)[valid] > 0))
        cross = np.cross(normals, tangents)
        handed = np.sign(np.einsum("ij,ij->i", cross, acc_b))
        for label, table in maps.items():
            mapped = np.array([table.get(int(x), 0.0) for x in range(4)])[tw]
            report["wMaps"][label] += int(np.count_nonzero((handed == mapped) & valid))
    report["validVertices"] = report["vertices"] - report["degenerate"]
    for table in ("nearZeroCodes", "normalNearZeroCodes"):
        report[table] = dict(sorted(report[table].items(), key=lambda kv: -kv[1])[:20])
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "frames.json"), "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=1)
    print(json.dumps({k: v for k, v in report.items() if k not in ("nearZeroCodes", "normalNearZeroCodes")}),
          flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("census")
    p.add_argument("--data", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--limit", type=int, default=0)
    p = sub.add_parser("cover")
    p.add_argument("--records", required=True)
    p.add_argument("--out", required=True)
    p = sub.add_parser("verify")
    p.add_argument("--manifest", required=True)
    p.add_argument("--data", required=True)
    p.add_argument("--out", required=True)
    p = sub.add_parser("selfcheck")
    p.add_argument("--out", required=True)
    p.add_argument("--manifest")
    p.add_argument("--data")
    p = sub.add_parser("dump")
    p.add_argument("--data", required=True)
    p.add_argument("--archive", required=True)
    p.add_argument("--entry", required=True)
    p.add_argument("--arrays", action="store_true")
    p = sub.add_parser("frames")
    p.add_argument("--records", required=True)
    p.add_argument("--data", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--every", type=int, default=200)
    args = parser.parse_args()
    handler = {"census": census, "cover": cover, "verify": verify, "selfcheck": selfcheck, "dump": dump,
               "frames": frames}[args.command]
    return handler(args) or 0


if __name__ == "__main__":
    sys.exit(main())
