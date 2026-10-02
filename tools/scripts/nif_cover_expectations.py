#!/usr/bin/env python3
"""nif_cover_expectations.py -- checked-in oracle expectations for the cut-1a cover manifest (slice 11).

PURPOSE
    For every file of tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut1a-cover-manifest.json this
    script resolves the bytes exactly as the C# fixture resolver (Cut1aFixtureResolver) does, runs the
    independent probe (tools/scripts/nif_feature_probe.py, oracle A1) on them, and writes one JSON line per
    manifest file, keyed by the manifest's SHA-256, holding only the fields the Bucket-B hops compare:

      A2  the header block table (type names + per-block type index), the probe's decline reason,
      A1  every parsed NiAVObject's name, hidden bit, rotation checks and scale (plus billboard modes),
          NiAlphaProperty, NiMaterialProperty, NiStencilProperty, NiZBufferProperty, NiVertexColorProperty,
          BSShader*Property, BSShaderTextureSet and NiTexturingProperty fields, NiTri*Data stream facts,
          skin instance / partition facts, NiMorphData facts, and (console files) every
          BSPackedAdditionalGeometryData stream table -- the vertex count, each stream's type, unit size,
          total size, stride, block index, block offset and flags, and each data block's sizes and shader
          index -- so the big-endian rows can derive the packed layout's channel presence from the probe,
      A4  every texture string the file authors, resolved through the build's Data folder the way the reader
          resolves it (loose first, then archives in case-insensitive file-name order, .dds -> .ddx fallback,
          the Xbox *_s companion of a *_n.ddx) and the SHA-256 of the bytes an independent BSA reader yields.

RESOLUTION ORDER (identical to the C# resolver; every candidate must reproduce the pinned SHA-256)
    1. the manifest's `source` (+ `entry`),
    2. each `alsoIn` entry,
    3. the same Data-relative path through the Steam Final build's Data folder
       (Sample/Builds/Fallout - New Vegas (2022-5-24, Steam - Final)/Data: loose files, then every BSA),
    4. the same path through the installed Steam Fallout New Vegas Data folder.
    "<SteamLibrary>/<game>/<path>" sources resolve through every fixed drive's Steam library, as
    RealAssetPaths does. The unpacked PC tree the manifest names first no longer exists, which is what the
    fallback chain is for.

INDEPENDENCE
    Nothing here calls BethesdaMultitool. The BSA reader is the probe's own (a copy of ddxm_common.py's);
    the texture-path normalization is a transcription of the reader's rule (NifTexturePathUtility.Normalize)
    so that the C# side and this side look up the same key, and the comparison that matters -- the bytes'
    SHA-256 -- comes from this reader, not from the code under test.

SCOPE AND PAYLOADS (2026-09-25, cut 1b)
    --scope cut1a|cut1b (default cut1a) is passed to every probe call (probe_bytes, parse_header, walk), and
    --payloads to probe_bytes and walk. The default writes exactly the cut-1a record, byte for byte (the
    checked-in cut1a-probe-expectations.jsonl regenerates identically). Any other choice adds two members to
    every record, probeScope and probePayloads, so a reader can refuse expectations generated for another scope.
    With --payloads each walked record also carries:
      A0  payloads   every decoded payload of the file, as the probe gives it ({i, type, payload} per block,
                     every float with its IEEE-754 bits: see nif_feature_probe.py PAYLOADS);
      A0  animation  {i: fields} for every parsed controller, interpolator, sequence, palette, anim-note,
                     NiTextKeyExtraData and payload-bearing data block: the probe's block fields (clock fields,
                     flags, refs, controlled-block strings, text keys, ...) with the payload left out, and every
                     float field given a sibling <name>Bits (lists of floats a list of bits) reconstructed from the
                     probe's float by packing it as a binary32: exact for every non-NaN value.
    At scope cut1b the tags include the probe's anim:* cover vocabulary when --payloads is given.

VERIFY-ONLY
    --no-fallback limits the chain to the manifest's own candidates (source, then alsoIn), in both modes.
    --verify-only resolves every manifest file through the same candidate chain and checks its SHA-256 and size,
    probing nothing and writing no expectations: one line per file (resolved step, or UNRESOLVED with every
    candidate tried and why it was rejected), then the totals; exit 1 when any file is unresolved. A candidate whose
    bytes differ from the pinned digest is rejected as "sha mismatch", never read.

USAGE
    python nif_cover_expectations.py --manifest <manifest.json> --out <expectations.jsonl>
        [--sample <Sample dir>] [--probe-dir <tools/scripts>] [--limit N] [--scope cut1a|cut1b] [--payloads]
    python nif_cover_expectations.py --manifest <manifest.json> --verify-only [--sample ...]
    Prints the resolver coverage (resolved / unresolved with the unresolved list) and the output size.
"""
import argparse
import gzip
import hashlib
import io
import json
import os
import string
import struct
import sys
import time

