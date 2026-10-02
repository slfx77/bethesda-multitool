# SPDX-License-Identifier: 0BSD
"""Cut-2 Starfield ``.mesh`` cover tool: the checked-in cover manifest with SHA-256 pins, its verification, and the
A-hop expectations the Bucket-B tests compare BMT's ``StarfieldMeshModelReader`` against.

Every byte this tool reads comes through the independent GNRL BA2 reader and ``.mesh`` decoder of
``starfield_mesh_probe.py`` (same directory). Nothing here reads, imports or consumes a result of BMT's C# code, so
the expectations are a second decoding of the same bytes (plan ``docs/design/cut2-starfield-mesh-reader-plan-20260928.md``
sections 7 and 8).

Subcommands (one process at a time; every one is read-only over the Starfield install):

    manifest     --records <census-records.jsonl.gz> --out <manifest.json> [--data <Starfield Data dir>]
                 The exact MILP joint cover of the probe's ``cover`` (every value and every observed pair of values of
                 the six cell dimensions plus every file-level tag; minimum file count, then minimum bytes), the edge
                 files, and the decline controls drawn from ``controls.jsonl.gz`` beside the records: one retail entry
                 per control extension and one per deeper probe check, never the same entry twice. Every retail row is
                 re-read from its archive so that its size, SHA-256 and directory index are pinned from the bytes, not
                 copied from the census. Writes the manifest (schema ``cut2-starfield-mesh-cover/2``) and a text report
                 beside it.
    verify       --manifest <manifest.json> --out <receipt dir> [--data <dir>]
                 Re-reads every file row (primary source and every ``alsoIn``) and every retail decline control; size
                 and SHA-256 must match. The built-in control alters one SHA-256 digit and must be detected.
    expectations --manifest <manifest.json> --out <expectations.jsonl> [--data <dir>]
                 One JSON line per manifest file and retail decline control, keyed by SHA-256: the probe verdict under
                 BMT's bounds, the section list, the coverage census, the header facts, and the SHA-256 of every typed
                 array under the reader's numeric routes (see ``DIGEST_RULES``), plus the legacy-route and swapped-W
                 control values. The typed tangent w is glTF's handedness, the stored sign negated (see
                 ``tangent_w``), and the UV frame the ``frame`` counts score it against is glTF's (-dP/dv).
    selfcheck    --out <receipt dir>
                 The expectation rules on synthetic meshes: each control (a flipped index byte, the legacy two-rounding
                 position route, the engine's tangent W map, one color byte, the BGRA order, one LOD index) must change
                 exactly the digest it targets; the glTF tangent sign agrees with the glTF UV frame on both W codes; a
                 tail holding no records digests its meshlets and cull as the SHA-256 of no bytes.

``--data`` defaults to the Starfield Data folder found the way ``tests/BethesdaMultitool.Tests/Helpers/RealAssetPaths.cs``
finds a Steam game (every fixed drive's Steam library).

Standard library plus numpy (and scipy for ``manifest``, through the probe's MILP).
"""

import argparse
import gzip
import hashlib
import json
import os
import string
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import starfield_mesh_probe as probe_module  # noqa: E402  (the independent decoder beside this file)

SCHEMA = "cut2-starfield-mesh-cover/2"
EXPECTATIONS_SCHEMA = "cut2-starfield-mesh-expectations/1"
PROBE_BYTES = probe_module.PROBE_BYTES
STEAM_SOURCE = probe_module.STEAM_SOURCE

# The probe bounds BMT's StarfieldMeshModelProbe applies (plan decision D7): weightsPerVertex and lodCount at most 8.
MAX_WEIGHTS = 8
MAX_LODS = 8

# Control extensions: one retail NotAModel entry each (plan section 8, "Decline controls").
CONTROL_EXTENSIONS = ("af", "btd", "cdb", "dat", "nif", "rig")

DIGEST_RULES = {
    "indices": "int32 little-endian, the main index list as stored",
    "positions": "float32 little-endian x, y, z per vertex; fl32((q * scale) / 32767) evaluated in binary64",
    "normals": "float32 little-endian x, y, z per vertex; each channel fl32((2v - 1023) / 1023) in binary64",
    "tangents": "float32 little-endian x, y, z, w per vertex; channels as normals, w = glTF handedness = -1 for "
                "code 3, +1 for code 0 (the stored sign negated: the stored bitangent sign puts cross(N, T) x w along "
                "+dP/dv of the raw DirectX UVs, glTF's bitangent runs along -dP/dv)",
    "uv0": "float32 little-endian u, v per vertex; the half value exactly, a non-finite component read as +0",
    "uv1": "as uv0, for the second set",
    "starfield.uv0.raw": "uint16 little-endian u, v half bits per vertex (present only when a UV0 component is not finite)",
    "starfield.uv1.raw": "as starfield.uv0.raw, for the second set",
    "starfield.color": "uint8 R, G, B, A per vertex (the stored B, G, R, A relaid)",
    "starfield.bone.k": "uint16 little-endian bone index of slot k per vertex, as stored",
    "starfield.weight.k": "uint16 little-endian weight of slot k per vertex, as stored",
    "lod.k": "int32 little-endian, LOD index list k (1-based) as stored",
    "meshlets": "uint32 little-endian vertexCount, vertexOffset, triangleCount, triangleOffset per record; present "
                "whenever the tail is (a tail with no records digests as the SHA-256 of no bytes)",
    "cull": "float32 bits little-endian, center xyz then extent xyz per record; present whenever the tail is",
}


