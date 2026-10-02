# SPDX-License-Identifier: 0BSD
"""The Redguard pitch-and-roll placement control over every retail map, from the raw RGM and ROB bytes.

This is the receipt behind RedguardSceneRetailTests.PlacementPitchRollControl_..., written from the byte
layouts (rgdata.py and control.py beside it) rather than by calling BMT's C# readers.

Usage, from the repository root:

    python tools/scripts/redguard/multiaxis_control.py <data root>

<data root> is a Redguard install's data directory, the one holding WORLD.INI, maps and 3dart (on Steam,
"The Elder Scrolls Adventures Redguard/Redguard"). Read-only; it writes nothing.

What it computes, as the test does:
  - Population: control.py's (every MPOB placement of type 1 or 257 with a mesh whose masked rotation is
    non-zero, whose mesh stem names a non-empty segment of the map's own ROB, and whose mesh has points),
    restricted to the placements whose masked pitch (component 0) or roll (component 2) is non-zero. The
    yaw-only placements are left out: every reading that keeps the engine's yaw scores them identically.
  - Surface: each map's own MPSO statics, triangulated and placed exactly as control.py does.
  - Near: a placed mesh point within 6 units of a static face, control.py's rule (the test's IsNear).
  - Pierces: an edge of the placed mesh (an unordered pair of consecutive stored points of any of its planes,
    before the decomposer's corner filter) whose two ends lie strictly on opposite sides of a static
    triangle's plane, each more than 0.05 units from it, where the crossing point is on or inside all three
    of the triangle's edges. A triangle is tested when it shares a 64-unit grid cell with the edge's
    bounding box. An object that stands in its room crosses no wall, so fewer is better.
  - Readings: all 48 compositions (control.compositions). The product's corrected reading is
    Ry(-y) . Rx(-x) . Rz(-z), the matrix RG.EXE's FUN_00082fc2 builds (placement_rotation.py beside this
    checks that against the executable); the reading it replaced is Ry(-y) . Rx(+x) . Rz(+z).
  - Cross-check, not pinned by that test: the yaw-only placements of the same kind, scored the same way under
    the corrected reading and its transpose Rz(+z) . Rx(+x) . Ry(+y) (on a yaw-only placement, the flipped
    yaw). That split depends on the Y helper's layout and on world = M . p alone, and it is the one the
    2026-09-08 census settled.

It prints every figure twice, in float64 and in float32 (the precision System.Numerics computes in). The
float32 run rounds every operation to single precision; it cannot mirror the JIT's instruction choice, so it
is a second reading, not a bit-exact copy of the C#. Last, it checks the test's thresholds against all 48
readings in both precisions: the corrected reading must be the ONLY one that meets both of its thresholds,
and the replaced reading must meet neither of the product's thresholds and both of its own.
"""
import itertools
import math
import os
import sys

import numpy as np

import control as C
import rgdata as R

CORRECTED = 'Ry(-y)Rx(-x)Rz(-z)'
REPLACED = 'Ry(-y)Rx(+x)Rz(+z)'
TRANSPOSED = 'Rz(+z)Rx(+x)Ry(+y)'
PIERCE_EPSILON = 0.05

# The thresholds RedguardSceneRetailTests.PlacementPitchRollControl_... asserts.
NEAR_AT_LEAST = 735
PIERCES_AT_MOST = 170
REPLACED_NEAR_AT_MOST = 700
REPLACED_PIERCES_AT_LEAST = 240


def cross3(u, v):
    """Row-wise cross products, the component formula Vector3.Cross uses."""
    return np.stack((u[:, 1] * v[:, 2] - u[:, 2] * v[:, 1],
                     u[:, 2] * v[:, 0] - u[:, 0] * v[:, 2],
                     u[:, 0] * v[:, 1] - u[:, 1] * v[:, 0]), axis=1)


