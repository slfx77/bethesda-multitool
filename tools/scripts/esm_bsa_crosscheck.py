#!/usr/bin/env python3
"""Assert every SOUN/MUSC path in an emitted plugin is actually reachable at runtime.

This is the check that directly states "the sound is not silent". For each SOUN FNAM (rooted
at Data\\Sound\\) and MUSC FNAM (rooted at Data\\Music\\), the referenced file must exist in
one of:
  * a BSA this conversion packed,
  * a vanilla FNV BSA,
  * loose vanilla content (Data\\Music\\ in particular — vanilla ships all music loose).

Before the 2026-08-13 asset-rename fix this reported 66 unreachable references on xex44
(49 SOUN naming .xma, 17 MUSC carrying a stray music\\ prefix).

Usage:
    esm_bsa_crosscheck.py <esm> --bsa <packed.bsa> [--bsa ...]
                                [--vanilla-data <FNV Data dir>] [--quiet]
"""
from __future__ import annotations

import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from esm_asset_path_audit import collect  # noqa: E402


def bsa_entries(path: str) -> set[str]:
    """Folder\\file paths inside a BSA (v103/v104/v105 — FO3/FNV/Skyrim layout)."""
    with open(path, "rb") as handle:
        data = handle.read()

    if data[:4] != b"BSA\x00":
        raise ValueError(f"{path}: not a BSA")

    (_version, folder_offset, flags, folder_count, file_count,
     total_folder_name_len, _total_file_name_len) = struct.unpack_from("<7I", data, 4)

    names_included = bool(flags & 0x1) and bool(flags & 0x2)
    if not names_included:
        return set()

    # Folder records: hash(8) + count(4) + offset(4)
    folders = []
    pos = folder_offset
    for _ in range(folder_count):
        count, offset = struct.unpack_from("<II", data, pos + 8)
        folders.append((count, offset))
        pos += 16

    # Folder name blocks + their file records, then one flat file-name block.
    file_record_pos = folder_offset + folder_count * 16
    entries: list[int] = []
    folder_names: list[str] = []
    pos = file_record_pos
    for count, _offset in folders:
        name_len = data[pos]
        name = data[pos + 1:pos + name_len].decode("latin-1").rstrip("\x00")
        folder_names.append(name)
        pos += 1 + name_len
        for _ in range(count):
            entries.append(len(folder_names) - 1)
            pos += 16

    name_block = data[pos:pos + total_folder_name_len + (1 << 24)]
    names = name_block.split(b"\x00")

    out = set()
    for i, folder_idx in enumerate(entries):
        if i >= len(names):
            break
        leaf = names[i].decode("latin-1")
        if not leaf:
            continue
        out.add(f"{folder_names[folder_idx]}\\{leaf}".lower())
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("esm")
    ap.add_argument("--bsa", action="append", default=[])
    ap.add_argument("--vanilla-data")
    ap.add_argument(
        "--loose-dir", action="append", default=[],
        help="Directory of loose Data-relative output shipped alongside the archives. "
             "Required since v152: .mp3 cannot be read from a BSA by the FNV engine, so "
             "music and the songs\\radio\\* family are delivered loose and would otherwise "
             "read here as unreachable.")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args()

    available: set[str] = set()
    for loose in args.loose_dir:
        found = 0
        for root, _dirs, files in os.walk(loose):
            rel = os.path.relpath(root, loose)
            prefix = "" if rel == "." else rel.lower().replace("/", "\\") + "\\"
            for name in files:
                if name.lower().endswith((".esm", ".esp", ".bsa")):
                    continue
                available.add(prefix + name.lower())
                found += 1
        if not args.quiet:
            print(f"  indexed {found:6,} loose file(s) from {loose}")

    for bsa in args.bsa:
        try:
            found = bsa_entries(bsa)
            available |= found
            if not args.quiet:
                print(f"  indexed {len(found):6,} entries from {os.path.basename(bsa)}")
        except Exception as exc:  # noqa: BLE001
            print(f"  WARN could not index {bsa}: {exc}")

    if args.vanilla_data:
        for bsa in sorted(f for f in os.listdir(args.vanilla_data) if f.lower().endswith(".bsa")):
            try:
                available |= bsa_entries(os.path.join(args.vanilla_data, bsa))
            except Exception:  # noqa: BLE001
                pass
        for root, _dirs, files in os.walk(args.vanilla_data):
            rel = os.path.relpath(root, args.vanilla_data)
            prefix = "" if rel == "." else rel.lower().replace("/", "\\") + "\\"
            for name in files:
                available.add(prefix + name.lower())

    records = collect(args.esm)
    unreachable = []
    folder_refs = 0
    checked = 0
    for (sig, formid), fields in records.items():
        root = {"SOUN": "sound\\", "MUSC": "music\\"}.get(sig)
        if root is None or "FNAM" not in fields:
            continue
        value = fields["FNAM"].lower().replace("/", "\\").lstrip("\\")
        if value.startswith("data\\"):
            value = value[5:]
        full = value if value.startswith(root) else root + value

        # A SOUN FNAM ending in a separator names a FOLDER: the engine picks a random file
        # from it at play time. There is no single file to check, so it is not a path defect.
        if full.endswith("\\"):
            folder_refs += 1
            continue

        checked += 1
        if full not in available:
            unreachable.append((sig, formid, fields["FNAM"], full))

    print(f"{args.esm}: {checked:,} SOUN/MUSC file references checked against "
          f"{len(available):,} available files ({folder_refs} random-sound folder refs skipped)")
    print(f"  unreachable: {len(unreachable)}   (target 0)")
    for sig, formid, raw, full in unreachable[:20]:
        print(f"    {sig} 0x{formid:08X} {raw}   -> looked for {full}")

    return 1 if unreachable else 0


if __name__ == "__main__":
    sys.exit(main())