# --------------------------------------------------------------------------------------------------------------------
# Locating the install and reading entries
# --------------------------------------------------------------------------------------------------------------------

def resolve_data_dir(explicit):
    """The Starfield Data folder: the explicit argument, else every fixed drive's Steam library in RealAssetPaths order."""
    if explicit:
        return explicit
    for letter in string.ascii_uppercase:
        root = f"{letter}:/"
        if not os.path.isdir(root):
            continue
        for library in (("SteamLibrary", "steamapps", "common"), ("SteamLibrary", "SteamApps", "common"),
                        ("Steam", "steamapps", "common"), ("Program Files (x86)", "Steam", "steamapps", "common")):
            candidate = os.path.join(root, *library, "Starfield", "Data")
            if os.path.isdir(candidate):
                return candidate
    raise SystemExit("The Starfield Data folder was not found; pass --data.")


class ArchiveCache:
    """Opens each GNRL archive once and reads entries by directory index, checking the path at that index."""

    def __init__(self, data_dir):
        self.data_dir = data_dir
        self.archives = {}

    def archive(self, name):
        if name not in self.archives:
            self.archives[name] = probe_module.Ba2Archive(os.path.join(self.data_dir, name))
        return self.archives[name]

    def read(self, name, entry_path, index=None):
        """The entry's decoded bytes, or raises when the index names a different path or no entry has the path."""
        archive = self.archive(name)
        if index is not None:
            if index < 0 or index >= len(archive.entries):
                raise LookupError(f"{name}: index {index} is out of range")
            entry = archive.entries[index]
            if entry.path != entry_path:
                raise LookupError(f"{name}: index {index} names {entry.path!r}, not {entry_path!r}")
        else:
            matches = [e for e in archive.entries if e.path == entry_path]
            if len(matches) != 1:
                raise LookupError(f"{name}: {len(matches)} entries named {entry_path!r}")
            entry = matches[0]
        with open(archive.path, "rb") as handle:
            return archive.read(handle, entry), entry.index


def sha256(data):
    return hashlib.sha256(data).hexdigest()


# --------------------------------------------------------------------------------------------------------------------
# manifest
# --------------------------------------------------------------------------------------------------------------------

