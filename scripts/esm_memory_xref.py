#!/usr/bin/env python3
"""ESM-Memory cross-reference research tool.

Investigates where dialogue response text lives in Xbox 360 crash dumps by
cross-referencing ESM files with memory dump contents.

Phase 1: Extract NAM1 texts from ESM, search for them in the memory dump.
Phase 2: Detect TESResponse structs around found text strings.
Phase 3: Investigate iFileOffset on known TESTopicInfo structs.

Usage:
    python scripts/esm_memory_xref.py search --esm FILE --dump FILE [--endian big|little]
    python scripts/esm_memory_xref.py structs --dump FILE --hits FILE
    python scripts/esm_memory_xref.py fileoffset --esm FILE --dump FILE --csv FILE
"""

import argparse
import csv
import json
import mmap
import os
import struct
import sys
import zlib


# ============================================================================
# ESM Binary Format Helpers
# ============================================================================

def read_u32(data, offset, big_endian=False):
    fmt = ">I" if big_endian else "<I"
    return struct.unpack_from(fmt, data, offset)[0]


def read_u16(data, offset, big_endian=False):
    fmt = ">H" if big_endian else "<H"
    return struct.unpack_from(fmt, data, offset)[0]


def read_sig(data, offset, big_endian=False):
    """Read a 4-byte ASCII signature, reversing for big-endian."""
    raw = data[offset:offset + 4]
    if big_endian:
        raw = bytes(reversed(raw))
    return raw.decode("ascii", errors="replace")


def detect_endianness(data):
    """Auto-detect ESM endianness from TES4 signature."""
    sig = data[0:4]
    if sig == b"TES4":
        return False  # Little-endian (PC)
    elif sig == b"4SET":
        return True  # Big-endian (Xbox 360)
    else:
        raise ValueError(f"Unknown ESM signature: {sig!r}")


# ============================================================================
# ESM Record Parser
# ============================================================================

class EsmParser:
    """Minimal ESM parser for extracting NAM1 subrecords from INFO records."""

    def __init__(self, data, big_endian=None):
        self.data = data
        self.be = big_endian if big_endian is not None else detect_endianness(data)
        self.size = len(data)

    def read_record_header(self, offset):
        """Parse a 24-byte main record or GRUP header."""
        if offset + 24 > self.size:
            return None
        sig = read_sig(self.data, offset, self.be)
        field1 = read_u32(self.data, offset + 4, self.be)
        field2 = read_u32(self.data, offset + 8, self.be)
        field3 = read_u32(self.data, offset + 12, self.be)
        return sig, field1, field2, field3

    def extract_nam1_from_info(self):
        """Walk the entire ESM, extracting NAM1 subrecords from INFO records.

        Returns list of dicts: {form_id, text, esm_offset}
        """
        results = []
        offset = 0

        # Read TES4 header
        hdr = self.read_record_header(offset)
        if hdr is None or hdr[0] != "TES4":
            raise ValueError(f"Expected TES4, got {hdr}")
        tes4_data_size = hdr[1]
        offset = 24 + tes4_data_size

        # Walk top-level records/GRUPs
        self._walk_records(offset, self.size, results)
        return results

    def _walk_records(self, start, end, results):
        """Recursively walk records within a range."""
        offset = start
        while offset + 24 <= end:
            hdr = self.read_record_header(offset)
            if hdr is None:
                break

            sig, field1, field2, field3 = hdr

            if sig == "GRUP":
                # GRUP: field1 = total size (including 24-byte header)
                grup_size = field1
                if grup_size < 24 or offset + grup_size > end:
                    break
                # Recurse into GRUP children (starting after header)
                self._walk_records(offset + 24, offset + grup_size, results)
                offset += grup_size
            else:
                # Regular record: field1 = data size (excluding 24-byte header)
                data_size = field1
                flags = field2
                form_id = field3

                if sig == "INFO":
                    self._extract_info_subrecords(
                        offset, data_size, flags, form_id, results
                    )

                offset += 24 + data_size

    def _extract_info_subrecords(self, record_offset, data_size, flags, form_id, results):
        """Extract NAM1 subrecords from an INFO record."""
        data_start = record_offset + 24
        data_end = data_start + data_size

        # Handle compressed records
        is_compressed = bool(flags & 0x00040000)
        if is_compressed:
            if data_size < 4:
                return
            decomp_size = read_u32(self.data, data_start, self.be)
            try:
                raw = zlib.decompress(self.data[data_start + 4:data_end])
            except zlib.error:
                return
            sub_data = raw
            sub_start = 0
            sub_end = len(raw)
        else:
            sub_data = self.data
            sub_start = data_start
            sub_end = data_end

        # Walk subrecords
        pos = sub_start
        while pos + 6 <= sub_end:
            sub_sig = read_sig(sub_data, pos, self.be)
            sub_size = read_u16(sub_data, pos + 4, self.be)

            if sub_sig == "NAM1" and sub_size > 0:
                text_bytes = sub_data[pos + 6:pos + 6 + sub_size]
                text = text_bytes.rstrip(b"\x00").decode("utf-8", errors="replace")
                if text.strip():
                    results.append({
                        "form_id": form_id,
                        "text": text,
                        "esm_offset": record_offset,
                        "sub_offset": pos if not is_compressed else -1,
                    })

            pos += 6 + sub_size


