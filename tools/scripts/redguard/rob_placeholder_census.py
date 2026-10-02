# SPDX-License-Identifier: 0BSD
"""Census of Redguard's EMPTY .ROB segments (size 0 placeholders) and the rule that resolves them.

This is the receipt behind RedguardMeshLibrary's placeholder rule (an empty segment stands for the loose
3dart\\<NAME>.3DC of its name) and the loose-origin pin in RedguardSceneRetailTests.

Written from the file layouts, not from the C# readers:
  ROB  = "OARC" + BE u32 4 + LE u32 count, "OARD" + BE u32 bodyLength, then count x (80-byte segment header +
         payload), then "END ". Segment header: LE u32 next (== 80 + size), 8-byte NUL-padded name, LE u32 type at +12,
         fifteen LE dwords at +16..+72, LE u32 size at +76.
  RGM  = tag(4) + BE u32 length + payload, repeated, then a bare "END ". Values inside are LE.
         RAHD: u32 count, u32 compile word, count x 165-byte objects (label char[9] at +4, animation-mesh count at +33).
         RAAN: per object in RAHD order, animCount x (u32 planeCount, u8 frameCount, u8 flag, cstring path).
         MPOB: u32 count, count x 66 bytes (+4 u16 type, +6 char[9] object, +15 char[9] mesh, +24 u16 has-mesh,
               +26/+30/+34 i32 position x256).
         MPSO: u32 count, count x 66 bytes (+4 char[12] name, +16/+20/+24 i32 position in world units).
         MPRP: u32 count, count x 80 bytes (+34 char[9] anchor mesh, +43 char[9] link mesh).
  TEXTURE.nnn: u16 LE record count at offset 0 (Daggerfall container).
  .3DC / .3D header: +0 version tag, +4 point count, +8 plane count, +60 plane-list offset; Redguard planes use the
         8-byte Daggerfall plane header (u8 corners, u8, u16 texture = archive*128 + record, u32) + 8 bytes a corner.

Usage, from the repository root:

    python tools/scripts/redguard/rob_placeholder_census.py <data root> [--compare <data root>] [--out <file>]

<data root> is a Redguard install's data directory, the one holding WORLD.INI, maps and 3dart (on Steam,
"The Elder Scrolls Adventures Redguard/Redguard"). --compare names a second install's data directory (the 1998
Disc 1 tree, extracted) whose 3dart and maps are compared byte for byte with the first. Read-only; the summary
goes to standard output, and --out also writes the full receipts (one row per placeholder) to that file.
"""
import argparse
import collections
import hashlib
import json
import math
import os
import struct
import sys

from segment_header_vs_mesh import bbox_row, three_dc_frames


def cstr(field):
    end = field.find(b'\0')
    return (field if end < 0 else field[:end]).decode('ascii', 'replace')


def files_ci(directory):
    """Upper-case file name -> real path, for one directory."""
    return {name.upper(): os.path.join(directory, name) for name in os.listdir(directory)}


def read_rob(path):
    d = open(path, 'rb').read()
    assert d[:4] == b'OARC' and struct.unpack('>I', d[4:8])[0] == 4 and d[12:16] == b'OARD', path
    count = struct.unpack('<I', d[8:12])[0]
    body = struct.unpack('>I', d[16:20])[0]
    assert 20 + body + 4 == len(d) and d[-4:] == b'END ', path
    pos, segs = 20, []
    for i in range(count):
        h = d[pos:pos + 80]
        dwords = list(struct.unpack('<20I', h))
        name_raw = h[4:12]
        name = cstr(name_raw)
        size = dwords[19]
        assert dwords[0] == 80 + size, (path, i)
        payload = d[pos + 80:pos + 80 + size]
        segs.append({
            'index': i, 'name': name, 'type': dwords[3], 'size': size,
            'unknown_dwords_16_72': dwords[4:19],
            'tag': payload[:4].decode('ascii', 'replace') if size >= 4 else '',
            'sha1': hashlib.sha1(payload).hexdigest() if size else None,
            'textures': plane_textures(payload)[0] if size else [],
            'bbox_row': list(struct.unpack('<12i', h[28:76])),
            'frames_byte': h[23], 'planes_byte': h[19], 'kind_byte': h[13],
        })
        pos += 80 + size
    assert pos == 20 + body, path
    return segs