FNV_STEAM_FINAL_DATA = 'Builds/Fallout - New Vegas (2022-5-24, Steam - Final)/Data'
STEAM_FNV_DATA = ('Fallout New Vegas', 'Data')
KNOWN_ROOTS = ('materials\\', 'textures\\', 'meshes\\', 'geometries\\')


# =====================================================================================================
# Steam library discovery (RealAssetPaths.SteamLibraryRoots) and manifest source resolution
# =====================================================================================================

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


class BsaCache:
    """Opens each BSA once; entries are looked up by lower-cased forward-slash path."""

    def __init__(self, probe):
        self.p = probe
        self.archives = {}

    def get(self, path):
        key = os.path.normcase(os.path.abspath(path))
        a = self.archives.get(key)
        if a is None:
            idx = self.p.read_bsa_index(path)
            by_path = {}
            for e in idx['entries']:
                by_path.setdefault(self.p.entry_path(e).lower(), e)
            a = {'idx': idx, 'byPath': by_path, 'file': open(path, 'rb')}
            self.archives[key] = a
        return a

    def read(self, path, entry):
        a = self.get(path)
        e = a['byPath'].get(entry.replace('\\', '/').lower())
        if e is None:
            return None, 'entry not found'
        return self.p.read_entry_payload(a['file'], a['idx']['flags'], e)

    def close(self):
        for a in self.archives.values():
            a['file'].close()


def sample_path(sample, relative):
    return os.path.join(sample, relative.replace('/', os.sep))


def resolve_source(sample, libraries, source):
    """A manifest source string to an absolute path (file or directory), or None."""
    if source.startswith('<SteamLibrary>/'):
        rest = source[len('<SteamLibrary>/'):]
        for lib in libraries:
            full = os.path.join(lib, rest.replace('/', os.sep))
            if os.path.exists(full):
                return full
        return None
    if source.startswith('Sample/'):
        full = sample_path(sample, source[len('Sample/'):])
        return full if os.path.exists(full) else None
    return source if os.path.exists(source) else None


def read_candidate(cache, sample, libraries, source, entry):
    """(bytes, label) for one candidate, or (None, reason)."""
    full = resolve_source(sample, libraries, source)
    if full is None:
        return None, 'absent: %s' % source
    if os.path.isdir(full):
        path = os.path.join(full, entry.replace('/', os.sep))
        if not os.path.isfile(path):
            return None, 'no such loose file: %s :: %s' % (source, entry)
        with open(path, 'rb') as f:
            return f.read(), '%s :: %s' % (source, entry)
    if full.lower().endswith('.bsa'):
        data, err = cache.read(full, entry)
        if err:
            return None, '%s :: %s (%s)' % (source, entry, err)
        return data, '%s :: %s' % (source, entry)
    return None, 'unsupported source kind: %s' % source


def data_relative(entry):
    e = entry.replace('\\', '/')
    return e if e.lower().startswith('meshes/') else 'meshes/' + e


def read_data_folder(cache, data_dir, relative):
    """Loose file first, then every BSA in case-insensitive file-name order (GameFileSystem.OpenDataFolder)."""
    loose = os.path.join(data_dir, relative.replace('/', os.sep))
    if os.path.isfile(loose):
        with open(loose, 'rb') as f:
            return f.read(), 'loose'
    for bsa in sorted((n for n in os.listdir(data_dir) if n.lower().endswith('.bsa')), key=str.upper):
        data, _ = cache.read(os.path.join(data_dir, bsa), relative)
        if data is not None:
            return data, bsa
    return None, None