# ============================================================================
# Phase 1: Search for NAM1 texts in memory dump
# ============================================================================

def cmd_search(args):
    """Extract NAM1 texts from ESM and search for them in the memory dump."""
    print(f"Loading ESM: {args.esm}")
    with open(args.esm, "rb") as f:
        esm_data = f.read()

    be = None
    if args.endian == "big":
        be = True
    elif args.endian == "little":
        be = False
    # else auto-detect

    parser = EsmParser(esm_data, big_endian=be)
    print(f"  Endianness: {'big' if parser.be else 'little'}")

    print("Extracting NAM1 subrecords from INFO records...")
    nam1_records = parser.extract_nam1_from_info()
    print(f"  Found {len(nam1_records)} NAM1 entries")

    # Filter to searchable texts (min length, no generic)
    min_len = args.min_length
    searchable = []
    seen_texts = set()
    for rec in nam1_records:
        text = rec["text"].strip()
        if len(text) < min_len:
            continue
        if text in seen_texts:
            continue  # Skip duplicates for searching
        seen_texts.add(text)
        searchable.append(rec)

    print(f"  Searchable (>={min_len} chars, unique): {len(searchable)}")

    print(f"\nLoading dump: {args.dump}")
    dump_size = os.path.getsize(args.dump)
    print(f"  Size: {dump_size:,} bytes ({dump_size / 1024 / 1024:.0f} MB)")

    with open(args.dump, "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)

    hits = []
    searched = 0
    found = 0
    multi_hit = 0

    for rec in searchable:
        text = rec["text"]
        search_bytes = text.encode("utf-8")

        offsets = []
        pos = 0
        while True:
            pos = mm.find(search_bytes, pos)
            if pos == -1:
                break
            offsets.append(pos)
            pos += 1
            if len(offsets) >= 50:
                break

        searched += 1
        if offsets:
            found += 1
            if len(offsets) > 1:
                multi_hit += 1
            hits.append({
                "form_id": rec["form_id"],
                "text": text[:80],
                "text_full": text,
                "esm_offset": rec["esm_offset"],
                "dump_offsets": offsets,
                "hit_count": len(offsets),
            })

        if searched % 500 == 0:
            print(f"  Searched {searched}/{len(searchable)}, found {found}...")

    mm.close()

    print(f"\n{'=' * 70}")
    print(f"  SEARCH RESULTS")
    print(f"{'=' * 70}")
    print(f"  NAM1 entries in ESM:      {len(nam1_records):>8,}")
    print(f"  Unique searchable texts:  {len(searchable):>8,}")
    print(f"  Found in dump:            {found:>8,}  ({found * 100 / max(1, len(searchable)):.1f}%)")
    print(f"  Multiple hits:            {multi_hit:>8,}")
    print(f"  Not found:                {len(searchable) - found:>8,}")

    # Show some examples
    if hits:
        print(f"\n  First 20 hits:")
        for h in hits[:20]:
            offsets_str = ", ".join(f"0x{o:08X}" for o in h["dump_offsets"][:3])
            if h["hit_count"] > 3:
                offsets_str += f" (+{h['hit_count'] - 3} more)"
            print(f"    0x{h['form_id']:08X}: \"{h['text'][:60]}\" @ {offsets_str}")

    # Analyze offset distribution
    if hits:
        all_offsets = [o for h in hits for o in h["dump_offsets"]]
        if all_offsets:
            min_off = min(all_offsets)
            max_off = max(all_offsets)
            print(f"\n  Offset range: 0x{min_off:08X} - 0x{max_off:08X}")

            # Histogram by 64MB blocks
            block_size = 64 * 1024 * 1024
            blocks = {}
            for o in all_offsets:
                block = o // block_size
                blocks[block] = blocks.get(block, 0) + 1

            print(f"\n  Distribution by 64MB blocks:")
            for block in sorted(blocks.keys()):
                start = block * block_size
                print(f"    0x{start:08X}-0x{start + block_size:08X}: {blocks[block]:>6} hits")

    # Save hits for Phase 2
    hits_path = args.output or "scripts/search_hits.json"
    with open(hits_path, "w") as f:
        json.dump(hits, f, indent=2)
    print(f"\n  Hits saved to: {hits_path}")

    return hits


# ============================================================================
# Phase 2: Detect TESResponse structs around found text strings
# ============================================================================

# TESResponse (44 bytes) — PDB layout:
#   +0:  RESPONSE_DATA (24 bytes)
#        +0: eEmotion (uint32, enum 0-8)
#        +4: iEmotionValue (uint32)
#        +8: pConversationTopic (ptr, 4 bytes)
#        +12: ucResponseID (byte)
#        +16: pVoiceSound (ptr, 4 bytes)
#        +20: bUseEmotion (bool, 1 byte)
#   +24: cResponseText (BSStringT, 8 bytes)
#        +24: m_sLen (uint16)
#        +26: m_usBufLen (uint16)
#        +28: m_pString (ptr, 4 bytes)
#   +32: pSpeakerIdle (ptr, 4 bytes)
#   +36: pListenerIdle (ptr, 4 bytes)
#   +40: pNext (TESResponse*, 4 bytes)

# BSStringT layout (8 bytes, big-endian Xbox 360):
#   +0: m_sLen (uint16 BE) - string length without null
#   +2: m_usBufLen (uint16 BE) - buffer length
#   +4: m_pString (uint32 BE) - pointer to char buffer

XBOX_VA_BASE = 0x40000000  # Xbox 360 virtual address base
XBOX_VA_MAX = 0xD0000000   # Approximate max VA


def va_to_file_offset(va, dump_base=0x40000000):
    """Convert Xbox 360 virtual address to dump file offset."""
    if va < dump_base:
        return None
    return va - dump_base


def cmd_structs(args):
    """Detect TESResponse structs near found text locations."""
    hits_path = args.hits or "scripts/search_hits.json"
    if not os.path.exists(hits_path):
        print(f"ERROR: {hits_path} not found. Run 'search' first.", file=sys.stderr)
        return

    with open(hits_path) as f:
        hits = json.load(f)

    print(f"Loaded {len(hits)} search hits from {hits_path}")

    print(f"Loading dump: {args.dump}")
    with open(args.dump, "rb") as f:
        dump_data = f.read()

    dump_size = len(dump_data)
    print(f"  Size: {dump_size:,} bytes")

    # For each text hit, look for BSStringT pointing to it
    # BSStringT on Xbox 360: {m_sLen(u16 BE), m_usBufLen(u16 BE), m_pString(u32 BE)}
    # The m_pString should be a VA pointing to the text location in the dump

    tes_response_candidates = []
    bsstring_found = 0

    for hit in hits:
        for dump_offset in hit["dump_offsets"]:
            text_va = dump_offset + XBOX_VA_BASE
            text_len = len(hit["text_full"])

            # Scan the dump for BSStringT structs that point to this text VA
            # We search within a reasonable range (BSStringT can be anywhere in heap)
            # Strategy: search for the 4-byte VA as big-endian uint32
            va_bytes = struct.pack(">I", text_va)

            # Find all occurrences of this pointer value
            pos = 0
            while True:
                pos = dump_data.find(va_bytes, pos)
                if pos == -1:
                    break

                # Check if this could be the m_pString field of a BSStringT
                # m_pString is at BSStringT+4, so BSStringT starts at pos-4
                bsstring_offset = pos - 4

                if bsstring_offset < 0:
                    pos += 1
                    continue

                # Read BSStringT fields
                m_sLen = struct.unpack_from(">H", dump_data, bsstring_offset)[0]
                m_usBufLen = struct.unpack_from(">H", dump_data, bsstring_offset + 2)[0]

                # Validate: m_sLen should match text length (or close)
                # m_usBufLen >= m_sLen
                if m_sLen == 0 or m_usBufLen < m_sLen:
                    pos += 1
                    continue

                # Allow some tolerance (null terminator, encoding differences)
                if abs(m_sLen - text_len) > 2:
                    pos += 1
                    continue

                bsstring_found += 1

                # Check if this BSStringT is at offset +24 within a TESResponse struct
                # TESResponse starts at bsstring_offset - 24
                tes_response_offset = bsstring_offset - 24

                if tes_response_offset < 0:
                    pos += 1
                    continue

                # Validate RESPONSE_DATA at TESResponse+0
                emotion = struct.unpack_from(">I", dump_data, tes_response_offset)[0]
                emotion_val = struct.unpack_from(">I", dump_data, tes_response_offset + 4)[0]
                response_id = dump_data[tes_response_offset + 12]
                use_emotion = dump_data[tes_response_offset + 20]

                # Validate: emotion should be 0-8
                valid_emotion = emotion <= 8
                # emotion_val usually 0-100
                valid_emotion_val = emotion_val <= 200
                # use_emotion should be 0 or 1
                valid_use_emotion = use_emotion <= 1

                # Read pNext at +40
                pnext = struct.unpack_from(">I", dump_data, tes_response_offset + 40)[0]
                valid_pnext = pnext == 0 or (XBOX_VA_BASE <= pnext < XBOX_VA_MAX)

                is_valid = valid_emotion and valid_emotion_val and valid_use_emotion and valid_pnext

                candidate = {
                    "form_id": hit["form_id"],
                    "text": hit["text"][:60],
                    "text_dump_offset": dump_offset,
                    "bsstring_offset": bsstring_offset,
                    "tes_response_offset": tes_response_offset,
                    "m_sLen": m_sLen,
                    "m_usBufLen": m_usBufLen,
                    "emotion": emotion,
                    "emotion_val": emotion_val,
                    "response_id": response_id,
                    "use_emotion": use_emotion,
                    "pnext": pnext,
                    "valid": is_valid,
                }
                tes_response_candidates.append(candidate)

                pos += 1

    valid_count = sum(1 for c in tes_response_candidates if c["valid"])
    emotion_names = ["Neutral", "Anger", "Disgust", "Fear", "Sad", "Happy", "Surprise", "Pained", "Puzzled"]

    print(f"\n{'=' * 70}")
    print(f"  STRUCT DETECTION RESULTS")
    print(f"{'=' * 70}")
    print(f"  BSStringT candidates:     {bsstring_found:>8,}")
    print(f"  TESResponse candidates:   {len(tes_response_candidates):>8,}")
    print(f"  Valid TESResponse:         {valid_count:>8,}")

    if tes_response_candidates:
        print(f"\n  Sample TESResponse structs (first 30 valid):")
        shown = 0
        for c in tes_response_candidates:
            if not c["valid"]:
                continue
            emo = emotion_names[c["emotion"]] if c["emotion"] < len(emotion_names) else f"?{c['emotion']}"
            pnext_str = f"0x{c['pnext']:08X}" if c["pnext"] else "NULL"
            print(
                f"    0x{c['form_id']:08X} @ dump+0x{c['tes_response_offset']:08X}: "
                f"emotion={emo}({c['emotion_val']}), id={c['response_id']}, "
                f"pNext={pnext_str}, \"{c['text']}\""
            )
            shown += 1
            if shown >= 30:
                break

        # Follow pNext chains for valid candidates
        print(f"\n  Checking pNext chains (up to 10 chains):")
        chains_checked = 0
        for c in tes_response_candidates:
            if not c["valid"] or c["pnext"] == 0:
                continue

            chain = [c["tes_response_offset"]]
            next_va = c["pnext"]
            while next_va and len(chain) < 20:
                next_off = va_to_file_offset(next_va)
                if next_off is None or next_off + 44 > dump_size:
                    chain.append(f"INVALID(0x{next_va:08X})")
                    break
                chain.append(next_off)

                # Read next TESResponse
                next_emotion = struct.unpack_from(">I", dump_data, next_off)[0]
                next_pnext = struct.unpack_from(">I", dump_data, next_off + 40)[0]

                # Read text from next node
                next_str_ptr = struct.unpack_from(">I", dump_data, next_off + 28)[0]
                next_str_len = struct.unpack_from(">H", dump_data, next_off + 24)[0]
                next_text = ""
                next_str_off = va_to_file_offset(next_str_ptr)
                if next_str_off and next_str_off + next_str_len <= dump_size:
                    next_text = dump_data[next_str_off:next_str_off + next_str_len].decode("utf-8", errors="replace")

                if next_emotion > 8:
                    chain.append(f"BAD_EMOTION({next_emotion})")
                    break

                if next_pnext == 0 or not (XBOX_VA_BASE <= next_pnext < XBOX_VA_MAX):
                    break
                next_va = next_pnext

            offsets_str = " -> ".join(
                f"0x{o:08X}" if isinstance(o, int) else o for o in chain
            )
            print(f"    Chain ({len(chain)} nodes): {offsets_str}")
            chains_checked += 1
            if chains_checked >= 10:
                break

    # Save results
    out_path = args.output or "scripts/struct_results.json"
    # Only save serializable data
    with open(out_path, "w") as f:
        json.dump(tes_response_candidates, f, indent=2)
    print(f"\n  Results saved to: {out_path}")


# ============================================================================
# Phase 3: iFileOffset investigation
# ============================================================================

# TESTopicInfo (80 bytes PDB, dump has +4 shift):
#   PDB+76 → dump+80: iFileOffset (uint32)
# But we need to find the TESTopicInfo structs in the dump first.
# We can use the dialogue.csv to get known INFO FormIDs,
# then search the dump for their structs.

def cmd_fileoffset(args):
    """Investigate iFileOffset field on TESTopicInfo structs."""
    print(f"Loading ESM: {args.esm}")
    with open(args.esm, "rb") as f:
        esm_data = f.read()
    esm_be = detect_endianness(esm_data)
    print(f"  Endianness: {'big' if esm_be else 'little'}")
    print(f"  Size: {len(esm_data):,} bytes")

    print(f"Loading dump: {args.dump}")
    with open(args.dump, "rb") as f:
        dump_data = f.read()
    dump_size = len(dump_data)
    print(f"  Size: {dump_size:,} bytes")

    # Parse dialogue.csv to get known INFO FormIDs with their offsets and endianness
    print(f"Loading dialogue.csv: {args.csv}")
    info_entries = []
    with open(args.csv, "r", encoding="utf-8-sig") as f:
        reader = csv.reader(f)
        header = next(reader)
        col = {name: i for i, name in enumerate(header)}

        for row in reader:
            if len(row) < 2 or row[0].strip() != "DIALOGUE":
                continue
            formid_str = row[col["FormID"]].strip()
            offset_str = row[col["Offset"]].strip()
            endian = row[col["Endianness"]].strip() if "Endianness" in col else ""
            if not formid_str or not offset_str:
                continue
            try:
                formid = int(formid_str, 16)
                offset = int(offset_str)
            except ValueError:
                continue
            info_entries.append({
                "form_id": formid,
                "dump_offset": offset,
                "endian": endian,
            })

    be_entries = [e for e in info_entries if e["endian"] == "BE"]
    le_entries = [e for e in info_entries if e["endian"] == "LE"]
    print(f"  Found {len(info_entries)} DIALOGUE entries (BE={len(be_entries)}, LE={len(le_entries)})")

    # Only use BE entries (these are TESTopicInfo runtime structs)
    # LE entries are ESM record headers in dump, not C++ structs
    print(f"\n  Analyzing BE entries only (runtime TESTopicInfo structs)...")

    # Try multiple candidate offsets for iFileOffset
    # PDB+76 with dump shifts of +0, +4, +8, +12, +16
    candidate_offsets = [76, 80, 84, 88, 92]

    for field_off in candidate_offsets:
        values = []
        for entry in be_entries[:500]:  # Sample first 500
            dump_off = entry["dump_offset"]
            if dump_off + field_off + 4 > dump_size:
                continue
            val = struct.unpack_from(">I", dump_data, dump_off + field_off)[0]
            values.append(val)

        if not values:
            continue

        zeros = sum(1 for v in values if v == 0)
        in_esm = sum(1 for v in values if 0 < v < len(esm_data))
        is_va = sum(1 for v in values if XBOX_VA_BASE <= v < XBOX_VA_MAX)
        small = sum(1 for v in values if 0 < v < 0x10000)

        # Check ESM signature match for in-range values
        matches = 0
        for entry, val in zip(be_entries[:500], values):
            if 0 < val < len(esm_data) and val + 24 <= len(esm_data):
                sig = read_sig(esm_data, val, esm_be)
                if sig == "INFO":
                    fid = read_u32(esm_data, val + 12, esm_be)
                    if fid == entry["form_id"]:
                        matches += 1

        print(f"\n    Field at struct+{field_off}:")
        print(f"      Sampled: {len(values)}, zeros: {zeros}, "
              f"in ESM range: {in_esm}, VA range: {is_va}, small (<64K): {small}")
        print(f"      ESM INFO FormID match: {matches}/{len(values)}")

        # Show first 10 non-zero sample values
        samples = [(entry["form_id"], v) for entry, v in zip(be_entries[:500], values) if v != 0][:10]
        if samples:
            print(f"      Sample values:")
            for fid, val in samples:
                print(f"        0x{fid:08X}: value=0x{val:08X} ({val:,})")

    # Also hex-dump a few bytes around the struct end for a known entry
    print(f"\n  Hex dump of TESTopicInfo tail (bytes 64-96) for first 5 BE entries:")
    for entry in be_entries[:5]:
        dump_off = entry["dump_offset"]
        if dump_off + 96 > dump_size:
            continue
        raw = dump_data[dump_off + 64:dump_off + 96]
        hex_str = " ".join(f"{b:02X}" for b in raw)
        print(f"    0x{entry['form_id']:08X} @ dump+0x{dump_off:08X}+64: {hex_str}")


def _find_nam1_in_record(esm_data, record_offset, data_size, flags, big_endian):
    """Find first NAM1 subrecord text in an INFO record."""
    data_start = record_offset + 24
    data_end = data_start + data_size

    is_compressed = bool(flags & 0x00040000)
    if is_compressed:
        if data_size < 4:
            return None
        try:
            raw = zlib.decompress(esm_data[data_start + 4:data_end])
        except zlib.error:
            return None
        sub_data = raw
        sub_start = 0
        sub_end = len(raw)
    else:
        sub_data = esm_data
        sub_start = data_start
        sub_end = data_end

    pos = sub_start
    while pos + 6 <= sub_end:
        sub_sig = read_sig(sub_data, pos, big_endian)
        sub_size = read_u16(sub_data, pos + 4, big_endian)

        if sub_sig == "NAM1" and sub_size > 0:
            text_bytes = sub_data[pos + 6:pos + 6 + sub_size]
            return text_bytes.rstrip(b"\x00").decode("utf-8", errors="replace")

        pos += 6 + sub_size

    return None


# ============================================================================
# Main
# ============================================================================

def main():
    parser = argparse.ArgumentParser(
        description="ESM-Memory cross-reference research tool"
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    # search command
    p_search = subparsers.add_parser("search", help="Search for ESM NAM1 texts in memory dump")
    p_search.add_argument("--esm", required=True, help="Path to ESM file")
    p_search.add_argument("--dump", required=True, help="Path to memory dump")
    p_search.add_argument("--endian", choices=["big", "little"], default=None,
                          help="ESM endianness (auto-detect if omitted)")
    p_search.add_argument("--min-length", type=int, default=8,
                          help="Minimum text length to search for (default: 8)")
    p_search.add_argument("--output", help="Output JSON path (default: scripts/search_hits.json)")

    # structs command
    p_structs = subparsers.add_parser("structs", help="Detect TESResponse structs near found texts")
    p_structs.add_argument("--dump", required=True, help="Path to memory dump")
    p_structs.add_argument("--hits", help="Path to search_hits.json (default: scripts/search_hits.json)")
    p_structs.add_argument("--output", help="Output JSON path (default: scripts/struct_results.json)")

    # fileoffset command
    p_foff = subparsers.add_parser("fileoffset", help="Investigate iFileOffset on TESTopicInfo structs")
    p_foff.add_argument("--esm", required=True, help="Path to ESM file")
    p_foff.add_argument("--dump", required=True, help="Path to memory dump")
    p_foff.add_argument("--csv", required=True, help="Path to dialogue.csv")

    args = parser.parse_args()

    if args.command == "search":
        cmd_search(args)
    elif args.command == "structs":
        cmd_structs(args)
    elif args.command == "fileoffset":
        cmd_fileoffset(args)


if __name__ == "__main__":
    main()