class Walls:
    """The static triangles, binned by the 64-unit grid cells their bounding boxes overlap, for edge tests."""

    def __init__(self, tris, dtype):
        t = np.asarray(tris, dtype=dtype)
        a, b, c = t[:, 0], t[:, 1], t[:, 2]
        n = cross3((b - a).astype(dtype), (c - a).astype(dtype)).astype(dtype)
        length = np.sqrt(C.dot3(n, n)).astype(dtype)
        ok = length > 0
        self.dtype = dtype
        self.a, self.b, self.c = a[ok], b[ok], c[ok]
        self.n = (n[ok] / length[ok][:, None]).astype(dtype)
        lo = np.minimum(np.minimum(self.a, self.b), self.c)
        hi = np.maximum(np.maximum(self.a, self.b), self.c)
        grid = {}
        for i in range(len(self.a)):
            ranges = [range(C.cell(lo[i, k], dtype), C.cell(hi[i, k], dtype) + 1) for k in range(3)]
            for key in itertools.product(*ranges):
                grid.setdefault(key, []).append(i)
        self.grid = {k: np.array(v, dtype=np.int64) for k, v in grid.items()}

    def pierces(self, p0, p1):
        d_ = self.dtype
        lo = np.minimum(p0, p1)
        hi = np.maximum(p0, p1)
        ranges = [range(C.cell(lo[k], d_), C.cell(hi[k], d_) + 1) for k in range(3)]
        parts = [self.grid[key] for key in itertools.product(*ranges) if key in self.grid]
        if not parts:
            return False
        idx = np.unique(np.concatenate(parts))
        a, b, c, n = self.a[idx], self.b[idx], self.c[idx], self.n[idx]
        q0 = np.asarray(p0, dtype=d_)[None, :]
        q1 = np.asarray(p1, dtype=d_)[None, :]
        d0 = C.dot3((q0 - a).astype(d_), n).astype(d_)
        d1 = C.dot3((q1 - a).astype(d_), n).astype(d_)
        eps = d_(PIERCE_EPSILON)
        crossing = ((d0 > eps) & (d1 < -eps)) | ((d0 < -eps) & (d1 > eps))
        if not crossing.any():
            return False
        a, b, c, n, d0, d1 = a[crossing], b[crossing], c[crossing], n[crossing], d0[crossing], d1[crossing]
        s = (d0 / (d0 - d1).astype(d_)).astype(d_)
        x = (q0 + ((q1 - q0).astype(d_) * s[:, None]).astype(d_)).astype(d_)
        e0 = C.dot3(cross3((b - a).astype(d_), (x - a).astype(d_)).astype(d_), n)
        e1 = C.dot3(cross3((c - b).astype(d_), (x - b).astype(d_)).astype(d_), n)
        e2 = C.dot3(cross3((a - c).astype(d_), (x - c).astype(d_)).astype(d_), n)
        return bool(np.any((e0 >= 0) & (e1 >= 0) & (e2 >= 0)))


def pitch_or_roll(p):
    return (p['rot'][0] & C.MASK) != 0 or (p['rot'][2] & C.MASK) != 0


def score(population, segs, surface, walls, readings, dtype):
    """{reading: [near points, piercing edges]} over one map's placements."""
    scores = {name: [0, 0] for name in readings}
    if surface is None:
        return scores
    for p, pts in population:
        local = (np.array(pts, dtype=dtype) / dtype(C.POINT_DIVISOR)).astype(dtype)
        pos = (np.array(p['pos'], dtype=dtype) / dtype(C.POSITION_DIVISOR)).astype(dtype)
        pairs = edges(R.mesh(segs[p['stem'].upper()][2])[1])
        for name, (order, signs) in readings.items():
            m = C.matrix(p['rot'], order, signs, dtype)
            world = ((local @ m.T).astype(dtype) + pos).astype(dtype)
            scores[name][0] += sum(1 for w in world if surface.near(w))
            scores[name][1] += sum(1 for i, j in pairs if walls.pierces(world[i], world[j]))
    return scores


def edges(planes):
    """Unordered pairs of consecutive stored points of every plane, the closing pair included."""
    return sorted({(min(a, b), max(a, b)) for idx in planes for a, b in zip(idx, idx[1:] + idx[:1]) if a != b})


def maps(data_root):
    directory = os.path.join(data_root, 'maps')
    return sorted(os.path.splitext(name)[0].upper() for name in os.listdir(directory)
                  if name.upper().endswith('.RGM'))


def census(population, segs):
    """(placements, points, edges) of a population."""
    return (len(population), sum(len(pts) for _, pts in population),
            sum(len(edges(R.mesh(segs[p['stem'].upper()][2])[1])) for p, _ in population))


def measure(data_root, dtype):
    """Per map, for the pitch-or-roll population under all 48 readings and the yaw-only population under two:
    (placements, points, edges, {reading: [near, pierces]})."""
    readings = C.compositions()
    yaw_readings = {name: readings[name] for name in (CORRECTED, TRANSPOSED)}
    out, yaw = {}, {}
    for stem in maps(data_root):
        placements, statics, segs = C.load(data_root, stem)
        everything = C.population(placements, segs)
        population = [(p, pts) for p, pts in everything if pitch_or_roll(p)]
        yaw_only = [(p, pts) for p, pts in everything if C.yaw_only(p)]
        if not population and not yaw_only:
            continue
        surface = walls = None
        if any(segs.get(s['name'].upper(), (0, 0))[1] for s in statics):
            tris = C.static_triangles(statics, segs, dtype)
            surface = C.Surface(tris, dtype)
            walls = Walls(tris, dtype)
        if population:
            out[stem] = (*census(population, segs), score(population, segs, surface, walls, readings, dtype),
                         surface is not None)
        if yaw_only:
            yaw[stem] = (*census(yaw_only, segs), score(yaw_only, segs, surface, walls, yaw_readings, dtype),
                         surface is not None)
        print(f'  {stem}: {len(population)} pitch or roll, {len(yaw_only)} yaw-only', file=sys.stderr, flush=True)
    return out, readings, yaw