def resolve_file(cache, sample, libraries, rec, fallback=True):
    """The first candidate whose bytes reproduce the pinned SHA-256: (bytes, label, step) or (None, tried).
    fallback=False (--no-fallback) stops after the manifest's own candidates (source, then alsoIn): the file must
    come out of the container the manifest names."""
    tried = []
    candidates = [(rec['source'], rec['entry'], 'source')]
    candidates += [(a['source'], a['entry'], 'alsoIn') for a in rec.get('alsoIn', [])]
    for source, entry, step in candidates:
        data, label = read_candidate(cache, sample, libraries, source, entry)
        if data is None:
            tried.append(label)
            continue
        if hashlib.sha256(data).hexdigest() != rec['sha256']:
            tried.append('sha mismatch: ' + label)
            continue
        return data, label, step
    if not fallback:
        return None, tried, None
    relative = data_relative(rec['entry'])
    folders = [('steamFinalBuild', sample_path(sample, FNV_STEAM_FINAL_DATA))]
    for lib in libraries:
        folders.append(('steamInstall', os.path.join(lib, *STEAM_FNV_DATA)))
    for step, folder in folders:
        if not os.path.isdir(folder):
            tried.append('absent: ' + folder)
            continue
        data, layer = read_data_folder(cache, folder, relative)
        if data is None:
            tried.append('%s: no %s' % (step, relative))
            continue
        if hashlib.sha256(data).hexdigest() != rec['sha256']:
            tried.append('sha mismatch: %s %s (%s)' % (step, relative, layer))
            continue
        return data, '%s :: %s (%s)' % (folder, relative, layer), step
    return None, tried, None


# =====================================================================================================
# Texture resolution (a transcription of NifTexturePathUtility.Normalize + BethesdaTextureCompanions)
# =====================================================================================================

def texture_build(source):
    """The Data folder (Sample-relative) whose archives the file's textures live in, from the PRIMARY source."""
    s = source.replace('\\', '/')
    if 'X360' in s:
        return 'Builds/Fallout - New Vegas (2010-8-22, X360 - Final)/Data', 'x360'
    if 'PS3' in s:
        return 'Builds/Fallout - New Vegas (2010-9-5, PS3 - Final)/PS3_GAME/USRDIR/DATA', 'ps3'
    if 'Fallout 3 (' in s:
        return 'Builds/Fallout 3 (2026-2-15, Steam - Final)/Data', 'pc'
    if 'Skyrim (' in s:
        return 'Builds/The Elder Scrolls V - Skyrim (2026-6-16, Steam - Final)/Data', 'pc'
    if 'Oblivion (' in s:
        return None, None
    return FNV_STEAM_FINAL_DATA, 'pc'


def normalize_texture_path(path):
    n = path.replace('/', '\\').lower().strip()
    cut = n.find('\\data\\')
    if cut >= 0:
        n = n[cut + len('\\data\\'):]
    elif n.startswith('data\\'):
        n = n[5:]
    if not n.startswith(KNOWN_ROOTS):
        best = -1
        for root in KNOWN_ROOTS:
            k = n.find('\\' + root)
            if k >= 0 and (best < 0 or k < best):
                best = k
        if best >= 0:
            n = n[best + 1:]
    if not n.startswith(KNOWN_ROOTS):
        n = ('materials\\' if n.endswith(('.bgsm', '.bgem')) else 'textures\\') + n
    return n


def asset_path_ok(key):
    """AssetPath.Normalize rejects rooted paths and '..' segments; the resolver then yields nothing."""
    slashed = key.replace('\\', '/')
    if slashed.startswith('/') or '\0' in slashed or (len(slashed) >= 2 and slashed[0].isalpha() and slashed[1] == ':'):
        return False
    return all(seg != '..' for seg in slashed.split('/'))


class DataFolder:
    """One build's Data folder: loose tree over its BSAs, first hit wins."""

    def __init__(self, cache, directory):
        self.cache = cache
        self.dir = directory
        self.bsas = sorted((n for n in os.listdir(directory) if n.lower().endswith('.bsa')), key=str.upper) \
            if os.path.isdir(directory) else []

    def stat(self, key):
        """The layer holding `key` (a backslash lookup key), or None. Mirrors TryStat: no bytes are read."""
        loose = os.path.join(self.dir, key.replace('\\', os.sep).replace('/', os.sep))
        if os.path.isfile(loose):
            return 'loose'
        wanted = key.replace('\\', '/').lower()
        for bsa in self.bsas:
            a = self.cache.get(os.path.join(self.dir, bsa))
            if wanted in a['byPath']:
                return bsa
        return None

    def read(self, key, layer):
        if layer == 'loose':
            with open(os.path.join(self.dir, key.replace('\\', os.sep)), 'rb') as f:
                return f.read(), None
        return self.cache.read(os.path.join(self.dir, layer), key)