def manifest(args):
    data_dir = resolve_data_dir(args.data)
    cache = ArchiveCache(data_dir)
    groups = {}
    first_source = {}
    cells = {}
    edge = {}
    census = {"records": 0, "unique": 0, "archives": {}, "decodeErrors": 0}
    shas = set()
    for r in probe_module.iter_records(args.records):
        census["records"] += 1
        census["archives"][r["archive"]] = census["archives"].get(r["archive"], 0) + 1
        sha = r["sha256"]
        shas.add(sha)
        known = first_source.get(sha)
        if known is None or (not known["archive"].startswith("Starfield - ") and r["archive"].startswith("Starfield - ")):
            first_source[sha] = r
        if "decodeError" in r:
            census["decodeErrors"] += 1
            continue
        cell = probe_module.bucket_key(r)
        cells[cell] = cells.get(cell, 0) + 1
        key = (cell, frozenset(probe_module.secondary_tags(r)))
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
    census["unique"] = len(shas)
    group_list = []
    universe = set()
    for (cell, tags), ((size, sha), r) in sorted(groups.items(), key=lambda kv: kv[1][0]):
        items = probe_module.cell_items(cell) | set(tags)
        universe |= items
        group_list.append((items, size, sha, r))
    chosen, statuses = probe_module.solve_cover([(g[0], g[1], g[2]) for g in group_list], universe)
    rows = []
    selected = {}
    covered = set()
    for number, n in enumerate(chosen):
        items, _, sha, r = group_list[n]
        row = file_row(first_source[sha], "cover", f"cover {number + 1:02d} {probe_module.bucket_key(r)}")
        rows.append(row)
        selected[sha] = row
        covered |= items
    for key in sorted(edge):
        _, r = edge[key]
        if r["sha256"] in selected:
            selected[r["sha256"]].setdefault("edges", []).append(key)
            continue
        row = file_row(first_source[r["sha256"]], "edge", f"edge {key}")
        row["edges"] = [key]
        rows.append(row)
        selected[r["sha256"]] = row
    # Every other occurrence of each chosen payload, so a resolver can fall back and the manifest says where else the
    # bytes live (a patch archive repeating a path, a DLC repeating base-game bytes).
    for r in probe_module.iter_records(args.records):
        row = selected.get(r["sha256"])
        if row is None or (r["archive"] == row["archive"] and r["path"] == row["entry"]):
            continue
        row["alsoIn"].append({"source": STEAM_SOURCE + r["archive"], "archive": r["archive"], "entry": r["path"],
                              "index": r["index"]})
    # Pin every file row from the bytes themselves.
    for row in rows:
        data, index = cache.read(row["archive"], row["entry"], row["index"])
        if index != row["index"] or len(data) != row["size"] or sha256(data) != row["sha256"]:
            raise SystemExit(f"{row['name']}: the archive bytes disagree with the census row")
    controls = decline_controls(os.path.join(os.path.dirname(args.records), "controls.jsonl.gz"), cache)
    document = {
        "schema": SCHEMA,
        "generator": "tools/scripts/gate2/starfield_mesh_cover.py manifest",
        "plan": "docs/design/cut2-starfield-mesh-reader-plan-20260928.md, section 8",
        "census": census,
        "rule": "exact MILP joint cover over every value and every observed pair of values of (version, "
                "weightsPerVertex, lodCount, colors, UV1, meshlet-count bucket) plus every file-level tag; minimum "
                "file count, then minimum bytes; representatives are the smallest file (then SHA-256 order) of each "
                "(cell, tag set) group; then the edge files",
        "solver": statuses,
        "cellCount": len(cells),
        "groupCount": len(group_list),
        "itemCount": len(universe),
        "uncoveredItems": sorted(universe - covered),
        "files": rows,
        "declineControls": controls,
        "syntheticControls": [
            {"kind": "truncated", "expect": "never Supported by the probe; the read throws",
             "rule": "every truncation point of a builder mesh and of the smallest cover files; the tail-boundary "
                     "cut is refused only by the normal-W rule, which is its control"},
            {"kind": "version3", "expect": "NotAModel", "rule": "version dword set to 3"},
            {"kind": "trailing-byte", "expect": "NotAModel; the read throws", "rule": "one byte appended"},
            {"kind": "index-out-of-range", "expect": "NotAModel; the read throws",
             "rule": "one index set to vertexCount"},
        ],
    }
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, indent=1)
        handle.write("\n")
    report = os.path.splitext(args.out)[0] + "-report.txt"
    with open(report, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(f"census {census['records']} records, {census['unique']} payloads; cells {len(cells)}, groups "
                     f"{len(group_list)}, items {len(universe)}, uncovered {len(universe - covered)}, files "
                     f"{len(rows)}; solver {statuses}\n")
        for row in rows:
            handle.write(f"{row['role']:6s} {row['size']:>9} {row['sha256'][:16]} {row['archive']} :: {row['entry']}"
                         f"  [{row['name']}]\n")
        for control in controls:
            handle.write(f"control {control['size']:>9} {control['sha256'][:16]} {control['archive']} :: "
                         f"{control['entry']}  [{control['name']}]\n")
    print(f"manifest: {len(rows)} files, {len(controls)} retail controls, {len(universe)} items, "
          f"{len(universe - covered)} uncovered, {statuses}", flush=True)


def file_row(r, role, name):
    return {"role": role, "name": name, "source": STEAM_SOURCE + r["archive"], "archive": r["archive"],
            "entry": r["path"], "index": r["index"], "size": r["size"], "sha256": r["sha256"],
            "version": r["version"], "vertexCount": r["vertexCount"], "indexCount": r["indexCount"],
            "weightsPerVertex": r["weightsPerVertex"], "lodCount": r["lodCount"], "meshletCount": r["meshletCount"],
            "tail": r["tail"], "cell": probe_module.bucket_key(r), "tags": sorted(probe_module.secondary_tags(r)),
            "alsoIn": []}


def decline_controls(controls_path, cache):
    """One retail NotAModel entry per probe check beyond the version dword, then one per control extension, never one
    entry twice. ``passesVersion`` records whether the entry's first dword is a legal version (0 to 2), so a consumer can
    tell the controls a version-only probe would claim from the ones it already refuses."""
    deep = {}
    by_extension = {}
    for c in probe_module.iter_records(controls_path):
        verdict = c["probe"]
        if verdict["kind"] != "NotAModel" or c.get("probeStrict", "NotAModel") != "NotAModel":
            continue
        reason = verdict["reason"]
        if reason != "version" and reason not in deep:
            deep[reason] = c
        by_extension.setdefault(c["ext"], []).append(c)
    used = set()
    result = []
    for reason, c in sorted(deep.items()):
        used.add((c["archive"], c["path"]))
        result.append(control_row(c, "check:" + reason, cache))
    for ext in CONTROL_EXTENSIONS:
        pick = next((c for c in by_extension.get(ext, []) if (c["archive"], c["path"]) not in used), None)
        if pick is None:
            raise SystemExit(f"no unused retail NotAModel control with extension .{ext}")
        used.add((pick["archive"], pick["path"]))
        result.append(control_row(pick, "extension:" + ext, cache))
    return result


def control_row(c, kind, cache):
    data, index = cache.read(c["archive"], c["path"], c["index"])
    first = struct.unpack_from("<I", data, 0)[0] if len(data) >= 4 else None
    return {"role": "decline", "name": "control " + kind, "kind": kind, "source": STEAM_SOURCE + c["archive"],
            "archive": c["archive"], "entry": c["path"], "index": index, "size": len(data), "sha256": sha256(data),
            "expect": "NotAModel", "probeReason": c["probe"]["reason"], "probeStage": c["probe"]["stage"],
            "passesVersion": first is not None and first <= probe_module.MAX_VERSION}


# --------------------------------------------------------------------------------------------------------------------
# verify
# --------------------------------------------------------------------------------------------------------------------

def verify(args):
    cache = ArchiveCache(resolve_data_dir(args.data))
    with open(args.manifest, encoding="utf-8") as handle:
        document = json.load(handle)
    report = {"rows": [], "failures": 0}
    for row in document["files"] + document["declineControls"]:
        candidates = [{"archive": row["archive"], "entry": row["entry"], "index": row["index"]}] + \
                     [{"archive": o["archive"], "entry": o["entry"], "index": o["index"]} for o in row.get("alsoIn", [])]
        for number, candidate in enumerate(candidates):
            status = {"name": row["name"], "candidate": number, **candidate}
            try:
                data, _ = cache.read(candidate["archive"], candidate["entry"], candidate["index"])
                status["ok"] = len(data) == row["size"] and sha256(data) == row["sha256"]
            except LookupError as error:
                status["ok"] = False
                status["error"] = str(error)
            if not status["ok"]:
                report["failures"] += 1
            report["rows"].append(status)
    first = document["files"][0]
    data, _ = cache.read(first["archive"], first["entry"], first["index"])
    altered = first["sha256"][:-1] + ("0" if first["sha256"][-1] != "0" else "1")
    report["control"] = {"alteredDigitDetected": sha256(data) != altered, "name": first["name"]}
    report["passed"] = report["failures"] == 0 and report["control"]["alteredDigitDetected"]
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "verify.json"), "w", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, indent=1)
    print(f"verify: {len(report['rows'])} candidates, {report['failures']} failures, control {report['control']}, "
          f"passed={report['passed']}", flush=True)
    return 0 if report["passed"] else 1


