"""
String/Text Extractor for Xbox 360 Memory Dumps

Extracts readable text strings from memory dumps, including:
- ASCII strings (English text, file paths, debug messages)
- UTF-16 LE strings (Windows/Xbox wide strings)
- Game-specific text (dialog, achievements, localization)

Automatically excludes regions already carved as files to avoid duplication.
"""

import json
import logging
import re
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple

logger = logging.getLogger(__name__)


@dataclass
class ExtractedString:
    """A single extracted string with metadata."""

    offset: int
    text: str
    encoding: str  # 'ascii', 'utf-16-le'
    length: int
    category: str = "general"


@dataclass
class StringExtractionResult:
    """Results from string extraction."""

    strings: List[ExtractedString] = field(default_factory=list)
    total_found: int = 0
    total_kept: int = 0
    skipped_carved: int = 0
    by_category: Dict[str, int] = field(default_factory=dict)
    by_encoding: Dict[str, int] = field(default_factory=dict)


class StringExtractor:
    """Extract readable strings from memory dumps."""

    # Minimum string lengths
    MIN_ASCII_LENGTH = 8
    MIN_UTF16_LENGTH = 6

    # Category patterns for classification
    CATEGORY_PATTERNS = {
        "filepath": re.compile(
            r"^[A-Za-z]:[/\\]|^[/\\]{2}|[/\\][A-Za-z0-9_]+[/\\]|\.(?:nif|dds|wav|txt|esp|esm|xma|bik)$",
            re.IGNORECASE,
        ),
        "debug": re.compile(
            r"error|warning|assert|debug|failed|exception|nullptr|invalid",
            re.IGNORECASE,
        ),
        "function": re.compile(r"^[A-Z][a-z]+[A-Z]|::|__[a-z]+__|^(?:Get|Set|Is|Has)[A-Z]"),
        "dialog": re.compile(r'[.!?]"?$|^"[A-Z]|\.{3}|[—…]'),
        "achievement": re.compile(
            r"^(?:Completed|Unlocked) |Achievement|Quest|Mission",
            re.IGNORECASE,
        ),
        "localization": re.compile(r"[àâäéèêëïîôùûüçÀÂÄÉÈÊËÏÎÔÙÛÜÇ]|[А-Яа-яЁё]|[\u4e00-\u9fff]"),
        "xml": re.compile(r"^<[A-Za-z]|/>$|</[A-Za-z]"),
    }

    def __init__(self) -> None:
        self.carved_regions: List[Tuple[int, int]] = []

    def load_carved_regions(self, manifest_path: str) -> int:
        """
        Load carved file regions from a carve manifest.

        Returns:
            Number of carved regions loaded.
        """
        self.carved_regions.clear()

        manifest_file = Path(manifest_path)
        if not manifest_file.exists():
            return 0

        with open(manifest_file, "r", encoding="utf-8") as f:
            manifest = json.load(f)

        for entry in manifest.get("entries", []):
            offset = entry.get("offset", 0)
            size = entry.get("size_in_dump", 0)
            if offset and size:
                self.carved_regions.append((offset, offset + size))

        # Merge overlapping regions
        if self.carved_regions:
            self.carved_regions.sort()
            merged: List[Tuple[int, int]] = [self.carved_regions[0]]
            for start, end in self.carved_regions[1:]:
                if start <= merged[-1][1]:
                    merged[-1] = (merged[-1][0], max(merged[-1][1], end))
                else:
                    merged.append((start, end))
            self.carved_regions = merged

        return len(self.carved_regions)

    def _is_in_carved_region(self, offset: int) -> bool:
        """Check if an offset falls within an already-carved region (binary search)."""
        if not self.carved_regions:
            return False

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

    def extract(self, dump_path: str) -> StringExtractionResult:
        """
        Extract all strings from a dump file.

        Automatically skips strings in regions that have already been
        carved as files (if carved regions were loaded).

        Args:
            dump_path: Path to the dump file

        Returns:
            StringExtractionResult with extracted strings and statistics.
        """
        result = StringExtractionResult()
        seen_texts: Set[str] = set()

        with open(dump_path, "rb") as f:
            data = f.read()

        # Extract ASCII strings
        ascii_strings = self._extract_ascii(data)
        for s in ascii_strings:
            result.total_found += 1
            if self._is_in_carved_region(s.offset):
                result.skipped_carved += 1
                continue
            if s.text not in seen_texts:
                seen_texts.add(s.text)
                s.category = self._categorize(s.text)
                result.strings.append(s)

        # Extract UTF-16 LE strings
        utf16_strings = self._extract_utf16le(data)
        for s in utf16_strings:
            result.total_found += 1
            if self._is_in_carved_region(s.offset):
                result.skipped_carved += 1
                continue
            if s.text not in seen_texts:
                seen_texts.add(s.text)
                s.category = self._categorize(s.text)
                result.strings.append(s)

        result.total_kept = len(result.strings)

        # Calculate statistics
        for s in result.strings:
            result.by_category[s.category] = result.by_category.get(s.category, 0) + 1
            result.by_encoding[s.encoding] = result.by_encoding.get(s.encoding, 0) + 1

        return result

    def _extract_ascii(self, data: bytes) -> List[ExtractedString]:
        """Extract printable ASCII strings."""
        strings: List[ExtractedString] = []
        pattern = rb"[\x20-\x7E\t\r\n]{" + str(self.MIN_ASCII_LENGTH).encode() + rb",}"

        for match in re.finditer(pattern, data):
            text_bytes = match.group()
            try:
                decoded = text_bytes.decode("utf-8").strip()
                if len(decoded) >= self.MIN_ASCII_LENGTH and self._is_meaningful(decoded):
                    strings.append(
                        ExtractedString(
                            offset=match.start(),
                            text=decoded,
                            encoding="ascii",
                            length=len(text_bytes),
                        )
                    )
            except UnicodeDecodeError:
                pass

        return strings

    def _extract_utf16le(self, data: bytes) -> List[ExtractedString]:
        """Extract UTF-16 LE strings (Windows wide strings)."""
        strings: List[ExtractedString] = []
        i = 0

        while i < len(data) - 2:
            # Look for start of potential UTF-16 string
            if 0x20 <= data[i] <= 0x7E and data[i + 1] == 0x00:
                start = i
                chars: List[str] = []

                while i < len(data) - 1:
                    low, high = data[i], data[i + 1]

                    if high == 0x00 and (0x20 <= low <= 0x7E or low in (0x09, 0x0A, 0x0D)):
                        chars.append(chr(low))
                        i += 2
                    elif high == 0x00 and low == 0x00:
                        break
                    else:
                        if high != 0:
                            try:
                                char = bytes([low, high]).decode("utf-16-le")
                                if char.isprintable() or char in "\t\r\n":
                                    chars.append(char)
                                    i += 2
                                    continue
                            except (UnicodeDecodeError, ValueError):
                                pass
                        break

                text = "".join(chars).strip()
                if len(text) >= self.MIN_UTF16_LENGTH and self._is_meaningful(text):
                    strings.append(
                        ExtractedString(
                            offset=start,
                            text=text,
                            encoding="utf-16-le",
                            length=i - start,
                        )
                    )
            else:
                i += 1

        return strings

    def _is_meaningful(self, text: str) -> bool:
        """Check if a string is meaningful (not garbage)."""
        if not text or len(text.strip()) < 4:
            return False

        # Filter strings that are mostly punctuation/numbers
        alnum_count = sum(1 for c in text if c.isalnum())
        if alnum_count < len(text) * 0.3:
            return False

        # Filter strings with too few unique characters
        if len(set(text.replace(" ", ""))) < 3:
            return False

        # Filter hex dumps
        if re.match(r"^[0-9A-Fa-f\s]+$", text) and len(text) > 20:
            return False

        return True

    def _categorize(self, text: str) -> str:
        """Categorize a string based on its content."""
        for category, pattern in self.CATEGORY_PATTERNS.items():
            if pattern.search(text):
                return category
        return "general"

    def save_strings(
        self,
        result: StringExtractionResult,
        output_dir: Path,
    ) -> Dict[str, Path]:
        """
        Save extracted strings to files, organized by category.

        Args:
            result: The extraction result
            output_dir: Directory to save string files

        Returns:
            Dictionary mapping category to file path.
        """
        strings_dir = output_dir / "strings"
        strings_dir.mkdir(parents=True, exist_ok=True)

        # Group by category
        by_category: Dict[str, List[ExtractedString]] = defaultdict(list)
        for s in result.strings:
            by_category[s.category].append(s)

        saved_files: Dict[str, Path] = {}

        # Save each category
        for category, cat_strings in by_category.items():
            filename = f"{category}.txt"
            filepath = strings_dir / filename

            with open(filepath, "w", encoding="utf-8") as f:
                f.write(f"# {category.upper()} STRINGS\n")
                f.write(f"# Total: {len(cat_strings)}\n")
                f.write("#" + "=" * 70 + "\n\n")

                for s in sorted(cat_strings, key=lambda x: x.offset):
                    f.write(f"[0x{s.offset:08X}] ({s.encoding})\n")
                    f.write(f"  {s.text}\n\n")

            saved_files[category] = filepath

        return saved_files