def resolve_texture(folder, authored):
    """The reader's outcome for one authored path: (lookupKey, resolvedPath|None, layer, sha256|None, error)."""
    key = normalize_texture_path(authored)
    if not asset_path_ok(key):
        return key, None, None, None, 'the asset path is rooted or traverses'
    candidates = [key]
    if key.endswith('.dds'):
        candidates.append(key[:-4] + '.ddx')
    for candidate in candidates:
        layer = folder.stat(candidate)
        if layer is None:
            continue
        data, err = folder.read(candidate, layer)
        resolved = candidate.replace('\\', '/')
        if err:
            return key, resolved, layer, None, err
        return key, resolved, layer, hashlib.sha256(data).hexdigest(), None
    return key, None, None, None, None


def authored_textures(blocks):
    """Every texture string the file authors, from the probe's parsed blocks: [(authored, kind, block)]."""
    out = []
    for b in blocks:
        if not b.get('parsed'):
            continue
        t = b['type']
        if t == 'BSShaderTextureSet':
            for k, name in enumerate(b['textures']):
                if name:
                    out.append((name, 'textureSet[%d]' % k, b['i']))
        elif t in ('BSShaderNoLightingProperty', 'SkyShaderProperty', 'TileShaderProperty', 'TallGrassShaderProperty'):
            if b.get('fileName'):
                out.append((b['fileName'], 'shaderFileName', b['i']))
        elif t == 'NiSourceTexture':
            if b.get('useExternal') and b.get('fileName'):
                out.append((b['fileName'], 'sourceTexture', b['i']))
    return out


def texture_expectations(cache, sample, rec, blocks):
    build, platform = texture_build(rec['source'])
    if build is None:
        return None
    folder = DataFolder(cache, sample_path(sample, build))
    entries = {}
    for authored, kind, block in authored_textures(blocks):
        key, resolved, layer, sha, err = resolve_texture(folder, authored)
        entry = entries.get(key)
        if entry is None:
            entry = {'authored': authored, 'path': resolved, 'layer': layer, 'sha256': sha, 'kinds': []}
            if err:
                entry['error'] = err
            entries[key] = entry
        if kind not in entry['kinds']:
            entry['kinds'].append(kind)
        # The Xbox specular companion: a resolved DDX (by magic or by name) whose stem ends in _n.
        if resolved and sha and (resolved.endswith('.ddx') or entry.get('ddx')):
            stem, ext = os.path.splitext(resolved)
            if stem.lower().endswith('_n'):
                companion = stem[:-2] + '_s' + ext
                ckey, cresolved, clayer, csha, cerr = resolve_texture(folder, companion)
                if ckey not in entries:
                    entries[ckey] = {'authored': companion, 'path': cresolved, 'layer': clayer, 'sha256': csha,
                                     'kinds': ['companion'], 'companionOf': key}
                    if cerr:
                        entries[ckey]['error'] = cerr
    return {'build': build, 'platform': platform, 'entries': entries}


# =====================================================================================================
# Per-file expectation record
# =====================================================================================================

def av_facts(p, b):
    err, det = p.rotation_checks(b['rotation'])
    facts = {'name': b.get('name'), 'hidden': bool(b['flags'] & 1), 'flags': b['flags'],
             'rotErr': err, 'rotDet': det, 'scale': b['scale']}
    if b['type'] == 'NiBillboardNode':
        facts['billboardMode'] = b['billboardMode']
    return facts


def geometry_data_facts(b):
    facts = {'type': b['type'], 'numVertices': b['numVertices'], 'hasVertices': b['hasVertices'],
             'hasNormals': b['hasNormals'], 'hasVertexColors': b['hasVertexColors'], 'uvSets': b['uvSets'],
             'hasTangents': b['hasTangents'], 'additionalData': b['additionalData']}
    if b['type'] == 'NiTriShapeData':
        facts['form'] = 'list'
        facts['numTriangles'] = b['numTriangles']
        facts['hasTriangles'] = b['hasTriangles']
    else:
        facts['form'] = 'strips'
        facts['numTriangles'] = b['numTriangles']
        facts['numStrips'] = b['numStrips']
        facts['stripPoints'] = b['stripPoints']
        facts['hasPoints'] = b['hasPoints']
    return facts


