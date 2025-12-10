"""
Multi-dump comparison and high-entropy analysis tool.
Compares coverage across multiple dump files and analyzes unknown regions.
"""

import os
import json
import math
from pathlib import Path
from collections import Counter, defaultdict
from typing import Dict, List, Tuple, Any


def entropy(data: bytes) -> float:
    """Calculate Shannon entropy of data (0-8 bits)."""
    if not data:
        return 0
    counts = Counter(data)
    length = len(data)
    return -sum((c / length) * math.log2(c / length) for c in counts.values() if c > 0)


def classify_entropy(ent: float) -> str:
    """Classify data based on entropy."""
    if ent < 1.0:
        return "zeros/padding"
    elif ent < 3.0:
        return "structured (text/strings)"
    elif ent < 5.0:
        return "structured binary"
    elif ent < 7.0:
        return "mixed/binary"
    else:
        return "compressed/encrypted"


def analyze_high_entropy_region(data: bytes, offset: int, size: int) -> Dict[str, Any]:
    """Analyze a high-entropy region for patterns."""
    sample = data[offset : offset + min(size, 64 * 1024)]
    ent = entropy(sample)

    result = {
        "offset": offset,
        "size": size,
        "entropy": ent,
        "classification": classify_entropy(ent),
        "sample_hex": sample[:32].hex(),
    }

    # Check for zlib header variants
    if len(sample) >= 2:
        first_two = sample[:2]
        # Common zlib headers
        if first_two in (b"\x78\x9c", b"\x78\xda", b"\x78\x01", b"\x78\x5e"):
            result["possible_format"] = "zlib_stream"
        elif first_two == b"\x1f\x8b":
            result["possible_format"] = "gzip"
        elif sample[:4] == b"PK\x03\x04":
            result["possible_format"] = "zip"
        elif sample[:3] == b"BZh":
            result["possible_format"] = "bzip2"

    # Check for repeating patterns
    if size >= 16:
        pattern_4 = sample[:4]
        pattern_count = sample[:256].count(pattern_4)
        if pattern_count > 50:
            result["possible_format"] = f"repeating_pattern ({pattern_4.hex()})"

    return result


def load_manifest(dump_name: str) -> Dict[str, Any]:
    """Load carve manifest for a dump."""
    manifest_path = Path(f"coverage_analysis/{dump_name}/carved/{dump_name}/carve_manifest.json")
    if manifest_path.exists():
        with open(manifest_path) as f:
            return json.load(f)
    return {"entries": [], "summary": {}}


def analyze_dump(dump_path: str) -> Dict[str, Any]:
    """Analyze a single dump file."""
    dump_path = Path(dump_path)
    dump_name = dump_path.stem

    with open(dump_path, "rb") as f:
        data = f.read()

    dump_size = len(data)

    # Load manifest if available
    manifest = load_manifest(dump_name)

    # Calculate coverage from manifest
    total_in_dump = manifest.get("summary", {}).get("total_bytes_in_dump", 0)
    total_output = manifest.get("summary", {}).get("total_bytes_output", 0)
    file_count = manifest.get("summary", {}).get("total_files", 0)

    # Analyze entropy distribution
    chunk_size = 64 * 1024
    entropy_dist = {"zeros/padding": 0, "structured (text/strings)": 0, "structured binary": 0, "mixed/binary": 0, "compressed/encrypted": 0}

    high_entropy_regions = []

    for offset in range(0, len(data), chunk_size):
        chunk = data[offset : offset + chunk_size]
        if len(chunk) < 1024:
            continue
        ent = entropy(chunk)
        classification = classify_entropy(ent)
        entropy_dist[classification] += len(chunk)

        # Track high-entropy regions
        if ent >= 7.0:
            high_entropy_regions.append({"offset": offset, "entropy": ent, "sample": chunk[:16].hex()})

    # Find unique signatures in this dump
    signatures_found = defaultdict(int)
    for entry in manifest.get("entries", []):
        signatures_found[entry["file_type"]] += 1

    # Detect scripts (debug builds have them)
    has_scripts = signatures_found.get("script_scn", 0) + signatures_found.get("script_sn", 0) > 0

    return {
        "name": dump_name,
        "size_mb": dump_size / (1024 * 1024),
        "file_count": file_count,
        "coverage_bytes": total_in_dump,
        "coverage_percent": (total_in_dump / dump_size * 100) if dump_size > 0 else 0,
        "output_bytes": total_output,
        "entropy_distribution": entropy_dist,
        "high_entropy_count": len(high_entropy_regions),
        "high_entropy_mb": sum(chunk_size for _ in high_entropy_regions) / (1024 * 1024),
        "signatures_found": dict(signatures_found),
        "has_scripts": has_scripts,
        "build_type": "debug" if has_scripts else "release",
    }


