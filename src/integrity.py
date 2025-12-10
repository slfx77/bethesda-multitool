"""
Integrity checking for carved files.
Validates that carved files are likely to be valid/complete.
"""

import os
import struct
from typing import Optional, Dict, Any
from .utils import read_uint32_le, read_uint32_be


class IntegrityChecker:
    """Checks integrity of carved files."""

    @staticmethod
    def check_file(file_path: str, file_type: str) -> Dict[str, Any]:
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
        result = {"valid": False, "size": 0, "issues": [], "info": {}}

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

            # Check based on file type
            if file_type == "dds":
                return IntegrityChecker._check_dds(data, file_size, result)
            elif file_type in ("xma", "wav"):
                return IntegrityChecker._check_riff(data, file_size, result)
            elif file_type in ("nif", "kf", "egm", "egt"):
                return IntegrityChecker._check_gamebryo(data, file_size, result)
            elif file_type in ("script_begin", "script_scriptname"):
                return IntegrityChecker._check_script(file_path, result)
            elif file_type == "bik":
                return IntegrityChecker._check_bik(data, file_size, result)
            elif file_type in ("esp", "esm"):
                return IntegrityChecker._check_plugin(data, file_size, result)
            elif file_type == "lip":
                return IntegrityChecker._check_lip(data, file_size, result)
            elif file_type == "bsa":
                return IntegrityChecker._check_bsa(data, file_size, result)
            elif file_type == "sdt":
                return IntegrityChecker._check_sdt(data, file_size, result)
            elif file_type == "mp3":
                return IntegrityChecker._check_mp3(data, file_size, result)
            elif file_type == "ogg":
                return IntegrityChecker._check_ogg(data, file_size, result)
            elif file_type == "fnt":
                return IntegrityChecker._check_fnt(data, file_size, result)
            elif file_type == "tex":
                return IntegrityChecker._check_tex(data, file_size, result)
            elif file_type in ("exe", "dll"):
                return IntegrityChecker._check_pe(data, file_size, result)
            else:
                # Generic check - just verify magic bytes
                result["valid"] = True
                result["info"]["note"] = "Basic validation only"
                return result

        except (IOError, OSError) as e:
            result["issues"].append(f"Error reading file: {e}")
            return result

    @staticmethod
    def _check_dds(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_riff(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_gamebryo(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_script(file_path: str, result: dict) -> dict:
        """Check Bethesda script integrity."""
        try:
            with open(file_path, "r", encoding="ascii", errors="ignore") as f:
                content = f.read()

            # Check for script markers
            has_scriptname = "ScriptName" in content or "scn " in content
            has_begin = "BEGIN" in content or "begin" in content
            has_end = "\nEND" in content or "\nend" in content or "\nEnd" in content

            if not has_scriptname:
                result["issues"].append("No ScriptName found")

            if has_begin and not has_end:
                result["issues"].append("BEGIN found but no END")

            # Extract script name
            if "ScriptName" in content:
                start = content.find("ScriptName") + 10
                line_end = content.find("\n", start)
                if line_end != -1:
                    script_name = content[start:line_end].strip()
                    result["info"]["script_name"] = script_name

            # Count BEGIN/END blocks
            begin_count = content.count("BEGIN") + content.count("begin")
            end_count = content.count("\nEND") + content.count("\nend") + content.count("\nEnd")

            result["info"]["begin_blocks"] = begin_count
            result["info"]["end_blocks"] = end_count

            if begin_count != end_count:
                result["issues"].append(f"Mismatched BEGIN/END: {begin_count} BEGIN, {end_count} END")

            # Check if mostly ASCII
            printable = sum(1 for c in content if c.isprintable() or c in "\n\r\t")
            if len(content) > 0 and printable / len(content) < 0.9:
                result["issues"].append("Contains non-printable characters")

            result["valid"] = has_scriptname and (not has_begin or has_end)

        except (IOError, UnicodeDecodeError) as e:
            result["issues"].append(f"Error reading script: {e}")

        return result

    @staticmethod
    def _check_bik(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_plugin(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_lip(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_bsa(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_sdt(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_mp3(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_ogg(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_fnt(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_tex(data: bytes, file_size: int, result: dict) -> dict:
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
    def _check_pe(data: bytes, file_size: int, result: dict) -> dict:
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


def generate_integrity_report(output_dir: str, file_types: Optional[list] = None) -> str:
    """
    Generate an integrity report for carved files.

    Args:
        output_dir: Directory containing carved files
        file_types: List of file types to check (None = all)

    Returns:
        Path to the generated report file
    """
    import glob
    from pathlib import Path

    report_path = os.path.join(output_dir, "integrity_report.txt")
    checker = IntegrityChecker()

    with open(report_path, "w", encoding="utf-8") as report:
        report.write("=" * 80 + "\n")
        report.write("File Integrity Report\n")
        report.write("=" * 80 + "\n\n")

        # Find all carved files
        for root, dirs, files in os.walk(output_dir):
            for file in files:
                # Skip the report itself
                if file == "integrity_report.txt":
                    continue

                file_path = os.path.join(root, file)
                rel_path = os.path.relpath(file_path, output_dir)

                # Determine file type from filename
                file_type = None
                for ftype in ["dds", "xma", "nif", "kf", "egm", "egt", "script_begin", "script_scriptname", "bik", "esp", "esm", "lip", "wav", "mp3", "ogg"]:
                    if file.startswith(ftype + "_"):
                        file_type = ftype
                        break

                if not file_type:
                    continue

                if file_types and file_type not in file_types:
                    continue

                # Check integrity
                result = checker.check_file(file_path, file_type)

                # Write to report
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

        report.write("\n" + "=" * 80 + "\n")
        report.write("End of Report\n")
        report.write("=" * 80 + "\n")

    return report_path
