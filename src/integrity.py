"""
Integrity checking for carved files.
Validates that carved files are likely to be valid/complete.
"""

import os
from typing import Any, Dict, List, Optional, TypedDict
from .utils import read_uint32_le, read_uint32_be


class IntegrityResult(TypedDict):
    """Type for integrity check result."""

    valid: bool
    size: int
    issues: List[str]
    info: Dict[str, Any]


def _create_result() -> IntegrityResult:
    """Create a new integrity result with default values."""
    return {"valid": False, "size": 0, "issues": [], "info": {}}


class IntegrityChecker:
    """Checks integrity of carved files."""

    @staticmethod
    def check_file(file_path: str, file_type: str) -> IntegrityResult:
        """
        Check integrity of a carved file.

        Args:
            file_path: Path to the carved file
            file_type: Type of file (dds, xma, nif, etc.)

        Returns:
            Dictionary with integrity results:
            {
                'valid': bool,
                'size': int,
                'issues': list of strings,
                'info': dict with file-specific info
            }
        """
        result = _create_result()

        if not os.path.exists(file_path):
            result["issues"].append("File does not exist")
            return result

        file_size = os.path.getsize(file_path)
        result["size"] = file_size

        if file_size == 0:
            result["issues"].append("File is empty")
            return result

        try:
            with open(file_path, "rb") as f:
                data = f.read(min(file_size, 2048))  # Read header

            # Dispatch to specific checker based on file type
            checker = IntegrityChecker._get_checker(file_type)
            if checker:
                return checker(data, file_size, result, file_path)

            # Generic check - just verify magic bytes
            result["valid"] = True
            result["info"]["note"] = "Basic validation only"
            return result

        except (IOError, OSError) as e:
            result["issues"].append(f"Error reading file: {e}")
            return result

    @staticmethod
    def _get_checker(file_type: str) -> Any:
        """Get the appropriate checker function for a file type."""
        checkers = {
            "dds": IntegrityChecker._check_dds,
            "xma": IntegrityChecker._check_riff,
            "wav": IntegrityChecker._check_riff,
            "nif": IntegrityChecker._check_gamebryo,
            "kf": IntegrityChecker._check_gamebryo,
            "egm": IntegrityChecker._check_gamebryo,
            "egt": IntegrityChecker._check_gamebryo,
            "script_begin": IntegrityChecker._check_script,
            "script_scriptname": IntegrityChecker._check_script,
            "bik": IntegrityChecker._check_bik,
            "esp": IntegrityChecker._check_plugin,
            "esm": IntegrityChecker._check_plugin,
            "lip": IntegrityChecker._check_lip,
            "bsa": IntegrityChecker._check_bsa,
            "sdt": IntegrityChecker._check_sdt,
            "mp3": IntegrityChecker._check_mp3,
            "ogg": IntegrityChecker._check_ogg,
            "fnt": IntegrityChecker._check_fnt,
            "tex": IntegrityChecker._check_tex,
            "exe": IntegrityChecker._check_pe,
            "dll": IntegrityChecker._check_pe,
        }
        return checkers.get(file_type)

    @staticmethod
    def _check_dds(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check DDS file integrity."""
        if len(data) < 128:
            result["issues"].append("File too small for DDS header")
            return result

        if data[0:4] != b"DDS ":
            result["issues"].append("Invalid DDS magic bytes")
            return result

        # Try little-endian first
        header_size = read_uint32_le(data, 4)
        height = read_uint32_le(data, 12)
        width = read_uint32_le(data, 16)

        # If invalid, try big-endian
        if header_size != 124 or height == 0 or width == 0 or height > 16384 or width > 16384:
            height = read_uint32_be(data, 12)
            width = read_uint32_be(data, 16)
            header_size = read_uint32_be(data, 4)

        if header_size != 124:
            result["issues"].append(f"Invalid header size: {header_size} (expected 124)")

        if height == 0 or width == 0:
            result["issues"].append(f"Invalid dimensions: {width}x{height}")
        elif height > 16384 or width > 16384:
            result["issues"].append(f"Suspicious dimensions: {width}x{height}")
        else:
            result["info"]["width"] = width
            result["info"]["height"] = height
            result["info"]["fourcc"] = data[84:88].decode("ascii", errors="ignore")
            result["valid"] = True

        return result

    @staticmethod
    def _check_riff(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check RIFF file integrity (XMA, WAV)."""
        if len(data) < 12:
            result["issues"].append("File too small for RIFF header")
            return result

        if data[0:4] != b"RIFF":
            result["issues"].append("Invalid RIFF magic bytes")
            return result

        chunk_size = read_uint32_le(data, 4)
        format_type = data[8:12]

        result["info"]["format"] = format_type.decode("ascii", errors="ignore")
        result["info"]["declared_size"] = chunk_size + 8

        if chunk_size + 8 != file_size:
            result["issues"].append(f"Size mismatch: declared {chunk_size + 8}, actual {file_size}")
        else:
            result["valid"] = True

        return result

    @staticmethod
    def _check_gamebryo(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check Gamebryo file integrity (NIF, KF, EGM, EGT)."""
        if len(data) < 40:
            result["issues"].append("File too small for Gamebryo header")
            return result

        if data[0:22] != b"Gamebryo File Format":
            result["issues"].append("Invalid Gamebryo magic bytes")
            return result

        # Find version string
        version_start = 22
        null_pos = data.find(b"\x00", version_start, version_start + 40)

        if null_pos != -1:
            version = data[version_start:null_pos].decode("ascii", errors="ignore")
            result["info"]["version"] = version
            result["valid"] = True
        else:
            result["issues"].append("Could not find version string")

        return result

    @staticmethod
    def _check_script_markers(content: str) -> tuple[bool, bool, bool]:
        """Check for script markers in content."""
        has_scriptname = "ScriptName" in content or "scn " in content
        has_begin = "BEGIN" in content or "begin" in content
        has_end = "\nEND" in content or "\nend" in content or "\nEnd" in content
        return has_scriptname, has_begin, has_end

    @staticmethod
    def _extract_script_name(content: str) -> Optional[str]:
        """Extract script name from content."""
        if "ScriptName" not in content:
            return None
        start = content.find("ScriptName") + 10
        line_end = content.find("\n", start)
        if line_end != -1:
            return content[start:line_end].strip()
        return None

    @staticmethod
    def _count_script_blocks(content: str) -> tuple[int, int]:
        """Count BEGIN and END blocks in script."""
        begin_count = content.count("BEGIN") + content.count("begin")
        end_count = content.count("\nEND") + content.count("\nend") + content.count("\nEnd")
        return begin_count, end_count

    @staticmethod
    def _check_script_printable(content: str) -> bool:
        """Check if content is mostly printable ASCII."""
        if len(content) == 0:
            return True
        printable = sum(1 for c in content if c.isprintable() or c in "\n\r\t")
        return printable / len(content) >= 0.9

    @staticmethod
    def _check_script(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check Bethesda script integrity."""
        try:
            with open(file_path, "r", encoding="ascii", errors="ignore") as f:
                content = f.read()

            has_scriptname, has_begin, has_end = IntegrityChecker._check_script_markers(content)

            if not has_scriptname:
                result["issues"].append("No ScriptName found")

            if has_begin and not has_end:
                result["issues"].append("BEGIN found but no END")

            script_name = IntegrityChecker._extract_script_name(content)
            if script_name:
                result["info"]["script_name"] = script_name

            begin_count, end_count = IntegrityChecker._count_script_blocks(content)
            result["info"]["begin_blocks"] = begin_count
            result["info"]["end_blocks"] = end_count

            if begin_count != end_count:
                result["issues"].append(f"Mismatched BEGIN/END: {begin_count} BEGIN, {end_count} END")

            if not IntegrityChecker._check_script_printable(content):
                result["issues"].append("Contains non-printable characters")

            result["valid"] = has_scriptname and (not has_begin or has_end)

        except (IOError, UnicodeDecodeError) as e:
            result["issues"].append(f"Error reading script: {e}")

        return result

    @staticmethod
    def _check_bik(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check Bink video integrity."""
        if len(data) < 8:
            result["issues"].append("File too small for BIK header")
            return result

        if data[0:4] != b"BIKi":
            result["issues"].append("Invalid BIK magic bytes")
            return result

        declared_size = read_uint32_le(data, 4) + 8

        result["info"]["declared_size"] = declared_size

        if declared_size != file_size:
            result["issues"].append(f"Size mismatch: declared {declared_size}, actual {file_size}")
        else:
            result["valid"] = True

        return result

    @staticmethod
    def _check_plugin(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check ESP/ESM plugin integrity."""
        if len(data) < 24:
            result["issues"].append("File too small for plugin header")
            return result

        if data[0:4] != b"TES4":
            result["issues"].append("Invalid TES4 magic bytes")
            return result

        # TES4 record structure is complex, basic validation
        result["info"]["type"] = "TES4 Plugin"
        result["valid"] = True

        return result

    @staticmethod
    def _check_lip(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check LIP lip-sync file integrity."""
        if len(data) < 8:
            result["issues"].append("File too small for LIP header")
            return result

        if data[0:4] != b"LIPS":
            result["issues"].append("Invalid LIP magic bytes")
            return result

        result["info"]["type"] = "Lip-sync file"
        result["valid"] = True

        return result

    @staticmethod
    def _check_bsa(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check BSA archive integrity."""
        if len(data) < 36:
            result["issues"].append("File too small for BSA header")
            return result

        if data[0:4] != b"BSA\x00":
            result["issues"].append("Invalid BSA magic bytes")
            return result

        # Read BSA version
        version = read_uint32_le(data, 4)
        folder_record_offset = read_uint32_le(data, 8)
        folder_count = read_uint32_le(data, 16)
        file_count = read_uint32_le(data, 20)

        result["info"]["version"] = version
        result["info"]["folders"] = folder_count
        result["info"]["files"] = file_count

        # Sanity checks
        if folder_count > 10000:
            result["issues"].append(f"Suspicious folder count: {folder_count}")
        if file_count > 100000:
            result["issues"].append(f"Suspicious file count: {file_count}")
        if folder_record_offset < 36 or folder_record_offset > file_size:
            result["issues"].append(f"Invalid folder offset: {folder_record_offset}")

        result["valid"] = len(result["issues"]) == 0

        return result

    @staticmethod
    def _check_sdt(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check SDT shader data integrity."""
        if len(data) < 8:
            result["issues"].append("File too small for SDT header")
            return result

        if data[0:4] != b"SDAT":
            result["issues"].append("Invalid SDAT magic bytes")
            return result

        result["info"]["type"] = "Shader Data"
        result["valid"] = True

        return result

    @staticmethod
    def _check_mp3(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check MP3 file integrity."""
        if len(data) < 3:
            result["issues"].append("File too small for MP3 header")
            return result

        # Check for MP3 frame sync
        if data[0:2] not in (b"\xff\xfb", b"\xff\xfa", b"\xff\xf3", b"\xff\xf2"):
            result["issues"].append("Invalid MP3 sync bytes")
            return result

        # Extract MP3 frame info
        if len(data) >= 4:
            mpeg_version = (data[1] >> 3) & 0x3
            layer = (data[1] >> 1) & 0x3

            result["info"]["mpeg_version"] = ["MPEG 2.5", "Reserved", "MPEG 2", "MPEG 1"][mpeg_version]
            result["info"]["layer"] = ["Reserved", "Layer III", "Layer II", "Layer I"][layer]

            result["valid"] = True
        else:
            result["issues"].append("Incomplete MP3 header")

        return result

    @staticmethod
    def _check_ogg(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check OGG file integrity."""
        if len(data) < 27:
            result["issues"].append("File too small for OGG header")
            return result

        if data[0:4] != b"OggS":
            result["issues"].append("Invalid OggS magic bytes")
            return result

        # Check OGG page structure
        version = data[4]
        header_type = data[5]

        if version != 0:
            result["issues"].append(f"Unknown OGG version: {version}")

        result["info"]["version"] = version
        result["info"]["header_type"] = header_type
        result["valid"] = version == 0

        return result

    @staticmethod
    def _check_fnt(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check FNT font file integrity."""
        if len(data) < 4:
            result["issues"].append("File too small for FNT header")
            return result

        if data[0:4] != b"\x00\x01\x00\x00":
            result["issues"].append("Invalid FNT magic bytes")
            return result

        result["info"]["type"] = "Font file"
        result["valid"] = True

        return result

    @staticmethod
    def _check_tex(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check TEX texture info file integrity."""
        if len(data) < 4:
            result["issues"].append("File too small for TEX header")
            return result

        if data[0:4] != b"TEXI":
            result["issues"].append("Invalid TEXI magic bytes")
            return result

        result["info"]["type"] = "Texture info"
        result["valid"] = True

        return result

    @staticmethod
    def _check_pe(data: bytes, file_size: int, result: IntegrityResult, file_path: str = "") -> IntegrityResult:
        """Check PE (Portable Executable) file integrity for EXE/DLL."""
        if len(data) < 64:
            result["issues"].append("File too small for PE header")
            return result

        if data[0:2] != b"MZ":
            result["issues"].append("Invalid MZ magic bytes")
            return result

        # Get PE header offset
        if len(data) < 0x3C + 4:
            result["issues"].append("File too small for PE offset")
            return result

        pe_offset = read_uint32_le(data, 0x3C)

        if pe_offset > len(data) - 24 or pe_offset > 1024:
            result["issues"].append(f"Invalid PE offset: {pe_offset}")
            result["info"]["format"] = "DOS/MZ only"
            return result

        # Check PE signature
        if len(data) < pe_offset + 24:
            result["issues"].append("File too small for PE signature")
            return result

        pe_sig = data[pe_offset : pe_offset + 4]
        if pe_sig != b"PE\x00\x00":
            result["issues"].append("Invalid PE signature")
            result["info"]["format"] = "DOS executable"
            return result

        # Read COFF header
        machine = read_uint32_le(data, pe_offset + 4) & 0xFFFF
        num_sections = read_uint32_le(data, pe_offset + 6) & 0xFFFF

        # Determine machine type
        machine_types = {
            0x01F2: "Xbox 360 (PowerPC)",
            0x014C: "x86",
            0x8664: "x64",
            0x01C0: "ARM",
            0x01C4: "ARM Thumb-2",
        }
        machine_type = machine_types.get(machine, f"Unknown (0x{machine:04X})")

        result["info"]["machine"] = machine_type
        result["info"]["sections"] = num_sections

        # Validate section count
        if num_sections == 0:
            result["issues"].append("No sections found")
        elif num_sections > 96:
            result["issues"].append(f"Suspicious section count: {num_sections}")

        result["valid"] = len(result["issues"]) == 0

        return result


# Known file type prefixes for detection
_KNOWN_FILE_TYPES = ["dds", "xma", "nif", "kf", "egm", "egt", "script_begin", "script_scriptname", "bik", "esp", "esm", "lip", "wav", "mp3", "ogg"]


def _detect_file_type(filename: str) -> Optional[str]:
    """Detect file type from filename prefix."""
    for ftype in _KNOWN_FILE_TYPES:
        if filename.startswith(ftype + "_"):
            return ftype
    return None


def _write_report_header(report: Any) -> None:
    """Write report header."""
    report.write("=" * 80 + "\n")
    report.write("File Integrity Report\n")
    report.write("=" * 80 + "\n\n")


def _write_report_footer(report: Any) -> None:
    """Write report footer."""
    report.write("\n" + "=" * 80 + "\n")
    report.write("End of Report\n")
    report.write("=" * 80 + "\n")


def _write_file_result(report: Any, rel_path: str, file_type: str, result: IntegrityResult) -> None:
    """Write a single file's integrity result to the report."""
    status = "✓ VALID" if result["valid"] else "✗ INVALID"
    report.write(f"\n{status} - {rel_path}\n")
    report.write(f"  Type: {file_type}\n")
    report.write(f"  Size: {result['size']} bytes\n")

    if result["info"]:
        report.write("  Info:\n")
        for key, value in result["info"].items():
            report.write(f"    {key}: {value}\n")

    if result["issues"]:
        report.write("  Issues:\n")
        for issue in result["issues"]:
            report.write(f"    - {issue}\n")


def _should_check_file(filename: str, file_type: Optional[str], file_types: Optional[List[str]]) -> bool:
    """Determine if a file should be checked."""
    if filename == "integrity_report.txt":
        return False
    if file_type is None:
        return False
    if file_types and file_type not in file_types:
        return False
    return True


def generate_integrity_report(output_dir: str, file_types: Optional[List[str]] = None) -> str:
    """
    Generate an integrity report for carved files.

    Args:
        output_dir: Directory containing carved files
        file_types: List of file types to check (None = all)

    Returns:
        Path to the generated report file
    """
    report_path = os.path.join(output_dir, "integrity_report.txt")
    checker = IntegrityChecker()

    with open(report_path, "w", encoding="utf-8") as report:
        _write_report_header(report)

        for root, _dirs, files in os.walk(output_dir):
            for filename in files:
                file_type = _detect_file_type(filename)

                if not _should_check_file(filename, file_type, file_types):
                    continue

                file_path = os.path.join(root, filename)
                rel_path = os.path.relpath(file_path, output_dir)
                result = checker.check_file(file_path, file_type)  # type: ignore[arg-type]

                _write_file_result(report, rel_path, file_type, result)  # type: ignore[arg-type]

        _write_report_footer(report)

    return report_path