def packed_facts(b):
    """A BSPackedAdditionalGeometryData block's stream table as the probe parsed it (p_packedagd): the packed
    vertex count, every stream descriptor and every data block's sizes. The stream semantics are not stored in
    the file; the C# side matches this table against the layout table measured in
    TestOutput/packed-semantics-20260924 to derive which channels the packed vertex carries."""
    return {'numVertices': b['numVertices'],
            'streams': [{'type': s['type'], 'unitSize': s['unitSize'], 'totalSize': s['totalSize'],
                         'stride': s['stride'], 'blockIndex': s['blockIndex'], 'blockOffset': s['blockOffset'],
                         'flags': s['flags']} for s in b['streams']],
            'blocks': [None if blk is None else
                       {'blockSize': blk['blockSize'], 'numBlocks': blk['numBlocks'], 'numData': blk['numData'],
                        'dataSizes': blk['dataSizes'], 'shaderIndex': blk['shaderIndex'],
                        'totalSize': blk['totalSize']} for blk in b['blocks']]}


ANIMATION_KINDS = frozenset(('controller', 'interpolator', 'sequence', 'palette', 'animnotes', 'animnote'))
BLOCK_BOOKKEEPING = frozenset(('i', 'offset', 'size', 'parsed', 'payload'))


def float_bits(value):
    """The IEEE-754 binary32 bits of a float the probe read from a float32 field (exact for every non-NaN value)."""
    return struct.unpack('<I', struct.pack('<f', value))[0]


def with_bits(value):
    """A probe block field with every float given its bits: a dict gains <name>Bits beside each float or list of
    floats, recursively; a list of dicts is walked element by element."""
    if isinstance(value, dict):
        out = {}
        for k, v in value.items():
            out[k] = with_bits(v)
            if isinstance(v, float):
                out[k + 'Bits'] = float_bits(v)
            elif isinstance(v, (list, tuple)) and v and all(isinstance(x, float) for x in v):
                out[k + 'Bits'] = [float_bits(x) for x in v]
        return out
    if isinstance(value, (list, tuple)):
        return [with_bits(v) for v in value]
    return value


def animation_facts(p, blocks, scope):
    """{i: fields} for every parsed animation block (see SCOPE AND PAYLOADS), payloads left out."""
    facts = {}
    for b in blocks:
        if not b.get('parsed'):
            continue
        t = b['type']
        if p.kind_of(t, scope) in ANIMATION_KINDS or t == 'NiTextKeyExtraData' or 'payload' in b:
            fields = {k: v for k, v in b.items() if k not in BLOCK_BOOKKEEPING}
            fields['kind'] = p.kind_of(t, scope)
            facts[str(b['i'])] = with_bits(fields)
    return facts


