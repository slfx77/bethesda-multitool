# SPDX-License-Identifier: 0BSD
"""Resolves the cut-1a cover manifest's files to the source the BMT exe will read, SHA-256 checked.

Mirrors ``tests/BethesdaMultitool.Tests/Core/Modeling/RealAsset/Cut1aFixtureResolver.cs``: for each manifest
file the candidates are its ``source``, each ``alsoIn`` source, then the Steam Final build's Data folder
(loose files over every .bsa in case-insensitive file-name order) and the installed Steam Fallout New Vegas
Data folder; the first candidate whose bytes reproduce the pinned SHA-256 wins, every other one is rejected
and named. Sources spelled ``Sample/...`` resolve under the sample root, ``<SteamLibrary>/<game>/<path>``
through every fixed drive's Steam library (RealAssetPaths.SteamLibraryRoots), anything else as a path.

Usage:
    python gate1a_resolve.py --manifest <cut1a-cover-manifest.json> --sample-root <Sample dir> --out <resolved.json>
                             [--roles cover,floor] [--limit N] [--filter substring]

The output lists one record per manifest file with the resolved container (``bsa`` or ``loose``), the entry
the exe must be given, the console platform token (x360/ps3 from the PRIMARY source, as Cut1aCoverFile
.ConsolePlatform reads it) and the game token (fnv/fo3, or null when the manifest game is neither).
The BSA reader below is this file's own (v103/v104: DDS tooling's documented layout); it reads one entry
at a time and never decompresses more than the entry it hashes. Standard library only.
"""

import argparse
import hashlib
import json
import os
import string
import struct
import sys
import zlib

STEAM_FINAL_DATA = 'Builds/Fallout - New Vegas (2022-5-24, Steam - Final)/Data'
STEAM_INSTALL_DATA = ('Fallout New Vegas', 'Data')
F_DIRNAMES, F_FILENAMES, F_COMPRESSED, F_EMBED, F_XMEM = 0x1, 0x2, 0x4, 0x100, 0x200
SIZE_MASK, TOGGLE_BIT = 0x3FFFFFFF, 0x40000000


class BsaArchive:
    """One v103/v104 BSA: the directory parsed once, entries read on demand by lower-cased forward-slash path."""

    def __init__(self, path):
        self.path = path
        self.file = open(path, 'rb')
        header = self.file.read(36)
        if len(header) < 36:
            raise ValueError('shorter than a BSA header')
        magic, version, offset, flags, folder_count, file_count, folder_names, file_names, file_flags = struct.unpack('<4sIIIIIIIH', header[:34])
        if magic != b'BSA\0' or version not in (103, 104):
            raise ValueError('not a v103/v104 BSA (%r v%d)' % (magic, version))
        if version < 104:
            flags &= ~(F_EMBED | F_XMEM)
        self.version, self.flags = version, flags
        self.file.seek(offset)
        folders = [struct.unpack('<QII', self.file.read(16)) for _ in range(folder_count)]
        entries = []
        for _hash, count, _offset in folders:
            folder = ''
            if flags & F_DIRNAMES:
                length = self.file.read(1)[0]
                folder = self.file.read(length).rstrip(b'\0').decode('latin-1')
            for _ in range(count):
                _file_hash, size, file_offset = struct.unpack('<QII', self.file.read(16))
                entries.append([folder, None, size, file_offset])
        if flags & F_FILENAMES:
            names = self.file.read(file_names).split(b'\0')
            for index, entry in enumerate(entries):
                entry[1] = names[index].decode('latin-1')
        self.by_path = {}
        for entry in entries:
            path_in_archive = ((entry[0] + '/' + (entry[1] or '')) if entry[0] else (entry[1] or '')).replace('\\', '/').lower()
            self.by_path.setdefault(path_in_archive, entry)

    def read(self, entry_path):
        """(bytes, None) or (None, reason)."""
        entry = self.by_path.get(entry_path.replace('\\', '/').lower())
        if entry is None:
            return None, 'entry not found'
        _folder, _name, raw_size, offset = entry
        size = raw_size & SIZE_MASK
        compressed = bool(self.flags & F_COMPRESSED) != bool(raw_size & TOGGLE_BIT)
        self.file.seek(offset)
        data = self.file.read(size)
        if len(data) != size:
            return None, 'record runs past the archive end'
        if self.flags & F_EMBED:
            name_length = data[0] if size else 0
            data = data[1 + name_length:]
        if compressed:
            if self.flags & F_XMEM:
                return None, 'XMem-coded entry (Xbox 360 LZX) is outside this reader'
            if len(data) < 4:
                return None, 'compressed entry shorter than its size prefix'
            original = struct.unpack('<I', data[:4])[0]
            try:
                out = zlib.decompress(data[4:])
            except zlib.error:
                out = zlib.decompressobj(-15).decompress(data[4:])
            if len(out) != original:
                return None, 'zlib size %d differs from the declared %d' % (len(out), original)
            data = out
        return data, None

    def close(self):
        self.file.close()


