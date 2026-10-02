# SPDX-License-Identifier: 0BSD
"""The Redguard catacomb placement-sign control, reproduced from the raw CATACOMB.RGM and CATACOMB.ROB bytes.

This is the receipt behind RedguardSceneRetailTests.PlacementSignControl_Catacomb..., written from the byte
layouts (rgdata.py beside it) rather than by calling BMT's C# readers.

Usage, from the repository root:

    python tools/scripts/redguard/control.py <data root>

<data root> is a Redguard install's data directory, the one holding WORLD.INI, maps and 3dart (on Steam,
"The Elder Scrolls Adventures Redguard/Redguard"). Read-only; it writes nothing.

What it computes, as the test does:
  - Population: every MPOB placement of type 1 or 257 with a mesh whose masked rotation (11 bits a component) is
    non-zero, whose mesh stem names a non-empty segment of the map's own ROB, and whose mesh has points.
  - Surface: the MPSO statics' triangles as XnGineMeshDecomposer emits them, each placed by its stored 4.28
    row-major matrix (world = M . p) plus its world-unit position.
  - Near: the test's StaticSurface.IsNear. The point lies within 6 units of a triangle's PLANE, its projection
    falls inside the triangle with 2% barycentric slack, and the triangle is reachable through a 64-unit grid
    (its bounding box overlaps the point's cell or a neighbor).
  - Readings: all 48 compositions of the three axis rotations (6 orders x 8 sign patterns), in column form with
    the standard right-handed Rx, Ry and Rz. Component 0 drives X (pitch), 1 drives Y (yaw), 2 drives Z (roll).
    The engine's reading, RedguardSceneAssembler.PlacementRotation, is Ry(-y) . Rx(-x) . Rz(-z), the matrix
    RG.EXE's FUN_00082fc2 builds (placement_rotation.py beside this checks that against the executable); the
    test's un-negated rival is the same matrix with no angle negated, Ry(+y) . Rx(+x) . Rz(+z). Until
    2026-09-28 the product read Ry(-y) . Rx(+x) . Rz(+z) and the rival Ry(+y) . Rx(-x) . Rz(-z); this control
    sees the yaw sign only, so it scores the two pairs almost alike (the 48-reading sweep prints both).

It prints every figure twice: once in float64 and once in float32, the precision System.Numerics computes in.
A point that sits on the 6-unit or the 2% boundary can fall either side depending on rounding, so the two runs
can differ by one or so on a count, and the test's comment states the range both give. The float32 run rounds
every operation to single precision; it cannot mirror the JIT's instruction choice (fused multiply-adds, for
one), so it is a second reading, not a bit-exact copy of the C#.
"""
import itertools
import math
import os
import sys

import numpy as np

import rgdata as R

MASK = 0x7FF
UNITS_PER_TURN = 2048
POSITION_DIVISOR = 256
POINT_DIVISOR = 256
MATRIX_ONE = 268435456
CELL = 64
TOLERANCE = 6
SLACK = 0.02
ENGINE = 'Ry(-y)Rx(-x)Rz(-z)'
RIVAL = 'Ry(+y)Rx(+x)Rz(+z)'
AXIS_COMPONENT = {'x': 0, 'y': 1, 'z': 2}


def rotation(axis, angle, dtype):
    """The standard right-handed rotation about one axis, column form (world = M . p)."""
    c = dtype(math.cos(float(angle)))
    s = dtype(math.sin(float(angle)))
    if axis == 'x':
        m = [[1, 0, 0], [0, c, -s], [0, s, c]]
    elif axis == 'y':
        m = [[c, 0, s], [0, 1, 0], [-s, 0, c]]
    else:
        m = [[c, -s, 0], [s, c, 0], [0, 0, 1]]
    return np.array(m, dtype=dtype)


def angles(rot, dtype):
    """The masked components in radians: the int times 2 pi / 2048, rounded to dtype as the C# float is."""
    scale = dtype(dtype(2 * math.pi) / dtype(UNITS_PER_TURN))
    return tuple(dtype(dtype(v & MASK) * scale) for v in rot)


def compositions():
    """name -> (order, signs): every order of the three axes and every sign pattern, 48 readings."""
    out = {}
    for order in itertools.permutations('yxz'):
        for signs in itertools.product((1, -1), repeat=3):
            name = ''.join(f"R{axis}({'+' if sign > 0 else '-'}{axis})" for axis, sign in zip(order, signs))
            out[name] = (order, signs)
    return out