# --------------------------------------------------------------------------------------------------------------------
# expectations
# --------------------------------------------------------------------------------------------------------------------

DEC4_TABLE = ((2 * np.arange(1024, dtype=np.int64) - 1023) / 1023.0).astype(np.float32)


def digest(array, dtype):
    return hashlib.sha256(np.ascontiguousarray(np.asarray(array), dtype=dtype).tobytes()).hexdigest()


def positions_route(m):
    """fl32((q * scale) / 32767) in binary64: the reader's route (plan D5)."""
    scale = float(np.float32(m["scale"]))
    return (m["positions"].astype(np.float64) * scale / 32767.0).astype(np.float32)


def positions_legacy(m):
    """The legacy renderer's route: float32 scale / 32767, then the float32 product (two roundings)."""
    step = np.float32(np.float32(m["scale"]) / np.float32(32767.0))
    return (m["positions"].astype(np.float32) * step).astype(np.float32)


def dec4_channels(codes):
    v = np.asarray(codes, dtype=np.uint32)
    return np.stack([DEC4_TABLE[v & 0x3FF], DEC4_TABLE[(v >> 10) & 0x3FF], DEC4_TABLE[(v >> 20) & 0x3FF]], axis=1)


def dec4_legacy(codes):
    v = np.asarray(codes, dtype=np.uint32)
    out = []
    for shift in (0, 10, 20):
        channel = ((v >> shift) & 0x3FF).astype(np.float32)
        out.append((channel / np.float32(511.5) - np.float32(1.0)).astype(np.float32))
    return np.stack(out, axis=1)


def tangent_w(codes, engine=False):
    """The typed tangent w per code: glTF's handedness (-1 for code 3, +1 for code 0), or with ``engine`` the stored
    sign as the engine reads it (+1 for code 3, -1 for code 0), which is the control. Codes 1 and 2 read NaN."""
    w = (np.asarray(codes, dtype=np.uint32) >> 30).astype(np.int64)
    three, zero = (np.float32(1.0), np.float32(-1.0)) if engine else (np.float32(-1.0), np.float32(1.0))
    return np.where(w == 3, three, np.where(w == 0, zero, np.float32(np.nan))).astype(np.float32)


def uv_values(bits):
    values = np.asarray(bits, dtype=np.uint16).view(np.float16).astype(np.float32)
    return np.where(np.isfinite(values), values, np.float32(0.0)).astype(np.float32)


