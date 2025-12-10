"""
String/Text Extractor for Xbox 360 Memory Dumps

Extracts readable text strings from memory dumps, including:
- ASCII strings (English text, file paths, debug messages)
- UTF-8 strings (localized text with accents)
- UTF-16 LE strings (Windows/Xbox wide strings)
- Game-specific text (dialog, achievements, karma titles)

Can optionally exclude regions already carved as files to avoid duplication.
"""

import os
import re
import json
import logging
from pathlib import Path
from typing import List, Tuple, Dict, Optional, Set
from dataclasses import dataclass, field
from collections import defaultdict

logging.basicConfig(level=logging.INFO, format="%(message)s")
logger = logging.getLogger(__name__)


@dataclass
class ExtractedString:
    """A single extracted string with metadata."""

    offset: int
    text: str
    encoding: str  # 'ascii', 'utf-8', 'utf-16-le'
    length: int
    category: str = "unknown"  # 'path', 'dialog', 'debug', 'localization', etc.


@dataclass
class StringExtractionReport:
    """Report of all extracted strings."""

    dump_file: str
    total_strings: int = 0
    strings_by_category: Dict[str, int] = field(default_factory=dict)
    strings_by_encoding: Dict[str, int] = field(default_factory=dict)
    unique_strings: int = 0