class Archives:
    def __init__(self):
        self.open = {}

    def get(self, path):
        key = os.path.normcase(os.path.abspath(path))
        if key not in self.open:
            self.open[key] = BsaArchive(path)
        return self.open[key]

    def close(self):
        for archive in self.open.values():
            archive.close()


def steam_library_roots():
    roots = []
    for letter in string.ascii_uppercase:
        drive = letter + ':/'
        if not os.path.isdir(drive):
            continue
        for tail in ('SteamLibrary/steamapps/common', 'SteamLibrary/SteamApps/common', 'Steam/steamapps/common',
                     'Program Files (x86)/Steam/steamapps/common'):
            full = drive + tail
            if os.path.isdir(full):
                roots.append(full)
    return roots


def locate(source, sample_root, libraries):
    if source.startswith('<SteamLibrary>/'):
        rest = source[len('<SteamLibrary>/'):]
        for library in libraries:
            full = os.path.join(library, rest.replace('/', os.sep))
            if os.path.exists(full):
                return full
        return None
    if source.startswith('Sample/'):
        full = os.path.join(sample_root, source[len('Sample/'):].replace('/', os.sep))
        return full if os.path.exists(full) else None
    return source if os.path.exists(source) else None


def read_candidate(archives, location, entry):
    """(bytes, container path, container kind, entry for the exe) or (None, reason, None, None)."""
    if os.path.isdir(location):
        path = os.path.join(location, entry.replace('/', os.sep))
        if not os.path.isfile(path):
            return None, 'no such loose file: %s' % path, None, None
        with open(path, 'rb') as f:
            return f.read(), path, 'loose', None
    if location.lower().endswith('.bsa'):
        try:
            archive = archives.get(location)
        except (OSError, ValueError) as failure:
            return None, 'cannot open %s: %s' % (location, failure), None, None
        data, error = archive.read(entry)
        if error:
            return None, '%s :: %s (%s)' % (location, entry, error), None, None
        return data, location, 'bsa', entry
    return None, 'unsupported source kind: %s' % location, None, None


def data_relative(entry):
    entry = entry.replace('\\', '/')
    return entry if entry.lower().startswith('meshes/') else 'meshes/' + entry


def read_data_folder(archives, data_dir, relative):
    loose = os.path.join(data_dir, relative.replace('/', os.sep))
    if os.path.isfile(loose):
        with open(loose, 'rb') as f:
            return f.read(), loose, 'loose', None, 'loose'
    for name in sorted((n for n in os.listdir(data_dir) if n.lower().endswith('.bsa')), key=str.upper):
        bsa = os.path.join(data_dir, name)
        try:
            data, error = archives.get(bsa).read(relative)
        except (OSError, ValueError):
            continue
        if data is not None:
            return data, bsa, 'bsa', relative, name
    return None, None, None, None, None


def console_platform(record):
    key = record.get('key') or ''
    if not key.endswith('/BE'):
        return None
    source = record['source']
    if 'x360' in source.lower():
        return 'x360'
    if 'ps3' in source.lower():
        return 'ps3'
    return None


def game_token(record):
    platforms = record.get('gamePlatforms') or []
    source = record['source']
    if 'Fallout 3' in source or (platforms and all(p.startswith('FO3') for p in platforms)):
        return 'fo3'
    if 'New Vegas' in source or 'PC_Final_Unpacked' in source or any(p.startswith('FNV') for p in platforms):
        return 'fnv'
    return None


