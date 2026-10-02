# SPDX-License-Identifier: 0BSD
"""What an EMPTY Redguard ROB segment's 80-byte header says about the mesh it stands for.

This is a receipt behind RedguardMeshLibrary's placeholder rule, written from the file layouts rather than by
calling BMT's C# readers. rob_placeholder_census.py (beside it) finds that every one of the 1,203 empty segments
has a same-named loose 3dart\\<NAME>.3DC and no other source. This script tests whether the empty header
DESCRIBES that .3DC, using the 4,667 full segments to establish the header fields on an independent population
first:

  header +13        u8  2 on an empty segment (the "type 512"), 0 on an inline mesh, 1 on MENU.ROB's four pages
  header +19        u8  plane count (low byte)        -> payload/.3DC header +8
  header +23        u8  frame count                    -> 1 for a static mesh, .3DC header dword +16
  header +28..+36   i32 total extent x, y, z           == a + b
  header +40..+48   i32 zero
  header +52..+60   i32 a = max(0, floor(max / 256))   per axis over the mesh's points
  header +64..+72   i32 b = max(0, floor(-min / 256))  per axis

(the same 12-dword row the RGM MPSZ chunk carries). For an empty segment the points are the loose .3DC's, and
several readings of them are scored so the control can discriminate:
  keyframe   = frame 0, int32 triples at frame-table record 0's vertex offset (Redguard3DcFile.Keyframe)
  frame 1    = the second pose (int32 pose, or int16 deltas added to the keyframe)
  some frame = any single pose
  union      = the union over every pose
  as-3D      = int32 triples at header +48, what a plain .3D reader takes
and a chance control counts, for each empty header, how many OTHER loose .3DC files' frame 1 reproduces it. It
also records which frame, if any, each loose .3DC's own header offsets (+48 points, +52 normals, +24 plane data)
name in its frame table.

Usage, from the repository root:

    python tools/scripts/redguard/segment_header_vs_mesh.py <data root> [--out <receipts.json>]

<data root> is a Redguard install's data directory, the one holding WORLD.INI, maps and 3dart. Read-only. The
summary goes to standard output; --out also writes the full receipts (every miss) to that file.
"""
import argparse
import collections
import json
import os
import struct
import sys

NUL3 = bytes(3)


def floor_div(v, d):
    return v // d  # Python floors toward -inf, which is the floor the MPSZ note states


def bbox_row(points):
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    zs = [p[2] for p in points]
    a = [max(0, floor_div(max(v), 256)) for v in (xs, ys, zs)]
    b = [max(0, floor_div(-min(v), 256)) for v in (xs, ys, zs)]
    return tuple(a[i] + b[i] for i in range(3)) + (0, 0, 0) + tuple(a) + tuple(b)


def int32_points(data, offset, count):
    return [struct.unpack_from('<3i', data, offset + 12 * i) for i in range(count)]


def three_dc_frames(data):
    """Every pose of a .3DC, by the documented layout. The frame table (at the frame block's dword 0) is followed
    directly by the plane list (header +60), so its record width is (planeList - table) / (4 x frames): 3 dwords
    means int16 deltas added to the keyframe, 4 dwords int32 poses (the 147/147 width-predicts-frames rule)."""
    points = struct.unpack_from('<i', data, 4)[0]
    frames = struct.unpack_from('<i', data, 16)[0]
    block = struct.unpack_from('<i', data, 20)[0]
    table = struct.unpack_from('<i', data, block)[0]
    plane_list = struct.unpack_from('<i', data, 60)[0]
    width, rem = divmod(plane_list - table, 4 * frames)
    assert rem == 0 and width in (3, 4), (width, rem)
    offsets = [struct.unpack_from('<i', data, table + 4 * width * f)[0] for f in range(frames)]
    key = int32_points(data, offsets[0], points)
    poses = [key]
    for o in offsets[1:]:
        if width == 4:
            poses.append(int32_points(data, o, points))
        else:
            deltas = [struct.unpack_from('<3h', data, o + 6 * i) for i in range(points)]
            poses.append([(key[i][0] + deltas[i][0], key[i][1] + deltas[i][1], key[i][2] + deltas[i][2])
                          for i in range(points)])
    return poses, width, width == 4


def header_names_frame(data):
    """The frames whose frame-table record (point, normal, plane-data offsets) equals the .3DC header's own
    +48/+52/+24 triple, and the record width in dwords."""
    frames = struct.unpack_from('<i', data, 16)[0]
    block = struct.unpack_from('<i', data, 20)[0]
    table = struct.unpack_from('<i', data, block)[0]
    plane_list = struct.unpack_from('<i', data, 60)[0]
    width = (plane_list - table) // (4 * frames)
    header = tuple(struct.unpack_from('<i', data, k)[0] for k in (48, 52, 24))
    records = [struct.unpack_from('<3i', data, table + 4 * width * f) for f in range(frames)]
    return [f for f, record in enumerate(records) if record == header], width


def segment_name(header):
    end = header.find(0, 4, 12)
    return header[4:12 if end < 0 else end].decode('ascii')