def uv_nonfinite(bits):
    return int(np.count_nonzero(((np.asarray(bits, dtype=np.uint16) >> 10) & 0x1F) == 0x1F))


def sections(m, length):
    """The parse's section list in stream order: (name, offset, length, count); fixed fields count 1."""
    off = m["offsets"]
    result = [("version", 0, 4, 1), ("indices", off["indices"], 4 + 2 * m["indexCount"], m["indexCount"]),
              ("scale", off["scale"], 4, 1), ("weightsPerVertex", off["weightsPerVertex"], 4, 1),
              ("positions", off["positions"], 4 + 6 * m["vertexCount"], m["vertexCount"])]
    for key, width in (("uv0", 4), ("uv1", 4), ("colors", 4), ("normals", 4), ("tangents", 4), ("weights", 4)):
        count = m[key + "Count"]
        result.append((key, off[key], 4 + width * count, count))
    if m["version"] != 0:
        cursor = off["lods"] + 4
        lods = []
        for number, lod in enumerate(m["lods"], start=1):
            lods.append((f"lod:{number}", cursor, 4 + 2 * len(lod), len(lod)))
            cursor += 4 + 2 * len(lod)
        result.append(("lods", off["lods"], cursor - off["lods"], len(m["lods"])))
        result.extend(lods)
    if m["tail"]:
        result.append(("meshlets", off["meshlets"], 4 + 16 * m["meshletCount"], m["meshletCount"]))
        result.append(("cull", off["cull"], 4 + 24 * m["cullCount"], m["cullCount"]))
    end = result[-1][1] + result[-1][2]
    if end != length:
        raise ValueError(f"the section list ends at {end}, not at the stream end {length}")
    return [{"name": n, "offset": o, "length": l, "count": c} for n, o, l, c in result]


CENSUS_SECTIONS = ("indices", "positions", "uv0", "uv1", "colors", "normals", "tangents", "weights")


def census_identities(section_list):
    """The coverage census the reader must declare: every non-empty data section, LOD lists one by one."""
    names = []
    for s in section_list:
        name = s["name"]
        if (name in CENSUS_SECTIONS or name.startswith("lod:") or name in ("meshlets", "cull")) and s["count"] > 0:
            names.append(name)
    return names


def uv_frame(m, positions, normals, tangents, w):
    """The typed tangent handedness against glTF's UV-derived bitangent, in binary64 (plan section 0.2).

    glTF's tangent-space +Y is the top of the image, which for its top-left UV origin (the raw DirectX UVs, passed
    through unflipped) is -dP/dv; ``agree`` counts sign(dot(cross(N, T), -dP/dv)) == w and ``agreeSwapped`` the engine
    sign (-w) against the same frame.
    """
    if not (m["uv0Count"] and m["normalsCount"] and m["tangentsCount"]):
        return None
    tris = m["indices"][:len(m["indices"]) - len(m["indices"]) % 3].reshape(-1, 3).astype(np.int64)
    if not len(tris):
        return None
    p = positions.astype(np.float64)
    uv = uv_values(m["uv0"]).reshape(-1, 2).astype(np.float64)
    e1, e2 = p[tris[:, 1]] - p[tris[:, 0]], p[tris[:, 2]] - p[tris[:, 0]]
    d1, d2 = uv[tris[:, 1]] - uv[tris[:, 0]], uv[tris[:, 2]] - uv[tris[:, 0]]
    det = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
    ok = np.abs(det) > 1e-12
    inv = np.where(ok, 1.0 / np.where(ok, det, 1.0), 0.0)
    bv = -(e2 * d1[:, 0:1] - e1 * d2[:, 0:1]) * inv[:, None]  # -dP/dv, glTF's bitangent direction
    acc = np.zeros((m["vertexCount"], 3))
    for corner in range(3):
        np.add.at(acc, tris[:, corner], bv)
    n = normals.astype(np.float64)
    t = tangents.astype(np.float64)
    valid = (np.linalg.norm(acc, axis=1) > 1e-9) & (np.linalg.norm(t, axis=1) > 0.01) & (np.linalg.norm(n, axis=1) > 0.01)
    handed = np.sign(np.einsum("ij,ij->i", np.cross(n, t), acc))
    return {"valid": int(np.count_nonzero(valid)), "agree": int(np.count_nonzero((handed == w) & valid)),
            "agreeSwapped": int(np.count_nonzero((handed == -w) & valid))}