def rgm_chunks(data):
    pos, out = 0, {}
    while pos < len(data):
        if data[pos:] == b'END ':
            break
        tag = data[pos:pos + 4].decode('ascii')
        length = struct.unpack('>I', data[pos + 4:pos + 8])[0]
        out.setdefault(tag, data[pos + 8:pos + 8 + length])
        pos += 8 + length
    return out


def read_rgm(path):
    c = rgm_chunks(open(path, 'rb').read())
    rahd = c['RAHD']
    n = struct.unpack('<I', rahd[:4])[0]
    assert 8 + n * 165 == len(rahd)
    objects = []
    for i in range(n):
        r = rahd[8 + i * 165: 8 + (i + 1) * 165]
        objects.append({'label': cstr(r[4:13]), 'anim_count': struct.unpack('<I', r[33:37])[0],
                        'mesh_size_indices': list(struct.unpack('<3i', r[137:149]))})
    raan = c.get('RAAN', b'')
    pos = 0
    for o in objects:
        o['raan'] = []
        for _ in range(o['anim_count']):
            planes, frames, flag = struct.unpack('<IBB', raan[pos:pos + 6])
            end = raan.index(b'\0', pos + 6)
            o['raan'].append({'path': raan[pos + 6:end].decode('ascii'), 'planes': planes, 'frames': frames,
                              'flag': chr(flag)})
            pos = end + 1
    assert pos == len(raan), path
    mpob = c.get('MPOB', b'')
    placements = []
    if mpob:
        cnt = struct.unpack('<I', mpob[:4])[0]
        assert 4 + cnt * 66 == len(mpob)
        for i in range(cnt):
            r = mpob[4 + i * 66: 4 + (i + 1) * 66]
            mesh = cstr(r[15:24])
            placements.append({
                'index': i, 'type': struct.unpack('<H', r[4:6])[0], 'object': cstr(r[6:15]), 'mesh': mesh,
                'stem': mesh.split('.', 1)[0], 'has_mesh': struct.unpack('<H', r[24:26])[0] != 0,
                'mesh_word': struct.unpack('<H', r[24:26])[0],
                'position': list(struct.unpack('<3i', r[26:38])), 'rotation': list(struct.unpack('<3i', r[38:50])),
                'mesh_slot': struct.unpack('<h', r[56:58])[0],
            })
    mpso = c.get('MPSO', b'')
    statics = []
    if mpso:
        cnt = struct.unpack('<I', mpso[:4])[0]
        assert 4 + cnt * 66 == len(mpso)
        for i in range(cnt):
            r = mpso[4 + i * 66: 4 + (i + 1) * 66]
            statics.append({'index': i, 'name': cstr(r[4:16]), 'position': list(struct.unpack('<3i', r[16:28]))})
    mprp = c.get('MPRP', b'')
    ropes = []
    if mprp:
        cnt = struct.unpack('<I', mprp[:4])[0]
        assert 4 + cnt * 80 == len(mprp)
        for i in range(cnt):
            r = mprp[4 + i * 80: 4 + (i + 1) * 80]
            ropes.append({'index': i, 'anchor': cstr(r[34:43]), 'link': cstr(r[43:52])})
    return objects, placements, statics, ropes