def build_record(p, rec, data, label, step, scope='cut1a', payloads=False):
    probe = p.probe_bytes(data, want_details=True, platform='auto', source=rec['source'], payloads=payloads,
                          scope=scope)
    out = {'sha256': rec['sha256'], 'entry': rec['entry'], 'role': rec['role'], 'key': probe['keyString'],
           'size': len(data), 'resolvedFrom': label, 'resolvedStep': step, 'declined': probe['declined'],
           'platform': probe['platform'], 'tags': probe['tags'], 'errors': probe['errors']}
    if scope != 'cut1a' or payloads:
        out['probeScope'] = scope
        out['probePayloads'] = bool(payloads)
    header = None
    try:
        header = p.parse_header(data, scope)
    except p.Decline:
        header = p.partial_header(data)
    except Exception as e:  # noqa: BLE001
        out['headerError'] = '%s: %s' % (type(e).__name__, e)
    if header is not None:
        out['header'] = {'version': p.vstr(header['version']), 'userVersion': header['userVersion'],
                         'bsVersion': header['bsVersion'], 'bigEndian': header['bigEndian'],
                         'blockCount': header['blockCount']}
        out['blockTypeNames'] = header['typeNames']
        out['blockTypeIndices'] = header['typeIndex']
    if probe['declined'] is not None:
        return out, []
    blocks, _errors = p.walk(data, header, payloads, scope)
    out['unparsedTypes'] = probe['details'].get('unparsedTypes', [])
    av, alpha, materials, stencil, zbuffer, vcolor, shaders, sets, texturing = {}, {}, {}, {}, {}, {}, {}, {}, {}
    geometry, skins, morphs, morphers, sources, packed = {}, {}, {}, {}, {}, {}
    for b in blocks:
        if not b.get('parsed'):
            continue
        i = str(b['i'])
        t = b['type']
        if 'rotation' in b and 'flags' in b and 'translation' in b and t != 'NiSkinData':
            av[i] = av_facts(p, b)
        if t == 'NiAlphaProperty':
            alpha[i] = {'flags': b['flags'], 'threshold': b['threshold'], 'blend': b['blend'], 'src': b['srcBlend'],
                        'dst': b['dstBlend'], 'test': b['test'], 'testFunc': b['testFunc'], 'noSorter': b['noSorter']}
        elif t == 'NiMaterialProperty':
            m = {'specular': list(b['specular']), 'emissive': list(b['emissive']), 'glossiness': b['glossiness'],
                 'alpha': b['alpha']}
            if 'ambient' in b:
                m['ambient'] = list(b['ambient'])
                m['diffuse'] = list(b['diffuse'])
            if 'emissiveMult' in b:
                m['emissiveMult'] = b['emissiveMult']
            materials[i] = m
        elif t == 'NiStencilProperty':
            stencil[i] = {'flags': b['flags'], 'enable': b['enable'], 'drawMode': b['drawMode'],
                          'testFunc': b['testFunc'], 'failAction': b['failAction'], 'zFailAction': b['zFailAction'],
                          'passAction': b['passAction'], 'stencilRef': b['stencilRef'], 'stencilMask': b['stencilMask']}
        elif t == 'NiZBufferProperty':
            zbuffer[i] = {'flags': b['flags'], 'zTest': b['zTest'], 'zWrite': b['zWrite'], 'testFunc': b['testFunc']}
        elif t == 'NiVertexColorProperty':
            vcolor[i] = {'flags': b['flags'], 'lightingMode': b['lightingMode'],
                         'sourceVertexMode': b['sourceVertexMode']}
        elif 'shaderFlags1' in b:
            s = {'type': t, 'shaderType': b['shaderType'], 'flags1': b['shaderFlags1'], 'flags2': b['shaderFlags2'],
                 'envMapScale': b['envMapScale']}
            for k in ('textureClampMode', 'textureSet', 'fileName', 'falloff', 'refractionStrength',
                      'refractionFirePeriod', 'skyObjectType'):
                if k in b:
                    s[k] = b[k]
            shaders[i] = s
        elif t == 'BSShaderTextureSet':
            sets[i] = b['textures']
        elif t == 'NiTexturingProperty':
            tx = {'flags': b['flags'], 'applyMode': b['applyMode'], 'textureCount': b['textureCount'], 'maps': {}}
            for slot, m in b['maps'].items():
                md = {'source': m['source'], 'flags': m['flags'], 'uvSet': m['textureIndex'], 'filter': m['filterMode'],
                      'clamp': m['clampMode'], 'hasTransform': bool(m['hasTransform'])}
                if m['hasTransform']:
                    md.update(translation=list(m['translation']), scale=list(m['scale']), rotation=m['rotation'],
                              method=m['transformMethod'], center=list(m['center']))
                tx['maps'][slot] = md
            tx['shaderMaps'] = len(b['shaderMaps'])
            texturing[i] = tx
        elif t == 'NiSourceTexture':
            sources[i] = {'useExternal': b['useExternal'], 'fileName': b['fileName'], 'pixelData': b['pixelData']}
        elif t in ('NiTriShapeData', 'NiTriStripsData'):
            geometry[i] = geometry_data_facts(b)
        elif t in ('NiSkinInstance', 'BSDismemberSkinInstance'):
            sk = {'type': t, 'bones': len(b['bones']), 'skeletonRoot': b['skeletonRoot'], 'data': b['data'],
                  'skinPartition': b['skinPartition']}
            if t == 'BSDismemberSkinInstance':
                sk['bodyParts'] = [q['bodyPart'] for q in b['partitions']]
                sk['partFlags'] = [q['partFlag'] for q in b['partitions']]
            sp = blocks[b['skinPartition']] if 0 <= b['skinPartition'] < len(blocks) else None
            if sp is not None and sp.get('parsed'):
                sk['partitions'] = len(sp['partitions'])
                sk['partitionVertices'] = [q['numVertices'] for q in sp['partitions']]
                sk['partitionTriangles'] = [q['numTriangles'] for q in sp['partitions']]
                sk['weightsPerVertex'] = sorted({q['weightsPerVertex'] for q in sp['partitions']})
            sd = blocks[b['data']] if 0 <= b['data'] < len(blocks) else None
            if sd is not None and sd.get('parsed') and sd['type'] == 'NiSkinData':
                sk['skinDataBones'] = sd['numBones']
                sk['hasVertexWeights'] = sd['hasVertexWeights']
            skins[i] = sk
        elif t == 'NiMorphData':
            morphs[i] = {'numMorphs': b['numMorphs'], 'numVertices': b['numVertices'],
                         'relativeTargets': b['relativeTargets'], 'frameNames': b['frameNames']}
        elif t == 'NiGeomMorpherController':
            morphers[i] = {'data': b['data'], 'target': b['target'], 'interpolators': len(b['interpolators'])}
        elif t == 'BSPackedAdditionalGeometryData':
            packed[i] = packed_facts(b)
    out.update(avObjects=av, alpha=alpha, materials=materials, stencil=stencil, zbuffer=zbuffer, vertexColor=vcolor,
               shaders=shaders, textureSets=sets, texturing=texturing, sourceTextures=sources, geometryData=geometry,
               skins=skins, morphData=morphs, morphers=morphers, packed=packed)
    if payloads:
        out['animation'] = animation_facts(p, blocks, scope)
        out['payloads'] = [{'i': b['i'], 'type': b['type'], 'payload': b['payload']} for b in blocks if 'payload' in b]
    return out, blocks