def file_expectation(data):
    """Everything hops A1, A2 and A3 compare for one mesh payload."""
    m = probe_module.decode(data)
    if m["trailing"]:
        raise ValueError(f"{m['trailing']} trailing bytes")
    nv = m["vertexCount"]
    section_list = sections(m, len(data))
    digests = {"indices": digest(m["indices"].astype(np.int64), "<i4")}
    positions = positions_route(m)
    digests["positions"] = digest(positions, "<f4")
    streams = []
    counts = {}
    controls = {}
    legacy = positions_legacy(m)
    controls["legacyPositionComponentsDiffer"] = int(np.count_nonzero(legacy != positions))
    controls["legacyPositionsDigest"] = digest(legacy, "<f4")
    normals = tangents = w = None
    if m["normalsCount"]:
        normals = dec4_channels(m["normals"])
        digests["normals"] = digest(normals, "<f4")
        legacy_normals = dec4_legacy(m["normals"])
        controls["legacyNormalComponentsDiffer"] = int(np.count_nonzero(legacy_normals != normals))
        controls["legacyNormalsDigest"] = digest(legacy_normals, "<f4")
        nw = (m["normals"].astype(np.uint32) >> 30)
        counts["normalW"] = [int(x) for x in np.bincount(nw, minlength=4)]
        sentinel = (m["normals"].astype(np.uint32) & 0x3FFFFFFF) == (511 | (511 << 10) | (511 << 20))
        counts["normalSentinelByW"] = [int(np.count_nonzero(sentinel & (nw == k))) for k in range(4)]
    if m["tangentsCount"]:
        tangents = dec4_channels(m["tangents"])
        tw = (m["tangents"].astype(np.uint32) >> 30)
        counts["tangentW"] = [int(x) for x in np.bincount(tw, minlength=4)]
        sentinel = (m["tangents"].astype(np.uint32) & 0x3FFFFFFF) == (511 | (511 << 10) | (511 << 20))
        counts["tangentSentinelByW"] = [int(np.count_nonzero(sentinel & (tw == k))) for k in range(4)]
        if counts["tangentW"][1] == 0 and counts["tangentW"][2] == 0:
            w = tangent_w(m["tangents"])
            digests["tangents"] = digest(np.concatenate([tangents, w[:, None]], axis=1), "<f4")
            controls["swappedTangentsDigest"] = digest(
                np.concatenate([tangents, tangent_w(m["tangents"], engine=True)[:, None]], axis=1), "<f4")
    for key, raw_name in (("uv0", "starfield.uv0.raw"), ("uv1", "starfield.uv1.raw")):
        if m[key + "Count"]:
            bits = m[key].reshape(-1).astype(np.uint16)
            digests[key] = digest(uv_values(bits), "<f4")
            nonfinite = uv_nonfinite(bits)
            counts[key + "NonFinite"] = nonfinite
            if nonfinite:
                # The set's exact half bits, in stream order: the raw UV0 stream precedes the raw UV1 stream.
                streams.append(raw_name)
                digests[raw_name] = digest(bits, "<u2")
    if m["colorsCount"]:
        bgra = m["colors"].reshape(-1, 4)
        rgba = bgra[:, [2, 1, 0, 3]]
        streams.append("starfield.color")
        digests["starfield.color"] = digest(rgba, "u1")
        controls["colorAsStoredDigest"] = digest(bgra, "u1")
    wpv = m["weightsPerVertex"]
    if m["weightsCount"]:
        pairs = m["weights"].reshape(nv, wpv, 2)
        for slot in range(wpv):
            streams.append(f"starfield.bone.{slot}")
            digests[f"starfield.bone.{slot}"] = digest(pairs[:, slot, 0], "<u2")
            streams.append(f"starfield.weight.{slot}")
            digests[f"starfield.weight.{slot}"] = digest(pairs[:, slot, 1], "<u2")
    for number, lod in enumerate(m["lods"], start=1):
        digests[f"lod.{number}"] = digest(lod.astype(np.int64), "<i4")
    if m["tail"]:
        digests["meshlets"] = digest(m["meshlets"], "<u4")
        digests["cull"] = digest(m["cull"].view(np.uint32), "<u4")
    frame = uv_frame(m, positions, normals, tangents, w) if w is not None else None
    header = {"version": m["version"], "indexCount": m["indexCount"],
              "scaleBits": f"0x{struct.unpack('<I', struct.pack('<f', m['scale']))[0]:08X}",
              "weightsPerVertex": wpv, "vertexCount": nv,
              "uv0Count": m["uv0Count"], "uv1Count": m["uv1Count"], "colorsCount": m["colorsCount"],
              "normalsCount": m["normalsCount"], "tangentsCount": m["tangentsCount"],
              "weightsCount": m["weightsCount"], "lodIndexCounts": [int(len(x)) for x in m["lods"]],
              "tail": bool(m["tail"]), "meshletCount": m["meshletCount"], "cullCount": m["cullCount"],
              "firstNormalW": int(m["normals"][0] >> 30) if m["normalsCount"] else None,
              "qMaxAbs": int(np.abs(m["positions"].astype(np.int32)).max())}
    used = np.zeros(nv, dtype=bool)
    used[m["indices"].astype(np.int64)] = True
    counts["unusedVertices"] = int(nv - np.count_nonzero(used))
    first = {"position": [float(x) for x in positions[0]], "uv0": [float(x) for x in uv_values(m["uv0"][0])]
             if m["uv0Count"] else None}
    if normals is not None:
        first["normal"] = [float(x) for x in normals[0]]
    return {"header": header, "sections": section_list, "census": census_identities(section_list),
            "streams": streams, "digests": digests, "counts": counts, "controls": controls, "frame": frame,
            "first": first}


