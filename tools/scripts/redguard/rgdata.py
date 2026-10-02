# SPDX-License-Identifier: 0BSD
"""Independent readers for the Redguard data the catacomb placement-sign control uses.

Written from the byte layouts, not by calling BMT's C# readers, so a count it reproduces is a second
measurement rather than the same code run twice:

  RGM: chunks of 4-byte tag + BIG-endian u32 length + payload, ending at a bare "END ".
       MPOB = LE u32 count + count x 66-byte records: +4 u16 type, +6 9-byte object name,
       +15 9-byte mesh name (NUL-terminated or full width), +24 u16 has-mesh, +26 3 x i32 position
       (world units x 256), +38 3 x i32 rotation (2048 per turn).
       MPSO = LE u32 count + count x 66-byte records: +4 12-byte name, +16 3 x i32 position (world
       units), +28 9 x i32 matrix (4.28 fixed point, row-major).
  ROB: "OARC" + BE u32 4 + LE u32 count, "OARD" + BE u32 dataLength, then count x (80-byte segment
       header: LE u32 next, 8-byte name, LE u32 type, ..., LE u32 size at +76) + size bytes, "END ".
  XnGine .3D (Redguard = 8-byte plane header): 64-byte header (+4 points, +8 planes, +48 point
       list, +52 normal list, +60 plane list); a plane = u8 count, u8, u16 texture, 4 bytes, then
       count x (i32 byte offset into the 12-byte point list, i16 u, i16 v); v2.5 stores offset / 3.

A module, not a program: control.py beside it imports it. Every function takes its input path or bytes;
nothing here names an install.
"""
import math
import struct


def chunks(data):
    """First payload per tag, in file order."""
    pos, out = 0, {}
    while pos < len(data):
        if data[pos:pos + 4] == b'END ':
            break
        tag = data[pos:pos + 4].decode('ascii')
        (length,) = struct.unpack('>I', data[pos + 4:pos + 8])
        out.setdefault(tag, data[pos + 8:pos + 8 + length])
        pos += 8 + length
    return out


def cstr(field):
    end = field.find(b'\0')
    return (field if end < 0 else field[:end]).decode('ascii', 'replace')


def placements(rgm):
    mpob = chunks(rgm).get('MPOB', b'')
    if not mpob:
        return []
    (count,) = struct.unpack('<I', mpob[:4])
    assert 4 + count * 66 == len(mpob)
    out = []
    for i in range(count):
        r = mpob[4 + i * 66: 4 + (i + 1) * 66]
        mesh = cstr(r[15:24])
        out.append(dict(
            index=i,
            type=struct.unpack('<H', r[4:6])[0],
            object=cstr(r[6:15]),
            mesh=mesh,
            stem=mesh.split('.', 1)[0],
            has_mesh=struct.unpack('<H', r[24:26])[0] != 0,
            pos=struct.unpack('<3i', r[26:38]),
            rot=struct.unpack('<3i', r[38:50]),
        ))
    return out


def statics(rgm):
    mpso = chunks(rgm).get('MPSO', b'')
    if not mpso:
        return []
    (count,) = struct.unpack('<I', mpso[:4])
    assert 4 + count * 66 == len(mpso)
    out = []
    for i in range(count):
        r = mpso[4 + i * 66: 4 + (i + 1) * 66]
        out.append(dict(
            index=i,
            name=cstr(r[4:16]),
            pos=struct.unpack('<3i', r[16:28]),
            matrix=struct.unpack('<9i', r[28:64]),
        ))
    return out


def rob(path):
    """name (upper) -> (type, size, bytes); the FIRST segment of a name wins, as a name lookup must pick one."""
    with open(path, 'rb') as handle:
        d = handle.read()
    assert d[:4] == b'OARC' and d[12:16] == b'OARD'
    count = struct.unpack('<I', d[8:12])[0]
    pos, segs, order = 20, {}, []
    for _ in range(count):
        h = d[pos:pos + 80]
        name = cstr(h[4:12])
        typ = struct.unpack('<I', h[12:16])[0]
        size = struct.unpack('<I', h[76:80])[0]
        assert struct.unpack('<I', h[0:4])[0] == 80 + size
        body = d[pos + 80: pos + 80 + size]
        order.append((name, typ, size))
        segs.setdefault(name.upper(), (typ, size, body))
        pos += 80 + size
    assert d[pos:pos + 4] == b'END '
    return segs, order


def mesh(body):
    """Points (native units) and planes (list of point-index lists) of one .3D record."""
    tag = body[:4]
    npts, nplanes = struct.unpack('<2i', body[4:12])
    point_list, normal_list = struct.unpack('<2i', body[48:56])
    plane_list = struct.unpack('<i', body[60:64])[0]
    pts = [struct.unpack('<3i', body[point_list + 12 * i: point_list + 12 * i + 12]) for i in range(npts)]
    normals = [struct.unpack('<3i', body[normal_list + 12 * i: normal_list + 12 * i + 12]) for i in range(nplanes)]
    planes = []
    pos = plane_list
    for _ in range(nplanes):
        n = body[pos]
        pos += 8
        idx = []
        for _q in range(n):
            off = struct.unpack('<i', body[pos:pos + 4])[0]
            if tag == b'v2.5':
                off *= 3
            assert off % 12 == 0 and 0 <= off // 12 < npts
            idx.append(off // 12)
            pos += 8
        planes.append(idx)
    return pts, planes, normals


def single(value):
    """value rounded to the nearest IEEE single, the precision System.Numerics computes in."""
    return struct.unpack('<f', struct.pack('<f', value))[0]


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _dot(a, b, precision=float):
    return precision(precision(precision(a[0] * b[0]) + precision(a[1] * b[1])) + precision(a[2] * b[2]))


def corner_polygon(points, single_precision=False):
    """The daggerfall-unity corner filter the decomposer ports: keep index (i+1) mod n when the angle AT
    point i between the edges to i+1 and i+2 exceeds 0.001 rad; the kept list starts at index 1.
    single_precision rounds the lengths and the cosine the way the C# decomposer's float Vector3 does
    (the arccosine itself is a double there too)."""
    p = single if single_precision else float
    n = len(points)
    keep = []
    for i in range(n):
        v0 = points[i]
        v1 = points[(i + 1) % n]
        v2 = points[(i + 2) % n]
        l0, l1 = _sub(v1, v0), _sub(v2, v0)
        lengths = p(p(math.sqrt(_dot(l0, l0, p))) * p(math.sqrt(_dot(l1, l1, p))))
        if lengths > 0:
            c = max(-1.0, min(1.0, p(_dot(l0, l1, p) / lengths)))
            angle = math.acos(c)
            if angle > 0.001:
                keep.append((i + 1) % n)
    return [points[k] for k in keep]


def triangles(pts, planes, single_precision=False):
    """Native-unit triangles as XnGineMeshDecomposer emits them: 3-point planes as stored, larger ones
    corner-filtered and then fanned from their first kept point."""
    out = []
    for idx in planes:
        if len(idx) < 3:
            continue
        poly = [pts[k] for k in idx]
        if len(poly) > 3:
            poly = corner_polygon(poly, single_precision)
            if len(poly) < 3:
                continue
        for i in range(1, len(poly) - 1):
            out.append((poly[0], poly[i], poly[i + 1]))
    return out