def matrix(rot, order, signs, dtype):
    """One reading's rotation for a stored rotation; order lists the factors left to right, so the RIGHTMOST
    applies first."""
    a = angles(rot, dtype)
    m = np.eye(3, dtype=dtype)
    for axis, sign in zip(order, signs):
        m = (m @ rotation(axis, dtype(sign) * a[AXIS_COMPONENT[axis]], dtype)).astype(dtype)
    return m


def dot3(u, v):
    """Row-wise dot products summed left to right, the order Vector3.Dot uses."""
    return (u[:, 0] * v[:, 0] + u[:, 1] * v[:, 1]) + u[:, 2] * v[:, 2]


def cell(value, dtype):
    return int(math.floor(float(dtype(value) / dtype(CELL))))


class Surface:
    """The static triangles, binned by 64-unit grid cell, with the test's plane-and-projection near rule."""

    def __init__(self, tris, dtype):
        t = np.asarray(tris, dtype=dtype)
        a, b, c = t[:, 0], t[:, 1], t[:, 2]
        n = np.cross(b - a, c - a).astype(dtype)
        ln = np.sqrt(dot3(n, n)).astype(dtype)
        ok = ln > 0
        a, b, c, n, ln = a[ok], b[ok], c[ok], n[ok], ln[ok]
        self.dtype = dtype
        self.a = a
        self.n = (n / ln[:, None]).astype(dtype)
        self.v0 = (b - a).astype(dtype)
        self.v1 = (c - a).astype(dtype)
        self.d00 = dot3(self.v0, self.v0)
        self.d01 = dot3(self.v0, self.v1)
        self.d11 = dot3(self.v1, self.v1)
        self.den = (self.d00 * self.d11 - self.d01 * self.d01).astype(dtype)
        lo = np.minimum(np.minimum(a, b), c)
        hi = np.maximum(np.maximum(a, b), c)
        grid = {}
        for i in range(len(a)):
            ranges = [range(cell(lo[i, k], dtype), cell(hi[i, k], dtype) + 1) for k in range(3)]
            for key in itertools.product(*ranges):
                grid.setdefault(key, []).append(i)
        self.grid = {k: np.array(v, dtype=np.int64) for k, v in grid.items()}
        self.count = len(a)

    def near(self, p):
        """StaticSurface.IsNear(p, 6)."""
        d_ = self.dtype
        cx, cy, cz = (cell(v, d_) for v in p)
        parts = [self.grid[k] for k in ((cx + dx, cy + dy, cz + dz)
                                        for dx in (-1, 0, 1) for dy in (-1, 0, 1) for dz in (-1, 0, 1))
                 if k in self.grid]
        if not parts:
            return False
        idx = np.unique(np.concatenate(parts))
        p = np.asarray(p, dtype=d_)[None, :]
        a, n = self.a[idx], self.n[idx]
        d = dot3((p - a).astype(d_), n)
        m = np.abs(d) <= d_(TOLERANCE)
        if not m.any():
            return False
        idx, a, n, d = idx[m], a[m], n[m], d[m]
        proj = (p - d[:, None] * n).astype(d_)
        v2 = (proj - a).astype(d_)
        d20 = dot3(v2, self.v0[idx])
        d21 = dot3(v2, self.v1[idx])
        den = self.den[idx]
        ok = den != 0
        safe = np.where(ok, den, d_(1))
        u = ((self.d11[idx] * d20 - self.d01[idx] * d21) / safe).astype(d_)
        v = ((self.d00[idx] * d21 - self.d01[idx] * d20) / safe).astype(d_)
        inside = (u >= d_(-SLACK)) & (v >= d_(-SLACK)) & ((u + v).astype(d_) <= d_(1 + SLACK))
        return bool(np.any(ok & inside))


def find(directory, name):
    """A file by name, without regard to case (retail ships ISLAND.ROB beside catacomb.ROB)."""
    for entry in os.listdir(directory):
        if entry.upper() == name.upper():
            return os.path.join(directory, entry)
    raise FileNotFoundError(os.path.join(directory, name))


def load(data_root, stem='CATACOMB'):
    """The map's placements and statics, and its own ROB's segments by upper-case name."""
    with open(find(os.path.join(data_root, 'maps'), stem + '.RGM'), 'rb') as handle:
        rgm = handle.read()
    segs, _ = R.rob(find(os.path.join(data_root, '3dart'), stem + '.ROB'))
    return R.placements(rgm), R.statics(rgm), segs