def read_mpsz(path):
    """MPSZ: no count, 49-byte rows of u8 0 + 12 x i32 (total, zero, a, b)."""
    mpsz = rgm_chunks(open(path, 'rb').read()).get('MPSZ', b'')
    assert len(mpsz) % 49 == 0
    return [list(struct.unpack('<12i', mpsz[i * 49 + 1:(i + 1) * 49])) for i in range(len(mpsz) // 49)]


def plane_textures(data):
    """Distinct (archive, record) pairs of a .3D/.3DC plane list, Redguard's 8-byte plane header."""
    planes = struct.unpack('<i', data[8:12])[0]
    pos = struct.unpack('<i', data[60:64])[0]
    out = set()
    for _ in range(planes):
        corners = data[pos]
        tex = struct.unpack('<H', data[pos + 2:pos + 4])[0]
        out.add((tex >> 7, tex & 0x7F))
        pos += 8 + corners * 8
    return sorted(out), pos


def rgm_paths(data_root):
    """Every maps\\*.RGM, matched without regard to case."""
    directory = os.path.join(data_root, 'maps')
    return sorted(os.path.join(directory, n) for n in os.listdir(directory) if n.upper().endswith('.RGM'))


def census(data_root, compare_root=None):
    art = os.path.join(data_root, '3dart')
    art_files = files_ci(art)
    loose_by_stem = collections.defaultdict(list)
    for upper, real in art_files.items():
        stem, _, ext = upper.rpartition('.')
        if stem:
            loose_by_stem[stem].append(ext)

    # 1. Every ROB, every segment.
    robs = {}
    for upper, real in sorted(art_files.items()):
        if upper.endswith('.ROB'):
            robs[upper[:-4]] = read_rob(real)

    all_segments = sum(len(s) for s in robs.values())
    empties = [(rob, s) for rob, segs in robs.items() for s in segs if s['size'] == 0]
    type_table = collections.Counter((s['type'], s['size'] == 0) for segs in robs.values() for s in segs)

    # Non-empty segment names across ALL archives (to test "another ROB holds it").
    nonempty_where = collections.defaultdict(list)
    for rob, segs in robs.items():
        for s in segs:
            if s['size']:
                nonempty_where[s['name'].upper()].append(rob)

    # Does any unidentified header dword separate an empty segment from a full one?
    dword_split = {}
    for k in range(15):
        empty_vals = collections.Counter(s['unknown_dwords_16_72'][k] for _, s in empties)
        full_vals = collections.Counter(s['unknown_dwords_16_72'][k] for segs in robs.values() for s in segs
                                        if s['size'])
        dword_split[f'+{16 + 4 * k}'] = {
            'empty_top': empty_vals.most_common(4), 'full_top': full_vals.most_common(4),
            'empty_distinct': len(empty_vals), 'full_distinct': len(full_vals)}

    # 2. Maps and their references.
    maps = {}
    for path in rgm_paths(data_root):
        stem = os.path.splitext(os.path.basename(path))[0].upper()
        maps[stem] = read_rgm(path)
    robs_without_map = sorted(set(robs) - set(maps))

    # 3. Per-empty-placeholder census.
    rows = []
    for rob, s in empties:
        name = s['name'].upper()
        exts = sorted(loose_by_stem.get(name, []))
        others = sorted(r for r in nonempty_where.get(name, []) if r != rob)
        mpob_refs = mpob_mesh_refs = mpso_refs = mprp_refs = raan_refs = 0
        mpob_detail = []
        if rob in maps:
            objects, placements, statics, ropes = maps[rob]
            for p in placements:
                if p['stem'].upper() == name:
                    mpob_refs += 1
                    if p['has_mesh']:
                        mpob_mesh_refs += 1
                        mpob_detail.append({k: p[k] for k in ('index', 'type', 'object', 'mesh', 'position')})
            mpso_refs = sum(1 for st in statics if st['name'].upper() == name)
            mprp_refs = sum((r['anchor'].upper() == name) + (r['link'].upper() == name) for r in ropes)
            raan_refs = sum(1 for o in objects for a in o['raan']
                            if os.path.splitext(a['path'].replace('\\', '/').split('/')[-1])[0].upper() == name)
        rows.append({'rob': rob, 'segment': s['index'], 'name': s['name'], 'type': s['type'],
                     'loose_exts': exts, 'nonempty_in_other_robs': others,
                     'rob_has_map': rob in maps, 'mpob_refs': mpob_refs, 'mpob_has_mesh_refs': mpob_mesh_refs,
                     'mpob_detail': mpob_detail, 'mpso_refs': mpso_refs, 'mprp_refs': mprp_refs,
                     'raan_refs': raan_refs})

    def tally(pred):
        return sum(1 for r in rows if pred(r))

    has3dc = lambda r: '3DC' in r['loose_exts']
    has3d = lambda r: '3D' in r['loose_exts']
    summary = {
        'robs': len(robs), 'maps': len(maps), 'segments': all_segments, 'empty': len(empties),
        'type_by_emptiness': {f'type {t} {"empty" if e else "full"}': n for (t, e), n in sorted(type_table.items())},
        'empty_with_loose_3dc': tally(has3dc),
        'empty_with_loose_3d_only': tally(lambda r: has3d(r) and not has3dc(r)),
        'empty_with_loose_3dc_and_3d': tally(lambda r: has3d(r) and has3dc(r)),
        'empty_with_other_loose_ext_only': tally(lambda r: r['loose_exts'] and not has3d(r) and not has3dc(r)),
        'empty_with_no_loose_file': tally(lambda r: not r['loose_exts']),
        'empty_nonempty_elsewhere': tally(lambda r: bool(r['nonempty_in_other_robs'])),
        'empty_no_loose_and_nonempty_elsewhere': tally(lambda r: not r['loose_exts'] and r['nonempty_in_other_robs']),
        'empty_no_loose_no_elsewhere': tally(lambda r: not r['loose_exts'] and not r['nonempty_in_other_robs']),
        'empty_in_map_robs': tally(lambda r: r['rob_has_map']),
        'empty_named_by_mpob_has_mesh': tally(lambda r: r['mpob_has_mesh_refs'] > 0),
        'mpob_has_mesh_placements_on_empties': sum(r['mpob_has_mesh_refs'] for r in rows),
        'mpob_any_placements_on_empties': sum(r['mpob_refs'] for r in rows),
        'mpso_statics_on_empties': sum(r['mpso_refs'] for r in rows),
        'mprp_refs_on_empties': sum(r['mprp_refs'] for r in rows),
        'empty_named_by_raan': tally(lambda r: r['raan_refs'] > 0),
        'raan_refs_on_empties': sum(r['raan_refs'] for r in rows),
        'empty_by_type_and_loose': dict(collections.Counter(
            f"type {r['type']} / {'3DC' if has3dc(r) else '3D' if has3d(r) else ('+'.join(r['loose_exts']) or 'none')}"
            for r in rows)),
        'robs_without_map': robs_without_map,
    }

    # Full segments that also have a loose namesake: which kind, and is the payload the same bytes?
    full_with_loose = collections.Counter()
    full_loose_identical = collections.Counter()
    for rob, segs in robs.items():
        for s in segs:
            if not s['size']:
                continue
            for ext in ('3DC', '3D'):
                real = art_files.get(f"{s['name'].upper()}.{ext}")
                if real:
                    full_with_loose[f"type {s['type']} full + loose .{ext}"] += 1
                    same = hashlib.sha1(open(real, 'rb').read()).hexdigest() == s['sha1']
                    full_loose_identical[f".{ext} byte-identical {same}"] += 1
    summary['full_segments_with_loose_namesake'] = dict(full_with_loose)
    summary['full_segment_vs_loose_bytes'] = dict(full_loose_identical)

    # 4. RAAN against the ROB: every RAAN name, is its own-map segment empty, full or absent?
    raan_vs_rob = collections.Counter()
    raan_loose = collections.Counter()
    raan_cross = collections.Counter()
    raan_not_on_empty = []
    for stem, (objects, placements, statics, ropes) in maps.items():
        seg_by_name = {s['name'].upper(): s for s in robs[stem]}
        for o in objects:
            for a in o['raan']:
                n = os.path.splitext(a['path'].replace('\\', '/').split('/')[-1])[0].upper()
                s = seg_by_name.get(n)
                where = 'absent' if s is None else 'empty' if s['size'] == 0 else 'full'
                loose = '3DC' if f'{n}.3DC' in art_files else '3D' if f'{n}.3D' in art_files else 'none'
                raan_vs_rob[where] += 1
                raan_loose[loose] += 1
                raan_cross[f'{where} segment / loose {loose}'] += 1
                if where != 'empty':
                    raan_not_on_empty.append((stem, o['label'], a['path'], where, loose, a['frames']))
    summary['raan_entries_vs_own_rob'] = dict(raan_vs_rob)
    summary['raan_entries_loose_file'] = dict(raan_loose)
    summary['raan_segment_kind_by_loose_file'] = dict(raan_cross)
    summary['raan_entries_not_on_an_empty_segment_by_frames'] = dict(
        collections.Counter(f'frames {t[5]} / {t[3]} / loose {t[4]}' for t in raan_not_on_empty))

    # 5. Converse: loose .3DC/.3D no empty placeholder names (in any ROB), and no segment at all.
    placeholder_names = {r['name'].upper() for r in rows}
    any_segment_names = {s['name'].upper() for segs in robs.values() for s in segs}
    raan_names = {os.path.splitext(a['path'].replace('\\', '/').split('/')[-1])[0].upper()
                  for objects, *_ in maps.values() for o in objects for a in o['raan']}
    converse = {}
    for ext in ('3DC', '3D'):
        stems = sorted(u[:-len(ext) - 1] for u in art_files if u.endswith('.' + ext))
        converse[ext] = {
            'loose': len(stems),
            'named_by_a_placeholder': sum(1 for s in stems if s in placeholder_names),
            'not_named_by_a_placeholder': [s for s in stems if s not in placeholder_names],
            'not_named_by_any_segment': [s for s in stems if s not in any_segment_names],
            'named_by_raan': sum(1 for s in stems if s in raan_names),
            'not_named_by_a_placeholder_but_by_raan': [s for s in stems
                                                       if s not in placeholder_names and s in raan_names],
            'named_by_nothing (no segment, no RAAN)': [s for s in stems
                                                      if s not in any_segment_names and s not in raan_names],
        }
    summary['converse'] = {ext: {k: (len(v) if isinstance(v, list) else v) for k, v in c.items()}
                           for ext, c in converse.items()}

    # 6. Placement and static counts under the current rule and under the placeholder rule.
    def resolve(stem_map, name, rule):
        seg = next((s for s in robs[stem_map] if s['name'].upper() == name.upper()), None)
        if seg is not None and seg['size']:
            return 'rob'
        if rule == 'current':
            return None
        if seg is not None and seg['size'] == 0:
            if f'{name.upper()}.3DC' in art_files:
                return 'loose-3dc'
            if f'{name.upper()}.3D' in art_files:
                return 'loose-3d'
        return None

    counts = {}
    for rule in ('current', 'placeholder'):
        per_map = {}
        tot = collections.Counter()
        for stem, (objects, placements, statics, ropes) in maps.items():
            placed = [p for p in placements if p['has_mesh'] and p['stem']]
            res = [resolve(stem, p['stem'], rule) for p in placed]
            st = [resolve(stem, s['name'], rule) for s in statics]
            m = {'statics': len(statics), 'statics_resolved': sum(r is not None for r in st),
                 'placements': len(placed), 'placements_resolved': sum(r is not None for r in res),
                 'via_loose': sum(r in ('loose-3dc', 'loose-3d') for r in res + st)}
            per_map[stem] = m
            tot.update(m)
        counts[rule] = {'total': dict(tot), 'ISLAND': per_map['ISLAND'], '_per_map': per_map}
    moved = sorted(k for k in counts['current']['_per_map']
                   if counts['current']['_per_map'][k] != counts['placeholder']['_per_map'][k])
    summary['counts'] = {rule: {'total': c['total'], 'ISLAND': c['ISLAND']} for rule, c in counts.items()}
    summary['maps_whose_counts_move'] = moved
    summary['map_count_deltas'] = {
        k: {f: counts['placeholder']['_per_map'][k][f] - counts['current']['_per_map'][k][f]
            for f in counts['current']['_per_map'][k]} for k in moved}

    # 7. ISLAND origin extents (statics in world units, placements /256) with and without the placeholder rule.
    objects, placements, statics, ropes = maps['ISLAND']

    def extents(rule):
        origins = [tuple(s['position']) for s in statics if resolve('ISLAND', s['name'], rule)]
        origins += [tuple(v / 256 for v in p['position']) for p in placements
                    if p['has_mesh'] and p['stem'] and resolve('ISLAND', p['stem'], rule)]
        return {'count': len(origins),
                'x': [min(o[0] for o in origins), max(o[0] for o in origins)],
                'y': [min(o[1] for o in origins), max(o[1] for o in origins)],
                'z': [min(o[2] for o in origins), max(o[2] for o in origins)]}

    summary['island_origin_extents'] = {'current': extents('current'), 'placeholder': extents('placeholder')}

    # 8. The placeholder mesh the rule brings in: its header and its materials against TEXTURE.nnn.
    brought = {}
    for r in rows:
        if r['mpob_has_mesh_refs'] or r['mpso_refs']:
            for ext in ('3DC', '3D'):
                real = art_files.get(f"{r['name'].upper()}.{ext}")
                if real:
                    data = open(real, 'rb').read()
                    textures, plane_end = plane_textures(data)
                    in_range = []
                    for a, rec in textures:
                        tpath = art_files.get(f'TEXTURE.{a:03d}')
                        count = struct.unpack('<H', open(tpath, 'rb').read(2))[0] if tpath else None
                        in_range.append({'archive': a, 'record': rec, 'archive_ships': tpath is not None,
                                         'record_count': count, 'in_range': count is not None and rec < count})
                    brought[f"{r['rob']}:{r['name']}.{ext}"] = {
                        'bytes': len(data), 'tag': data[:4].decode('ascii', 'replace'),
                        'points': struct.unpack('<i', data[4:8])[0], 'planes': struct.unpack('<i', data[8:12])[0],
                        'header_20': struct.unpack('<i', data[20:24])[0],
                        'header_44': struct.unpack('<i', data[44:48])[0],
                        'frames_dword16': struct.unpack('<i', data[16:20])[0],
                        'plane_list_end': plane_end, 'textures': in_range,
                        'sha1': hashlib.sha1(data).hexdigest()}
                    if ext == '3DC':
                        # Which pose the placeholder's bounds describe, and how far frame 0 and frame 1 differ:
                        # the largest change of any one coordinate, and the largest distance any point moves.
                        poses, width, wide = three_dc_frames(data)
                        brought_mesh = brought[f"{r['rob']}:{r['name']}.{ext}"]
                        brought_mesh['frame_table_width'] = width
                        brought_mesh['bbox_row_per_frame'] = [list(bbox_row(p)) for p in poses]
                        if len(poses) > 1:
                            moves = [(math.dist(p0, p1), i, p0, p1)
                                     for i, (p0, p1) in enumerate(zip(poses[0], poses[1]))]
                            farthest = max(moves)
                            brought_mesh['max_coordinate_change_frame0_to_frame1_world_units'] = max(
                                max(abs(a - b) for a, b in zip(p0, p1)) for _, _, p0, p1 in moves) / 256
                            brought_mesh['max_point_displacement_frame0_to_frame1_world_units'] = farthest[0] / 256
                            brought_mesh['max_point_displacement_point'] = {
                                'index': farthest[1], 'frame0_native': list(farthest[2]),
                                'frame1_native': list(farthest[3])}
                    break
    summary['meshes_the_rule_brings_in'] = brought

    # 8b. Are the brought-in materials already used by a mesh ISLAND resolves today? (Then no new material can
    # fail to resolve.) And the map's own MPSZ row for the placement against the placeholder header's row.
    island_rob = {s['name'].upper(): s for s in robs['ISLAND']}
    island_pairs = set()
    for s in statics:
        seg = island_rob.get(s['name'].upper())
        if seg and seg['size']:
            island_pairs.update(map(tuple, seg['textures']))
    for p in placements:
        seg = island_rob.get(p['stem'].upper()) if p['has_mesh'] and p['stem'] else None
        if seg and seg['size']:
            island_pairs.update(map(tuple, seg['textures']))
    rgm_by_stem = {os.path.splitext(os.path.basename(p))[0].upper(): p for p in rgm_paths(data_root)}
    mpsz = read_mpsz(rgm_by_stem['ISLAND'])
    for key, mesh in brought.items():
        pairs = {(t['archive'], t['record']) for t in mesh['textures']}
        mesh['pairs_already_used_by_island_meshes'] = len(pairs & island_pairs)
        mesh['pairs_new_to_island'] = sorted(pairs - island_pairs)
        name = key.split(':', 1)[1].rsplit('.', 1)[0].upper()
        slots = sorted({p['mesh_slot'] for p in placements if p['stem'].upper() == name})
        mesh['mpob_mesh_slots'] = slots
        mesh['mpsz_rows'] = [mpsz[k] for k in slots if 0 <= k < len(mpsz)]
        mesh['rob_header_row'] = island_rob[name]['bbox_row']
        mesh['mpsz_row_equals_rob_header_row'] = all(r == island_rob[name]['bbox_row'] for r in mesh['mpsz_rows'])
    summary['island_distinct_material_pairs_current'] = len(island_pairs)

    # 8c. The MPOB meshSlot (+56): which has-mesh placements carry -1 (no MPSZ row), over all 27 maps, and the
    # owning object's RAHD MPSZ indices for each placement on a placeholder.
    slot_minus_one = []
    for stem, (objs, pls, sts, rps) in maps.items():
        for p in pls:
            if p['has_mesh'] and p['stem'] and p['mesh_slot'] < 0:
                slot_minus_one.append((stem, p['index'], p['object'], p['stem']))
    summary['has_mesh_placements_with_mesh_slot_minus_1'] = slot_minus_one
    summary['has_mesh_placements_total'] = sum(1 for _, pls, _, _ in maps.values()
                                               for p in pls if p['has_mesh'] and p['stem'])
    owner_rows = {}
    for r in rows:
        for d in r['mpob_detail']:
            objs = maps[r['rob']][0]
            owner = next((o for o in objs if o['label'] == d['object']), None)
            map_mpsz = read_mpsz(rgm_by_stem[r['rob']])
            owner_rows[f"{r['rob']}:{d['index']}:{d['object']}"] = {
                'anim_count': owner and owner['anim_count'],
                'raan_paths': owner and [a['path'] for a in owner['raan']],
                'rahd_mesh_size_indices': owner and owner['mesh_size_indices'],
                'mpsz_rows_at_those_indices': owner and [map_mpsz[k] if 0 <= k < len(map_mpsz) else None
                                                        for k in owner['mesh_size_indices']],
                'mpsz_rows_equal_to_placeholder_header_row': [k for k, row in enumerate(map_mpsz)
                                                              if row == island_rob[r['name'].upper()]['bbox_row']]
                if r['rob'] == 'ISLAND' else None,
            }
    summary['placeholder_placement_owners'] = owner_rows

    # 8d. MPOB +24 (read as a has-mesh flag) against the ROB header's frame byte (+23) of the segment the name lands
    # on, and the suffix after the stem in MPOB's 9-byte name field.
    word_values = collections.Counter()
    word_vs_frames = collections.Counter()
    suffixes = collections.Counter()
    for stem, (objs, pls, sts, rps) in maps.items():
        segs = {s['name'].upper(): s for s in robs[stem]}
        for p in pls:
            word_values[p['mesh_word']] += 1
            if p['has_mesh'] and p['stem']:
                seg = segs[p['stem'].upper()]
                word_vs_frames[f"word {p['mesh_word']} / {'empty' if seg['size'] == 0 else 'full'} segment "
                               f"frame byte {seg['frames_byte']}"] += 1
                suffixes[p['mesh'][len(p['stem']):]] += 1
    summary['mpob_word_24_values'] = {str(k): v for k, v in sorted(word_values.items())}
    summary['mpob_word_24_vs_segment_frame_byte'] = dict(word_vs_frames)
    summary['mpob_name_suffix_after_stem'] = dict(suffixes)

    # 8e. Placements by (type, owner object has RAAN animation meshes, has-mesh, kind of segment the name lands on).
    owner_tab = collections.Counter()
    for stem, (objs, pls, sts, rps) in maps.items():
        by_label = {o['label']: o for o in objs}
        segs = {s['name'].upper(): s for s in robs[stem]}
        for p in pls:
            o = by_label.get(p['object'])
            s = segs.get(p['stem'].upper()) if p['stem'] else None
            kind = 'no name' if not p['stem'] else 'absent' if s is None else 'empty' if s['size'] == 0 else 'full'
            owner_tab[f"type {p['type']} / {'owner has RAAN' if o and o['anim_count'] else 'owner no RAAN'} / "
                      f"{'has-mesh' if p['has_mesh'] else 'no mesh'} / {kind}"] += 1
    summary['placements_by_owner_animation_and_segment_kind'] = dict(sorted(owner_tab.items()))
    summary['empty_loose_ext_sets'] = dict(collections.Counter('+'.join(r['loose_exts']) for r in rows))
    summary['map_placeholders_by_reference_kind'] = dict(collections.Counter(
        f"RAAN {'yes' if r['raan_refs'] else 'no'} / MPOB {'yes' if r['mpob_refs'] else 'no'} / "
        f"MPSO {'yes' if r['mpso_refs'] else 'no'} / MPRP {'yes' if r['mprp_refs'] else 'no'}"
        for r in rows if r['rob_has_map']))
    summary['distinct_placeholder_names'] = {'all_robs': len({r['name'].upper() for r in rows}),
                                             'map_robs': len({r['name'].upper() for r in rows if r['rob_has_map']})}

    # 9. A second install's 3dart and maps against the first's: the populations above are the same bytes.
    if compare_root is not None:
        disc_art = files_ci(os.path.join(compare_root, '3dart'))
        diff = []
        compared = 0
        for upper, real in art_files.items():
            if upper.rsplit('.', 1)[-1] in ('ROB', '3DC', '3D'):
                compared += 1
                other = disc_art.get(upper)
                if other is None or open(other, 'rb').read() != open(real, 'rb').read():
                    diff.append(upper)
        maps_diff = []
        disc_maps = files_ci(os.path.join(compare_root, 'maps'))
        for path in rgm_paths(data_root):
            other = disc_maps.get(os.path.basename(path).upper())
            if other is None or open(other, 'rb').read() != open(path, 'rb').read():
                maps_diff.append(os.path.basename(path))
        summary['compared_install'] = {'art_files_compared': compared, 'art_files_differing': diff,
                                       'rgm_differing': sorted(maps_diff)}

    receipts = {'summary': summary, 'dword_split': dword_split, 'placeholders': rows,
                'converse': converse, 'per_map_counts': {r: c['_per_map'] for r, c in counts.items()}}
    return summary, receipts


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.split('\n', 1)[0])
    parser.add_argument('data_root', help='the Redguard data directory (holds WORLD.INI, maps and 3dart)')
    parser.add_argument('--compare', help='a second data directory whose 3dart and maps are compared byte for byte')
    parser.add_argument('--out', help='also write the full receipts, one row per placeholder, to this JSON file')
    args = parser.parse_args(argv[1:])
    summary, receipts = census(args.data_root, args.compare)
    if args.out:
        with open(args.out, 'w', newline='\n') as f:
            json.dump(receipts, f, indent=1)
    json.dump(summary, sys.stdout, indent=1)
    sys.stdout.write('\n')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