def bits_only(value):
    """Drop every float member that has an exact-bits sibling (<name> beside <name>Bits), recursively.

    The probe gives every decoded float twice: as the Python float and as its IEEE-754 binary32 pattern. The
    pattern alone is lossless (the float is struct.unpack of it), so the checked-in cut-1b expectations carry only
    the bits: that halves the file, and the oracle compares bits anyway. Used only with --payloads."""
    if isinstance(value, dict):
        return {k: bits_only(v) for k, v in value.items() if (k + 'Bits') not in value}
    if isinstance(value, list):
        return [bits_only(v) for v in value]
    return value


class _GzipText(io.TextIOWrapper):
    """UTF-8 text over a gzip stream whose header carries no file name and mtime 0, so the same records give the same
    bytes whatever the output is called. GzipFile does not close a file object it was handed; this closes it."""

    def __init__(self, path):
        self._file = open(path, 'wb')
        super().__init__(gzip.GzipFile(filename='', mode='wb', compresslevel=9, mtime=0, fileobj=self._file),
                         encoding='utf-8', newline='\n')

    def close(self):
        try:
            super().close()
        finally:
            self._file.close()


def open_output(path):
    """A text writer for the expectations; a path ending in .gz is written gzip-compressed and reproducibly."""
    if path.endswith('.gz'):
        return _GzipText(path)
    return open(path, 'w', encoding='utf-8', newline='\n')