def static_triangles(statics, segs, dtype):
    """World-unit triangles of every static that names a non-empty segment."""
    single = dtype == np.float32
    out = []
    cache = {}
    for s in statics:
        key = s['name'].upper()
        seg = segs.get(key)
        if seg is None or seg[1] == 0:
            continue
        if key not in cache:
            pts, planes, _ = R.mesh(seg[2])
            native = np.array(R.triangles(pts, planes, single), dtype=dtype).reshape(-1, 3, 3)
            cache[key] = (native / dtype(POINT_DIVISOR)).astype(dtype)
        m = (np.array(s['matrix'], dtype=dtype).reshape(3, 3) / dtype(MATRIX_ONE)).astype(dtype)
        out.append(((cache[key] @ m.T).astype(dtype) + np.array(s['pos'], dtype=dtype)).astype(dtype))
    return np.concatenate(out)


def population(placements, segs):
    """The test's population: (placement, native points) for each placement the control scores."""
    out = []
    for p in placements:
        if p['type'] not in (1, 257) or not p['has_mesh']:
            continue
        if all((v & MASK) == 0 for v in p['rot']):
            continue
        seg = segs.get(p['stem'].upper())
        if seg is None or seg[1] == 0:
            continue
        pts, _planes, _normals = R.mesh(seg[2])
        if pts:
            out.append((p, pts))
    return out


def yaw_only(p):
    """True when the placement turns about Y alone (masked pitch and roll both zero)."""
    return [(v & MASK) != 0 for v in p['rot']] == [False, True, False]


def measure(placements, statics, segs, dtype):
    """Per placement, the near count under each of the 48 readings, in one precision."""
    surface = Surface(static_triangles(statics, segs, dtype), dtype)
    readings = compositions()
    per = []
    for p, pts in population(placements, segs):
        local = (np.array(pts, dtype=dtype) / dtype(POINT_DIVISOR)).astype(dtype)
        pos = (np.array(p['pos'], dtype=dtype) / dtype(POSITION_DIVISOR)).astype(dtype)
        row = {'placement': p, 'points': len(pts)}
        for name, (order, signs) in readings.items():
            m = matrix(p['rot'], order, signs, dtype)
            world = ((local @ m.T).astype(dtype) + pos).astype(dtype)
            row[name] = sum(1 for w in world if surface.near(w))
        per.append(row)
    return surface.count, per, readings


def report(label, triangles, per, readings):
    def total(rows, name):
        return sum(r[name] for r in rows)

    def span(values):
        return f'{min(values):,} to {max(values):,}'

    yaw = [r for r in per if yaw_only(r['placement'])]
    other = [r for r in per if not yaw_only(r['placement'])]
    same = [n for n in readings if 'Ry(-y)' in n]
    flipped = [n for n in readings if 'Ry(+y)' in n]
    print(label)
    print(f'  static triangles {triangles:,}')
    print(f"  population {len(per)} placements, {sum(r['points'] for r in per):,} points")
    print(f'  engine {ENGINE} {total(per, ENGINE):,} near; rival {RIVAL} {total(per, RIVAL):,} near')
    print(f"  yaw-only {len(yaw)} placements ({sum(r['points'] for r in yaw):,} points): "
          f'engine {total(yaw, ENGINE):,}, rival {total(yaw, RIVAL):,}')
    print(f"  pitch or roll present {len(other)} placements ({sum(r['points'] for r in other):,} points): "
          f'engine {total(other, ENGINE):,}, rival {total(other, RIVAL):,}')
    print(f'  48 readings, all placements: the {len(same)} keeping the engine yaw '
          f'{span([total(per, n) for n in same])}, the {len(flipped)} flipping it '
          f'{span([total(per, n) for n in flipped])}')
    print(f'  48 readings, yaw-only placements: same yaw {span([total(yaw, n) for n in same])}, '
          f'flipped {span([total(yaw, n) for n in flipped])}')
    print(f'  48 readings, pitch or roll placements: {span([total(other, n) for n in readings])}')
    for name in sorted(readings, key=lambda n: -total(per, n)):
        tag = '  <- engine' if name == ENGINE else '  <- rival' if name == RIVAL else ''
        print(f'    {name:20s} {total(per, name):5,}{tag}')


def main(argv):
    if len(argv) != 2:
        sys.stderr.write(__doc__)
        return 2
    placements, statics, segs = load(argv[1])
    for label, dtype in (('float64', np.float64), ('float32 (System.Numerics precision)', np.float32)):
        triangles, per, readings = measure(placements, statics, segs, dtype)
        report(label, triangles, per, readings)
        sys.stdout.flush()
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