def resolve(record, archives, sample_root, libraries):
    tried = []
    candidates = [(record['source'], record['entry'], 'source')]
    candidates += [(a['source'], a['entry'], 'alsoIn') for a in record.get('alsoIn', [])]
    for source, entry, step in candidates:
        location = locate(source, sample_root, libraries)
        if location is None:
            tried.append('absent: %s' % source)
            continue
        data, container, kind, exe_entry = read_candidate(archives, location, entry)
        if data is None:
            tried.append(container)
            continue
        digest = hashlib.sha256(data).hexdigest()
        if digest != record['sha256'] or len(data) != record['size']:
            tried.append('SHA-256 mismatch: %s :: %s' % (source, entry))
            continue
        return {'status': 'resolved', 'step': step, 'container': container, 'containerKind': kind, 'exeEntry': exe_entry,
                'label': '%s :: %s' % (source, entry), 'tried': tried}
    relative = data_relative(record['entry'])
    folders = [('steamFinalBuild', os.path.join(sample_root, STEAM_FINAL_DATA.replace('/', os.sep)))]
    for library in libraries:
        folders.append(('steamInstall', os.path.join(library, *STEAM_INSTALL_DATA)))
    for step, folder in folders:
        if not os.path.isdir(folder):
            tried.append('%s: the Data folder is not present (%s)' % (step, folder))
            continue
        data, container, kind, exe_entry, layer = read_data_folder(archives, folder, relative)
        if data is None:
            tried.append('%s: %s is in no layer of %s' % (step, relative, folder))
            continue
        digest = hashlib.sha256(data).hexdigest()
        if digest != record['sha256'] or len(data) != record['size']:
            tried.append('SHA-256 mismatch: %s %s (%s)' % (step, relative, layer))
            continue
        return {'status': 'resolved', 'step': step, 'container': container, 'containerKind': kind, 'exeEntry': exe_entry,
                'label': '%s :: %s (%s)' % (step, relative, layer), 'tried': tried}
    return {'status': 'unresolved', 'step': None, 'container': None, 'containerKind': None, 'exeEntry': None, 'label': None, 'tried': tried}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--manifest', required=True)
    parser.add_argument('--sample-root', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--roles', default='cover,floor')
    parser.add_argument('--limit', type=int)
    parser.add_argument('--filter')
    args = parser.parse_args(argv)
    with open(args.manifest, 'rb') as f:
        manifest = json.loads(f.read().decode('utf-8'))
    roles = {r.strip() for r in args.roles.split(',') if r.strip()}
    records = []
    for key in manifest['keys']:
        for record in key['files']:
            if record['role'] not in roles:
                continue
            record = dict(record, key=key['key'])
            if args.filter and args.filter.lower() not in (record['entry'] + ' ' + record['key']).lower():
                continue
            records.append(record)
    if args.limit:
        records = records[:args.limit]
    libraries = steam_library_roots()
    archives = Archives()
    output = []
    counts = {'resolved': 0, 'unresolved': 0, 'bySteps': {}}
    for index, record in enumerate(records):
        result = resolve(record, archives, args.sample_root, libraries)
        counts[result['status']] += 1
        if result['step']:
            counts['bySteps'][result['step']] = counts['bySteps'].get(result['step'], 0) + 1
        output.append({
            'id': '%s :: %s' % (record['key'], record['entry']),
            'key': record['key'], 'role': record['role'], 'entry': record['entry'], 'sha256': record['sha256'], 'size': record['size'],
            'primarySource': record['source'], 'gamePlatforms': record.get('gamePlatforms'),
            'platform': console_platform(record), 'game': game_token(record),
            'resolution': result,
        })
        print('[%d/%d] %s: %s (%s)' % (index + 1, len(records), record['entry'], result['status'], result['step'] or '; '.join(result['tried'])[:200]), flush=True)
    archives.close()
    document = {'schema': 'gate1a-resolved/1', 'manifest': os.path.abspath(args.manifest), 'sampleRoot': os.path.abspath(args.sample_root),
                'steamLibraries': libraries, 'roles': sorted(roles), 'counts': counts, 'samples': output}
    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or '.', exist_ok=True)
    with open(args.out, 'w', encoding='utf-8') as f:
        json.dump(document, f, indent=1)
    print('resolved %d, unresolved %d, by step %r' % (counts['resolved'], counts['unresolved'], counts['bySteps']))
    return 0 if counts['unresolved'] == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
