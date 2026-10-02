# SPDX-License-Identifier: 0BSD
"""Gate 1a record: does each GLB's TANGENT follow glTF's frame, judged from the GLB's own UVs?

Usage:
    python tangent_frame_census.py <artifacts.json> --out <directory> [--min-along R] [--min-w R]
    python tangent_frame_census.py --selfcheck

glTF 2.0 defines TANGENT.xyz as the tangent direction and ``bitangent = cross(normal.xyz, tangent.xyz) * tangent.w``,
and its normal texture as +X right, +Y up; TEXCOORD's origin is the image's top-left with V running down. So the
tangent must run along +dP/du and the bitangent along -dP/dv (up the image), and the handedness the UVs ask for is
``w = sign(dot(cross(N, dP/du), -dP/dv))``. This record measures both on every GLB of a produced-artifacts manifest
(``gate1a-artifacts/1``), from the GLB's POSITION, NORMAL, TEXCOORD_0, indices and TANGENT only: nothing is read from
the dump, the package, the NIF or any writer code, so the record judges the reader's tangent mapping end to end
(TestOutput/nif-tangent-frame-20260928: the reader typed the stored V axis as TANGENT before that fix, which scored
0.06% along +dP/du, 59 of 92,315 scored vertices, on the 2026-09-28 pin-0152179 gate GLBs).

The per-vertex frame is the independent measurement's (measure/nif_tangent_frame.py ``uv_frame``): per kept triangle
the UV derivatives, each corner accumulating the unit derivatives weighted by the triangle area, projected into the
normal's plane; a vertex is scored only when it is referenced, has a UV-non-degenerate non-zero-area incident
triangle, a non-zero normal, a single incident orientation (not a mirror seam), non-cancelled sums and a non-zero
tangent. The GLB's axis change is a rotation (Z-up to Y-up), which leaves every cosine and orientation unchanged.

The per-vertex rates say nothing about glTF's per-triangle rule: "Vertices of the same triangle SHOULD have equal W
components of their TANGENT values. When vertices of the same triangle have different W values, its tangent space is
considered undefined." So the record also counts, per primitive, the triangles whose three TANGENT.w differ
(``mixedWTriangles``), how many of them are UV-non-degenerate (``mixedWTrianglesUvLive``), and how many touch a
seam vertex, by two definitions: the ``mirrorSeam`` class the per-vertex rates exclude (``mixedWTrianglesAtSeam``:
incident triangles of both orientations relative to the normal) and a vertex whose incident triangles wind both ways
in UV space (``mixedWTrianglesAtUvWindingSeam``). A source frame whose w flips across a UV mirror seam on a shared
vertex produces them; the reader may not split that vertex, so they are a source property the writer carries,
reported rather than gated (TestOutput/nif-tangent-frame-20260928/IMPLEMENTATION.md, "Review fixes"). On the
pin-0152179 GLBs: 2,652 of 89,405 triangles, 2,474 UV-non-degenerate; landscape/trees/wastelandhedgerow04.nif alone
holds 1,009 of its 1,528, 998 of them at a UV-winding seam.

It is a RECORD, not a hop: the driver does not run it and it never fails the gate by default. ``--min-along`` and
``--min-w`` turn it into a check (exit 1 when the total rate falls below). The expected rates are MEASURE.md's: along
+dP/du on about 99.9%, w agreeing on about 99.9% except terrain LOD and a few FO3 SCOL shapes, whose stored frame is
constant-handed (an owner ruling is pending there, so the total w rate depends on how many such shapes the manifest
holds). ``--selfcheck`` pins the census on hand-built quads (exit 1 on any failed expectation).
"""

import argparse
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from glb_reader import Glb  # noqa: E402

ALIGN = 0.7
UV_DET_REL = 1e-7
AREA_REL = 1e-10
CANCEL_REL = 1e-3
ZERO_VEC = 1e-6
CLASSES = ('unreferenced', 'degenerateUv', 'zeroNormal', 'mirrorSeam', 'edgeOn', 'cancelled', 'zeroTangent')


TRIANGLE_KEYS = ('triangles', 'mixedWTriangles', 'mixedWTrianglesUvLive', 'mixedWTrianglesAtSeam',
                 'mixedWTrianglesAtUvWindingSeam')
COUNT_KEYS = ('vertices', 'scored', 'alongU', 'alongV', 'wAgrees', 'wDirectX') + TRIANGLE_KEYS