class StringExtractor:
    """Extract readable strings from memory dumps."""

    # Minimum string lengths by encoding
    MIN_ASCII_LENGTH = 8
    MIN_UTF8_LENGTH = 8
    MIN_UTF16_LENGTH = 6

    # Patterns for categorization
    CATEGORY_PATTERNS = {
        "filepath": re.compile(r"^[A-Za-z]:\\|^\\\\|^/|\\[A-Za-z0-9_]+\\|\.nif$|\.dds$|\.wav$|\.txt$|\.esp$|\.esm$", re.IGNORECASE),
        "debug": re.compile(r"error|warning|assert|debug|failed|exception|null|nullptr|invalid", re.IGNORECASE),
        "function": re.compile(r"^[A-Z][a-z]+[A-Z]|::|__[a-z]+__|^Get[A-Z]|^Set[A-Z]|^Is[A-Z]|^Has[A-Z]"),
        "dialog": re.compile(r'[.!?]"?$|^"[A-Z]|\.\.\.|—|…'),
        "achievement": re.compile(r"^Completed |^Unlocked |Achievement|Quest|Mission", re.IGNORECASE),
        "localization": re.compile(r"[àâäéèêëïîôùûüç]|[А-Яа-яЁё]|[日本語中文]", re.IGNORECASE),
        "menu": re.compile(r"^OK$|^Cancel$|^Yes$|^No$|^Back$|^Continue$|^Start$|^Load$|^Save$|^Options$", re.IGNORECASE),
        "xml": re.compile(r"^<[A-Za-z]|/>$|</[A-Za-z]"),
    }

    def __init__(self, output_dir: str = "./string_extraction", carved_manifest: Optional[str] = None):
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self.carved_regions: List[Tuple[int, int]] = []  # (start, end) tuples

        # Load carved regions from manifest if provided
        if carved_manifest and Path(carved_manifest).exists():
            self._load_carved_regions(carved_manifest)

    def _load_carved_regions(self, manifest_path: str):
        """Load carved file regions from a carve manifest."""
        with open(manifest_path, "r") as f:
            manifest = json.load(f)

        for entry in manifest.get("entries", []):
            offset = entry.get("offset", 0)
            size = entry.get("size_in_dump", 0)
            if offset and size:
                self.carved_regions.append((offset, offset + size))

        # Sort and merge overlapping regions for efficient lookup
        self.carved_regions.sort()
        if self.carved_regions:
            merged = [self.carved_regions[0]]
            for start, end in self.carved_regions[1:]:
                if start <= merged[-1][1]:
                    merged[-1] = (merged[-1][0], max(merged[-1][1], end))
                else:
                    merged.append((start, end))
            self.carved_regions = merged

        logger.info(f"  Loaded {len(self.carved_regions)} carved regions to exclude")

    def _is_in_carved_region(self, offset: int) -> bool:
        """Check if an offset falls within an already-carved region."""
        # Binary search for efficiency
        lo, hi = 0, len(self.carved_regions)
        while lo < hi:
            mid = (lo + hi) // 2
            start, end = self.carved_regions[mid]
            if start <= offset < end:
                return True
            elif offset < start:
                hi = mid
            else:
                lo = mid + 1
        return False

    def extract_strings(self, dump_path: str, exclude_carved: bool = True) -> List[ExtractedString]:
        """Extract all strings from a dump file.

        Args:
            dump_path: Path to the dump file
            exclude_carved: If True and carved_manifest was provided, skip carved regions
        """
        dump_path = Path(dump_path)
        dump_name = dump_path.stem

        logger.info(f"Extracting strings from: {dump_path.name}")
        if exclude_carved and self.carved_regions:
            logger.info(f"  Excluding {len(self.carved_regions)} carved regions")

        with open(dump_path, "rb") as f:
            data = f.read()

        all_strings: List[ExtractedString] = []
        seen_texts: Set[str] = set()
        skip_carved = exclude_carved and bool(self.carved_regions)

        # Extract ASCII strings
        logger.info("  Scanning for ASCII strings...")
        ascii_strings = self._extract_ascii(data)
        ascii_kept = 0
        ascii_skipped = 0
        for s in ascii_strings:
            if skip_carved and self._is_in_carved_region(s.offset):
                ascii_skipped += 1
                continue
            if s.text not in seen_texts:
                seen_texts.add(s.text)
                all_strings.append(s)
                ascii_kept += 1
        if skip_carved:
            logger.info(f"    Found {len(ascii_strings)} ASCII strings ({ascii_kept} kept, {ascii_skipped} in carved regions)")
        else:
            logger.info(f"    Found {len(ascii_strings)} ASCII strings ({ascii_kept} after dedup)")

        # Extract UTF-16 LE strings (common on Xbox/Windows)
        logger.info("  Scanning for UTF-16 LE strings...")
        utf16_strings = self._extract_utf16le(data)
        utf16_kept = 0
        utf16_skipped = 0
        for s in utf16_strings:
            if skip_carved and self._is_in_carved_region(s.offset):
                utf16_skipped += 1
                continue
            if s.text not in seen_texts:
                seen_texts.add(s.text)
                all_strings.append(s)
                utf16_kept += 1
        if skip_carved:
            logger.info(f"    Found {len(utf16_strings)} UTF-16 strings ({utf16_kept} new, {utf16_skipped} in carved regions)")
        else:
            logger.info(f"    Found {len(utf16_strings)} UTF-16 strings ({utf16_kept} new)")

        # Categorize all strings
        for s in all_strings:
            s.category = self._categorize_string(s.text)

        logger.info(f"  Total unique strings: {len(all_strings)}")

        return all_strings

    def _extract_ascii(self, data: bytes) -> List[ExtractedString]:
        """Extract printable ASCII strings."""
        strings = []

        # Match sequences of printable ASCII (0x20-0x7E) plus common whitespace
        # Extended to include common UTF-8 continuation bytes for accented chars
        pattern = rb"[\x20-\x7E\t\r\n]{" + str(self.MIN_ASCII_LENGTH).encode() + rb",}"

        for match in re.finditer(pattern, data):
            text = match.group()
            try:
                # Try UTF-8 decode first (handles accented characters)
                decoded = text.decode("utf-8").strip()
                if len(decoded) >= self.MIN_ASCII_LENGTH and self._is_meaningful(decoded):
                    strings.append(ExtractedString(offset=match.start(), text=decoded, encoding="ascii", length=len(text)))
            except UnicodeDecodeError:
                pass

        return strings

    def _extract_utf16le(self, data: bytes) -> List[ExtractedString]:
        """Extract UTF-16 LE strings (Windows wide strings)."""
        strings = []

        # Look for sequences of [printable byte][0x00] patterns
        # This is how UTF-16 LE encodes ASCII characters
        i = 0
        while i < len(data) - 2:
            # Look for start of potential UTF-16 string
            if data[i] >= 0x20 and data[i] <= 0x7E and data[i + 1] == 0x00:
                # Found potential start, collect the string
                start = i
                chars = []
                while i < len(data) - 1:
                    low = data[i]
                    high = data[i + 1]

                    if high == 0x00 and (0x20 <= low <= 0x7E or low in (0x09, 0x0A, 0x0D)):
                        chars.append(chr(low))
                        i += 2
                    elif high == 0x00 and low == 0x00:
                        # Null terminator
                        break
                    else:
                        # Non-ASCII UTF-16 or end of string
                        if high != 0:
                            # Try to decode as actual UTF-16
                            try:
                                char = bytes([low, high]).decode("utf-16-le")
                                if char.isprintable() or char in "\t\r\n":
                                    chars.append(char)
                                    i += 2
                                    continue
                            except:
                                pass
                        break

                text = "".join(chars).strip()
                if len(text) >= self.MIN_UTF16_LENGTH and self._is_meaningful(text):
                    strings.append(ExtractedString(offset=start, text=text, encoding="utf-16-le", length=i - start))
            else:
                i += 1

        return strings

    def _is_meaningful(self, text: str) -> bool:
        """Check if a string is meaningful (not garbage)."""
        if not text or len(text.strip()) < 4:
            return False

        # Filter out strings that are mostly punctuation or numbers
        alnum_count = sum(1 for c in text if c.isalnum())
        if alnum_count < len(text) * 0.3:
            return False

        # Filter out strings that are all the same character
        if len(set(text.replace(" ", ""))) < 3:
            return False

        # Filter out hex dumps
        if re.match(r"^[0-9A-Fa-f\s]+$", text) and len(text) > 20:
            return False

        return True

    def _categorize_string(self, text: str) -> str:
        """Categorize a string based on its content."""
        for category, pattern in self.CATEGORY_PATTERNS.items():
            if pattern.search(text):
                return category
        return "general"

    def save_strings(self, strings: List[ExtractedString], dump_name: str) -> Dict[str, str]:
        """Save extracted strings to files, organized by category."""
        output_path = self.output_dir / dump_name
        output_path.mkdir(parents=True, exist_ok=True)

        # Group by category
        by_category: Dict[str, List[ExtractedString]] = defaultdict(list)
        for s in strings:
            by_category[s.category].append(s)

        saved_files = {}

        # Save each category to a separate file
        for category, cat_strings in by_category.items():
            filename = f"{category}_strings.txt"
            filepath = output_path / filename

            with open(filepath, "w", encoding="utf-8") as f:
                f.write(f"# {category.upper()} STRINGS\n")
                f.write(f"# Extracted from: {dump_name}\n")
                f.write(f"# Total: {len(cat_strings)} strings\n")
                f.write("#" + "=" * 70 + "\n\n")

                # Sort by offset
                for s in sorted(cat_strings, key=lambda x: x.offset):
                    f.write(f"[0x{s.offset:08X}] ({s.encoding})\n")
                    f.write(f"  {s.text}\n\n")

            saved_files[category] = str(filepath)
            logger.info(f"  Saved {len(cat_strings)} {category} strings to {filename}")

        # Save combined file
        combined_path = output_path / "all_strings.txt"
        with open(combined_path, "w", encoding="utf-8") as f:
            f.write(f"# ALL EXTRACTED STRINGS\n")
            f.write(f"# Source: {dump_name}\n")
            f.write(f"# Total: {len(strings)} unique strings\n")
            f.write("#" + "=" * 70 + "\n\n")

            for s in sorted(strings, key=lambda x: x.offset):
                f.write(f"{s.text}\n")

        saved_files["all"] = str(combined_path)

        return saved_files

    def generate_report(self, strings: List[ExtractedString], dump_name: str) -> StringExtractionReport:
        """Generate extraction statistics."""
        report = StringExtractionReport(dump_file=dump_name)
        report.total_strings = len(strings)
        report.unique_strings = len(set(s.text for s in strings))

        for s in strings:
            report.strings_by_category[s.category] = report.strings_by_category.get(s.category, 0) + 1
            report.strings_by_encoding[s.encoding] = report.strings_by_encoding.get(s.encoding, 0) + 1

        return report


