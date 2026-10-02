#!/usr/bin/env python3
"""nif_cover.py - per-key joint cover of NIF block types and probe tag values (design section 7.1).

Reads one or more JSONL files produced by the independent NIF probe (nif_feature_probe.py) or by the
seed header census (nif_census.py, the census.jsonl format) and, per version key
(header string, file version, user version, BS version, endianness), selects the minimal set of files
whose block types and tag values together cover every item seen in that key:

  * exact: a set-cover MILP (scipy.optimize.milp / HiGHS) solved lexicographically - minimum file
    count first (unit costs, zero gap), then minimum bytes among the minimum-count covers - under a
    time limit; the manifest states whether each stage proved optimality;
  * else greedy with reverse-delete pruning. Greedy ties: most new items, fewest bytes, then
    SHA-256 order (or entry path + source when no SHA-256 is present).

Plus one file per (game, platform) sharing the key, so every platform that ships the key is in the sample.
A key whose every record the probe DECLINED (a version outside its walk) gets one `declined-control`
file instead of a cover: those records carry block types but no feature parse.

Input contract, output shape, the game/platform mapping and the validation record are in
tools/scripts/nif_cover.README.md. Memory: the input is streamed twice and never loaded whole; the
per-key state is one bitmask per distinct item set, so a 300 MB census stays well under 1 GB.

SCOPES (2026-09-25, the cut-1b sample; docs/design/cut1b-nif-animation-reader-plan-20260925.md, slice 0):
  --scope cut1a (the default) is the tool exactly as before: every tag is an item and the manifest is byte for byte
  what it was (the checked-in cut1a-cover-manifest.json and report regenerate identically with --path-alias). A
  record carrying a cut-1b animation tag (anim:*) is refused, so a cut-1a cover is never built from that vocabulary.
  --scope cut1b --payloads (the pair is required: the vocabulary is payload-derived and the probe emits it only for a
  census run with --scope cut1b --payloads) covers the animation vocabulary instead:
    * candidates are the records carrying the probe's anim:animated marker, plus declined .kf records (the decline
      controls of the cut-1b reader); every other record is counted as outOfScope;
    * the version key gains the file kind as a sixth element ('20.2.0.7/uv11/bs34/LE/.kf', manifest 'fileKind'), so
      .nif and .kf are covered separately;
    * the items are the block types and the anim:* tags only; every cut-1a tag is dropped;
    * every manifest .kf (cover, floor or pinned; not a decline control) gets its skeleton companion: the nearest
      ancestor skeleton.nif in its primary source's (game, platform) namespace (first record per path in input
      order, which is the census's archive order), with no compatibility gate; an FNV .kf with none there resolves
      through FO3/PC's namespace (rule 'fo3-fallback', what the reader's --skeleton gives). The .kf carries
      'skeleton' {sha256, source, entry, gamePlatform, rule} (or null and 'skeletonMissing'), the skeleton is added
      with role 'skeleton' and 'skeletonFor' [.kf SHA-256], and same-path skeletons with different bytes in one
      namespace are listed under additions.skeletons.namespaceConflicts;
    * a skeleton path that has more than one distinct copy in that namespace is pinned PROVISIONALLY: census order
      is archive-name order, not established as the engine's archive override order (plan slice 3 settles it), so
      the .kf's 'skeleton' also carries 'provisional' (the reason) and 'alternatives' [{sha256, source, entry,
      gamePlatform}] (every other copy), each alternative is a manifest file (role 'skeleton') listing the .kf in
      'skeletonCandidateFor', and additions.skeletons.provisional lists the .kf;
    * every decline control carries 'declined', the probe's decline reason verbatim, whether the key's own decline
      control or a pinned one (a cut-1a manifest without pins keeps its exact shape).
  --pin pins.json (either scope) adds named controls by SHA-256 (role 'control' or 'declined-control', see
  load_pins); a pin already in the manifest is annotated ('controls'), never duplicated, and a pin that is in no
  input record, not at its entry and source, or of another size stops the run. Absent options (scope, payloads,
  pins) are not written, so a cut-1a manifest keeps its exact shape.
  verify re-derives each key's universe from the census and checks the manifest independently of the solver (see
  run_verify); --drop is its control.

Usage:
  nif_cover.py cover <probe.jsonl...> --out manifest.json --report report.txt
                     [--milp-seconds 120] [--key 20.2.0.7/11/34/LE ...] [--tie-order sha|input]
                     [--include-errors] [--include-declined] [--no-floor] [--merge-keys]
                     [--source-map census_spec.json] [--scope cut1a|cut1b] [--payloads] [--pin pins.json]
                     [--path-alias OLD=NEW ...]
  nif_cover.py verify <probe.jsonl...> --manifest manifest.json [--scope cut1a|cut1b] [--payloads]
                     [--drop SHA256 ...] [--json-out result.json]
  nif_cover.py keys <probe.jsonl...>          # list the version keys and record counts only
  nif_cover.py selftest                       # synthetic regression fixtures (probe shape, declined,
                                              # census error record, BOM, MILP vs greedy, determinism,
                                              # and the scope-cut1b rules B1-B15)
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import posixpath
import re
import sys
from typing import Iterable, Iterator

TOOL = 'nif_cover.py'
MANIFEST_SCHEMA = 2
TYPE_PREFIX = 'type:'
TAG_PREFIX = 'tag:'
ROLE_COVER = 'cover'
ROLE_FLOOR = 'floor'
ROLE_DECLINED = 'declined-control'
ROLE_SKELETON = 'skeleton'  # scope cut1b: the skeleton companion of a manifest .kf
ROLE_CONTROL = 'control'  # a named control pinned by --pin
PIN_ROLES = (ROLE_CONTROL, ROLE_DECLINED)
IDENTITY_SHA = 'sha256'
IDENTITY_PATH = 'path+size'

# --------------------------------------------------------------------------- scope (see SCOPES in the docstring)
SCOPES = ('cut1a', 'cut1b')
DEFAULT_SCOPE = 'cut1a'
ANIM_TAG_PREFIX = 'anim:'  # nif_feature_probe.py ANIMATION_TAG_PREFIX: emitted only at --scope cut1b --payloads
ANIM_MARKER_ITEM = TAG_PREFIX + ANIM_TAG_PREFIX + 'animated'  # ANIMATION_MARKER_TAG as a cover item
ANIM_ITEM_PREFIX = TAG_PREFIX + ANIM_TAG_PREFIX
KF_KIND = '.kf'
SKELETON_NAME = 'skeleton.nif'
SKELETON_RULE_WALK_UP = 'walk-up'  # the nearest ancestor skeleton.nif in the .kf's own (game, platform) namespace
SKELETON_RULE_FO3 = 'fo3-fallback'  # none in an FNV namespace: the same walk-up through FO3/PC (reader: --skeleton)
SKELETON_FALLBACK_GP = 'FO3/PC'

# --------------------------------------------------------------------------- game / platform mapping
# Census format: the `src` label written by nif_census.py, matched by prefix (first hit wins).
CENSUS_SOURCE_MAP = (
    ('FNV PC 2010 loose', 'FNV', 'PC'),
    ('FNV X360', 'FNV', 'X360'),
    ('FNV PS3', 'FNV', 'PS3'),
    ('FNV Steam', 'FNV', 'PC'),
    ('FO3 Steam', 'FO3', 'PC'),
    ('Oblivion Steam', 'Oblivion', 'PC'),
    ('Morrowind Steam', 'Morrowind', 'PC'),
    ('Tribunal disc', 'Morrowind', 'PC'),
    ('Bloodmoon disc', 'Morrowind', 'PC'),
    ('TES Construction Set', 'Morrowind', 'PC'),
    ('Skyrim LE Steam', 'Skyrim LE', 'PC'),
    ('Skyrim SE Steam', 'Skyrim SE', 'PC'),
    ('FO4 Steam', 'FO4', 'PC'),
    ('FO76 Steam', 'FO76', 'PC'),
    ('Starfield Steam', 'Starfield', 'PC'),
)

# Probe format: substrings of the lower-cased, forward-slashed path (first hit wins). A platform of
# None means "read the platform token of the Sample/Builds directory name", e.g.
# "Fallout - New Vegas (2010-8-22, X360 - Final)" -> X360. Steam and Steam Disc builds are PC here:
# the sampling axis is the file format the platform ships, not the distribution channel.
PATH_GAME_MAP = (
    ('fallout - new vegas (', 'FNV', None),
    ('pc_final_unpacked', 'FNV', 'PC'),
    ('360_july_unpacked', 'FNV', 'X360'),
    ('/common/fallout new vegas/', 'FNV', 'PC'),
    ('fallout 3 (', 'FO3', None),
    ('/common/fallout 3', 'FO3', 'PC'),
    ('the elder scrolls iv - oblivion (', 'Oblivion', None),
    ('/common/oblivion/', 'Oblivion', 'PC'),
    ('the elder scrolls iii - morrowind (', 'Morrowind', None),
    ('the elder scrolls iii - tribunal (', 'Morrowind', None),
    ('the elder scrolls iii - bloodmoon (', 'Morrowind', None),
    ('the elder scrolls construction set (', 'Morrowind', None),
    ('/common/morrowind/', 'Morrowind', 'PC'),
    ('the elder scrolls v - skyrim special edition (', 'Skyrim SE', None),
    ('/common/skyrim special edition/', 'Skyrim SE', 'PC'),
    ('the elder scrolls v - skyrim (', 'Skyrim LE', None),
    ('/common/skyrim/', 'Skyrim LE', 'PC'),
    ('fallout 4 (', 'FO4', None),
    ('/common/fallout 4/', 'FO4', 'PC'),
    ('fallout 76 (', 'FO76', None),
    ('/common/fallout76/', 'FO76', 'PC'),
    ('starfield (', 'Starfield', None),
    ('/common/starfield/', 'Starfield', 'PC'),
)
BUILD_TOKEN_RE = re.compile(r'\((?:\d{4}-\d{1,2}-\d{1,2}, )?([a-z0-9 ]+?) - [a-z0-9. ]+\)')
PLATFORM_TOKENS = {'pc': 'PC', 'steam': 'PC', 'steam disc': 'PC', 'x360': 'X360', 'ps3': 'PS3', 'pc floppy': 'PC'}
UNKNOWN = 'unknown'

ARCHIVE_EXT_RE = re.compile(r'\.(bsa|ba2)(?=/)', re.IGNORECASE)


def game_platform_from_source_label(label: str) -> tuple[str, str]:
    for prefix, game, platform in CENSUS_SOURCE_MAP:
        if label.startswith(prefix):
            return game, platform
    return UNKNOWN, UNKNOWN


def game_platform_from_path(path: str) -> tuple[str, str]:
    p = path.replace('\\', '/').lower()
    for needle, game, platform in PATH_GAME_MAP:
        if needle in p:
            if platform is None:
                m = BUILD_TOKEN_RE.search(p)
                platform = PLATFORM_TOKENS.get(m.group(1).strip(), UNKNOWN) if m else UNKNOWN
            return game, platform
    return UNKNOWN, UNKNOWN


# --------------------------------------------------------------------------- records
def version_to_int(v) -> int:
    if isinstance(v, int):
        return v
    parts = [int(x) for x in str(v).split('.')]
    while len(parts) < 4:
        parts.append(0)
    a, b, c, d = parts[:4]
    return (a << 24) | (b << 16) | (c << 8) | d


def version_str(v: int) -> str:
    return '%d.%d.%d.%d' % ((v >> 24) & 255, (v >> 16) & 255, (v >> 8) & 255, v & 255)


MERGED_KEY = ('(all keys merged)', 0, 0, 0, 0)  # --merge-keys: one cover across every key, for cross-key comparisons
UNREADABLE_KEY = ('(unreadable)', 0, 0, 0, 0)  # an error record that carries no version at all


def key_label(key) -> str:
    """'20.2.0.7/uv11/bs34/LE'; a scope-cut1b key carries the file kind as a sixth element: '.../LE/.kf'."""
    if key[:5] == MERGED_KEY:
        return 'merged' + ('/' + key[5] if len(key) > 5 else '')
    if key[:5] == UNREADABLE_KEY:
        return 'unreadable' + ('/' + key[5] if len(key) > 5 else '')
    _hs, ver, uv, bs, be = key[:5]
    return '%s/uv%d/bs%d/%s' % (version_str(ver), uv, bs, 'BE' if be else 'LE') + ('/' + key[5] if len(key) > 5 else '')


def key_order(key):
    """Manifest key order: version, user, BS, endianness, header, then (scope cut1b) the file kind."""
    return (key[1], key[2], key[3], key[4], key[0]) + tuple(key[5:])


def file_kind(entry: str) -> str:
    """The scope-cut1b key's sixth element: the entry's extension, lower case ('.nif', '.kf')."""
    return os.path.splitext(str(entry))[1].lower()


def standard_header(ver: int) -> str:
    """The header string retail files carry for a version: NetImmerse below 10.1.0.0, Gamebryo from it."""
    return '%s File Format, Version %s' % ('Gamebryo' if ver >= 0x0A010000 else 'NetImmerse', version_str(ver))


def parse_probe_key(k, header: str | None):
    """Accept {"header","version","user","bs","endian"} or "20.2.0.7/11/34/LE" (uv/bs prefixes optional).

    The real probe writes {"headerString","version","userVersion","bsVersion","bigEndian"}; every spelling
    is accepted. A missing header string is synthesized from the version so that a key is never split
    by its absence.
    """
    if isinstance(k, dict):
        hs = k.get('header', k.get('headerString', header))
        ver = version_to_int(k.get('version', k.get('ver', 0)))
        uv = int(k.get('user', k.get('userVersion', k.get('uv', 0))))
        bs = int(k.get('bs', k.get('bsVersion', 0)))
        e = k.get('endian', k.get('endianness', k.get('be', k.get('bigEndian', 'LE'))))
        be = 1 if (e in (1, True) or str(e).upper() == 'BE') else 0
        return hs or standard_header(ver), ver, uv, bs, be
    parts = str(k).split('/')
    if len(parts) != 4:
        raise ValueError('key %r: expected version/user/bs/endian' % k)
    ver = version_to_int(parts[0])
    uv = int(parts[1][2:] if parts[1].lower().startswith('uv') else parts[1])
    bs = int(parts[2][2:] if parts[2].lower().startswith('bs') else parts[2])
    be = 1 if parts[3].upper() == 'BE' else 0
    return header or standard_header(ver), ver, uv, bs, be


def strip_entry(entry: str) -> str:
    p = entry.replace('\\', '/').lower()
    i = p.find('meshes/')
    return p[i + 7:] if i >= 0 else p


def split_probe_path(rec: dict) -> tuple[str, str]:
    """(source, entry) for a record WITHOUT a `source` field: explicit archive/entry fields,
    'archive::entry', 'x.bsa/entry', a loose path split at '/meshes/', or a bare file."""
    if rec.get('archive') and rec.get('entry'):
        return str(rec['archive']), str(rec['entry'])
    path = str(rec.get('path', ''))
    if '::' in path:
        a, e = path.split('::', 1)
        return a, e
    m = ARCHIVE_EXT_RE.search(path.replace('\\', '/'))
    if m:
        p = path.replace('\\', '/')
        return p[:m.end()], p[m.end() + 1:]
    p = path.replace('\\', '/')
    i = p.lower().find('/meshes/')
    if i >= 0:
        return p[:i + 7], p[i + 8:]
    d, f = os.path.split(p)
    return d, f


def probe_tags(tags) -> Iterator[str]:
    if tags is None:
        return
    if isinstance(tags, dict):
        for k in sorted(tags):
            v = tags[k]
            if isinstance(v, (list, tuple, set)):
                for x in sorted(str(y) for y in v):
                    yield '%s%s=%s' % (TAG_PREFIX, k, x)
            elif v is True:
                yield '%s%s' % (TAG_PREFIX, k)
            elif v is False or v is None:
                continue
            else:
                yield '%s%s=%s' % (TAG_PREFIX, k, v)
        return
    for t in tags:
        yield TAG_PREFIX + t


class Rec:
    __slots__ = ('key', 'items', 'size', 'sha', 'source', 'entry', 'errors', 'declined', 'game', 'platform',
                 'sourcePath')

    def __init__(self, key, items, size, sha, source, entry, errors, game, platform, source_path=None,
                 declined=None):
        self.key = key
        self.items = items
        self.size = size
        self.sha = sha
        self.source = source
        self.entry = entry
        self.errors = errors
        self.declined = declined
        self.game = game
        self.platform = platform
        self.sourcePath = source_path

    @property
    def identity(self):
        return self.sha if self.sha else (strip_entry(self.entry), self.size)

    @property
    def identity_kind(self) -> str:
        return IDENTITY_SHA if self.sha else IDENTITY_PATH

    @property
    def gp(self):
        return '%s/%s' % (self.game, self.platform)


def census_key(raw: dict) -> tuple:
    ver = int(raw.get('ver', 0) or 0)
    if not ver and not raw.get('hs'):
        return UNREADABLE_KEY
    return (raw.get('hs') or standard_header(ver), ver, int(raw.get('uv', 0) or 0), int(raw.get('bs', 0) or 0),
            int(raw.get('be', 0) or 0))


def normalize(raw: dict, source_map: dict | None) -> Rec | None:
    """Census line -> Rec (block types only); probe line -> Rec (block types + tags). None = unusable."""
    if 'key' in raw:
        # Probe format (nif_feature_probe.py). A record is recognised by its key alone; an error record
        # may omit blockTypes and tags.
        key = parse_probe_key(raw['key'], raw.get('header'))
        bt = raw.get('blockTypes') or []
        if isinstance(bt, dict):
            bt = list(bt.keys())  # the probe writes {name: count}; the names are the items
        items = [TYPE_PREFIX + str(t) for t in bt]
        items.extend(probe_tags(raw.get('tags')))
        if raw.get('archive') and raw.get('entry'):
            source, entry = str(raw['archive']), str(raw['entry'])
        elif raw.get('source'):
            # The probe writes `path` RELATIVE to `source` (a tree root or an archive path): the entry
            # is the path verbatim, directories included.
            source = str(raw['source']).replace('\\', '/')
            entry = str(raw.get('path', '')).replace('\\', '/')
        else:
            source, entry = split_probe_path(raw)
        details = raw.get('details') or {}
        size = raw.get('size')
        if size is None and isinstance(details, dict):
            size = details.get('size', details.get('bytes', 0))
        errors = raw.get('errors') or []
        if isinstance(errors, str):
            errors = [errors]
        declined = raw.get('declined')
        game = raw.get('game')
        platform = raw.get('platform')
        if platform and str(platform).lower() in ('x360', 'ps3', 'pc'):
            platform = str(platform).upper() if str(platform).lower() != 'pc' else 'PC'
        elif platform and str(platform).lower() in ('unknown', 'auto'):
            platform = None
        if not game or not platform:
            g2, p2 = game_platform_from_path(str(raw.get('path', '')) + ' ' + source)
            game, platform = game or g2, platform or p2
        sha = raw.get('sha256')
        return Rec(key, items, int(size or 0), str(sha).lower() if sha else None, source, entry, list(errors),
                   str(game), str(platform), declined=str(declined) if declined else None)
    if 'err' in raw and 'src' in raw and 'blockTypes' not in raw:
        # Census error record: nif_census.py writes {err, src, path, size} with no hs/types (or a partial
        # header). Counted per key as an error, never a candidate.
        src = raw.get('src', '')
        return Rec(census_key(raw), [], int(raw.get('size', 0) or 0), None, src, raw.get('path', ''),
                   [str(raw['err'])], *game_platform_from_source_label(src))
    if 'hs' in raw and 'types' in raw and 'blockTypes' not in raw:
        key = census_key(raw)
        items = [TYPE_PREFIX + t for t in raw['types']]
        src = raw.get('src', '')
        game, platform = game_platform_from_source_label(src)
        sp = source_map.get(src) if source_map else None
        return Rec(key, items, int(raw.get('size', 0) or 0), None, src, raw.get('path', ''), [], game, platform, sp)
    return None


def stream(paths: Iterable[str], source_map: dict | None) -> Iterator[tuple[int, Rec]]:
    """Yield (record index, Rec) over every line of every input, streaming. utf-8-sig: a BOM (what a
    PowerShell redirection writes) is skipped and a BOM-less file reads identically."""
    i = 0
    for path in paths:
        with open(path, encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                rec = normalize(json.loads(line), source_map)
                if rec is None:
                    raise ValueError('%s: line %d is neither a census nor a probe record' % (path, i + 1))
                yield i, rec
                i += 1


# --------------------------------------------------------------------------- per-key state (pass 1)
class Group:
    """All candidate files sharing one item set, represented by the smallest (size, tie) member."""
    __slots__ = ('mask', 'size', 'tie', 'index', 'source', 'entry', 'sha', 'identity', 'identity_kind', 'count',
                 'nitems', 'declined')

    def __init__(self, mask, size, tie, index, rec: Rec):
        self.mask = mask
        self.size = size
        self.tie = tie
        self.index = index
        self.source = rec.source
        self.entry = rec.entry
        self.sha = rec.sha
        self.identity = rec.identity
        self.identity_kind = rec.identity_kind
        self.count = 1
        self.nitems = mask.bit_count()
        self.declined = rec.declined


class KeyState:
    def __init__(self):
        self.items: dict[str, int] = {}
        self.groups: dict[int, Group] = {}
        self.identities: set = set()
        # (identity, mask) pairs already admitted as candidates. Items are NOT a pure function of the bytes:
        # the probe keys some tags on the platform (speccompanion=1 fires for an X360 copy and not for the
        # byte-identical PS3 or PC copy), so two records with one identity can carry two item sets, and a
        # dedup on identity alone registered items that no candidate carried (measured 2026-09-23: the
        # universe bit count fell short of the item count on the bs34 keys).
        self.candidates: set = set()
        self.records = 0
        self.errors = 0
        self.declined = 0
        self.declined_best: Group | None = None
        self.gp_records: collections.Counter = collections.Counter()
        self.floor_best: dict[str, Group] = {}
        self.sources: collections.Counter = collections.Counter()

    def mask_of(self, items) -> int:
        m = 0
        for it in items:
            b = self.items.get(it)
            if b is None:
                b = len(self.items)
                self.items[it] = b
            m |= 1 << b
        return m

    def item_names(self, mask: int) -> list[str]:
        names = self._names()
        out = []
        while mask:
            low = mask & -mask
            out.append(names[low.bit_length() - 1])
            mask ^= low
        return sorted(out)

    def _names(self):
        names = [None] * len(self.items)
        for k, v in self.items.items():
            names[v] = k
        return names


# --------------------------------------------------------------------------- scope
def check_scope_args(scope: str, payloads: bool) -> None:
    """The valid pairs: the default (cut1a, no --payloads) and (cut1b, --payloads). The cut-1b vocabulary is derived
    from the animation payloads and the probe emits it only for a census run with --scope cut1b --payloads, so the
    other two pairs would silently cover something else."""
    if scope not in SCOPES:
        raise SystemExit('--scope %r: expected one of %s' % (scope, ', '.join(SCOPES)))
    if scope == 'cut1b' and not payloads:
        raise SystemExit('--scope cut1b needs --payloads: the cut-1b vocabulary (anim:* tags) exists only in a probe '
                         'census run with --scope cut1b --payloads')
    if scope == 'cut1a' and payloads:
        raise SystemExit('--payloads needs --scope cut1b: the cut-1a cover takes no payload-derived item')


def apply_scope(rec: Rec, scope: str) -> bool:
    """Scope the record in place and say whether it is a candidate record of the scope.

    cut1a (the default): the record is left exactly as it was read; a record carrying a cut-1b animation tag is
    refused, since a cut-1a cover must never be built from the cut-1b vocabulary by accident.
    cut1b: the key gains the file kind (the entry's extension) as a sixth element, the items keep the block types
    and the anim:* tags only (every cut-1a tag is dropped), and the record is in scope when it is animated (the
    probe's anim:animated marker) or, declined by the probe, a .kf (the decline controls of the cut-1b reader).
    """
    if scope == DEFAULT_SCOPE:
        if any(it.startswith(ANIM_ITEM_PREFIX) for it in rec.items):
            raise SystemExit('%s :: %s carries cut-1b animation tags (anim:*); cover it with --scope cut1b --payloads'
                             % (rec.source, rec.entry))
        return True
    kind = file_kind(rec.entry)
    rec.key = tuple(rec.key[:5]) + (kind,)
    animated = ANIM_MARKER_ITEM in rec.items
    rec.items = [it for it in rec.items if it.startswith(TYPE_PREFIX) or it.startswith(ANIM_ITEM_PREFIX)]
    if rec.declined:
        return kind == KF_KIND
    return animated


class Side:
    """Side tables filled in pass 1 from EVERY record, in scope or not: the records of the --pin SHA-256s and, at
    scope cut1b, every skeleton.nif (the namespace the .kf walk-up searches, first record per (game, platform) and
    path in input order, which is the census's archive order)."""

    def __init__(self, pin_shas=()):
        self.pin_shas = set(pin_shas)
        self.pinned: dict[str, list[Rec]] = collections.defaultdict(list)
        self.namespace: dict[str, dict[str, Rec]] = collections.defaultdict(dict)
        self.skeleton_copies: dict[str, list[Rec]] = collections.defaultdict(list)
        self.path_copies: dict[tuple[str, str], dict[str, Rec]] = collections.defaultdict(dict)  # first per SHA
        self.conflicts: list[dict] = []
        self.animated_records = 0

    def note(self, rec: Rec, scope: str) -> None:
        if rec.sha and rec.sha in self.pin_shas:
            self.pinned[rec.sha].append(rec)
        if scope != 'cut1b':
            return
        if ANIM_MARKER_ITEM in rec.items:
            self.animated_records += 1
        path = rec.entry.replace('\\', '/').lower()
        if os.path.basename(path) != SKELETON_NAME or not rec.sha:
            return
        self.skeleton_copies[rec.sha].append(rec)
        self.path_copies[(rec.gp, path)].setdefault(rec.sha, rec)
        ns = self.namespace[rec.gp]
        first = ns.get(path)
        if first is None:
            ns[path] = rec
        elif first.sha != rec.sha:
            self.conflicts.append({'gamePlatform': rec.gp, 'entry': path, 'kept': first.sha, 'keptSource': first.source,
                                   'shadowed': rec.sha, 'shadowedSource': rec.source})


def tie_of(order: str, index: int, rec: Rec) -> tuple[str, str, str]:
    """The third greedy tie-break: SHA-256 (else stripped entry, source), or first-seen order as a fixed-width string."""
    if order == 'input':
        return ('', '', '%012d' % index)
    return (rec.sha or '', strip_entry(rec.entry), rec.source)


def pass1(paths, order: str, include_errors: bool, source_map, key_filter, merge: bool = False,
          include_declined: bool = False, scope: str = DEFAULT_SCOPE, side: Side | None = None) -> tuple[dict, dict]:
    keys: dict[tuple, KeyState] = {}
    stats = {'records': 0, 'skippedErrors': 0, 'skippedDeclined': 0, 'filteredOut': 0,
             'unmappedSources': collections.Counter()}
    if scope != DEFAULT_SCOPE:
        stats['outOfScope'] = 0
    for index, rec in stream(paths, source_map):
        stats['records'] += 1
        in_scope = apply_scope(rec, scope)
        if side is not None:
            side.note(rec, scope)
        if key_filter and not key_filter(rec.key):
            stats['filteredOut'] += 1
            continue
        if not in_scope:
            stats['outOfScope'] += 1
            continue
        if merge:
            rec.key = MERGED_KEY + tuple(rec.key[5:])
        st = keys.get(rec.key)
        if st is None:
            st = keys[rec.key] = KeyState()
        st.records += 1
        st.sources[rec.source] += 1
        st.gp_records[rec.gp] += 1
        if rec.game == UNKNOWN or rec.platform == UNKNOWN:
            stats['unmappedSources'][rec.source] += 1
        if rec.errors and not include_errors:
            st.errors += 1
            stats['skippedErrors'] += 1
            continue
        tie = tie_of(order, index, rec)
        if rec.declined and not include_declined:
            # Declined by the probe: the header's block types are known, nothing else was parsed. Not a
            # candidate; the smallest one stands in as the key's decline control when nothing else exists.
            st.declined += 1
            stats['skippedDeclined'] += 1
            mask = 0
            db = st.declined_best
            if db is None or (rec.size, tie) < (db.size, db.tie):
                st.declined_best = Group(mask, rec.size, tie, index, rec)
                st.declined_best.entry = rec.entry
            continue
        mask = st.mask_of(rec.items)
        # The floor candidate is tracked BEFORE the identity check: a (game, platform) whose every file is a
        # byte-identical copy of another platform's file still needs its own copy in the sample.
        fb = st.floor_best.get(rec.gp)
        cand = (-mask.bit_count(), rec.size, tie)
        if fb is None or cand < (-fb.nitems, fb.size, fb.tie):
            st.floor_best[rec.gp] = Group(mask, rec.size, tie, index, rec)
        ident = rec.identity
        st.identities.add(ident)
        if (ident, mask) in st.candidates:
            continue  # same bytes (sha) or same stripped path + size AND the same items: first occurrence is the candidate
        st.candidates.add((ident, mask))
        g = st.groups.get(mask)
        if g is None:
            st.groups[mask] = Group(mask, rec.size, tie, index, rec)
        else:
            g.count += 1
            if (rec.size, tie) < (g.size, g.tie):
                st.groups[mask] = ng = Group(mask, rec.size, tie, index, rec)
                ng.count = g.count
    return keys, stats


# --------------------------------------------------------------------------- covers
def greedy(groups: list[Group], universe: int) -> list[Group]:
    uncovered = universe
    chosen = []
    while uncovered:
        best = None
        best_k = None
        for g in groups:
            gain = (g.mask & uncovered).bit_count()
            if gain == 0:
                continue
            k = (-gain, g.size, g.tie)
            if best_k is None or k < best_k:
                best, best_k = g, k
        if best is None:
            break
        chosen.append(best)
        uncovered &= ~best.mask
    return chosen


def prune(chosen: list[Group]) -> list[Group]:
    """Reverse-delete: drop any member whose items the rest still cover, largest file first."""
    keep = list(chosen)
    order = sorted(range(len(chosen)), key=lambda i: (-chosen[i].size, i))
    for i in order:
        g = chosen[i]
        rest = [h for h in keep if h is not g]
        if not rest:
            continue
        union = 0
        for h in rest:
            union |= h.mask
        if g.mask & ~union == 0:
            keep = rest
    return keep


def _cover_matrix(groups: list[Group], n_items: int):
    import numpy as np
    from scipy.sparse import csc_matrix
    rows, cols = [], []
    for j, g in enumerate(groups):
        m = g.mask
        while m:
            low = m & -m
            rows.append(low.bit_length() - 1)
            cols.append(j)
            m ^= low
    return csc_matrix((np.ones(len(rows)), (rows, cols)), shape=(n_items, len(groups)))


def _covers_universe(chosen: list[Group], n_items: int) -> bool:
    union = 0
    for g in chosen:
        union |= g.mask
    return union.bit_count() == n_items


def exact(groups: list[Group], n_items: int, seconds: float) -> dict:
    """Lexicographic minimum: (1) the minimum file count with unit costs, (2) the minimum bytes among
    the minimum-count covers (count fixed by an equality constraint). Both stages run with
    mip_rel_gap = mip_abs_gap = 0, so a proved stage is a proved optimum, not a tolerance.

    A single objective 1 + size/total cannot enforce the byte order: the whole secondary term is smaller
    than HiGHS's default absolute gap, so the solver may return any minimum-count cover while reporting
    mip_gap 0 (measured 2026-09-23 on bs34 BE, 25 B above the byte minimum, and bs83, 24 B).
    """
    out = {'available': False, 'status': None, 'message': None, 'provedOptimal': False, 'bytesProved': False,
           'chosen': None, 'candidates': len(groups), 'timeLimitSeconds': seconds,
           'countStage': None, 'bytesStage': None}
    try:
        import numpy as np
        from scipy.optimize import Bounds, LinearConstraint, milp
        from scipy.sparse import csc_matrix
    except ImportError as e:  # noqa: BLE001
        out['message'] = 'scipy unavailable: %s' % e
        return out
    out['available'] = True
    n = len(groups)
    if n == 0 or n_items == 0:
        out.update(status=0, message='empty', provedOptimal=True, bytesProved=True, chosen=[])
        return out
    a = _cover_matrix(groups, n_items)
    cover = LinearConstraint(a, lb=np.ones(n_items), ub=np.inf)
    # mip_rel_gap 0 closes the relative gap; both objectives are integers (a count, then bytes), so
    # HiGHS's default absolute gap of 1e-6 cannot admit a suboptimal integer solution either.
    opts = {'time_limit': seconds, 'disp': False, 'mip_rel_gap': 0.0}

    def stage_record(res):
        rec = {'status': int(res.status), 'message': str(res.message), 'success': bool(res.success)}
        for attr in ('mip_gap', 'mip_dual_bound', 'mip_node_count'):
            v = getattr(res, attr, None)
            if v is not None:
                rec[attr] = float(v) if attr != 'mip_node_count' else int(v)
        return rec

    # Stage 1: minimum count.
    r1 = milp(c=np.ones(n), constraints=cover, integrality=np.ones(n), bounds=Bounds(0, 1), options=opts)
    out['countStage'] = stage_record(r1)
    out['status'] = int(r1.status)
    out['message'] = str(r1.message)
    for k in ('mip_gap', 'mip_dual_bound', 'mip_node_count'):
        if k in out['countStage']:
            out[k] = out['countStage'][k]
    count_proved = bool(r1.success) and int(r1.status) == 0
    chosen1 = None
    if r1.x is not None:
        c1 = [groups[j] for j in range(n) if r1.x[j] > 0.5]
        if _covers_universe(c1, n_items):
            chosen1 = c1
        else:
            out['message'] += ' (stage-1 solution does not cover the universe; discarded)'
    out['provedOptimal'] = count_proved and chosen1 is not None
    if chosen1 is None:
        return out
    out['chosen'] = chosen1
    if not count_proved:
        return out
    # Stage 2: minimum bytes with the count fixed at the proved minimum.
    k = len(chosen1)
    sizes = np.array([float(g.size) for g in groups])
    count_eq = LinearConstraint(csc_matrix(np.ones((1, n))), lb=[k], ub=[k])
    r2 = milp(c=sizes, constraints=[cover, count_eq], integrality=np.ones(n), bounds=Bounds(0, 1), options=opts)
    out['bytesStage'] = stage_record(r2)
    if r2.x is not None:
        c2 = [groups[j] for j in range(n) if r2.x[j] > 0.5]
        if len(c2) == k and _covers_universe(c2, n_items):
            if sum(g.size for g in c2) <= sum(g.size for g in chosen1):
                out['chosen'] = c2
            out['bytesProved'] = bool(r2.success) and int(r2.status) == 0
        else:
            out['bytesStage']['message'] += ' (stage-2 solution invalid; discarded)'
    return out


def solve_key(st: KeyState, seconds: float) -> dict:
    groups = sorted(st.groups.values(), key=lambda g: (g.size, g.tie))
    universe = 0
    for g in groups:
        universe |= g.mask
    n_items = len(st.items)
    assert universe.bit_count() == n_items
    g0 = greedy(groups, universe)
    g1 = prune(g0)
    ex = exact(groups, n_items, seconds)
    result = {'greedy': g0, 'greedyPruned': g1, 'exact': ex, 'universe': universe}
    candidates = [('milp', ex['chosen'])] if ex['chosen'] is not None else []
    candidates.append(('greedy+prune', g1))
    method, chosen = min(candidates, key=lambda mc: (len(mc[1]), sum(g.size for g in mc[1])))
    if method == 'milp' and not ex['provedOptimal']:
        method = 'milp-feasible'
    result['method'] = method
    result['chosen'] = sorted(chosen, key=lambda g: (g.size, g.tie))
    return result


# --------------------------------------------------------------------------- pass 2: sources of the chosen files
def pass2(paths, keys: dict, solved: dict, source_map, key_filter, merge: bool = False,
          scope: str = DEFAULT_SCOPE) -> dict:
    wanted: dict[tuple, dict] = {}
    for key, st in keys.items():
        want = {}
        for g in solved[key]['chosen']:
            want[g.identity] = []
        for g in st.floor_best.values():
            want.setdefault(g.identity, [])
        if st.declined_best is not None:
            want.setdefault(st.declined_best.identity, [])
        wanted[key] = want
    for _index, rec in stream(paths, source_map):
        if not apply_scope(rec, scope):
            continue
        if key_filter and not key_filter(rec.key):
            continue
        if merge:
            rec.key = MERGED_KEY + tuple(rec.key[5:])
        want = wanted.get(rec.key)
        if not want:
            continue
        lst = want.get(rec.identity)
        if lst is not None:
            lst.append((rec.source, rec.entry, rec.gp, rec.sourcePath))
    return wanted


# --------------------------------------------------------------------------- output
def file_entry(st: KeyState, g: Group, role: str, sources: list, source_map_used: bool) -> dict:
    names = st.item_names(g.mask)
    srcs = sorted(set(sources), key=lambda s: (s[0], s[1]))
    primary = next((s for s in srcs if s[0] == g.source and s[1] == g.entry), srcs[0] if srcs else (g.source, g.entry, UNKNOWN, None))
    also = [{'source': s[0], 'entry': s[1], 'gamePlatform': s[2], 'identity': g.identity_kind} for s in srcs if s is not primary]
    e = {
        'role': role,
        'source': primary[0],
        'entry': primary[1],
        'sha256': g.sha,
        'identity': g.identity_kind,
        'size': g.size,
        'gamePlatforms': sorted({s[2] for s in srcs}) or [primary[2]],
        'itemCount': len(names),
        'blockTypes': [n[len(TYPE_PREFIX):] for n in names if n.startswith(TYPE_PREFIX)],
        'tags': [n[len(TAG_PREFIX):] for n in names if n.startswith(TAG_PREFIX)],
        'identicalItemSetFiles': g.count,
        'alsoIn': also,
    }
    if source_map_used and primary[3]:
        e['sourcePath'] = primary[3]
    return e


def key_entry(key, st: KeyState, r: dict, want: dict, args) -> tuple[dict, list, list, str | None]:
    """One key's manifest entry: its cover files, its floor and its decline control. Returns the entry with the
    cover files, the floor files and the decline control's entry (None when the key has a cover)."""
    files = []
    covered_gp = set()
    chosen_ids = set()
    for g in r['chosen']:
        fe = file_entry(st, g, ROLE_COVER, want.get(g.identity, []), args.source_map is not None)
        files.append(fe)
        covered_gp.update(fe['gamePlatforms'])
        chosen_ids.add(g.identity)
    floor = {}
    floor_unavailable = []
    declined_control = None
    if not st.groups and st.declined_best is not None:
        # Every walkable record of this key was declined: one decline control, no cover, no floor.
        g = st.declined_best
        fe = file_entry(st, g, ROLE_DECLINED, want.get(g.identity, []), args.source_map is not None)
        if args.scope != DEFAULT_SCOPE or args.pin:
            fe['declined'] = g.declined  # the same member a pinned decline control carries (added_file_entry)
        files.append(fe)
        declined_control = fe['entry']
    elif not args.no_floor:
        for gp in sorted(st.gp_records):
            if gp in covered_gp:
                floor[gp] = None
                continue
            g = st.floor_best.get(gp)
            if g is None:  # every record of this game/platform carried probe errors or was declined
                floor[gp] = None
                floor_unavailable.append(gp)
                continue
            if g.identity in chosen_ids:
                floor[gp] = None
                continue
            fe = file_entry(st, g, ROLE_FLOOR, want.get(g.identity, []), args.source_map is not None)
            fe['floorFor'] = gp
            files.append(fe)
            chosen_ids.add(g.identity)
            covered_gp.update(fe['gamePlatforms'])
            floor[gp] = fe['entry']
    n_types = sum(1 for it in st.items if it.startswith(TYPE_PREFIX))
    n_tags = len(st.items) - n_types
    ex = r['exact']
    cover_files = [f for f in files if f['role'] == ROLE_COVER]
    floor_files = [f for f in files if f['role'] == ROLE_FLOOR]
    exact_count = len(ex['chosen']) if ex['chosen'] is not None else None
    entry = {
        'key': key_label(key),
        'merged': key[:5] == MERGED_KEY,
        'header': key[0],
        'version': version_str(key[1]),
        'versionInt': key[1],
        'userVersion': key[2],
        'bsVersion': key[3],
        'endianness': 'BE' if key[4] else 'LE',
    }
    if len(key) > 5:
        entry['fileKind'] = key[5]
    entry.update({
        'records': st.records,
        'recordsWithErrorsSkipped': st.errors,
        'recordsDeclined': st.declined,
        'uniqueFiles': len(st.identities),
        'distinctItemSets': len(st.groups),
        'items': {'blockTypes': n_types, 'tags': n_tags, 'total': len(st.items)},
        'gamePlatforms': dict(sorted(st.gp_records.items())),
        'sources': dict(sorted(st.sources.items())),
        'cover': {
            'method': r['method'],
            'count': len(cover_files),
            'bytes': sum(f['size'] for f in cover_files),
            'greedy': {'count': len(r['greedy']), 'bytes': sum(g.size for g in r['greedy'])},
            'greedyPruned': {'count': len(r['greedyPruned']), 'bytes': sum(g.size for g in r['greedyPruned'])},
            'milp': {k: v for k, v in ex.items() if k != 'chosen'} | {
                'count': exact_count,
                'bytes': sum(g.size for g in ex['chosen']) if ex['chosen'] is not None else None},
        },
        'floor': floor,
        'floorUnavailable': floor_unavailable,
        'floorFiles': len(floor_files),
        'declinedControl': declined_control,
        'files': files,
    })
    return entry, cover_files, floor_files, declined_control


def load_pins(path: str | None) -> list[dict]:
    """--pin <pins.json>: {"schema": 1, "pins": [{"name", "sha256", "role", ["size"], ["entry"], ["sourceHint"],
    ["note"]}]}. role is 'control' (a named control) or 'declined-control' (a decline control beyond the key's
    automatic one). Every pin must be found in the input; see apply_additions."""
    if not path:
        return []
    with open(path, encoding='utf-8-sig') as f:
        spec = json.load(f)
    pins = spec['pins'] if isinstance(spec, dict) else spec
    for p in pins:
        if p.get('role') not in PIN_ROLES:
            raise SystemExit('--pin %s: pin %r has role %r; expected one of %s' % (path, p.get('name'), p.get('role'),
                                                                                  ', '.join(PIN_ROLES)))
        if not p.get('name') or len(str(p.get('sha256', ''))) != 64:
            raise SystemExit('--pin %s: every pin needs a name and a 64-digit sha256 (%r)' % (path, p))
        p['sha256'] = p['sha256'].lower()
    return pins


def walk_up(namespace: dict, entry: str):
    """The nearest ancestor skeleton.nif of `entry` in a namespace {lower-case path: Rec}: the entry's own
    directory first, then each parent, then the root. No compatibility gate: the nearest one wins."""
    d = posixpath.dirname(entry.replace('\\', '/').lower())
    while True:
        rec = namespace.get(posixpath.join(d, SKELETON_NAME) if d else SKELETON_NAME)
        if rec is not None:
            return rec
        if not d:
            return None
        d = posixpath.dirname(d)


def added_file_entry(recs: list, primary: Rec, role: str) -> dict:
    """A manifest file that is not a cover candidate (a pinned control, a skeleton companion): the same fields as
    file_entry, its items those of its primary record (scoped), identicalItemSetFiles 0."""
    srcs = sorted({(r.source, r.entry, r.gp) for r in recs}, key=lambda s: (s[0], s[1]))
    names = sorted(set(primary.items))
    e = {
        'role': role,
        'source': primary.source,
        'entry': primary.entry,
        'sha256': primary.sha,
        'identity': primary.identity_kind,
        'size': primary.size,
        'gamePlatforms': sorted({s[2] for s in srcs}) or [primary.gp],
        'itemCount': len(names),
        'blockTypes': [n[len(TYPE_PREFIX):] for n in names if n.startswith(TYPE_PREFIX)],
        'tags': [n[len(TAG_PREFIX):] for n in names if n.startswith(TAG_PREFIX)],
        'identicalItemSetFiles': 0,
        'alsoIn': [{'source': s[0], 'entry': s[1], 'gamePlatform': s[2], 'identity': primary.identity_kind}
                   for s in srcs if (s[0], s[1]) != (primary.source, primary.entry)],
    }
    if primary.declined:
        e['declined'] = primary.declined
    return e


def pin_primary(recs: list, pin: dict) -> tuple[Rec | None, list[str]]:
    """The record a pin names (its entry, in a source containing its sourceHint), and the problems found."""
    problems = []
    want_entry = str(pin.get('entry') or '').replace('\\', '/').lower()
    hint = str(pin.get('sourceHint') or '').replace('\\', '/').lower()
    matches = [r for r in recs if (not want_entry or r.entry.replace('\\', '/').lower() == want_entry)
               and (not hint or hint in r.source.replace('\\', '/').lower())]
    if not matches:
        problems.append('pin %r: SHA-256 %s is in the input, but not at entry %r in a source containing %r (found at: '
                        '%s)' % (pin['name'], pin['sha256'], pin.get('entry'), pin.get('sourceHint'),
                                 '; '.join(sorted({'%s :: %s' % (r.source, r.entry) for r in recs}))))
    primary = matches[0] if matches else recs[0]
    if pin.get('size') is not None and int(pin['size']) != primary.size:
        problems.append('pin %r: size %d, the input record has %d' % (pin['name'], int(pin['size']), primary.size))
    return primary, problems


def apply_additions(entries: dict, keys: dict, side: Side, pins: list, args) -> dict:
    """Adds the pinned controls and (scope cut1b) the skeleton companions of every manifest .kf to the per-key
    entries, creating an entry for a key that holds added files only. A file already in the manifest is annotated,
    never duplicated: every SHA-256 appears once. Returns the manifest's 'additions' summary.

    Pins fail closed: a pin whose SHA-256 is not in the input, or is not at the entry and source the pin names, or
    whose size differs, stops the run with the list of problems."""
    by_sha = {}
    for key, e in entries.items():
        for f in e['files']:
            if f['sha256']:
                by_sha.setdefault(f['sha256'], (key, f))

    def entry_for(key):
        if key not in entries:
            st = KeyState()
            r = solve_key(st, args.milp_seconds)
            e, _c, _f, _d = key_entry(key, st, r, {}, args)
            e['cover']['method'] = 'none'
            e['addedFilesOnly'] = True
            entries[key] = e
            keys[key] = st
        return entries[key]

    def add(recs, primary, role):
        f = added_file_entry(recs, primary, role)
        entry_for(primary.key)['files'].append(f)
        by_sha[primary.sha] = (primary.key, f)
        return f

    summary = {'pins': [], 'skeletons': None}
    problems = []
    for pin in pins:
        recs = side.pinned.get(pin['sha256'], [])
        if not recs:
            problems.append('pin %r: SHA-256 %s is in no input record' % (pin['name'], pin['sha256']))
            continue
        primary, issues = pin_primary(recs, pin)
        problems.extend(issues)
        hit = by_sha.get(pin['sha256'])
        f = hit[1] if hit is not None else add(recs, primary, pin['role'])
        control = {'name': pin['name']}
        if pin.get('note'):
            control['note'] = pin['note']
        f.setdefault('controls', []).append(control)
        summary['pins'].append({'name': pin['name'], 'sha256': pin['sha256'], 'role': f['role'],
                                'key': key_label(by_sha[pin['sha256']][0]), 'source': f['source'], 'entry': f['entry'],
                                'addedByPin': hit is None})
    if problems:
        raise SystemExit('--pin: %d problem(s):\n  %s' % (len(problems), '\n  '.join(problems)))
    if args.scope != 'cut1b':
        return summary
    kf_files = [f for key in sorted(entries, key=key_order) if len(key) > 5 and key[5] == KF_KIND
                for f in entries[key]['files'] if f['role'] != ROLE_DECLINED]
    counts = collections.Counter()
    missing = []
    provisional = []

    def skeleton_file(rec):
        hit = by_sha.get(rec.sha)
        return hit[1] if hit is not None else add(side.skeleton_copies.get(rec.sha) or [rec], rec, ROLE_SKELETON)
    for f in kf_files:
        gp = '%s/%s' % game_platform_from_path(f['source'])
        rec = walk_up(side.namespace.get(gp, {}), f['entry'])
        rule = SKELETON_RULE_WALK_UP
        if rec is None and gp.startswith('FNV/'):
            rec = walk_up(side.namespace.get(SKELETON_FALLBACK_GP, {}), f['entry'])
            rule = SKELETON_RULE_FO3
        if rec is None:
            f['skeleton'] = None
            f['skeletonMissing'] = 'no ancestor %s in %s%s' % (
                SKELETON_NAME, gp, ' or %s' % SKELETON_FALLBACK_GP if gp.startswith('FNV/') else '')
            missing.append({'sha256': f['sha256'], 'entry': f['entry'], 'gamePlatform': gp})
            counts['missing'] += 1
            continue
        counts[rule] += 1
        f['skeleton'] = {'sha256': rec.sha, 'source': rec.source, 'entry': rec.entry, 'gamePlatform': rec.gp,
                         'rule': rule}
        s = skeleton_file(rec)
        if f['sha256'] not in s.setdefault('skeletonFor', []):
            s['skeletonFor'].append(f['sha256'])
        copies = side.path_copies.get((rec.gp, rec.entry.replace('\\', '/').lower()), {})
        alternatives = [r for sha, r in sorted(copies.items(), key=lambda kv: (kv[1].source, kv[0])) if sha != rec.sha]
        if alternatives:
            f['skeleton']['provisional'] = (
                '%d distinct copies of %s in the %s namespace; the census-order (archive-name) pick is pinned, and that '
                'order is not established as the engine\'s archive override order (plan slice 3 settles it); every '
                'other copy is pinned under alternatives' % (len(copies), rec.entry, rec.gp))
            f['skeleton']['alternatives'] = [{'sha256': r.sha, 'source': r.source, 'entry': r.entry,
                                              'gamePlatform': r.gp} for r in alternatives]
            for r in alternatives:
                a = skeleton_file(r)
                if f['sha256'] not in a.setdefault('skeletonCandidateFor', []):
                    a['skeletonCandidateFor'].append(f['sha256'])
            provisional.append({'sha256': f['sha256'], 'entry': f['entry'], 'skeleton': rec.entry,
                                'gamePlatform': rec.gp, 'pinned': rec.sha,
                                'alternatives': [r.sha for r in alternatives]})
    summary['skeletons'] = {'kfFiles': len(kf_files), 'walkUp': counts[SKELETON_RULE_WALK_UP],
                            'fo3Fallback': counts[SKELETON_RULE_FO3], 'missing': missing,
                            'skeletonFiles': sum(1 for _k, f in by_sha.values() if f['role'] == ROLE_SKELETON),
                            'provisional': provisional, 'namespaceConflicts': side.conflicts}
    return summary


def build_manifest(paths, keys, solved, wanted, stats, args, side: Side | None = None,
                   pins: list | None = None) -> dict:
    entries = {}
    totals = collections.Counter()
    proved = 0
    bytes_proved = 0
    for key in sorted(keys, key=key_order):
        st = keys[key]
        r = solved[key]
        entry, cover_files, floor_files, declined_control = key_entry(key, st, r, wanted[key], args)
        files = entry['files']
        ex = r['exact']
        exact_count = len(ex['chosen']) if ex['chosen'] is not None else None
        entries[key] = entry
        totals['keys'] += 1
        totals['coverFiles'] += len(cover_files)
        totals['floorFiles'] += len(floor_files)
        totals['declinedControlFiles'] += 1 if declined_control else 0
        totals['files'] += len(files)
        totals['bytes'] += sum(f['size'] for f in files)
        totals['greedyTotal'] += len(r['greedy'])
        totals['greedyPrunedTotal'] += len(r['greedyPruned'])
        if ex['provedOptimal']:
            totals['exactTotal'] += exact_count  # proved minimum counts only; unproved keys are not "exact"
            proved += 1
            bytes_proved += 1 if ex['bytesProved'] else 0
        elif st.groups:
            totals['exactUnprovedKeys'] += 1
    scoped = args.scope != DEFAULT_SCOPE
    additions = None
    if pins or scoped:
        n_keys = len(entries)
        additions = apply_additions(entries, keys, side or Side(), pins or [], args)
        totals['keys'] += len(entries) - n_keys
        every = [f for e in entries.values() for f in e['files']]
        totals['files'] = len(every)
        totals['bytes'] = sum(f['size'] for f in every)
        totals['controlFiles'] = sum(1 for f in every if f['role'] == ROLE_CONTROL)
        totals['pinnedFiles'] = sum(1 for f in every if f.get('controls'))
        totals['declinedControlFiles'] = sum(1 for f in every if f['role'] == ROLE_DECLINED)
        if scoped:
            totals['skeletonFiles'] = sum(1 for f in every if f['role'] == ROLE_SKELETON)
    options = {'milpSeconds': args.milp_seconds, 'tieOrder': args.tie_order, 'includeErrors': args.include_errors,
               'includeDeclined': args.include_declined, 'floor': not args.no_floor, 'keyFilter': args.key or [],
               'sourceMap': args.source_map, 'mergeKeys': args.merge_keys}
    if scoped:
        options.update(scope=args.scope, payloads=args.payloads)
    if pins:
        options['pins'] = os.path.abspath(args.pin).replace('\\', '/')
    man_input = {'records': stats['records'], 'filteredOut': stats['filteredOut'],
                 'skippedErrors': stats['skippedErrors'], 'skippedDeclined': stats['skippedDeclined'],
                 'unmappedSources': dict(sorted(stats['unmappedSources'].items()))}
    if 'outOfScope' in stats:
        man_input['outOfScope'] = stats['outOfScope']
    man = {
        'tool': TOOL,
        'manifestSchema': MANIFEST_SCHEMA,
        'inputs': [os.path.abspath(p).replace('\\', '/') for p in paths],
        'options': options,
        'input': man_input,
        'totals': dict(totals) | {'provedOptimalKeys': proved, 'bytesProvedKeys': bytes_proved,
                                  'exactTotal': totals['exactTotal'], 'exactUnprovedKeys': totals['exactUnprovedKeys']},
        'keys': [entries[k] for k in sorted(entries, key=key_order)],
    }
    if additions is not None:
        man['additions'] = additions
    return man


def apply_path_aliases(value, aliases: list[tuple[str, str]]):
    """--path-alias OLD=NEW: every string (and dict key) starting with OLD starts with NEW instead, first match
    wins. Used to write machine-independent sources ('Sample/...', '<evidence>/...') into a checked-in manifest."""
    if not aliases:
        return value
    if isinstance(value, str):
        for old, new in aliases:
            if value.startswith(old):
                return new + value[len(old):]
        return value
    if isinstance(value, dict):
        return {apply_path_aliases(k, aliases): apply_path_aliases(v, aliases) for k, v in value.items()}
    if isinstance(value, list):
        return [apply_path_aliases(v, aliases) for v in value]
    return value


def parse_path_aliases(specs: list[str] | None) -> list[tuple[str, str]]:
    out = []
    for spec in specs or []:
        if '=' not in spec:
            raise SystemExit('--path-alias %r: use OLD=NEW' % spec)
        old, new = spec.split('=', 1)
        out.append((old.replace('\\', '/'), new))
    return out


def exact_column(c: dict) -> str:
    m = c['milp']
    if m['count'] is None:
        return '-'
    return str(m['count']) if m['provedOptimal'] else 'feasible %d' % m['count']


def write_report(man: dict, path: str) -> None:
    t = man['totals']
    lines = []
    lines.append('%s manifest schema %d' % (man['tool'], man['manifestSchema']))
    lines.append('inputs: %s' % ', '.join(man['inputs']))
    lines.append('options: %s' % json.dumps(man['options'], sort_keys=True))
    lines.append('records %d, filtered out %d, skipped with errors %d, skipped declined %d, unmapped sources %d' % (
        man['input']['records'], man['input']['filteredOut'], man['input']['skippedErrors'],
        man['input']['skippedDeclined'], len(man['input']['unmappedSources'])))
    for s, n in man['input']['unmappedSources'].items():
        lines.append('  UNMAPPED game/platform: %s (%d records)' % (s, n))
    lines.append('keys %d: cover files %d (greedy %d, greedy+prune %d, exact %d over the %d of %d keys proved '
                 'optimal, %d unproved, bytes proved on %d), floor files %d, declined controls %d, total files %d, %.1f MB' % (
                     t['keys'], t['coverFiles'], t['greedyTotal'], t['greedyPrunedTotal'], t['exactTotal'],
                     t['provedOptimalKeys'], t['keys'], t['exactUnprovedKeys'], t['bytesProvedKeys'],
                     t['floorFiles'], t.get('declinedControlFiles', 0), t['files'], t['bytes'] / 1e6))
    if 'additions' in man:
        a = man['additions']
        lines.append('additions: %d pinned control(s) (%d added by the pin, the rest already in the cover), '
                     'control files %d, skeleton files %d, declined controls %d' % (
                         len(a['pins']), sum(1 for p in a['pins'] if p['addedByPin']), t.get('controlFiles', 0),
                         t.get('skeletonFiles', 0), t.get('declinedControlFiles', 0)))
        if a.get('skeletons'):
            sk = a['skeletons']
            lines.append('skeletons: %d manifest .kf, walk-up %d, %s fallback %d, missing %d, namespace conflicts %d, '
                         'provisional %d' % (sk['kfFiles'], sk['walkUp'], SKELETON_FALLBACK_GP, sk['fo3Fallback'],
                                             len(sk['missing']), len(sk['namespaceConflicts']),
                                             len(sk.get('provisional', []))))
            for p in sk.get('provisional', []):
                lines.append('  PROVISIONAL skeleton for %s: %s %s (pinned %s, alternatives %s)' % (
                    p['entry'], p['gamePlatform'], p['skeleton'], p['pinned'][:16],
                    ', '.join(a[:16] for a in p['alternatives'])))
            for m in sk['missing']:
                lines.append('  NO SKELETON %s %s (%s)' % (m['gamePlatform'], m['entry'], m['sha256'][:16]))
    lines.append('')
    lines.append('%-22s %-3s %8s %8s %8s %6s %5s %4s %6s %6s %-11s %-14s %5s %5s %s' % (
        'key', 'end', 'records', 'declined', 'unique', 'sets', 'types', 'tags', 'greedy', 'pruned', 'exact', 'method',
        'proof', 'floor', 'game/platform records'))
    for k in man['keys']:
        c = k['cover']
        lines.append('%-22s %-3s %8d %8d %8d %6d %5d %4d %6d %6d %-11s %-14s %5s %5d %s' % (
            k['key'], k['endianness'], k['records'], k['recordsDeclined'], k['uniqueFiles'], k['distinctItemSets'],
            k['items']['blockTypes'], k['items']['tags'], c['greedy']['count'], c['greedyPruned']['count'],
            exact_column(c), c['method'] if k['distinctItemSets'] else ROLE_DECLINED,
            ('yes' if c['milp']['bytesProved'] else 'count') if c['milp']['provedOptimal'] else 'no', k['floorFiles'],
            ', '.join('%s:%d' % kv for kv in k['gamePlatforms'].items())))
    for k in man['keys']:
        lines.append('')
        if k.get('addedFilesOnly'):
            lines.append('== %s  (%s)  no cover: added files only (pinned controls, skeleton companions)' % (
                k['key'], k['header']))
        elif k['declinedControl']:
            lines.append('== %s  (%s)  every record declined by the probe (%d): one decline control, no cover' % (
                k['key'], k['header'], k['recordsDeclined']))
        else:
            lines.append('== %s  (%s)  %d cover + %d floor files, %d items (%d block types + %d tags), method %s%s' % (
                k['key'], k['header'], k['cover']['count'], k['floorFiles'], k['items']['total'],
                k['items']['blockTypes'], k['items']['tags'], k['cover']['method'],
                '' if k['cover']['milp']['provedOptimal'] else ' [MILP: %s]' % k['cover']['milp']['message']))
        for f in k['files']:
            lines.append('  %-16s %9d B %3d items %-20s %s :: %s%s' % (
                f['role'], f['size'], f['itemCount'], ','.join(f['gamePlatforms']), f['source'], f['entry'],
                '  sha256 %s' % f['sha256'][:16] if f['sha256'] else '  identity %s' % f['identity']))
            if f['alsoIn']:
                lines.append('        also in (%s): %s' % (f['identity'], '; '.join('%s :: %s' % (a['source'], a['entry']) for a in f['alsoIn'][:6]))
                             + (' (+%d more)' % (len(f['alsoIn']) - 6) if len(f['alsoIn']) > 6 else ''))
            for c in f.get('controls', []):
                lines.append('        control: %s' % c['name'])
            if 'skeleton' in f:
                sk = f['skeleton']
                lines.append('        skeleton: %s' % ('%s %s :: %s (%s)' % (sk['rule'], sk['source'], sk['entry'], sk['sha256'][:16])
                                                     if sk else 'NONE (%s)' % f.get('skeletonMissing')))
                for a in (sk or {}).get('alternatives', []):
                    lines.append('        PROVISIONAL, alternative skeleton: %s :: %s (%s)' % (
                        a['source'], a['entry'], a['sha256'][:16]))
            if f.get('declined') and f['role'] == ROLE_DECLINED:
                lines.append('        declined: %s' % f['declined'])
            if f.get('skeletonFor'):
                lines.append('        skeleton for %d manifest .kf' % len(f['skeletonFor']))
            if f.get('skeletonCandidateFor'):
                lines.append('        alternative skeleton for %d manifest .kf' % len(f['skeletonCandidateFor']))
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')


# --------------------------------------------------------------------------- CLI
def make_key_filter(specs: list[str] | None):
    if not specs:
        return None
    wanted = []
    for s in specs:
        parts = s.split('/')
        if len(parts) < 1 or len(parts) > 4:
            raise SystemExit('--key %r: use version[/user[/bs[/LE|BE]]]' % s)
        ver = version_str(version_to_int(parts[0]))
        uv = parts[1][2:] if len(parts) > 1 and parts[1].lower().startswith('uv') else (parts[1] if len(parts) > 1 else None)
        bs = parts[2][2:] if len(parts) > 2 and parts[2].lower().startswith('bs') else (parts[2] if len(parts) > 2 else None)
        end = parts[3].upper() if len(parts) > 3 else None
        wanted.append((ver, None if uv is None else int(uv), None if bs is None else int(bs), end))

    def accept(key):
        ver, uv, bs, end = version_str(key[1]), key[2], key[3], 'BE' if key[4] else 'LE'
        for w in wanted:
            if w[0] == ver and (w[1] is None or w[1] == uv) and (w[2] is None or w[2] == bs) and (w[3] is None or w[3] == end):
                return True
        return False
    return accept


def load_source_map(path: str | None) -> dict | None:
    if not path:
        return None
    with open(path, encoding='utf-8-sig') as f:
        spec = json.load(f)
    return {item['src']: str(item['path']).replace('\\', '/') for item in spec if 'src' in item and 'path' in item}


def run_cover(args) -> dict:
    check_scope_args(args.scope, args.payloads)
    key_filter = make_key_filter(args.key)
    source_map = load_source_map(args.source_map)
    pins = load_pins(args.pin)
    side = Side(p['sha256'] for p in pins)
    keys, stats = pass1(args.inputs, args.tie_order, args.include_errors, source_map, key_filter, args.merge_keys,
                        args.include_declined, args.scope, side)
    if args.scope == 'cut1b' and not side.animated_records:
        raise SystemExit('--scope cut1b: no input record carries %s; run the census with nif_feature_probe.py census '
                         '--scope cut1b --payloads' % ANIM_MARKER_ITEM[len(TAG_PREFIX):])
    if not keys:
        raise SystemExit('no records selected')
    solved = {}
    for key in sorted(keys, key=key_order):
        st = keys[key]
        solved[key] = r = solve_key(st, args.milp_seconds)
        ex = r['exact']
        if not args.quiet:
            print('%-24s records %7d declined %6d unique %7d sets %5d items %4d greedy %3d pruned %3d exact %s %s' % (
                key_label(key), st.records, st.declined, len(st.identities), len(st.groups), len(st.items),
                len(r['greedy']), len(r['greedyPruned']), len(ex['chosen']) if ex['chosen'] is not None else '-',
                ('optimal' + ('' if ex['bytesProved'] else ' (count only)')) if ex['provedOptimal']
                else ('[%s]' % ex['message'])), flush=True)
    wanted = pass2(args.inputs, keys, solved, source_map, key_filter, args.merge_keys, args.scope)
    return build_manifest(args.inputs, keys, solved, wanted, stats, args, side, pins)


def cmd_cover(args) -> int:
    try:
        man = run_cover(args)
    except SystemExit as e:
        if str(e) == 'no records selected':
            print('no records selected', file=sys.stderr)
            return 2
        raise
    man = apply_path_aliases(man, parse_path_aliases(args.path_alias))
    with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(man, f, indent=1)
        f.write('\n')
    write_report(man, args.report)
    t = man['totals']
    print('keys %d: cover %d files (greedy %d, pruned %d, exact %d on the %d keys proved optimal, %d unproved), '
          'floor +%d, declined controls +%d, total %d' % (
              t['keys'], t['coverFiles'], t['greedyTotal'], t['greedyPrunedTotal'], t['exactTotal'],
              t['provedOptimalKeys'], t['exactUnprovedKeys'], t['floorFiles'], t.get('declinedControlFiles', 0),
              t['files']))
    return 0


def cmd_keys(args) -> int:
    counts: dict[tuple, collections.Counter] = collections.defaultdict(collections.Counter)
    declined: collections.Counter = collections.Counter()
    for _i, rec in stream(args.inputs, None):
        counts[rec.key][rec.gp] += 1
        if rec.declined:
            declined[rec.key] += 1
    for key in sorted(counts, key=key_order):
        c = counts[key]
        print('%-24s %-42s %8d %8s  %s' % (key_label(key), key[0][:42], sum(c.values()),
                                           ('declined %d' % declined[key]) if declined[key] else '',
                                           ', '.join('%s:%d' % kv for kv in sorted(c.items()))))
    return 0


# --------------------------------------------------------------------------- verify
def manifest_items(f: dict) -> frozenset:
    """The items a manifest file is credited with: its recorded block types and tags."""
    return frozenset([TYPE_PREFIX + t for t in f.get('blockTypes', [])] + [TAG_PREFIX + t for t in f.get('tags', [])])


def run_verify(inputs, manifest: dict, scope: str, drops=()) -> dict:
    """Re-derives every key's item universe from the census (the candidate records of the scope, as pass 1 selects
    them) and checks the manifest against it, independently of the solver:

      * every manifest file's recorded items are the scoped item set of some census record with the same SHA-256
        under the same key label (the credit is real, not merely claimed);
      * the union of the manifest files' items covers the key's universe; the items it does not are listed.

    `drops` removes files by SHA-256 before the union is taken, so the items a dropped file alone covered are
    reported as uncovered: the control that the check can fail."""
    drops = {d.lower() for d in drops}
    universe: dict[str, set] = collections.defaultdict(set)
    census_sets: dict[tuple, set] = collections.defaultdict(set)
    for _i, rec in stream(inputs, None):
        in_scope = apply_scope(rec, scope)
        if rec.errors or not rec.sha:
            continue
        label = key_label(rec.key)
        items = frozenset(rec.items)
        census_sets[(label, rec.sha)].add(items)
        if in_scope and not rec.declined:
            universe[label] |= items
    out = {'scope': scope, 'drops': sorted(drops), 'keys': [], 'uncovered': 0, 'unverifiedFiles': 0,
           'droppedFound': []}
    manifest_labels = set()
    for k in manifest['keys']:
        label = k['key']
        manifest_labels.add(label)
        covered = set()
        unverified = []
        for f in k['files']:
            if f['sha256'] in drops:
                out['droppedFound'].append({'key': label, 'sha256': f['sha256'], 'entry': f['entry'], 'role': f['role']})
                continue
            items = manifest_items(f)
            if not items:
                continue  # a decline control is credited with nothing, so there is nothing to verify
            if items not in census_sets.get((label, f['sha256']), ()):
                unverified.append({'sha256': f['sha256'], 'entry': f['entry'], 'role': f['role']})
                continue
            covered |= items
        missing = sorted(universe.get(label, set()) - covered)
        out['keys'].append({'key': label, 'universe': len(universe.get(label, ())), 'covered':
                            len(universe.get(label, set()) & covered), 'uncovered': missing, 'unverified': unverified})
        out['uncovered'] += len(missing)
        out['unverifiedFiles'] += len(unverified)
    absent = sorted(set(universe) - manifest_labels)
    for label in absent:  # a key with candidate records and no manifest entry at all
        out['keys'].append({'key': label, 'universe': len(universe[label]), 'covered': 0,
                            'uncovered': sorted(universe[label]), 'unverified': [], 'absentFromManifest': True})
        out['uncovered'] += len(universe[label])
    return out


def cmd_verify(args) -> int:
    check_scope_args(args.scope, args.payloads)
    with open(args.manifest, encoding='utf-8-sig') as f:
        manifest = json.load(f)
    r = run_verify(args.inputs, manifest, args.scope, args.drop or [])
    for k in r['keys']:
        state = 'OK' if not k['uncovered'] and not k['unverified'] else 'FAIL'
        print('%-4s %-28s universe %4d covered %4d uncovered %3d unverified files %d%s' % (
            state, k['key'], k['universe'], k['covered'], len(k['uncovered']), len(k['unverified']),
            '  (key absent from the manifest)' if k.get('absentFromManifest') else ''))
        for it in k['uncovered']:
            print('       UNCOVERED %s' % it)
        for u in k['unverified']:
            print('       UNVERIFIED %s %s %s' % (u['role'], u['sha256'], u['entry']))
    for d in r['droppedFound']:
        print('dropped: %s %s %s (%s)' % (d['key'], d['role'], d['entry'], d['sha256']))
    missing_drops = sorted(set(r['drops']) - {d['sha256'] for d in r['droppedFound']})
    for d in missing_drops:
        print('DROP NOT IN MANIFEST: %s' % d)
    print('verify: %d keys, %d uncovered items, %d unverified files' % (len(r['keys']), r['uncovered'],
                                                                      r['unverifiedFiles']))
    if args.json_out:
        with open(args.json_out, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(r, f, indent=1)
    return 1 if r['uncovered'] or r['unverifiedFiles'] or missing_drops else 0


# --------------------------------------------------------------------------- selftest
class _Args:
    def __init__(self, **kw):
        self.milp_seconds = 30.0
        self.tie_order = 'sha'
        self.include_errors = False
        self.include_declined = False
        self.no_floor = False
        self.key = None
        self.source_map = None
        self.merge_keys = False
        self.quiet = True
        self.inputs = []
        self.scope = DEFAULT_SCOPE
        self.payloads = False
        self.pin = None
        self.path_alias = None
        self.__dict__.update(kw)


def _probe_rec(path, source, sha, size, types, tags, key='20.2.0.7/uv11/bs34/LE', **extra):
    ver, uv, bs, end = key.split('/')
    d = {'sha256': sha, 'size': size,
         'key': {'headerString': 'Gamebryo File Format, Version ' + ver, 'version': ver, 'userVersion': int(uv[2:]),
                 'bsVersion': int(bs[2:]), 'bigEndian': end == 'BE'},
         'keyString': key, 'blockCount': sum(types.values()), 'blockTypes': types, 'tags': tags, 'details': {},
         'errors': [], 'declined': None, 'ms': 0.1, 'path': path, 'source': source}
    d.update(extra)
    return d


def cmd_selftest(_args) -> int:
    import hashlib
    import tempfile
    tmp = tempfile.mkdtemp(prefix='nif_cover_selftest_')
    fails = []

    def check(cond, what):
        print(('PASS ' if cond else 'FAIL ') + what)
        if not cond:
            fails.append(what)

    def sha(s):
        return hashlib.sha256(s.encode()).hexdigest()

    loose = 'C:/corpus/Fallout - New Vegas (2022-5-24, Steam - Final)/Data/meshes'
    bsa = 'C:/corpus/Fallout - New Vegas (2010-8-22, X360 - Final)/Data/Fallout - Meshes.bsa'
    # T5: the real probe shape - relative paths with directories, dict blockTypes, declined records, a
    # wholly declined key, an error record without blockTypes/tags (T8), and a BOM (T4).
    recs = [
        _probe_rec('architecture/westside/craftsmanwindowext.nif', loose, sha('a'), 3000,
                   {'BSFadeNode': 1, 'NiTriStrips': 2, 'NiTriStripsData': 2, 'NiAlphaProperty': 1},
                   ['blend=SRC_ALPHA/INV_SRC_ALPHA', 'normals=1']),
        _probe_rec('effects/fxfire01.nif', loose, sha('b'), 2000,
                   {'BSFadeNode': 1, 'NiParticleSystem': 1, 'NiPSysData': 1, 'NiAlphaProperty': 1},
                   ['blend=ONE/ONE', 'additive+psys=1']),
        _probe_rec('effects/fxfire01.nif', bsa, sha('b'), 2000,  # byte-identical X360 copy (LE file inside the BE BSA)
                   {'BSFadeNode': 1, 'NiParticleSystem': 1, 'NiPSysData': 1, 'NiAlphaProperty': 1},
                   ['blend=ONE/ONE', 'additive+psys=1']),
        _probe_rec('meshes/characters/_1stperson/locomotion/male/pipboy.kf', bsa, sha('c'), 7000,
                   {'NiControllerSequence': 1, 'NiTransformInterpolator': 3}, ['cycle=CLAMP', 'interp=NiTransformInterpolator']),
        _probe_rec('characters/_male/pa2hrholster.kf', loose, sha('d'), 1800,
                   {'NiControllerSequence': 1, 'NiTransformInterpolator': 9}, [], key='20.2.0.7/uv11/bs33/LE',
                   declined='BS version 33: outside cut 1a (only 14, 21, 26, 32, 34 are walked)'),
        _probe_rec('characters/_male/pa2hrholster2.kf', loose, sha('e'), 1700,
                   {'NiControllerSequence': 1, 'NiBoolInterpolator': 1}, [], key='20.2.0.7/uv11/bs33/LE',
                   declined='BS version 33: outside cut 1a (only 14, 21, 26, 32, 34 are walked)'),
        _probe_rec('clutter/broken.nif', loose, sha('f'), 500, {'BSFadeNode': 1}, ['normals=1'],
                   errors=['NiTriStripsData#3: size mismatch: parsed 10 of 20 declared bytes']),
        {'path': 'clutter/unreadable.nif', 'source': loose, 'sha256': sha('g'), 'size': 5,
         'key': '20.2.0.7/11/34/LE', 'errors': ['truncated header']},
    ]
    t5 = os.path.join(tmp, 't5.jsonl')
    with open(t5, 'w', encoding='utf-8-sig') as f:  # written WITH a BOM on purpose
        for r in recs:
            f.write(json.dumps(r) + '\n')
    man = run_cover(_Args(inputs=[t5]))
    keys = {k['key']: k for k in man['keys']}
    k34 = keys.get('20.2.0.7/uv11/bs34/LE')
    check(k34 is not None, 'T4 BOM input parses')
    entries = {f['entry'] for f in k34['files']}
    check('architecture/westside/craftsmanwindowext.nif' in entries and 'effects/fxfire01.nif' in entries,
          'T5 entries keep their directories (probe path relative to source)')
    check(all(f['identity'] == IDENTITY_SHA for f in k34['files']), 'T5 identity is sha256 for probe input')
    fx = next(f for f in k34['files'] if f['entry'] == 'effects/fxfire01.nif')
    check(sorted(fx['gamePlatforms']) == ['FNV/PC', 'FNV/X360'] and fx['alsoIn'] and fx['alsoIn'][0]['identity'] == IDENTITY_SHA,
          'T5 byte-identical X360 copy credited through alsoIn (sha256 identity)')
    check(k34['recordsWithErrorsSkipped'] == 2, 'T8 error record without blockTypes/tags is accepted and skipped (%d)' % k34['recordsWithErrorsSkipped'])
    k33 = keys.get('20.2.0.7/uv11/bs33/LE')
    check(k33 is not None and k33['recordsDeclined'] == 2 and k33['cover']['count'] == 0
          and k33['declinedControl'] == 'characters/_male/pa2hrholster2.kf' and k33['floorFiles'] == 0,
          'T5 wholly declined key -> one declined-control (smallest), no cover, no floor')
    check(man['input']['skippedDeclined'] == 2 and man['totals'].get('declinedControlFiles') == 1, 'T5 declined counted in totals')
    # T3: census error record beside a census record.
    t3 = os.path.join(tmp, 't3.jsonl')
    with open(t3, 'w', encoding='utf-8') as f:
        f.write(json.dumps({'hs': 'Gamebryo File Format, Version 20.2.0.7', 'ver': 0x14020007, 'uv': 11, 'bs': 34, 'be': 0,
                            'nb': 3, 'types': ['BSFadeNode', 'NiTriStrips'], 'tableOnly': [], 'src': 'FNV Steam Fallout - Meshes.bsa',
                            'path': 'meshes/ok.nif', 'size': 100}) + '\n')
        f.write(json.dumps({'err': 'ValueError: bad header', 'src': 'FNV Steam Fallout - Meshes.bsa', 'path': 'meshes/broken.nif',
                            'size': 5}) + '\n')
    man3 = run_cover(_Args(inputs=[t3]))
    k3 = {k['key']: k for k in man3['keys']}
    kb = k3.get('20.2.0.7/uv11/bs34/LE')
    ku = k3.get('unreadable')
    check(kb is not None and kb['records'] == 1 and kb['cover']['count'] == 1 and kb['files'][0]['identity'] == IDENTITY_PATH
          and ku is not None and ku['records'] == 1 and ku['recordsWithErrorsSkipped'] == 1 and ku['cover']['count'] == 0
          and man3['input']['skippedErrors'] == 1,
          'T3 census error record (no header) is counted under the "unreadable" key, not fatal; census identity is path+size')
    # T1: greedy 3 vs exact 2 on a synthetic instance (a 5-item distractor overlapping two disjoint
    # 4-item halves); the bytes stage proves the 200-byte minimum among 2-covers.
    t1 = os.path.join(tmp, 't1.jsonl')
    items = ['i%d' % i for i in range(8)]
    sets = {'half1': items[:4], 'half2': items[4:], 'distractor': items[:3] + items[4:6]}
    with open(t1, 'w', encoding='utf-8') as f:
        for name, its in sets.items():
            f.write(json.dumps(_probe_rec('x/%s.nif' % name, loose, sha(name), 100 if name != 'distractor' else 90,
                                          {t: 1 for t in its}, [])) + '\n')
    man1 = run_cover(_Args(inputs=[t1], no_floor=True))
    c = man1['keys'][0]['cover']
    check(c['greedy']['count'] == 3 and c['milp']['count'] == 2 and c['milp']['provedOptimal'] and c['milp']['bytesProved']
          and c['milp']['bytes'] == 200 and c['method'] == 'milp',
          'T1 greedy 3, lexicographic MILP 2 proved (count and bytes): got greedy %d milp %s bytes %s' % (
              c['greedy']['count'], c['milp']['count'], c['milp']['bytes']))
    # Determinism: two runs give identical manifests.
    man1b = run_cover(_Args(inputs=[t1], no_floor=True))
    check(json.dumps(man1, sort_keys=True) == json.dumps(man1b, sort_keys=True), 'determinism: identical manifest on rerun')
    # Report writer runs on every role.
    write_report(man, os.path.join(tmp, 't5_report.txt'))
    check(os.path.getsize(os.path.join(tmp, 't5_report.txt')) > 0, 'report written')
    selftest_cut1b(tmp, check, sha)
    print('selftest: %d failures (fixtures under %s)' % (len(fails), tmp))
    return 1 if fails else 0


def _raises(fn, needle: str) -> bool:
    """True when fn() stops the run with a SystemExit whose message contains needle."""
    try:
        fn()
    except SystemExit as e:
        return needle in str(e)
    return False


def selftest_cut1b(tmp: str, check, sha) -> None:
    """Scope cut1b: argument pairs, the animated filter, the file-kind key, the dropped cut-1a tags, the declined
    .kf decline controls, the nearest-wins skeleton walk-up and its FO3 fallback, pins, verify and its drop
    control, the cut-1a refusal of anim:* records and --path-alias."""
    fnv = 'C:/corpus/Fallout - New Vegas (2022-5-24, Steam - Final)/Data/Fallout - Meshes.bsa'
    fo3 = 'C:/corpus/Fallout 3 (2026-2-15, Steam - Final)/Data/Fallout - Meshes.bsa'
    seq = {'NiControllerSequence': 1, 'NiTransformInterpolator': 1, 'NiTransformData': 1}
    recs = [
        _probe_rec('meshes/creatures/dog/idle.kf', fnv, sha('k1'), 900, seq,
                   ['anim:animated', 'anim:key:NiTransformData/rotation/LINEAR_KEY', 'cycle=CLAMP']),
        _probe_rec('meshes/creatures/dog/walk.kf', fnv, sha('k2'), 800, seq,
                   ['anim:animated', 'anim:tbc:rotation', 'cycle=CLAMP']),
        _probe_rec('meshes/creatures/dog/skeleton.nif', fnv, sha('s1'), 5000, {'NiNode': 3}, ['normals=1']),
        _probe_rec('meshes/creatures/skeleton.nif', fnv, sha('s2'), 4000, {'NiNode': 2}, ['normals=1']),
        _probe_rec('meshes/dlc04/creatures/x/attack.kf', fnv, sha('k3'), 700, seq,
                   ['anim:animated', 'anim:quat:dotNeg:unit']),
        _probe_rec('meshes/dlc04/creatures/x/skeleton.nif', fo3, sha('s3'), 3000, {'NiNode': 2}, []),
        _probe_rec('meshes/effects/fx.nif', fnv, sha('n1'), 2000, {'BSFadeNode': 1, 'NiTransformController': 1},
                   ['anim:animated', 'anim:ctrl:NiTransformController|cycle=CLAMP|active=1|mgr=0', 'blend=ONE/ONE']),
        _probe_rec('meshes/clutter/cup.nif', fnv, sha('n2'), 1000, {'BSFadeNode': 1}, ['normals=1']),
        _probe_rec('meshes/characters/eatidle.kf', fnv, sha('d1'), 600, {'NiControllerSequence': 1}, [],
                   key='20.0.0.4/uv10/bs11/LE', declined='version 20.0.0.4: outside cuts 1a and 1b'),
        _probe_rec('meshes/triggers/box.nif', fnv, sha('d2'), 500, {'NiNode': 1}, [],
                   key='20.0.0.4/uv10/bs11/LE', declined='version 20.0.0.4: outside cuts 1a and 1b'),
    ]
    path = os.path.join(tmp, 'cut1b.jsonl')
    with open(path, 'w', encoding='utf-8') as f:
        for r in recs:
            f.write(json.dumps(r) + '\n')
    check(_raises(lambda: check_scope_args('cut1b', False), 'needs --payloads')
          and _raises(lambda: check_scope_args('cut1a', True), 'needs --scope cut1b'),
          'B1 --scope cut1b needs --payloads and --payloads needs --scope cut1b')
    check(_raises(lambda: run_cover(_Args(inputs=[path])), 'carries cut-1b animation tags'),
          'B2 the default scope refuses a record carrying anim:* tags')
    man = run_cover(_Args(inputs=[path], scope='cut1b', payloads=True))
    keys = {k['key']: k for k in man['keys']}
    check(sorted(keys) == ['20.0.0.4/uv10/bs11/LE/.kf', '20.2.0.7/uv11/bs34/LE/.kf', '20.2.0.7/uv11/bs34/LE/.nif'],
          'B3 keys split by file kind; the declined .nif and the unanimated .nif are out of scope: %s' % sorted(keys))
    nif = keys.get('20.2.0.7/uv11/bs34/LE/.nif', {'files': [], 'records': -1})
    check(nif['records'] == 1 and man['input'].get('outOfScope') == 5
          and all(not t.startswith('blend=') for f in nif['files'] for t in f['tags']),
          'B4 only animated records are candidates and cut-1a tags are no items (records %d, out of scope %s)' % (
              nif['records'], man['input'].get('outOfScope')))
    files = {f['entry']: f for k in man['keys'] for f in k['files']}
    idle = files.get('meshes/creatures/dog/idle.kf', {})
    check((idle.get('skeleton') or {}).get('entry') == 'meshes/creatures/dog/skeleton.nif'
          and idle['skeleton']['rule'] == SKELETON_RULE_WALK_UP,
          'B5 walk-up: the nearest ancestor skeleton.nif wins over a farther one')
    skel = files.get('meshes/creatures/dog/skeleton.nif', {})
    check(skel.get('role') == ROLE_SKELETON and sha('k1') in skel.get('skeletonFor', [])
          and 'meshes/creatures/skeleton.nif' not in files, 'B6 the skeleton is added with role skeleton and skeletonFor')
    attack = files.get('meshes/dlc04/creatures/x/attack.kf', {})
    check((attack.get('skeleton') or {}).get('rule') == SKELETON_RULE_FO3
          and 'Fallout 3 (' in attack['skeleton']['source'],
          'B7 an FNV .kf with no FNV skeleton resolves through the FO3 namespace')
    check(keys.get('20.0.0.4/uv10/bs11/LE/.kf', {}).get('declinedControl') == 'meshes/characters/eatidle.kf',
          'B8 a declined .kf is the decline control of its cut-1b key')
    pins_path = os.path.join(tmp, 'pins.json')

    def write_pins(pins):
        with open(pins_path, 'w', encoding='utf-8') as f:
            json.dump({'schema': 1, 'pins': pins}, f)
    write_pins([{'name': 'cup', 'sha256': sha('n2'), 'role': ROLE_CONTROL, 'entry': 'meshes/clutter/cup.nif',
                 'size': 1000, 'sourceHint': 'Steam - Final)/Data/Fallout - Meshes.bsa'},
                {'name': 'idle', 'sha256': sha('k1'), 'role': ROLE_CONTROL}])
    man2 = run_cover(_Args(inputs=[path], scope='cut1b', payloads=True, pin=pins_path))
    files2 = {f['entry']: f for k in man2['keys'] for f in k['files']}
    cup = files2.get('meshes/clutter/cup.nif', {})
    check(cup.get('role') == ROLE_CONTROL and [c['name'] for c in cup.get('controls', [])] == ['cup']
          and [c['name'] for c in files2['meshes/creatures/dog/idle.kf'].get('controls', [])] == ['idle']
          and files2['meshes/creatures/dog/idle.kf']['role'] == ROLE_COVER,
          'B9 a pin adds an out-of-scope file as a control and annotates a cover file without duplicating it')
    write_pins([{'name': 'cup', 'sha256': sha('n2'), 'role': ROLE_CONTROL, 'entry': 'meshes/clutter/mug.nif'}])
    check(_raises(lambda: run_cover(_Args(inputs=[path], scope='cut1b', payloads=True, pin=pins_path)), 'not at entry'),
          'B10 a pin at the wrong entry stops the run')
    write_pins([{'name': 'ghost', 'sha256': sha('none'), 'role': ROLE_CONTROL}])
    check(_raises(lambda: run_cover(_Args(inputs=[path], scope='cut1b', payloads=True, pin=pins_path)), 'in no input'),
          'B11 a pin in no input record stops the run')
    r = run_verify([path], man, 'cut1b')
    r_drop = run_verify([path], man, 'cut1b', [sha('k1')])
    dropped = [it for k in r_drop['keys'] for it in k['uncovered']]
    check(r['uncovered'] == 0 and r['unverifiedFiles'] == 0
          and dropped == ['tag:anim:key:NiTransformData/rotation/LINEAR_KEY'],
          'B12 verify: every item covered; dropping idle.kf uncovers exactly the item it alone covers (%s)' % dropped)
    aliased = apply_path_aliases(man, parse_path_aliases(['C:/corpus/=Sample/']))
    check(all(f['source'].startswith('Sample/') for k in aliased['keys'] for f in k['files']),
          'B13 --path-alias rewrites every source')
    write_pins([{'name': 'box', 'sha256': sha('d2'), 'role': ROLE_DECLINED, 'entry': 'meshes/triggers/box.nif'}])
    man3 = run_cover(_Args(inputs=[path], scope='cut1b', payloads=True, pin=pins_path))
    declined = [f for k in man3['keys'] for f in k['files'] if f['role'] == ROLE_DECLINED]
    path_plain = os.path.join(tmp, 'cut1b_plain.jsonl')
    _write_jsonl(path_plain, [r for r in recs if not any(t.startswith('anim:') for t in r['tags'])])
    plain = run_cover(_Args(inputs=[path_plain]))
    plain_declined = [f for k in plain['keys'] for f in k['files'] if f['role'] == ROLE_DECLINED]
    check(len(declined) == 2 and all(f.get('declined') == 'version 20.0.0.4: outside cuts 1a and 1b' for f in declined)
          and plain_declined and all('declined' not in f for f in plain_declined),
          'B14 every decline control of a scoped manifest carries the probe reason (the key\'s own and a pinned one); '
          'a cut-1a manifest without pins keeps its shape')
    other = 'C:/corpus/Fallout - New Vegas (2022-5-24, Steam - Final)/Data/OldWorldBlues - Main.bsa'
    conflict = recs + [_probe_rec('meshes/creatures/dog/skeleton.nif', other, sha('s1b'), 5100, {'NiNode': 3}, [])]
    path4 = os.path.join(tmp, 'cut1b_conflict.jsonl')
    _write_jsonl(path4, conflict)
    man4 = run_cover(_Args(inputs=[path4], scope='cut1b', payloads=True))
    files4 = {(f['entry'], f['sha256']): f for k in man4['keys'] for f in k['files']}
    idle4 = files4.get(('meshes/creatures/dog/idle.kf', sha('k1')), {})
    sk4 = idle4.get('skeleton') or {}
    alt = files4.get(('meshes/creatures/dog/skeleton.nif', sha('s1b')), {})
    prov = man4['additions']['skeletons']['provisional']
    check(sk4.get('sha256') == sha('s1') and sk4.get('provisional')
          and [a['sha256'] for a in sk4.get('alternatives', [])] == [sha('s1b')]
          and alt.get('role') == ROLE_SKELETON and set(alt.get('skeletonCandidateFor', [])) == {sha('k1'), sha('k2')}
          and 'skeletonFor' not in alt and {p['sha256'] for p in prov} == {sha('k1'), sha('k2')}
          and 'provisional' not in (files.get('meshes/creatures/dog/idle.kf', {}).get('skeleton') or {}),
          'B15 a skeleton path with two distinct copies in one namespace is pinned provisionally for every .kf that '
          'resolves to it, the other copy is pinned as an alternative; with one copy nothing is marked')


def _write_jsonl(path: str, recs: list) -> None:
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        for r in recs:
            f.write(json.dumps(r) + '\n')


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    c = sub.add_parser('cover', help='compute the per-key joint covers and write the manifest + report')
    c.add_argument('inputs', nargs='+', help='probe JSONL (nif_feature_probe.py) or census JSONL (nif_census.py)')
    c.add_argument('--out', required=True, help='manifest JSON path')
    c.add_argument('--report', required=True, help='text report path')
    c.add_argument('--milp-seconds', type=float, default=120.0, help='MILP time limit per stage and key (default 120)')
    c.add_argument('--key', action='append', help='only this key: version[/user[/bs[/LE|BE]]]; repeatable')
    c.add_argument('--tie-order', choices=('sha', 'input'), default='sha',
                   help='greedy tie-break after (most new items, fewest bytes): SHA-256 (else entry path, source) '
                        'or first-seen input order (reproduces the seed cover_analysis.py)')
    c.add_argument('--include-errors', action='store_true', help='keep records whose probe reported errors as candidates')
    c.add_argument('--include-declined', action='store_true',
                   help='keep records the probe declined as block-type-only candidates (the census-style view)')
    c.add_argument('--no-floor', action='store_true', help='skip the one-file-per-(game, platform) floor')
    c.add_argument('--merge-keys', action='store_true',
                   help='ignore the version key and cover all records as one universe (cross-key comparisons only)')
    c.add_argument('--source-map', help='census_spec.json: resolves census source labels to archive/tree paths')
    c.add_argument('--quiet', action='store_true', help='no per-key progress line')
    c.add_argument('--scope', choices=SCOPES, default=DEFAULT_SCOPE,
                   help='cut1a (default): every tag is an item, exactly as before; cut1b: animated records only, keys '
                        'split by file kind, items = block types + the anim:* vocabulary, plus skeleton companions '
                        '(needs --payloads; see SCOPES)')
    c.add_argument('--payloads', action='store_true',
                   help='the census was run with --payloads, so the payload-derived anim:* vocabulary is present '
                        '(required with --scope cut1b, refused with cut1a)')
    c.add_argument('--pin', help='pins.json: named controls and decline controls to add, each by SHA-256 (see load_pins)')
    c.add_argument('--path-alias', action='append',
                   help='OLD=NEW: write every path starting with OLD with NEW instead (repeatable), e.g. '
                        '"C:/.../Sample/=Sample/"')
    c.set_defaults(func=cmd_cover)
    k = sub.add_parser('keys', help='list version keys with record counts per game/platform')
    k.add_argument('inputs', nargs='+')
    k.set_defaults(func=cmd_keys)
    v = sub.add_parser('verify', help='check a manifest against the census: every item of every key covered, every '
                                      'file credited only with items a census record of it carries')
    v.add_argument('inputs', nargs='+', help='the probe JSONL the manifest was built from')
    v.add_argument('--manifest', required=True)
    v.add_argument('--scope', choices=SCOPES, default=DEFAULT_SCOPE)
    v.add_argument('--payloads', action='store_true')
    v.add_argument('--drop', action='append', help='SHA-256 of a manifest file to leave out (repeatable): the control')
    v.add_argument('--json-out', help='write the full result as JSON')
    v.set_defaults(func=cmd_verify)
    s = sub.add_parser('selftest', help='synthetic regression fixtures')
    s.set_defaults(func=cmd_selftest)
    args = ap.parse_args(argv)
    return args.func(args)


if __name__ == '__main__':
    sys.exit(main())