def _unit(v):
    n = np.linalg.norm(v, axis=1)
    safe = np.where(n > 0, n, 1.0)
    return np.where((n > 0)[:, None], v / safe[:, None], 0.0), n


def census(positions, normals, uvs, tangents, indices):
    """Counts for one triangle primitive: ``vertices``, ``scored``, ``alongU`` (cos(T, dP/du) > 0.7), ``wAgrees``
    (w equals the UV handedness), ``alongV`` (T runs along +dP/dv: the swapped-axis failure), the exclusion classes,
    ``wDirectX`` (w equals the opposite, DirectX, sense), and the per-triangle w uniformity: ``triangles``,
    ``mixedWTriangles`` (the three vertices' w are not all equal: glTF's undefined tangent space),
    ``mixedWTrianglesUvLive`` (those whose UVs are non-degenerate), ``mixedWTrianglesAtSeam`` (those touching a vertex
    of the ``mirrorSeam`` class: UV-live incident triangles of both orientations relative to the normal) and
    ``mixedWTrianglesAtUvWindingSeam`` (those touching a vertex whose UV-live incident triangles wind both ways in UV
    space, the plain reading of a UV mirror seam)."""
    P = np.asarray(positions, dtype=np.float64).reshape(-1, 3)
    N = np.asarray(normals, dtype=np.float64).reshape(-1, 3)
    UV = np.asarray(uvs, dtype=np.float64).reshape(-1, 2)
    T = np.asarray(tangents, dtype=np.float64).reshape(-1, 4)
    tris = np.asarray(indices, dtype=np.int64).reshape(-1, 3)
    nv = len(P)
    Nu, nlen = _unit(N)
    refc = np.zeros(nv, np.int64)
    np.add.at(refc, tris.ravel(), 1)
    Uacc, Vacc = np.zeros((nv, 3)), np.zeros((nv, 3))
    Asum = np.zeros(nv)
    okc, pos, neg = np.zeros(nv, np.int64), np.zeros(nv, np.int64), np.zeros(nv, np.int64)
    ok = np.zeros(len(tris), dtype=bool)
    wind_pos, wind_neg = np.zeros(nv, np.int64), np.zeros(nv, np.int64)
    if len(tris):
        e1, e2 = P[tris[:, 1]] - P[tris[:, 0]], P[tris[:, 2]] - P[tris[:, 0]]
        d1, d2 = UV[tris[:, 1]] - UV[tris[:, 0]], UV[tris[:, 2]] - UV[tris[:, 0]]
        det = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
        area2 = np.linalg.norm(np.cross(e1, e2), axis=1)
        with np.errstate(invalid='ignore', over='ignore'):
            ok = (np.isfinite(det) & np.isfinite(area2)
                  & (np.abs(det) > UV_DET_REL * np.linalg.norm(d1, axis=1) * np.linalg.norm(d2, axis=1))
                  & (area2 > AREA_REL * np.linalg.norm(e1, axis=1) * np.linalg.norm(e2, axis=1)))
            inv = np.where(ok, 1.0 / np.where(ok, det, 1.0), 0.0)
        dpdu = (e1 * d2[:, 1:2] - e2 * d1[:, 1:2]) * inv[:, None]
        dpdv = (e2 * d1[:, 0:1] - e1 * d2[:, 0:1]) * inv[:, None]
        area = 0.5 * area2
        ud = _unit(dpdu)[0] * area[:, None]
        vd = _unit(dpdv)[0] * area[:, None]
        orient = np.cross(dpdu, dpdv)
        tk = tris[ok]
        for k in range(3):
            idx = tk[:, k]
            np.add.at(Uacc, idx, ud[ok])
            np.add.at(Vacc, idx, vd[ok])
            np.add.at(Asum, idx, area[ok])
            np.add.at(okc, idx, 1)
            s = np.einsum('ij,ij->i', orient[ok], Nu[idx])
            np.add.at(pos, idx, (s > 0).astype(np.int64))
            np.add.at(neg, idx, (s < 0).astype(np.int64))
            np.add.at(wind_pos, idx, (det[ok] > 0).astype(np.int64))
            np.add.at(wind_neg, idx, (det[ok] < 0).astype(np.int64))
    Uh, ulen = _unit(Uacc - Nu * np.einsum('ij,ij->i', Uacc, Nu)[:, None])
    Vh, vlen = _unit(Vacc - Nu * np.einsum('ij,ij->i', Vacc, Nu)[:, None])
    th, tlen = _unit(T[:, :3])
    unref = refc == 0
    degen = ~unref & (okc == 0)
    live = ~unref & ~degen
    nzero = live & ~(nlen > ZERO_VEC)
    mixed = live & ~nzero & (pos > 0) & (neg > 0)
    edge = live & ~nzero & ~mixed & (pos == 0) & (neg == 0)
    cancelled = live & ~nzero & ~mixed & ~edge & ~((ulen > CANCEL_REL * Asum) & (vlen > CANCEL_REL * Asum))
    valid = live & ~nzero & ~mixed & ~edge & ~cancelled
    ztan = valid & ~(np.isfinite(tlen) & (tlen > ZERO_VEC))
    scored = valid & ~ztan
    w_uv = np.where(pos > 0, -1.0, 1.0)
    tri_w = T[tris, 3] if len(tris) else np.zeros((0, 3))
    mixed_w = ~((tri_w[:, 0] == tri_w[:, 1]) & (tri_w[:, 1] == tri_w[:, 2]))
    at_seam = mixed[tris].any(axis=1) if len(tris) else np.zeros(0, dtype=bool)
    uv_seam = (wind_pos > 0) & (wind_neg > 0)
    at_uv_seam = uv_seam[tris].any(axis=1) if len(tris) else np.zeros(0, dtype=bool)
    return {
        'vertices': int(nv), 'scored': int(scored.sum()),
        'alongU': int(((np.einsum('ij,ij->i', th, Uh) > ALIGN) & scored).sum()),
        'alongV': int(((np.einsum('ij,ij->i', th, Vh) > ALIGN) & scored).sum()),
        'wAgrees': int(((T[:, 3] == w_uv) & scored).sum()),
        'wDirectX': int(((T[:, 3] == -w_uv) & scored).sum()),
        'excluded': dict(zip(CLASSES, (int(x.sum()) for x in (unref, degen, nzero, mixed, edge, cancelled, ztan)))),
        'triangles': int(len(tris)), 'mixedWTriangles': int(mixed_w.sum()),
        'mixedWTrianglesUvLive': int((mixed_w & ok).sum()), 'mixedWTrianglesAtSeam': int((mixed_w & at_seam).sum()),
        'mixedWTrianglesAtUvWindingSeam': int((mixed_w & at_uv_seam).sum()),
    }


