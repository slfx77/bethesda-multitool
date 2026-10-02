# SPDX-License-Identifier: 0BSD
"""RG.EXE's placement matrix, ported from its instructions and checked against the retail executable.

This is the receipt behind RedguardSceneAssembler.PlacementRotation and the probe literals in
RedguardSceneAssemblerTests. It reads the sine table out of the user's own RG.EXE and runs a port of the
four routines that build an MPOB object's matrix, so the literals come from the engine's arithmetic rather
than from the C# under test.

Usage, from the repository root:

    python tools/scripts/redguard/placement_rotation.py <RG.EXE>

<RG.EXE> is the retail executable (on Steam, "The Elder Scrolls Adventures Redguard/Redguard/RG.EXE",
4,241,144 bytes). Read-only; it writes nothing.

What was read from the instructions (addresses are the object-relative ones Ghidra reports; RG.EXE is a
DOS/4GW linear executable, LE header at e_lfanew, object 1 = code at 0x10000, object 2 = data at 0x120000):

  - The matrix is nine little-endian int32 in 4.28 fixed point, row-major: element M[r][c] at byte
    4 * (3r + c). FUN_000b7a80 loads the identity (0x10000000 at +0x00, +0x10 and +0x20).
  - The sine table is 2,560 int32 at 0x121600: entry k is 2^28 sin(2 pi k / 2048), and entries 2048..2559
    repeat 0..511 so that entry k + 512 is the cosine. Each helper loads c = [a*4 + 0x121E00] and
    s = [a*4 + 0x121600]; in the file bytes the displacements are 0x1E00 and 0x1600, and the object-2 fixups
    add 0x120000.
  - Every product is `imul` then `shrd eax, edx, 0x1C`: the low 32 bits of the signed 64-bit product
    shifted right by 28. The two products of an element are joined by one `add` or `sub`.
  - FUN_000b7ac0 (about Y), for every row r (stores at +0x00/+0x08, +0x0C/+0x14, +0x18/+0x20):
        M[r][0] = c M[r][0] - s M[r][2]      (sub edx, eax at 0xB7B16, 0xB7B57, 0xB7B98)
        M[r][2] = c M[r][2] + s M[r][0]      (add at 0xB7B36, 0xB7B78, 0xB7BB4)
  - FUN_000b7bd0 (about X), for every row r (stores at +0x04/+0x08, +0x10/+0x14, +0x1C/+0x20):
        M[r][1] = c M[r][1] + s M[r][2]      (add edx, eax at 0xB7C27, 0xB7C69, 0xB7CAA)
        M[r][2] = c M[r][2] - s M[r][1]      (sub at 0xB7C48, 0xB7C8A, 0xB7CC6)
  - FUN_000b7ce0 (about Z), for every row r (stores at +0x00/+0x04, +0x0C/+0x10, +0x18/+0x1C):
        M[r][0] = c M[r][0] + s M[r][1]      (add edx, eax at 0xB7D36, 0xB7D77, 0xB7DB8)
        M[r][1] = c M[r][1] - s M[r][0]      (sub at 0xB7D56, 0xB7D98, 0xB7DD4)
    Each helper rewrites one pair of COLUMNS in every row, so it multiplies on the right, M <- M . R(a), and
    all three R(a) are the standard right-handed rotation by +a: Ry = [[c,0,s],[0,1,0],[-s,0,c]],
    Rx = [[1,0,0],[0,c,-s],[0,s,c]], Rz = [[c,-s,0],[s,c,0],[0,0,1]].
  - FUN_00082fc2 stores the three components masked with 0x7FF, loads the identity, then calls the Y helper
    with (-component 1) & 0x7FF, the X helper with (-component 0) & 0x7FF and the Z helper with
    (-component 2) & 0x7FF. The MPOB loader passes it the record's rotation at +0x26 (record +38, +42, +46).
    So M = Ry(-y) . Rx(-x) . Rz(-z).
  - The object transform 0x34E686 computes x' = m00 x + m01 y + m02 z (and so on), world = M . p.

What it checks, each able to fail:
  1. The address mapping: the table at 0x121600 fits 2^28 sin(2 pi k / 2048) to within one unit on all
     2,560 entries, and the same read one page lower or higher does not.
  2. Each helper, run from the identity at every angle 0..2047, equals the standard R(+a); R(-a) matches only
     at 0 and 1,024.
  3. The composition, over 64 pseudo-random stored triples (a fixed seed), matches exactly one of the 48
     readings (6 axis orders x 8 sign patterns): Ry(-y) . Rx(-x) . Rz(-z).
  4. The System.Numerics expression RedguardSceneAssembler.PlacementRotation uses (row vectors,
     Vector3.Transform(p, M) = p . M), CreateRotationZ(-z) * CreateRotationX(-x) * CreateRotationY(-y), equals
     the engine on those triples; the expression it replaced, CreateRotationZ(z) * CreateRotationX(x) *
     CreateRotationY(-y), does not.
Then it prints the engine's M . p for the probe the unit tests use.
"""
import itertools
import math
import random
import struct
import sys

