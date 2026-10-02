# SPDX-License-Identifier: 0BSD
"""Gate-1c hop A7 driver: runs the independent ``rg3dc_probe`` over Redguard ``.3DC`` files and writes one JSON
extraction per file, for the cut-1c Bucket-B tests that compare the C# ``.3DC`` reader's document and coverage
against it (``Cut1cRedguard3DcTests``). ONE process serves a whole directory.

Usage::

    python rg3dc_probe_json.py <file-or-directory> <output.json>

A directory is walked for ``*.3DC`` (any case), sorted by name. The output is ``{"files": {name: extraction}}``, an
extraction (or ``{"error": message}`` when the probe refuses the file) carrying:

- ``payload_sha256``, ``length``, ``tag``, ``width`` (``wide`` or ``narrow``), ``record_dwords``, ``frame_count``,
  ``point_count``, ``plane_count``, ``plane_list_end``, ``shape_test``;
- ``header``: the 64-byte header's named fields (``xngine_probe.read_header``);
- ``preamble`` (frame-block dwords 0 to 5), ``table`` (every frame-table record, all its dwords), ``unaccounted`` and
  ``gap`` (``[start, length]`` of the one region the tiling leaves, or null);
- ``referenced_points``: the sorted distinct point indices the planes name, and ``unreferenced_points``;
- ``unreferenced_poses``: per point no plane corner names, ascending, ``{"point": j, "poses": [[x, y, z], ...]}``
  with its rebuilt integer pose in every frame (the keyframe, then int32 poses on a wide file or the keyframe plus
  that frame's own int16 delta on a narrow one), for the reader's ``bmt.redguard.3dc.unreferenced-points`` row
  (slice-6 review finding 2: 7 points in 4 retail files);
- ``pose_sha256``: per frame, the SHA-256 of the pose over the referenced points in ascending order, each as
  ``<iiii`` (point index, x, y, z) little-endian int32: the keyframe for frame 0, int32 poses on a wide file, the
  keyframe plus that frame's OWN int16 deltas on a narrow file (never accumulated);
- ``keyframe``: every keyframe point as ``[x, y, z]``;
- ``normals_sha256`` and ``plane_data_sha256``: per frame, the SHA-256 of that frame's normal and plane-data blocks;
- ``planes``: per plane its count, unknown byte, texture key, header tail (hex), corners as
  ``[point_index, stored_u, stored_v]`` (raw int16) and ``triangles``, the reference fan over the POSE-INDEPENDENT
  kept corners (plan section 4: a corner is kept when any pose keeps it), in source-corner terms, orientation
  ``(K0, K(i), K(i+1))``;
- ``union_differs_from_keyframe``: the n-gons whose pose-independent kept list differs from the keyframe's own, and
  ``pose_dependent_ngons``: the n-gons some pose keeps differently from the keyframe (``rg3dc_probe``);
- ``uv_stream``: the stored-UV stream's row count and SHA-256 (``xngine_probe.uv_field_stream``);
- ``walk_as_3d_trap``: what a static ``.3D`` walk of the same bytes would read (``rg3dc_probe.walk_as_3d_trap``).

Shares no code with BMT's C# readers; see ``rg3dc_probe.py`` and ``xngine_probe.py`` for the restated layouts.
"""

import hashlib
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import rg3dc_probe  # noqa: E402
import xngine_probe  # noqa: E402


def extract(data):
    """The JSON-ready A7 extraction of one ``.3DC`` file."""
    parsed = rg3dc_probe.parse(data)
    poses = parsed["poses"]
    planes = parsed["planes"]
    kept = rg3dc_probe.pose_independent_kept(parsed)
    referenced = sorted({corner[0] for plane in planes for corner in plane["corners"]})
    named = set(referenced)
    unreferenced = [index for index in range(parsed["point_count"]) if index not in named]
    pose_digests = []
    for pose in poses:
        digest = hashlib.sha256()
        for index in referenced:
            x, y, z = pose[index]
            digest.update(struct.pack("<iiii", index, x, y, z))
        pose_digests.append(digest.hexdigest())
    width = parsed["width"]
    normal_length = 12 if width == "wide" else 4
    plane_data_length = 24 if width == "wide" else 12
    plane_count = parsed["plane_count"]
    normals = []
    plane_data = []
    for record in parsed["table"]:
        normals.append(xngine_probe.sha256_hex(data[record[1]:record[1] + plane_count * normal_length]))
        plane_data.append(xngine_probe.sha256_hex(data[record[2]:record[2] + plane_count * plane_data_length]))
    plane_rows = []
    for plane in planes:
        count = plane["count"]
        if count == 3:
            triangles = [[0, 1, 2]]
        elif count > 3:
            order = kept["union_kept"][plane["index"]]
            triangles = [[order[0], order[i], order[i + 1]] for i in range(1, len(order) - 1)]
        else:
            triangles = []
        plane_rows.append({
            "index": plane["index"],
            "count": count,
            "unknown1": plane["unknown1"],
            "key": plane["key"],
            "tail": plane["tail"],
            "corners": [list(c) for c in plane["corners"]],
            "triangles": triangles,
        })
    differs = [index for index, order in kept["union_kept"].items() if order != kept["keyframe_kept"][index]]
    rows, uv_sha = xngine_probe.uv_field_stream(planes)
    gap = parsed["gap"]
    return {
        "payload_sha256": parsed["payload_sha256"],
        "length": len(data),
        "tag": parsed["header"]["tag"],
        "width": width,
        "record_dwords": parsed["record_dwords"],
        "frame_count": parsed["frame_count"],
        "point_count": parsed["point_count"],
        "plane_count": plane_count,
        "plane_list_end": parsed["plane_list_end"],
        "shape_test": bool(rg3dc_probe.shape_test(data)),
        "header": parsed["header"],
        "preamble": parsed["preamble"],
        "table": parsed["table"],
        "unaccounted": parsed["unaccounted"],
        "gap": list(gap) if gap else None,
        "referenced_points": referenced,
        "unreferenced_points": parsed["point_count"] - len(referenced),
        "unreferenced_poses": [{"point": index, "poses": [list(pose[index]) for pose in poses]}
                               for index in unreferenced],
        "pose_sha256": pose_digests,
        "keyframe": [list(p) for p in poses[0]],
        "normals_sha256": normals,
        "plane_data_sha256": plane_data,
        "planes": plane_rows,
        "union_differs_from_keyframe": sorted(differs),
        "pose_dependent_ngons": sorted(kept["pose_dependent_ngons"]),
        "uv_stream": {"rows": len(rows), "sha256": uv_sha},
        "walk_as_3d_trap": rg3dc_probe.walk_as_3d_trap(data, parsed),
    }


def main(argv):
    if len(argv) != 3:
        sys.stderr.write("usage: rg3dc_probe_json.py <file-or-directory> <output.json>\n")
        return 2
    source = argv[1]
    if os.path.isdir(source):
        names = sorted((n for n in os.listdir(source) if n.upper().endswith(".3DC")), key=str.upper)
        paths = [(n, os.path.join(source, n)) for n in names]
    else:
        paths = [(os.path.basename(source), source)]
    files = {}
    for name, path in paths:
        with open(path, "rb") as handle:
            data = handle.read()
        try:
            files[name] = extract(data)
        except xngine_probe.ProbeError as error:
            files[name] = {"error": str(error)}
    with open(argv[2], "w", encoding="utf-8", newline="\n") as handle:
        json.dump({"files": files}, handle, separators=(",", ":"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