def glb_census(path):
    """(totals, per-primitive list) over every triangle primitive of a GLB that has TANGENT and TEXCOORD_0."""
    glb = Glb.from_path(path)
    totals = dict(dict.fromkeys(('primitives',) + COUNT_KEYS, 0), excluded=dict.fromkeys(CLASSES, 0))
    rows = []
    for m, mesh in enumerate(glb.json.get('meshes', [])):
        for p, prim in enumerate(mesh.get('primitives', [])):
            at = prim.get('attributes', {})
            if prim.get('mode', 4) != 4 or 'indices' not in prim or not all(
                    k in at for k in ('POSITION', 'NORMAL', 'TEXCOORD_0', 'TANGENT')):
                continue
            c = census(glb.accessor(at['POSITION']), glb.accessor(at['NORMAL']),
                       glb.accessor(at['TEXCOORD_0'], normalize=True), glb.accessor(at['TANGENT']),
                       glb.accessor(prim['indices']).reshape(-1))
            rows.append(dict(c, mesh=m, primitive=p))
            totals['primitives'] += 1
            for key in COUNT_KEYS:
                totals[key] += c[key]
            for key, value in c['excluded'].items():
                totals['excluded'][key] += value
    return totals, rows


def _rate(count, scored):
    return round(count / scored, 6) if scored else None