def probe_expectation(data):
    """The probe verdict under BMT's bounds on the candidate Shared's helper would build (complete below 64 KiB)."""
    complete = len(data) < PROBE_BYTES
    verdict = probe_module.probe(data[:PROBE_BYTES], complete, len(data), max_weights=MAX_WEIGHTS, max_lods=MAX_LODS)
    verdict["complete"] = complete
    return verdict


def expectations(args):
    cache = ArchiveCache(resolve_data_dir(args.data))
    with open(args.manifest, encoding="utf-8") as handle:
        document = json.load(handle)
    lines = []
    for row in document["files"] + document["declineControls"]:
        record = {"schema": EXPECTATIONS_SCHEMA, "sha256": row["sha256"], "name": row["name"], "role": row["role"],
                  "size": row["size"]}
        try:
            data, _ = cache.read(row["archive"], row["entry"], row["index"])
        except LookupError as error:
            record["unresolved"] = str(error)
            lines.append(record)
            continue
        if len(data) != row["size"] or sha256(data) != row["sha256"]:
            record["unresolved"] = "the primary source does not reproduce the pinned size and SHA-256"
            lines.append(record)
            continue
        record["resolvedFrom"] = f"{row['source']} :: {row['entry']} #{row['index']}"
        record["probe"] = probe_expectation(data)
        if row["role"] != "decline":
            record.update(file_expectation(data))
        lines.append(record)
    with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps({"schema": EXPECTATIONS_SCHEMA, "rules": DIGEST_RULES,
                                 "generator": "tools/scripts/gate2/starfield_mesh_cover.py expectations",
                                 "manifest": os.path.basename(args.manifest)}, separators=(",", ":")) + "\n")
        for record in lines:
            handle.write(json.dumps(record, separators=(",", ":")) + "\n")
    unresolved = sum(1 for r in lines if "unresolved" in r)
    print(f"expectations: {len(lines)} records, {unresolved} unresolved", flush=True)
    return 0 if unresolved == 0 else 1


# --------------------------------------------------------------------------------------------------------------------
# selfcheck
# --------------------------------------------------------------------------------------------------------------------