SINE_VA = 0x121600
SINE_ENTRIES = 2560
ONE = 0x10000000
MASK = 0x7FF
UNITS_PER_TURN = 2048
PROBE = (100.0, 20.0, 3.0)
PROBE_ROTATIONS = [(0, 512, 0), (512, 0, 0), (0, 0, 512), (256, 640, 128), (0, -512, 0), (0, 1536, 0),
                   (0, 2048, 0)]


class LinearExecutable:
    """The object table and page map of a DOS/4GW LE file: enough to read an object-relative address."""

    def __init__(self, data):
        self.data = data
        le = struct.unpack_from('<I', data, 0x3C)[0]
        if data[le:le + 2] != b'LE':
            raise ValueError('not a linear executable (no LE signature at e_lfanew)')

        def u32(offset):
            return struct.unpack_from('<I', data, le + offset)[0]

        self.page_size = u32(0x28)
        pages = u32(0x14)
        page_map = le + u32(0x48)
        self.data_pages = u32(0x80)
        # A page map entry is a 3-byte page number, high byte first, then a flags byte.
        self.pages = [((data[page_map + 4 * i] << 16) | (data[page_map + 4 * i + 1] << 8) | data[page_map + 4 * i + 2],
                       data[page_map + 4 * i + 3]) for i in range(pages)]
        table = le + u32(0x40)
        self.objects = [struct.unpack_from('<6I', data, table + 24 * i)[:5] for i in range(u32(0x44))]

    def file_offset(self, va):
        for size, base, _flags, first_page, page_count in self.objects:
            if base <= va < base + size:
                page = (va - base) // self.page_size
                if page >= page_count:
                    raise ValueError(f'{va:#x} lies in the zero-filled tail of its object')
                number, flags = self.pages[first_page - 1 + page]
                if flags != 0:
                    raise ValueError(f'{va:#x} lies on a page with flags {flags:#x}')
                return self.data_pages + (number - 1) * self.page_size + (va - base) % self.page_size
        raise ValueError(f'{va:#x} is in no object')

    def int32s(self, va, count):
        at = self.file_offset(va)
        return list(struct.unpack_from(f'<{count}i', self.data, at))


def i32(value):
    value &= 0xFFFFFFFF
    return value - (1 << 32) if value & 0x80000000 else value


def fixed_mul(a, b):
    """imul r32, then shrd eax, edx, 0x1C: bits 28..59 of the signed 64-bit product."""
    return i32((a * b) >> 28)


class Engine:
    """Ports of FUN_000b7a80, FUN_000b7ac0, FUN_000b7bd0, FUN_000b7ce0 and FUN_00082fc2."""

    def __init__(self, sine):
        self.sine = sine

    @staticmethod
    def identity():
        return [ONE, 0, 0, 0, ONE, 0, 0, 0, ONE]

    def about_y(self, m, a):
        c, s = self.sine[a + 512], self.sine[a]
        for r in range(3):
            m0, m2 = m[3 * r], m[3 * r + 2]
            m[3 * r] = i32(fixed_mul(m0, c) - fixed_mul(m2, s))
            m[3 * r + 2] = i32(fixed_mul(m2, c) + fixed_mul(m0, s))

    def about_x(self, m, a):
        c, s = self.sine[a + 512], self.sine[a]
        for r in range(3):
            m1, m2 = m[3 * r + 1], m[3 * r + 2]
            m[3 * r + 1] = i32(fixed_mul(m1, c) + fixed_mul(m2, s))
            m[3 * r + 2] = i32(fixed_mul(m2, c) - fixed_mul(m1, s))

    def about_z(self, m, a):
        c, s = self.sine[a + 512], self.sine[a]
        for r in range(3):
            m0, m1 = m[3 * r], m[3 * r + 1]
            m[3 * r] = i32(fixed_mul(m0, c) + fixed_mul(m1, s))
            m[3 * r + 1] = i32(fixed_mul(m1, c) - fixed_mul(m0, s))

    def placement(self, rotation):
        """FUN_00082fc2 on a stored (component 0, 1, 2) triple: the nine 4.28 ints."""
        x, y, z = (v & MASK for v in rotation)
        m = self.identity()
        self.about_y(m, (-y) & MASK)
        self.about_x(m, (-x) & MASK)
        self.about_z(m, (-z) & MASK)
        return m


def as_float(m):
    return [[m[3 * r + c] / ONE for c in range(3)] for r in range(3)]


def standard(axis, t):
    """The standard right-handed rotation about one axis, column form (world = R . p)."""
    c, s = math.cos(t), math.sin(t)
    if axis == 'x':
        return [[1, 0, 0], [0, c, -s], [0, s, c]]
    if axis == 'y':
        return [[c, 0, s], [0, 1, 0], [-s, 0, c]]
    return [[c, -s, 0], [s, c, 0], [0, 0, 1]]