def measure(data_root):
    art = os.path.join(data_root, '3dart')
    maps = os.path.join(data_root, 'maps')
    files = {n.upper(): os.path.join(art, n) for n in os.listdir(art)}
    map_stems = {os.path.splitext(n)[0].upper() for n in os.listdir(maps) if n.upper().endswith('.RGM')}

    # Every loose .3DC once: frame count, plane count and the row each reading gives.
    loose = {}
    for upper, path in files.items():
        if upper.endswith('.3DC'):
            with open(path, 'rb') as handle:
                x = handle.read()
            npts, nplanes = struct.unpack_from('<2i', x, 4)
            poses, width, _wide = three_dc_frames(x)
            named, _ = header_names_frame(x)
            loose[upper[:-4]] = {
                'frames': struct.unpack_from('<i', x, 16)[0], 'planes': nplanes, 'width': width,
                'header_names_frames': named,
                'rows': [bbox_row(p) for p in poses],
                'union': bbox_row([q for p in poses for q in p]),
                'as3d': bbox_row(int32_points(x, struct.unpack_from('<i', x, 48)[0], npts)),
            }

    full_score = collections.Counter()
    empty_score = collections.Counter()
    empty_misses = []
    full_misses = []
    byte13 = collections.Counter()
    byte15 = collections.Counter()
    byte20 = collections.Counter()
    full_with_3dc = []

    for upper, path in sorted(files.items()):
        if not upper.endswith('.ROB'):
            continue
        population = 'map' if upper[:-4] in map_stems else 'test'
        with open(path, 'rb') as handle:
            d = handle.read()
        count = struct.unpack_from('<I', d, 8)[0]
        pos = 20
        for _ in range(count):
            h = d[pos:pos + 80]
            name = segment_name(h)
            size = struct.unpack_from('<I', h, 76)[0]
            row = struct.unpack_from('<12i', h, 28)
            planes_byte = h[19]
            frames_byte = h[23]
            kind = 'empty' if size == 0 else 'full'
            byte13[(h[13], kind)] += 1
            byte15[(h[15], kind)] += 1
            byte20[(h[20], kind)] += 1
            if size:
                p = d[pos + 80:pos + 80 + size]
                npts, nplanes = struct.unpack_from('<2i', p, 4)
                pts = int32_points(p, struct.unpack_from('<i', p, 48)[0], npts)
                ok_row = bbox_row(pts) == row
                full_score['n'] += 1
                full_score['row == payload bbox'] += ok_row
                full_score['byte19 == planes & 0xFF'] += planes_byte == nplanes & 0xFF
                full_score['planes > 255'] += nplanes > 255
                full_score['bytes 16..18 zero'] += h[16:19] == NUL3
                full_score['byte23 == 1'] += frames_byte == 1
                if not ok_row:
                    full_misses.append((upper, name, h[13], row, bbox_row(pts)))
                if f'{name.upper()}.3DC' in files:
                    full_with_3dc.append((upper[:-4], name))
            else:
                own = loose[name.upper()]
                tag = f'{population}: '
                frame1 = len(own['rows']) > 1 and own['rows'][1] == row
                empty_score[tag + 'n'] += 1
                empty_score[tag + 'byte23 == .3DC frames'] += frames_byte == own['frames'] & 0xFF
                empty_score[tag + 'byte19 == .3DC planes & 0xFF'] += planes_byte == own['planes'] & 0xFF
                empty_score[tag + '.3DC planes > 255'] += own['planes'] > 255
                empty_score[tag + 'bytes 16..18 zero'] += h[16:19] == NUL3
                empty_score[tag + 'row == keyframe (frame 0)'] += own['rows'][0] == row
                empty_score[tag + 'row == frame 1'] += frame1
                empty_score[tag + 'row == some frame'] += row in own['rows']
                empty_score[tag + 'row == union of frames'] += own['union'] == row
                empty_score[tag + 'row == .3D reading (+48)'] += own['as3d'] == row
                # Chance control: how many OTHER loose .3DC files' frame 1 reproduces this header?
                others = sum(1 for n, o in loose.items()
                             if n != name.upper() and len(o['rows']) > 1 and o['rows'][1] == row)
                empty_score[tag + 'other .3DC whose frame 1 also matches (sum)'] += others
                empty_score[tag + 'headers some OTHER .3DC also matches'] += others > 0
                if not frame1:
                    empty_misses.append((upper[:-4], name, planes_byte, own['planes'], frames_byte, own['frames']))
            pos += 80 + size

    return {
        'full_segments': dict(full_score), 'full_row_misses': full_misses,
        'empty_segments': dict(sorted(empty_score.items())),
        'empty_frame1_misses (rob, name, byte19, .3DC planes, byte23, .3DC frames)': empty_misses,
        'empty_frame1_misses_by_name': dict(collections.Counter(m[1] for m in empty_misses)),
        'byte13': {f'{k[0]} {k[1]}': v for k, v in sorted(byte13.items())},
        'byte15': {f'{k[0]} {k[1]}': v for k, v in sorted(byte15.items())},
        'byte20': {f'{k[0]} {k[1]}': v for k, v in sorted(byte20.items())},
        'full_segments_with_a_loose_3dc_namesake': full_with_3dc,
        'loose_3dc_width': dict(collections.Counter(o['width'] for o in loose.values())),
        'loose_3dc_header_offsets_name': dict(sorted(collections.Counter(
            f"{o['width']}-dword records: "
            + (', '.join(f'frame {f}' for f in o['header_names_frames']) or 'no frame')
            for o in loose.values()).items())),
    }


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.split('\n', 1)[0])
    parser.add_argument('data_root', help='the Redguard data directory (holds WORLD.INI, maps and 3dart)')
    parser.add_argument('--out', help='also write the full receipts, every miss included, to this JSON file')
    args = parser.parse_args(argv[1:])
    result = measure(args.data_root)
    if args.out:
        with open(args.out, 'w', newline='\n') as handle:
            json.dump(result, handle, indent=1)
    json.dump({k: v for k, v in result.items() if 'misses (' not in k}, sys.stdout, indent=1)
    sys.stdout.write('\n')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