def selfcheck(args):
    checks = []

    def expect(name, condition, detail=None):
        checks.append({"check": name, "passed": bool(condition), "detail": detail})

    samples = probe_module.synthetic_samples()
    base = samples["v1-lod"]
    reference = file_expectation(base)
    expect("the synthetic LOD mesh has a LOD digest and a color stream",
           "lod.1" in reference["digests"] and "starfield.color" in reference["streams"], reference["streams"])

    def mutated(offset_of, mutate):
        data = bytearray(base)
        mutate(data, offset_of(probe_module.decode(bytes(base))))
        return file_expectation(bytes(data))["digests"]

    flipped = mutated(lambda m: m["offsets"]["indices"] + 4, lambda d, at: d.__setitem__(at, d[at] ^ 0x01))
    changed = sorted(k for k in reference["digests"] if flipped.get(k) != reference["digests"][k])
    expect("a flipped index byte changes exactly the index digest", changed == ["indices"], changed)
    color = mutated(lambda m: m["offsets"]["colors"] + 4, lambda d, at: d.__setitem__(at, d[at] ^ 0x01))
    changed = sorted(k for k in reference["digests"] if color.get(k) != reference["digests"][k])
    expect("one color byte changes exactly the color digest", changed == ["starfield.color"], changed)
    lod = mutated(lambda m: m["offsets"]["lods"] + 8, lambda d, at: d.__setitem__(at, d[at] ^ 0x01))
    changed = sorted(k for k in reference["digests"] if lod.get(k) != reference["digests"][k])
    expect("one LOD index byte changes exactly the LOD digest", changed == ["lod.1"], changed)
    expect("the stored BGRA order is not the reader's RGBA order",
           reference["controls"]["colorAsStoredDigest"] != reference["digests"]["starfield.color"])
    expect("the engine's tangent W map changes the tangent digest",
           reference["controls"]["swappedTangentsDigest"] != reference["digests"]["tangents"])
    # The glTF sign on both codes: cover 01's upright, unmirrored layout (V runs against +Y, code 0) reads w = +1, and
    # the builder quad's V-along-+Y layout (code 3) reads w = -1; each agrees with glTF's -dP/dv on every vertex and the
    # engine sign on none (a constant w, or the engine map, fails one of the two).
    for label, v_down, code, want in (("cover-01 layout, code 0", True, 0, 1.0), ("V along +Y, code 3", False, 3, -1.0)):
        uv = [(0, 0x3C00), (0x3C00, 0x3C00), (0x3C00, 0), (0, 0)] if v_down else \
            [(0, 0), (0x3C00, 0), (0x3C00, 0x3C00), (0, 0x3C00)]
        quad = probe_module.write_mesh(
            2, [0, 1, 2, 0, 2, 3], 25.0, [(-32767, -32767, 0), (32767, -32767, 0), (32767, 32767, 0), (-32767, 32767, 0)],
            uv0=uv, normals=[probe_module.pack_dec4(0, 0, 1, 1)] * 4, tangents=[probe_module.pack_dec4(1, 0, 0, code)] * 4,
            lods=[], meshlets=[(4, 0, 2, 0)], cull=[(0, 0, 0, 25, 25, 0)])
        record = file_expectation(quad)
        typed = tangent_w(probe_module.decode(quad)["tangents"])
        expect(f"glTF tangent sign ({label}): w = {want:+.0f}, agreeing with glTF's UV frame on every vertex",
               bool(np.all(typed == want)) and record["frame"] == {"valid": 4, "agree": 4, "agreeSwapped": 0},
               [typed.tolist(), record["frame"]])
    # A tail holding no records: the tail is present, so both digests are stated, as the SHA-256 of no bytes.
    empty_tail = probe_module.write_mesh(
        2, [0, 1, 2], 1.0, [(0, 0, 0), (1, 0, 0), (0, 1, 0)], normals=[probe_module.pack_dec4(0, 0, 1, 1)] * 3,
        lods=[], meshlets=[], cull=[])
    record = file_expectation(empty_tail)
    empty = hashlib.sha256(b"").hexdigest()
    expect("a tail with no records digests meshlets and cull as the SHA-256 of no bytes, and adds no census element",
           record["digests"].get("meshlets") == empty and record["digests"].get("cull") == empty
           and "meshlets" not in record["census"] and record["header"]["tail"], record)
    # Positions where the legacy two-rounding route differs from the reader's route (scale 4, q = 20000 and -10000).
    legacy_mesh = probe_module.write_mesh(
        2, [0, 1, 2], 4.0, [(20000, -10000, 0), (-10000, 20000, 0), (0, 0, 16383)],
        uv0=[(0, 0)] * 3, normals=[probe_module.pack_dec4(0, 0, 1, 1)] * 3,
        tangents=[probe_module.pack_dec4(1, 0, 0, 3)] * 3, lods=[], meshlets=[(3, 0, 1, 0)],
        cull=[(0, 0, 0, 4, 4, 4)])
    legacy = file_expectation(legacy_mesh)
    expect("the legacy two-rounding position route differs on q = 20000 and -10000 at scale 4",
           legacy["controls"]["legacyPositionComponentsDiffer"] == 4
           and legacy["controls"]["legacyPositionsDigest"] != legacy["digests"]["positions"],
           legacy["controls"])
    expect("the Dec4 route table equals (2v - 1023) / 1023 rounded once, and the legacy route differs on 556 codes",
           int(np.count_nonzero(dec4_legacy(np.arange(1024, dtype=np.uint32))[:, 0] != DEC4_TABLE)) == 556)
    tailless = samples["v2-no-tail"]
    record = file_expectation(tailless)
    expect("a tail-less mesh has no meshlet or cull digest or census element",
           "meshlets" not in record["digests"] and "cull" not in record["census"], record["census"])
    verdict = probe_expectation(tailless)
    expect("a tail-less mesh is Supported and Confirmed under BMT's bounds",
           verdict["kind"] == "Supported" and verdict["confidence"] == "Confirmed", verdict)
    weights = file_expectation(samples["v2-weights-uv1"])
    expect("weights become one bone and one weight stream per slot, in slot order",
           weights["streams"] == ["starfield.color", "starfield.bone.0", "starfield.weight.0", "starfield.bone.1",
                                  "starfield.weight.1"], weights["streams"])
    report = {"checks": checks, "passed": all(c["passed"] for c in checks)}
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "selfcheck.json"), "w", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, indent=1, default=str)
    failed = [c for c in checks if not c["passed"]]
    print(f"selfcheck: {len(checks)} checks, {len(failed)} failed", flush=True)
    for c in failed:
        print("  FAILED", c["check"], c["detail"])
    return 0 if report["passed"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("manifest")
    p.add_argument("--records", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--data")
    p = sub.add_parser("verify")
    p.add_argument("--manifest", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--data")
    p = sub.add_parser("expectations")
    p.add_argument("--manifest", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--data")
    p = sub.add_parser("selfcheck")
    p.add_argument("--out", required=True)
    args = parser.parse_args()
    handler = {"manifest": manifest, "verify": verify, "expectations": expectations, "selfcheck": selfcheck}[args.command]
    return handler(args) or 0


if __name__ == "__main__":
    sys.exit(main())