def numerics(axis, t):
    """System.Numerics' Matrix4x4.CreateRotationX/Y/Z 3x3 block (row-vector form), from its source."""
    c, s = math.cos(t), math.sin(t)
    if axis == 'x':
        return [[1, 0, 0], [0, c, s], [0, -s, c]]
    if axis == 'y':
        return [[c, 0, -s], [0, 1, 0], [s, 0, c]]
    return [[c, s, 0], [-s, c, 0], [0, 0, 1]]


def matmul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def transpose(a):
    return [[a[j][i] for j in range(3)] for i in range(3)]


def worst(a, b):
    return max(abs(a[i][j] - b[i][j]) for i in range(3) for j in range(3))


def radians(rotation):
    return [(v & MASK) * 2 * math.pi / UNITS_PER_TURN for v in rotation]


def reading(rotation, order, signs):
    """A column-form composition; order lists the factors left to right, so the rightmost applies first."""
    a = dict(zip('xyz', radians(rotation)))
    m = [[1, 0, 0], [0, 1, 0], [0, 0, 1]]
    for axis, sign in zip(order, signs):
        m = matmul(m, standard(axis, sign * a[axis]))
    return m


def numerics_product(rotation, signs):
    """CreateRotationZ(sz z) * CreateRotationX(sx x) * CreateRotationY(sy y), as the column matrix it applies."""
    x, y, z = radians(rotation)
    sx, sy, sz = signs
    row = matmul(matmul(numerics('z', sz * z), numerics('x', sx * x)), numerics('y', sy * y))
    return transpose(row)


def apply(m, p):
    return tuple(m[r][0] * p[0] + m[r][1] * p[1] + m[r][2] * p[2] for r in range(3))


def fit(sine):
    return max(abs(sine[k] - (1 << 28) * math.sin(2 * math.pi * (k % UNITS_PER_TURN) / UNITS_PER_TURN))
               for k in range(SINE_ENTRIES))


def main(argv):
    if len(argv) != 2:
        sys.stderr.write(__doc__)
        return 2
    with open(argv[1], 'rb') as handle:
        exe = LinearExecutable(handle.read())
    failures = 0

    sine = exe.int32s(SINE_VA, SINE_ENTRIES)
    error = fit(sine)
    print(f'1. sine table at {SINE_VA:#x} (file {exe.file_offset(SINE_VA):#x}): worst error {error:.2f} units '
          f'of 2^28 over {SINE_ENTRIES:,} entries')
    failures += error > 1
    for delta in (-exe.page_size, exe.page_size):
        control = fit(exe.int32s(SINE_VA + delta, SINE_ENTRIES))
        print(f'   control, the same read at {SINE_VA + delta:#x}: worst error {control:.3g}')
        failures += control <= 1
    engine = Engine(sine)

    print('2. each helper from the identity, every angle 0..2047, against the standard rotation (tolerance 1e-7)')
    for axis, helper in (('y', engine.about_y), ('x', engine.about_x), ('z', engine.about_z)):
        plus = minus = 0
        for a in range(UNITS_PER_TURN):
            m = engine.identity()
            helper(m, a)
            t = 2 * math.pi * a / UNITS_PER_TURN
            plus += worst(as_float(m), standard(axis, t)) < 1e-7
            minus += worst(as_float(m), standard(axis, -t)) < 1e-7
        print(f'   about {axis.upper()}: equals R(+a) at {plus:,} of 2,048, R(-a) at {minus:,}')
        failures += plus != UNITS_PER_TURN or minus != 2

    rng = random.Random(20260928)
    triples = [tuple(rng.randrange(-4096, 4096) for _ in range(3)) for _ in range(64)]
    engine_matrices = [as_float(engine.placement(t)) for t in triples]
    matches = []
    for order in itertools.permutations('yxz'):
        for signs in itertools.product((1, -1), repeat=3):
            if all(worst(e, reading(t, order, signs)) < 1e-6 for e, t in zip(engine_matrices, triples)):
                matches.append(''.join(f"R{a}({'+' if s > 0 else '-'}{a})" for a, s in zip(order, signs)))
    print(f'3. composition, 64 stored triples: {len(matches)} of 48 readings match: {", ".join(matches)}')
    failures += matches != ['Ry(-y)Rx(-x)Rz(-z)']

    for label, signs, expected in (('CreateRotationZ(-z) * CreateRotationX(-x) * CreateRotationY(-y)', (-1, -1, -1), True),
                                   ('CreateRotationZ(z) * CreateRotationX(x) * CreateRotationY(-y)', (1, -1, 1), False)):
        miss = max(worst(e, numerics_product(t, signs)) for e, t in zip(engine_matrices, triples))
        print(f'4. {label}: worst element error {miss:.2g} against the engine')
        failures += (miss < 1e-6) != expected

    print(f'probe p = {PROBE}, world = M . p with M = FUN_00082fc2')
    for rotation in PROBE_ROTATIONS:
        world = apply(as_float(engine.placement(rotation)), PROBE)
        print(f'   rotation {rotation}: ({world[0]:.4f}, {world[1]:.4f}, {world[2]:.4f})')

    print('all checks pass' if failures == 0 else f'{failures} check(s) FAILED')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