def finite(value):
    """Replace every non-finite float with the reader's spelling of it (the hex IEEE-754 single bits), so the
    JSON stays strictly parseable: .NET's JsonNode rejects NaN/Infinity tokens, and the C# side treats a string
    where a number is expected as "not finite"."""
    if isinstance(value, float):
        if value != value or value in (float('inf'), float('-inf')):
            return '0x%08X' % struct.unpack('<I', struct.pack('<f', value))[0]
        return value
    if isinstance(value, dict):
        return {k: finite(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [finite(v) for v in value]
    return value


def verify_only(cache, sample, libraries, files, fallback=True):
    """--verify-only: every manifest file resolved and digest-checked, nothing probed (see VERIFY-ONLY)."""
    t0 = time.perf_counter()
    steps = {}
    unresolved = []
    for rec in files:
        data, label, step = resolve_file(cache, sample, libraries, rec, fallback)
        if data is None or len(data) != rec['size']:
            unresolved.append((rec, label if data is None else ['size %d, pinned %d' % (len(data), rec['size'])]))
            print('UNRESOLVED %s %s %s :: %s' % (rec['key'], rec['role'], rec['sha256'], rec['entry']))
            for t in unresolved[-1][1]:
                print('    tried: %s' % t)
            continue
        steps[step] = steps.get(step, 0) + 1
        print('resolved  %-8s %s %s :: %s' % (step, rec['sha256'][:16], rec['key'], label))
    cache.close()
    print('verify-only: %d of %d manifest files reproduce their SHA-256 and size (%s), %d unresolved, %.1f s' % (
        len(files) - len(unresolved), len(files), ', '.join('%s=%d' % kv for kv in sorted(steps.items())),
        len(unresolved), time.perf_counter() - t0))
    return 1 if unresolved else 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('--manifest', required=True)
    ap.add_argument('--out')
    ap.add_argument('--sample', default=os.environ.get('BETHESDA_TEST_DATA_ROOT') or
                    os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Sample'))
    ap.add_argument('--probe-dir', default=os.path.dirname(os.path.abspath(__file__)))
    ap.add_argument('--limit', type=int, default=0)
    ap.add_argument('--scope', choices=('cut1a', 'cut1b'), default='cut1a',
                    help='the probe scope (see SCOPE AND PAYLOADS; default cut1a, the cut-1a record byte for byte)')
    ap.add_argument('--payloads', action='store_true', help='decode the animation payloads (see SCOPE AND PAYLOADS)')
    ap.add_argument('--no-fallback', action='store_true',
                    help="resolve through the manifest's own source and alsoIn candidates only, never a Data folder")
    ap.add_argument('--verify-only', action='store_true',
                    help='resolve and check every SHA-256 only; no probe, no output file (see VERIFY-ONLY)')
    args = ap.parse_args(argv)
    if not args.verify_only and not args.out:
        ap.error('--out is required unless --verify-only is given')
    sys.path.insert(0, args.probe_dir)
    import nif_feature_probe as p  # noqa: E402

    with open(args.manifest, 'r', encoding='utf-8') as f:
        manifest = json.load(f)
    files = [dict(rec, key=k['key']) for k in manifest['keys'] for rec in k['files']]
    if args.limit:
        files = files[:args.limit]
    libraries = steam_library_roots()
    cache = BsaCache(p)
    if args.verify_only:
        return verify_only(cache, args.sample, libraries, files, not args.no_fallback)
    t0 = time.perf_counter()
    resolved = unresolved = 0
    unresolved_list = []
    steps = {}
    textures_total = textures_resolved = 0
    with open_output(args.out) as out:
        for n, rec in enumerate(files, 1):
            data, label, step = resolve_file(cache, args.sample, libraries, rec, not args.no_fallback)
            if data is None:
                unresolved += 1
                unresolved_list.append((rec['key'], rec['role'], rec['source'], rec['entry'], label))
                line = {'sha256': rec['sha256'], 'entry': rec['entry'], 'role': rec['role'], 'key': rec['key'],
                        'unresolved': label}
            else:
                resolved += 1
                steps[step] = steps.get(step, 0) + 1
                line, blocks = build_record(p, rec, data, label, step, args.scope, args.payloads)
                if rec['entry'].lower().endswith('.nif') and line['declined'] is None:
                    tex = texture_expectations(cache, args.sample, rec, blocks)
                    if tex is not None:
                        line['textures'] = tex
                        textures_total += len(tex['entries'])
                        textures_resolved += sum(1 for e in tex['entries'].values() if e['sha256'])
            if args.payloads:
                line = bits_only(line)
            out.write(json.dumps(finite(line), separators=(',', ':'), allow_nan=False) + '\n')
            if n % 25 == 0:
                print('  %d/%d files (%.1f s)' % (n, len(files), time.perf_counter() - t0), file=sys.stderr)
    cache.close()
    print('resolved %d, unresolved %d of %d manifest files; by step: %s' % (
        resolved, unresolved, len(files), ', '.join('%s=%d' % kv for kv in sorted(steps.items()))))
    for key, role, source, entry, tried in unresolved_list:
        print('  UNRESOLVED %s %s %s :: %s' % (key, role, source, entry))
        for t in tried:
            print('      tried: %s' % t)
    print('textures: %d authored strings, %d resolved with bytes' % (textures_total, textures_resolved))
    print('output: %s (%d bytes, %.1f s)' % (args.out, os.path.getsize(args.out), time.perf_counter() - t0))
    return 0


if __name__ == '__main__':
    sys.exit(main())