def report_yaw(per_map):
    totals = {name: [sum(row[3][name][k] for row in per_map.values()) for k in (0, 1)]
              for name in (CORRECTED, TRANSPOSED)}
    scored = [row for row in per_map.values() if row[4]]
    print(f'  cross-check, yaw-only: {sum(row[0] for row in per_map.values())} placements on {len(per_map)} maps; '
          f'{sum(row[0] for row in scored)} on the {len(scored)} of them with statics to score against, '
          f'{sum(row[1] for row in scored):,} points, {sum(row[2] for row in scored):,} edges')
    for name in (CORRECTED, TRANSPOSED):
        print(f'    {name}: {totals[name][0]:,} near, {totals[name][1]:,} pierces')


def report(label, per_map, readings):
    totals = {name: [sum(row[3][name][k] for row in per_map.values()) for k in (0, 1)] for name in readings}
    placements = sum(row[0] for row in per_map.values())
    points = sum(row[1] for row in per_map.values())
    edge_count = sum(row[2] for row in per_map.values())
    print(label)
    print(f'  population {placements} placements on {len(per_map)} maps, {points:,} points, {edge_count:,} edges; '
          f'{sum(row[0] for row in per_map.values() if row[4])} of them on a map with statics to score against')
    for name, tag in ((CORRECTED, 'corrected (the product)'), (REPLACED, 'replaced')):
        print(f'  {tag:24s} {name}: {totals[name][0]:,} near, {totals[name][1]:,} pierces')
    fewer = [s for s, row in per_map.items() if row[3][CORRECTED][1] < row[3][REPLACED][1]]
    more = [s for s, row in per_map.items() if row[3][CORRECTED][1] > row[3][REPLACED][1]]
    print(f'  pierces per map, corrected against replaced: fewer on {len(fewer)}, more on {len(more)} '
          f'({", ".join(more) if more else "none"})')
    for stem, row in per_map.items():
        c, r = row[3][CORRECTED], row[3][REPLACED]
        print(f'    {stem:9s} {row[0]:3d} placements: near {c[0]:4d} against {r[0]:4d}, pierces {c[1]:4d} against {r[1]:4d}')
    print('  all 48 readings, by pierces:')
    for name in sorted(readings, key=lambda n: (totals[n][1], -totals[n][0])):
        tag = '  <- corrected' if name == CORRECTED else '  <- replaced' if name == REPLACED else ''
        print(f'    {name:20s} near {totals[name][0]:5,}  pierces {totals[name][1]:5,}{tag}')
    passing = [n for n in readings if totals[n][0] >= NEAR_AT_LEAST and totals[n][1] <= PIERCES_AT_MOST]
    near_only = sorted((totals[n][0] for n in readings if n != CORRECTED and totals[n][1] <= PIERCES_AT_MOST),
                       reverse=True)
    pierce_only = sorted(totals[n][1] for n in readings if n != CORRECTED and totals[n][0] >= NEAR_AT_LEAST)
    print(f'  readings meeting near >= {NEAR_AT_LEAST} and pierces <= {PIERCES_AT_MOST}: {passing}')
    print(f'    best near among the others within the pierce threshold: {near_only[:1]}; '
          f'fewest pierces among the others within the near threshold: {pierce_only[:1]}')
    replaced = totals[REPLACED]
    own = replaced[0] <= REPLACED_NEAR_AT_MOST and replaced[1] >= REPLACED_PIERCES_AT_LEAST
    product = replaced[0] >= NEAR_AT_LEAST or replaced[1] <= PIERCES_AT_MOST
    print(f'  replaced reading: meets near <= {REPLACED_NEAR_AT_MOST} and pierces >= {REPLACED_PIERCES_AT_LEAST}: '
          f'{own}; meets either product threshold: {product}')
    return int(passing != [CORRECTED]) + int(not own) + int(product)


def main(argv):
    if len(argv) != 2:
        sys.stderr.write(__doc__)
        return 2
    failures = 0
    for label, dtype in (('float64', np.float64), ('float32 (System.Numerics precision)', np.float32)):
        per_map, readings, yaw = measure(argv[1], dtype)
        failures += report(label, per_map, readings)
        report_yaw(yaw)
        sys.stdout.flush()
    print('the thresholds discriminate in both precisions' if failures == 0 else f'{failures} threshold check(s) FAILED')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