def run(manifest_path, out_dir, min_along=None, min_w=None):
    manifest = json.load(open(manifest_path, encoding='utf-8'))
    os.makedirs(out_dir, exist_ok=True)
    samples = []
    total = dict(dict.fromkeys(('samples', 'primitives') + COUNT_KEYS, 0), excluded=dict.fromkeys(CLASSES, 0))
    for sample in manifest.get('samples', []):
        if not sample.get('glb') or not os.path.isfile(sample['glb']):
            continue
        totals, rows = glb_census(sample['glb'])
        if not totals['primitives']:
            continue
        samples.append({'id': sample.get('id'), 'glb': sample['glb'], 'totals': totals, 'primitives': rows,
                        'alongURate': _rate(totals['alongU'], totals['scored']),
                        'wRate': _rate(totals['wAgrees'], totals['scored']),
                        'mixedWTriangleRate': _rate(totals['mixedWTriangles'], totals['triangles'])})
        total['samples'] += 1
        for key in ('primitives',) + COUNT_KEYS:
            total[key] += totals[key]
        for key, value in totals['excluded'].items():
            total['excluded'][key] += value
    total['alongURate'] = _rate(total['alongU'], total['scored'])
    total['alongVRate'] = _rate(total['alongV'], total['scored'])
    total['wRate'] = _rate(total['wAgrees'], total['scored'])
    total['wDirectXRate'] = _rate(total['wDirectX'], total['scored'])
    total['mixedWTriangleRate'] = _rate(total['mixedWTriangles'], total['triangles'])
    failures = []
    if min_along is not None and (total['alongURate'] or 0) < min_along:
        failures.append('TANGENT along +dP/du %s < %s' % (total['alongURate'], min_along))
    if min_w is not None and (total['wRate'] or 0) < min_w:
        failures.append('w = glTF handedness %s < %s' % (total['wRate'], min_w))
    record = {'schema': 'gate1a-tangent-frame-census/1', 'manifest': os.path.abspath(manifest_path),
              'convention': 'glTF: TANGENT.xyz along +dP/du; cross(N, T) * w along -dP/dv (V down the image); '
                            'w_uv = sign(dot(cross(N, dP/du), -dP/dv)); a triangle whose vertices carry different w '
                            'has an undefined tangent space (glTF 2.0, meshes: SHOULD have equal W)',
              'thresholds': {'align': ALIGN, 'uvDetRel': UV_DET_REL, 'areaRel': AREA_REL, 'cancelRel': CANCEL_REL,
                             'zeroVec': ZERO_VEC, 'minAlong': min_along, 'minW': min_w},
              'total': total, 'failures': failures, 'samples': samples}
    with open(os.path.join(out_dir, 'tangent_frame_census.json'), 'w', encoding='utf-8') as fh:
        json.dump(record, fh, indent=1)
    lines = ['# Gate 1a tangent-frame census', '',
             'Manifest: `%s`' % record['manifest'], '',
             '%d GLBs, %d primitives, %d vertices, %d scored: TANGENT along +dP/du %s, along +dP/dv %s, w = glTF '
             'handedness %s (DirectX sense %s).' % (total['samples'], total['primitives'], total['vertices'],
                                                    total['scored'], total['alongURate'], total['alongVRate'],
                                                    total['wRate'], total['wDirectXRate']), '',
             'Per triangle (glTF: a triangle whose vertices carry different w has an undefined tangent space): %d of %d '
             'triangles carry mixed w (%s), %d of them UV-non-degenerate, %d touching a mirrorSeam-class vertex, %d '
             'touching a UV-winding seam vertex. Reported, not gated.' % (
                 total['mixedWTriangles'], total['triangles'], total['mixedWTriangleRate'],
                 total['mixedWTrianglesUvLive'], total['mixedWTrianglesAtSeam'],
                 total['mixedWTrianglesAtUvWindingSeam']), '',
             '| sample | scored | along +dP/du | w agrees | mixed-w triangles |', '|---|---|---|---|---|']
    for s in sorted(samples, key=lambda x: (x['wRate'] if x['wRate'] is not None else 2)):
        lines.append('| %s | %d | %s | %s | %d of %d |' % (s['id'], s['totals']['scored'], s['alongURate'], s['wRate'],
                                                           s['totals']['mixedWTriangles'], s['totals']['triangles']))
    if failures:
        lines += ['', 'FAILED: ' + '; '.join(failures)]
    with open(os.path.join(out_dir, 'tangent_frame_census.md'), 'w', encoding='utf-8') as fh:
        fh.write('\n'.join(lines) + '\n')
    print(json.dumps({k: v for k, v in total.items()}, indent=1))
    return 1 if failures else 0