def compare_dumps(dump_paths: List[str]):
    """Compare multiple dump files."""
    print("=" * 80)
    print("MULTI-DUMP COMPARISON ANALYSIS")
    print("=" * 80)
    print()

    results = []
    for path in dump_paths:
        print(f"Analyzing: {path}...")
        try:
            result = analyze_dump(path)
            results.append(result)
        except Exception as e:
            print(f"  Error: {e}")

    print()
    print("-" * 80)
    print("SUMMARY TABLE")
    print("-" * 80)
    print(f"{'Dump Name':<35} {'Size':>8} {'Files':>7} {'Coverage':>10} {'Scripts':>8} {'Build':<8}")
    print("-" * 80)

    for r in results:
        scripts = r["signatures_found"].get("script_scn", 0) + r["signatures_found"].get("script_sn", 0)
        print(f"{r['name']:<35} {r['size_mb']:>7.1f}M {r['file_count']:>7} {r['coverage_percent']:>9.1f}% {scripts:>8} {r['build_type']:<8}")

    print()
    print("-" * 80)
    print("ENTROPY DISTRIBUTION (MB)")
    print("-" * 80)
    print(f"{'Dump Name':<35} {'Zeros':>8} {'Text':>8} {'Binary':>8} {'Mixed':>8} {'Compressed':>10}")
    print("-" * 80)

    for r in results:
        ed = r["entropy_distribution"]
        print(
            f"{r['name']:<35} {ed['zeros/padding']/1024/1024:>7.1f}M {ed['structured (text/strings)']/1024/1024:>7.1f}M "
            f"{ed['structured binary']/1024/1024:>7.1f}M {ed['mixed/binary']/1024/1024:>7.1f}M "
            f"{ed['compressed/encrypted']/1024/1024:>9.1f}M"
        )

    print()
    print("-" * 80)
    print("FILE TYPES FOUND")
    print("-" * 80)

    # Collect all file types
    all_types = set()
    for r in results:
        all_types.update(r["signatures_found"].keys())

    # Print header
    header = f"{'Type':<15}"
    for r in results:
        header += f" {r['name'][:12]:>12}"
    print(header)
    print("-" * 80)

    for ftype in sorted(all_types):
        row = f"{ftype:<15}"
        for r in results:
            count = r["signatures_found"].get(ftype, 0)
            row += f" {count:>12}"
        print(row)

    return results


def analyze_high_entropy_details(dump_path: str, min_size_mb: float = 1.0):
    """Detailed analysis of high-entropy regions in a dump."""
    print(f"\n{'=' * 80}")
    print(f"HIGH-ENTROPY REGION ANALYSIS: {dump_path}")
    print("=" * 80)

    with open(dump_path, "rb") as f:
        data = f.read()

    chunk_size = 64 * 1024
    regions = []

    # Find contiguous high-entropy regions
    in_region = False
    region_start = 0

    for offset in range(0, len(data), chunk_size):
        chunk = data[offset : offset + chunk_size]
        if len(chunk) < 1024:
            continue
        ent = entropy(chunk)

        if ent >= 7.0:
            if not in_region:
                region_start = offset
                in_region = True
        else:
            if in_region:
                region_size = offset - region_start
                if region_size >= min_size_mb * 1024 * 1024:
                    regions.append((region_start, region_size))
                in_region = False

    # Handle region at end of file
    if in_region:
        region_size = len(data) - region_start
        if region_size >= min_size_mb * 1024 * 1024:
            regions.append((region_start, region_size))

    print(f"\nFound {len(regions)} high-entropy regions >= {min_size_mb} MB")
    print("-" * 80)

    for i, (start, size) in enumerate(regions):
        sample = data[start : start + 256]
        ent = entropy(sample)

        print(f"\nRegion {i+1}: Offset 0x{start:08X}, Size {size/1024/1024:.2f} MB, Entropy {ent:.2f}")
        print(f"  First 32 bytes: {sample[:32].hex()}")

        # Try to identify content
        # Check for zlib markers within the region
        zlib_count = 0
        for marker in [b"\x78\x9c", b"\x78\xda", b"\x78\x01"]:
            zlib_count += data[start : start + size].count(marker)

        if zlib_count > 10:
            print(f"  Contains {zlib_count} potential zlib stream markers")

        # Check for common game data patterns
        if b"NiNode" in sample or b"NIF" in sample:
            print(f"  May contain NIF/Gamebryo data")
        if b"DDS " in sample or b"DXT" in sample:
            print(f"  May contain texture data")

        # Look for ASCII strings
        ascii_chars = sum(1 for b in sample if 32 <= b < 127)
        if ascii_chars > 128:
            print(f"  High ASCII content ({ascii_chars}/256 printable chars)")
            # Extract readable strings
            import re

            strings = re.findall(b"[A-Za-z0-9_./\\\\]{8,}", sample)
            if strings:
                print(f"  Sample strings: {[s.decode() for s in strings[:3]]}")


if __name__ == "__main__":
    # The four main dumps to compare
    dumps = ["Sample/Fallout_Debug.xex.dmp", "Sample/Fallout_Release_Beta.xex.dmp", "Sample/Fallout_Release_MemDebug.xex.dmp", "Sample/Jacobstown.dmp"]

    # Filter to existing files
    existing_dumps = [d for d in dumps if os.path.exists(d)]

    if existing_dumps:
        results = compare_dumps(existing_dumps)

        print("\n")
        # Detailed high-entropy analysis for each
        for dump in existing_dumps:
            analyze_high_entropy_details(dump, min_size_mb=0.5)
