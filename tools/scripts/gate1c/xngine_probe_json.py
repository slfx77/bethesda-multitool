# SPDX-License-Identifier: 0BSD
"""Gate-1c hop A6 and A2 driver: runs the independent ``xngine_probe`` over ONE XnGine ``.3D`` record
and writes its extraction as JSON, for the cut-1c Bucket-B tests that compare the C# reader's
document and coverage against it (``Cut1cXnGineOracleTests``).

Usage::

    python xngine_probe_json.py <record-file> <plane-header 8|10> <output.json>

The record file holds the payload bytes exactly (for a Battlespire LZSS entry the DECOMPRESSED
bytes; for a ROB segment the bytes after the 80-byte segment header). The plane-header size is the
caller's (the manifest game's), because the probe does not guess a layout. The output carries:

- ``payload_sha256`` and ``length``;
- ``header``: the 64-byte header's named fields (``xngine_probe.read_header``);
- ``points`` and ``normals`` as stored int32 triples;
- ``planes``: per plane its count, unknown byte, texture key, header tail (hex), corners as
  ``[point_index, stored_u, stored_v]`` (raw int16, no unfold, no accumulation) and the D5
  triangulation (``triangles`` in source-corner terms, ``reference_stream``, ``kept_corner_count``,
  ``zero_area_triangles``, ``omitted_zero_area_triangles``, ``folded_triangles``, ``zero_normal``,
  ``no_triangle``);
- ``texture_keys_first_use``, ``emptied_keys``, ``no_triangle_planes`` and ``totals``;
- ``plane_data``: offset, size and SHA-256 when the area fits;
- ``uv_stream``: the stored-UV stream's row count and SHA-256 (``xngine_probe.uv_field_stream``);
- ``tiling``: the M-T byte-area tiling (gaps as ``[start, end]``, overlaps, plane-data state);
- ``shape_test_3dc``: whether the record satisfies the .3DC shape test S (it must not, for a
  static mesh).

Shares no code with BMT's C# readers; see ``xngine_probe.py`` for the restated layouts.
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import rg3dc_probe  # noqa: E402
import xngine_probe  # noqa: E402


def extract(data, plane_header):
    """The JSON-ready A6/A2 extraction of one record."""
    probe = xngine_probe.probe_mesh(data, plane_header)
    header = probe["header"]
    tiling = xngine_probe.area_tiling(data, header, probe["plane_list_end"])
    rows, uv_sha = xngine_probe.uv_field_stream(probe["planes"])
    planes = []
    for plane in probe["planes"]:
        t = plane["triangulation"]
        planes.append({
            "index": plane["index"],
            "count": plane["count"],
            "unknown1": plane["unknown1"],
            "key": plane["key"],
            "tail": plane["tail"],
            "corners": [list(c) for c in plane["corners"]],
            "triangles": [list(x) for x in t["triangles"]],
            "reference_stream": [list(x) for x in t["reference_stream"]],
            "kept_corner_count": t["kept_corner_count"],
            "zero_area_triangles": t["zero_area_triangles"],
            "omitted_zero_area_triangles": t["omitted_zero_area_triangles"],
            "folded_triangles": t["folded_triangles"],
            "zero_normal": t["zero_normal"],
            "no_triangle": t["no_triangle"],
        })
    return {
        "payload_sha256": probe["payload_sha256"],
        "length": len(data),
        "plane_header": plane_header,
        "header": header,
        "points": [list(p) for p in probe["points"]],
        "normals": [list(n) for n in probe["normals"]],
        "plane_list_end": probe["plane_list_end"],
        "planes": planes,
        "texture_keys_first_use": probe["texture_keys_first_use"],
        "emptied_keys": probe["emptied_keys"],
        "no_triangle_planes": probe["no_triangle_planes"],
        "totals": probe["totals"],
        "plane_data": probe["plane_data"],
        "uv_stream": {"rows": len(rows), "sha256": uv_sha},
        "tiling": {
            "gaps": [list(g) for g in tiling["gaps"]],
            "overlaps": [list(o) for o in tiling["overlaps"]],
            "plane_data_state": tiling["plane_data_state"],
            "unclaimed_bytes": tiling["unclaimed_bytes"],
        },
        "shape_test_3dc": bool(rg3dc_probe.shape_test(data)),
    }


def main(argv):
    if len(argv) != 4 or argv[2] not in ("8", "10"):
        sys.stderr.write("usage: xngine_probe_json.py <record-file> <plane-header 8|10> <output.json>\n")
        return 2
    with open(argv[1], "rb") as handle:
        data = handle.read()
    try:
        result = extract(data, int(argv[2]))
    except xngine_probe.ProbeError as error:
        sys.stderr.write(f"probe error: {error}\n")
        return 3
    with open(argv[3], "w", encoding="utf-8", newline="\n") as handle:
        json.dump(result, handle, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
