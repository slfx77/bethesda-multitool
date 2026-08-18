#!/usr/bin/env python3
"""Audit record asset paths in an emitted ESM/ESP for references the game cannot resolve.

Two failure classes this catches, both introduced by the asset-rename pass and both fixed
2026-08-13:

  A1  A path naming a container the asset packer converts away from (.xma / .ddx). The BSA
      will hold the converted file (.wav / .ogg / .dds), so the record points at nothing.
  A2  A MUSC FNAM carrying a leading "music\\". MUSC paths are resolved relative to
      Data\\Music\\, so the prefix makes the engine look for Data\\Music\\music\\...

Two invariants that must not regress:

  A3  SOUN FNAM must stay relative to Data\\Sound\\ (no "sound\\"/"music\\" root in the field).
  A4  The songs\\radio\\* family are SOUN records rooted at Data\\Sound\\, NOT music. Their
      count must not move — it is the tripwire for mis-rooting .mp3 wholesale.

Usage:
    esm_asset_path_audit.py <esm> [--vs <baseline.esm>] [--fail-on-violation]
"""
from __future__ import annotations

import argparse
import struct
import sys
import zlib
from collections import Counter

HEADER = 24
CONVERTED_AWAY = (".xma", ".ddx")  # packer never emits these; see PrototypeAssetConverter
PATH_SUBRECORDS = {"FNAM", "ICON", "MICO", "MODL", "TX00"}


def _records(data: bytes):
    """Yield (signature, formid, body) for every non-GRUP record, descending into GRUPs."""
    def walk(off: int, end: int):
        while off + HEADER <= end:
            sig = data[off:off + 4].decode("ascii", "replace")
            size = struct.unpack_from("<I", data, off + 4)[0]
            if sig == "GRUP":
                yield from walk(off + HEADER, off + size)
                off += size
                continue
            flags, formid = struct.unpack_from("<II", data, off + 8)
            body = data[off + HEADER:off + HEADER + size]
            if flags & 0x00040000 and len(body) > 4:  # compressed
                try:
                    body = zlib.decompress(body[4:])
                except zlib.error:
                    body = b""
            yield sig, formid, body
            off += HEADER + size

    tes4 = struct.unpack_from("<I", data, 4)[0]
    yield from walk(HEADER + tes4, len(data))


def _subrecords(body: bytes):
    """Yield (signature, payload), honouring XXXX large-size escapes."""
    pos, pending = 0, None
    while pos + 6 <= len(body):
        sig = body[pos:pos + 4].decode("ascii", "replace")
        size = struct.unpack_from("<H", body, pos + 4)[0]
        if sig == "XXXX":
            pending = struct.unpack_from("<I", body, pos + 6)[0]
            pos += 6 + size
            continue
        if pending is not None:
            size, pending = pending, None
        if pos + 6 + size > len(body):
            break
        yield sig, body[pos + 6:pos + 6 + size]
        pos += 6 + size


def _text(payload: bytes) -> str:
    return payload.split(b"\x00", 1)[0].decode("latin-1")


def collect(path: str):
    """Return {(sig, formid): {subrecordSig: value}} for path-bearing subrecords."""
    data = open(path, "rb").read()
    out = {}
    for sig, formid, body in _records(data):
        fields = {}
        for sub, payload in _subrecords(body):
            if sub in PATH_SUBRECORDS and payload and payload[0] not in (0,):
                fields[sub] = _text(payload)
        if fields:
            out[(sig, formid)] = fields
    return out


def audit(path: str):
    records = collect(path)
    a1, a2, a3, a4 = [], [], [], 0
    for (sig, formid), fields in records.items():
        for sub, value in fields.items():
            low = value.lower().replace("/", "\\")
            if low.endswith(CONVERTED_AWAY):
                a1.append((sig, formid, sub, value))
            if sig == "MUSC" and sub == "FNAM" and low.startswith("music\\"):
                a2.append((sig, formid, sub, value))
            if sig == "SOUN" and sub == "FNAM":
                if low.startswith("sound\\") or low.startswith("music\\"):
                    a3.append((sig, formid, sub, value))
                if low.endswith(".mp3"):
                    a4 += 1
    return records, a1, a2, a3, a4


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("esm")
    ap.add_argument("--vs", dest="baseline")
    ap.add_argument("--fail-on-violation", action="store_true")
    args = ap.parse_args()

    records, a1, a2, a3, a4 = audit(args.esm)
    print(f"{args.esm}: {len(records):,} path-bearing records")
    print(f"  A1 packer-converted extension (.xma/.ddx) : {len(a1):5}   (target 0)")
    print(f"  A2 MUSC FNAM with leading 'music\\'        : {len(a2):5}   (target 0)")
    print(f"  A3 SOUN FNAM rooted at sound\\ or music\\   : {len(a3):5}   (target 0)")
    print(f"  A4 SOUN FNAM .mp3 (songs\\radio\\* family)  : {a4:5}   (must not move)")
    for label, rows in (("A1", a1), ("A2", a2), ("A3", a3)):
        for sig, formid, sub, value in rows[:5]:
            print(f"     {label} {sig} 0x{formid:08X} {sub} = {value}")

    if args.baseline:
        base = collect(args.baseline)
        keys = set(records) | set(base)
        changes = Counter()
        print(f"\nledger vs {args.baseline}")
        shown = 0
        for k in sorted(keys, key=lambda t: (t[0], t[1])):
            before, after = base.get(k, {}), records.get(k, {})
            if before == after:
                continue
            for sub in sorted(set(before) | set(after)):
                b, a = before.get(sub), after.get(sub)
                if b == a:
                    continue
                changes[(k[0], sub)] += 1
                if shown < 25:
                    print(f"  {k[0]} 0x{k[1]:08X} {sub}: {b!r} -> {a!r}")
                    shown += 1
        total = sum(changes.values())
        print(f"  total changed subrecords: {total}")
        for (sig, sub), n in sorted(changes.items()):
            print(f"    {sig}.{sub}: {n}")

    violations = len(a1) + len(a2) + len(a3)
    if args.fail_on_violation and violations:
        print(f"\nFAIL: {violations} violation(s)")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