def main():
    import argparse

    parser = argparse.ArgumentParser(description="Extract strings from Xbox 360 memory dumps")
    parser.add_argument("dump_file", help="Path to .dmp file")
    parser.add_argument("-o", "--output", default="./string_extraction", help="Output directory")
    parser.add_argument("-m", "--manifest", help="Path to carve_manifest.json to exclude carved regions")
    parser.add_argument("--include-carved", action="store_true", help="Include strings from carved regions")
    args = parser.parse_args()

    # Auto-detect manifest if not specified
    manifest_path = args.manifest
    if not manifest_path and not args.include_carved:
        dump_path = Path(args.dump_file)
        dump_name = dump_path.stem
        # Try common locations
        possible_manifests = [
            f"./output/{dump_name}/carve_manifest.json",
            f"./output/{dump_path.name}/carve_manifest.json",
        ]
        for p in possible_manifests:
            if Path(p).exists():
                manifest_path = p
                logger.info(f"Auto-detected manifest: {manifest_path}")
                break

    extractor = StringExtractor(output_dir=args.output, carved_manifest=manifest_path)

    dump_path = Path(args.dump_file)
    dump_name = dump_path.stem

    # Extract strings
    exclude_carved = not args.include_carved
    strings = extractor.extract_strings(args.dump_file, exclude_carved=exclude_carved)

    # Save to files
    logger.info(f"\nSaving strings to {args.output}/{dump_name}/")
    saved = extractor.save_strings(strings, dump_name)

    # Print report
    report = extractor.generate_report(strings, dump_name)

    print("\n" + "=" * 60)
    print("STRING EXTRACTION REPORT")
    print("=" * 60)
    print(f"Dump: {dump_name}")
    print(f"Total strings: {report.total_strings}")
    print(f"Unique strings: {report.unique_strings}")
    if manifest_path and exclude_carved:
        print(f"(Excluded strings from {len(extractor.carved_regions)} carved regions)")

    print("\nBy Category:")
    for cat, count in sorted(report.strings_by_category.items(), key=lambda x: -x[1]):
        print(f"  {cat:<20} {count:>6}")

    print("\nBy Encoding:")
    for enc, count in sorted(report.strings_by_encoding.items(), key=lambda x: -x[1]):
        print(f"  {enc:<20} {count:>6}")

    print(f"\nOutput files saved to: {args.output}/{dump_name}/")


if __name__ == "__main__":
    main()