def selfcheck():
    """Hand-built quads (z = 0, normal +Z): upright UVs (x, 1 - y) give dP/du = +X, dP/dv = -Y and glTF w = +1; UVs
    mirrored in U give dP/du = -X and w = -1."""
    problems = []

    def expect(condition, what):
        if not condition:
            problems.append(what)

    positions = [[0, 0, 0], [1, 0, 0], [0, 1, 0], [1, 1, 0]]
    normals = [[0, 0, 1]] * 4
    indices = [0, 1, 2, 1, 3, 2]
    upright = [[p[0], 1 - p[1]] for p in positions]
    mirrored = [[1 - p[0], 1 - p[1]] for p in positions]
    good = census(positions, normals, upright, [[1, 0, 0, 1]] * 4, indices)
    expect(good['scored'] == 4 and good['alongU'] == 4 and good['wAgrees'] == 4 and good['alongV'] == 0,
           'upright quad, glTF frame (1, 0, 0, +1): every vertex along +dP/du with w agreeing (%r)' % good)
    swapped = census(positions, normals, upright, [[0, -1, 0, 1]] * 4, indices)
    expect(swapped['alongU'] == 0 and swapped['alongV'] == 4,
           'the swapped axis (the stored V array as TANGENT) runs along +dP/dv, never +dP/du (%r)' % swapped)
    directx = census(positions, normals, upright, [[1, 0, 0, -1]] * 4, indices)
    expect(directx['wAgrees'] == 0 and directx['wDirectX'] == 4, 'the DirectX sense w = -1 disagrees on the upright quad')
    mirror = census(positions, normals, mirrored, [[-1, 0, 0, -1]] * 4, indices)
    expect(mirror['alongU'] == 4 and mirror['wAgrees'] == 4, 'U-mirrored quad, frame (-1, 0, 0, -1) passes (%r)' % mirror)
    constant = census(positions, normals, mirrored, [[-1, 0, 0, 1]] * 4, indices)
    expect(constant['wAgrees'] == 0, 'a constant w = +1 fails the mirrored quad')
    unreferenced = census(positions + [[5, 5, 0]], normals + [[0, 0, 1]], upright + [[0, 0]],
                          [[1, 0, 0, 1]] * 4 + [[1, 0, 0, 1]], indices)
    expect(unreferenced['excluded']['unreferenced'] == 1 and unreferenced['scored'] == 4,
           'an unreferenced slot (a writer placeholder) is excluded, not scored')
    seam = census([[0, 0, 0], [1, 0, 0], [0, 1, 0], [-1, 0, 0]], normals, [[0, 1], [1, 1], [0, 0], [1, 1]],
                  [[1, 0, 0, 1]] * 4, [0, 1, 2, 0, 2, 3])
    expect(seam['excluded']['mirrorSeam'] == 2, 'the two vertices on a mirror seam are excluded (%r)' % seam['excluded'])
    expect(seam['triangles'] == 2 and seam['mixedWTriangles'] == 0 and good['mixedWTriangles'] == 0,
           'a constant w leaves every triangle uniform (%r)' % seam)
    # The same seam with each side's own handedness on its unshared vertex (the upright side +1, the mirrored side -1)
    # and the shared seam vertices on the upright side's: the mirrored triangle carries (+1, +1, -1).
    flipped = census([[0, 0, 0], [1, 0, 0], [0, 1, 0], [-1, 0, 0]], normals, [[0, 1], [1, 1], [0, 0], [1, 1]],
                     [[1, 0, 0, 1], [1, 0, 0, 1], [1, 0, 0, 1], [-1, 0, 0, -1]], [0, 1, 2, 0, 2, 3])
    expect(flipped['mixedWTriangles'] == 1 and flipped['mixedWTrianglesUvLive'] == 1
           and flipped['mixedWTrianglesAtSeam'] == 1 and flipped['mixedWTrianglesAtUvWindingSeam'] == 1,
           'a triangle across a mirror seam whose vertices carry different w is counted once, UV-live, at the seam '
           '(%r)' % {k: flipped[k] for k in TRIANGLE_KEYS})
    expect(flipped['wAgrees'] == flipped['scored'] == 2,
           'the per-vertex rate cannot see it: both scored (non-seam) vertices agree (%r)' % flipped)
    for p in problems:
        print('FAILED: ' + p)
    print('tangent_frame_census selfcheck: %d problem(s)' % len(problems))
    return 1 if problems else 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('manifest', nargs='?')
    ap.add_argument('--out')
    ap.add_argument('--min-along', type=float)
    ap.add_argument('--min-w', type=float)
    ap.add_argument('--selfcheck', action='store_true')
    args = ap.parse_args(argv)
    if args.selfcheck:
        return selfcheck()
    if not args.manifest or not args.out:
        ap.error('a manifest and --out are required unless --selfcheck')
    return run(args.manifest, args.out, args.min_along, args.min_w)


if __name__ == '__main__':
    sys.exit(main())
