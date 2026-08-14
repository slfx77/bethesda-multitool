"""Generate Starfield's raw-index CTDA condition-function table.

The generated table treats the hash-pinned xEdit SF1 definitions as the oracle
for all 610 names and coarse on-disk parameter kinds. A separate, opt-in retail
mode hash-gates one installed build and verifies only what those files expose:
the physical CTDA layout, observed raw-index subset, and executable strings. It
does not promote retail usage to a complete condition, type, callback, script-
opcode, or Papyrus-command oracle.

Usage:
    python tools/extract_starfield_condition_functions.py
    python tools/extract_starfield_condition_functions.py --verify-only
    python tools/extract_starfield_condition_functions.py --verify-retail <install>
    python tools/extract_starfield_condition_functions.py --verify-retail <install> --verify-retail-corpus
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import mmap
import re
import struct
import uuid
import zlib
from dataclasses import dataclass
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_XEDIT = (
    REPO_ROOT
    / "Sample"
    / "Reference_Code"
    / "TES5Edit"
    / "Core"
    / "wbDefinitionsSF1.pas"
)
DEFAULT_OUTPUT = (
    REPO_ROOT
    / "src"
    / "BethesdaMultitool"
    / "Core"
    / "Formats"
    / "Esm"
    / "Script"
    / "StarfieldConditionFunctionTable.Generated.cs"
)

EXPECTED_XEDIT_SHA256 = (
    "8736162FCE44C970CFA3DDAC945A739530169390C4FDABAFC0209B36B247A576"
)
XEDIT_SOURCE_COMMIT = "e0e529a2d473756520f2d41f72c24dea0cf5ee0d"
CONDITION_FUNCTION_COUNT = 610
PARAMETER_TYPE_COUNT = 67
MAX_CONDITION_INDEX = 966
TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT = 82
TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT = 83

# Optional retail layout/usage oracle. These inputs are never needed for normal
# generation, --verify-only, compilation, tests, or runtime lookup.
EXPECTED_RETAIL_STEAM_APP_ID = "1716740"
EXPECTED_RETAIL_STEAM_BUILD_ID = "23518663"
EXPECTED_RETAIL_EXE_VERSION = (1, 16, 244, 0)
EXPECTED_RETAIL_EXE_COFF_TIMESTAMP = 0x6A1E0C18
EXPECTED_RETAIL_EXE = (
    102_476_200,
    "7E9ADB1414A8E1B325E5E1F097B9B17B78DEB7EEBEDA37A333351A43A60F9D28",
)
EXPECTED_RETAIL_ESM_HEDR_VERSION_BITS = 0x3F75C28F  # nominally 0.96f
EXPECTED_RETAIL_PDB_GUID = "f663cdf9-e091-4639-8547-2af3e0fcebf8"
EXPECTED_RETAIL_PDB_AGE = 1
EXPECTED_RETAIL_PDB_PATH = (
    r"E:\BuildAgent\work\fee57674ddcb42c9\Genesis\Build\PC\Starfield.pdb"
)

# The base master is sufficient for the default retail verification. The
# optional corpus mode pins the exact 14 official/Creation masters installed on
# 2026-08-14 so local mods or later DLC cannot silently change the census.
EXPECTED_RETAIL_CORPUS_ARTIFACTS = {
    "BlueprintShips-SFBGS050.esm": (
        16_136_720,
        "8848942D4B8A5143E434E17EA37828C920512AF8C3A9FFD87C109D537B704AC6",
    ),
    "BlueprintShips-Starfield.esm": (
        303_656_303,
        "4341D569FB5B840895D59880972C8F4622790A5229373860D0DE0DF352252371",
    ),
    "Constellation.esm": (
        39_210,
        "A314299BA8394A682B5BFCA5AC29D5EE33FEA9CA4E1FF70931E9D6022C81B0BC",
    ),
    "OldMars.esm": (
        24_358,
        "96F74CB008EE4B699B5430068906132DC63777453123EF227B93A629DB924BC9",
    ),
    "SFBGS003.esm": (
        33_279_036,
        "E3BAB5D184BC0D948E01A23D9166E0CB626D04878F01AE87D75D6F5421F2CF63",
    ),
    "SFBGS004.esm": (
        254_945,
        "2637FF51120C013E0742132F6E6B2C795A48C02A23FEFFEA0E5EEC531808C41D",
    ),
    "SFBGS006.esm": (
        7_315_458,
        "964A00BCFA2E9D1D183251EC883E6D7888C2AE49579F2B98395126EAFB325C45",
    ),
    "SFBGS007.esm": (
        27_312,
        "40687F89AC820A750436C71F5A023FDAFF552C3F1D314601DBB0177D341A2323",
    ),
    "SFBGS008.esm": (
        11_302_160,
        "E4426499386FCFCF74ED3047BE298A426CB2FAAE2B710904093A028A04B8B462",
    ),
    "SFBGS00D.esm": (
        102_054_268,
        "35271D3221310094C70AC2F418D7D393DB59DD94D303E830536F85034F44DA95",
    ),
    "SFBGS047.esm": (
        472_022,
        "A0D739B07988D49BFEFD2A8A6C0D054DE6A73781DC86C70AB917DA93C9DCC215",
    ),
    "SFBGS050.esm": (
        106_017_249,
        "0BDA63AE7E4FA48E1149A62E557C56CE65CE8C97BADFC6FAD1A5A0FAF5ABEB9D",
    ),
    "ShatteredSpace.esm": (
        501_394_621,
        "F8B9333000ABA2A4A8A417CB74A11EDE6F5B824D27DB1918FD1C1F7B64385A43",
    ),
    "Starfield.esm": (
        1_457_098_709,
        "1DABED00C3F4282DD3BB54D2E9601E40B577D8742D078B7CCEF203ADBFEF0DA7",
    ),
}

EXPECTED_RETAIL_BASE_CENSUS = {
    "records": 3_829_247,
    "groups": 101_341,
    "compressed_records": 91_149,
    "ctda": 96_486,
    "distinct_indices": 303,
    "maximum_index": 966,
    "parameter3_minus_one": 93_267,
    "parameter3_zero": 552,
    "parameter3_other": 2_667,
    "parameter3_minimum": -2,
    "parameter3_maximum": 2_948_587,
}

EXPECTED_RETAIL_CORPUS_CENSUS = {
    "records": 7_490_102,
    "groups": 137_589,
    "compressed_records": 136_741,
    "ctda": 124_096,
    "distinct_indices": 310,
    "maximum_index": 966,
    "parameter3_minus_one": 119_642,
    "parameter3_zero": 681,
    "parameter3_other": 3_773,
    "parameter3_minimum": -2,
    "parameter3_maximum": 2_948_587,
}

EXPECTED_PARAMETER_TYPES = (
    "ptNone",
    "ptBiomeMask",
    "ptLimbCategory",
    "ptReactionType",
    "ptFloat",
    "ptForm",
    "ptInteger",
    "ptString",
    "ptAlias",
    "ptEvent",
    "ptPackdata",
    "ptQuestStage",
    "ptAlignment",
    "ptAxis",
    "ptCastingSource",
    "ptCrimeType",
    "ptCriticalStage",
    "ptDamageCauseType",
    "ptFormType",
    "ptMiscStat",
    "ptPerkCategory",
    "ptPerkSkillGroup",
    "ptPerkSkillGroupComparison",
    "ptPronoun",
    "ptSex",
    "ptWardState",
    "ptFurnitureEntry",
    "ptActor",
    "ptActorBase",
    "ptActorValue",
    "ptAcousticSpace",
    "ptAssociationType",
    "ptCell",
    "ptClass",
    "ptConditionForm",
    "ptDamageType",
    "ptEffectItem",
    "ptEquipType",
    "ptEventData",
    "ptFaction",
    "ptFactionNull",
    "ptFormList",
    "ptFurniture",
    "ptGamePlayOption",
    "ptGlobal",
    "ptIdleForm",
    "ptInventoryObject",
    "ptKeyword",
    "ptLocation",
    "ptLocationRefType",
    "ptMagicEffect",
    "ptOwner",
    "ptPackage",
    "ptPerk",
    "ptPlanet",
    "ptQuest",
    "ptRace",
    "ptReference",
    "ptRegion",
    "ptResearchProject",
    "ptResource",
    "ptScene",
    "ptSpeechChallenge",
    "ptSnapTemplate",
    "ptVoiceType",
    "ptWeather",
    "ptWorldspace",
)

# The SF1 union is authoritative, not the broad comment heading: ptForm is a FormID
# inside the "Misc" section, while Starfield ptActorValue is explicitly an AVIF FormID.
FORM_ID_PARAMETER_TYPES = {
    "ptForm",
    *EXPECTED_PARAMETER_TYPES[27:],
}
NUMERIC_PARAMETER_TYPES = {
    item for item in EXPECTED_PARAMETER_TYPES[1:27] if item != "ptForm"
}
FORM_ID_TYPES = {item.casefold() for item in FORM_ID_PARAMETER_TYPES}
NUMERIC_TYPES = {item.casefold() for item in NUMERIC_PARAMETER_TYPES}
TYPE_OVERRIDE_ELIGIBLE_TYPES = {"ptreference", "ptactor", "ptpackage"}


@dataclass(frozen=True)
class ConditionFunction:
    index: int
    name: str
    param_types: tuple[str, str, str]


@dataclass(frozen=True)
class RetailPeMetadata:
    file_version: tuple[int, int, int, int]
    coff_timestamp: int
    pdb_guid: str
    pdb_age: int
    pdb_path: str


@dataclass(frozen=True)
class RetailEsmHeader:
    version_bits: int
    declared_record_count: int
    next_object_id: int


@dataclass(frozen=True)
class RetailCtdaCensus:
    record_count: int
    group_count: int
    compressed_record_count: int
    compressed_ctda_count: int
    width_counts: tuple[tuple[int, int], ...]
    observed_indices: frozenset[int]
    parameter3_counts: tuple[tuple[int, int], ...]
    parameter3_minimum: int | None
    parameter3_maximum: int | None

    @property
    def ctda_count(self) -> int:
        return sum(count for _, count in self.width_counts)


def require_xedit_hash(path: Path) -> None:
    if not path.is_file():
        raise SystemExit(f"missing xEdit input: {path}")
    actual = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    if actual != EXPECTED_XEDIT_SHA256:
        raise SystemExit(
            f"unexpected xEdit SHA-256 for {path}: expected "
            f"{EXPECTED_XEDIT_SHA256}, found {actual}"
        )


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def require_retail_artifact(path: Path, expected: tuple[int, str]) -> None:
    expected_size, expected_hash = expected
    if not path.is_file():
        raise SystemExit(f"missing pinned retail artifact: {path}")
    actual_size = path.stat().st_size
    if actual_size != expected_size:
        raise SystemExit(
            f"unexpected size for {path}: expected {expected_size}, found {actual_size}"
        )
    actual_hash = sha256_file(path)
    if actual_hash != expected_hash:
        raise SystemExit(
            f"unexpected SHA-256 for {path}: expected {expected_hash}, "
            f"found {actual_hash}"
        )


def read_appmanifest_value(path: Path, key: str) -> str:
    if not path.is_file():
        raise SystemExit(f"missing Steam app manifest: {path}")
    text = path.read_text(encoding="utf-8-sig", errors="strict")
    match = re.search(
        rf'^\s*"{re.escape(key)}"\s+"([^"]*)"\s*$', text, re.MULTILINE
    )
    if match is None:
        raise SystemExit(f"Steam app manifest {path} has no {key!r} value")
    return match.group(1)


def require_range(data: bytes, offset: int, size: int, label: str) -> None:
    if offset < 0 or size < 0 or offset + size > len(data):
        raise SystemExit(
            f"retail Starfield.exe has an out-of-range {label}: "
            f"offset=0x{offset:X}, size={size}, file_size={len(data)}"
        )


def read_retail_pe_metadata(data: bytes) -> RetailPeMetadata:
    require_range(data, 0, 0x40, "DOS header")
    if data[:2] != b"MZ":
        raise SystemExit("pinned retail Starfield.exe has no MZ signature")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    require_range(data, pe_offset, 24, "PE header")
    if data[pe_offset : pe_offset + 4] != b"PE\0\0":
        raise SystemExit("pinned retail Starfield.exe has no PE signature")

    section_count = struct.unpack_from("<H", data, pe_offset + 6)[0]
    coff_timestamp = struct.unpack_from("<I", data, pe_offset + 8)[0]
    optional_size = struct.unpack_from("<H", data, pe_offset + 20)[0]
    optional_offset = pe_offset + 24
    require_range(data, optional_offset, optional_size, "optional header")
    optional_magic = struct.unpack_from("<H", data, optional_offset)[0]
    if optional_magic != 0x20B:
        raise SystemExit(
            "pinned retail Starfield.exe is not the expected PE32+ executable"
        )

    section_offset = optional_offset + optional_size
    require_range(data, section_offset, section_count * 40, "section table")
    sections: list[tuple[int, int, int, int]] = []
    for index in range(section_count):
        entry = section_offset + index * 40
        virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from(
            "<IIII", data, entry + 8
        )
        sections.append((virtual_address, virtual_size, raw_pointer, raw_size))

    def rva_to_file_offset(rva: int, size: int) -> int:
        for virtual_address, _virtual_size, raw_pointer, raw_size in sections:
            if virtual_address <= rva and rva + size <= virtual_address + raw_size:
                result = raw_pointer + (rva - virtual_address)
                require_range(data, result, size, f"RVA 0x{rva:X}")
                return result
        raise SystemExit(f"retail Starfield.exe RVA 0x{rva:X} is not file-backed")

    debug_directory_entry = optional_offset + 112 + 6 * 8
    require_range(data, debug_directory_entry, 8, "debug data-directory entry")
    debug_rva, debug_size = struct.unpack_from("<II", data, debug_directory_entry)
    if debug_size == 0 or debug_size % 28 != 0:
        raise SystemExit(
            f"unexpected retail Starfield.exe debug-directory size: {debug_size}"
        )
    debug_offset = rva_to_file_offset(debug_rva, debug_size)
    codeview: list[tuple[str, int, str]] = []
    for entry in range(debug_offset, debug_offset + debug_size, 28):
        debug_type = struct.unpack_from("<I", data, entry + 12)[0]
        payload_size = struct.unpack_from("<I", data, entry + 16)[0]
        payload_offset = struct.unpack_from("<I", data, entry + 24)[0]
        if debug_type != 2:
            continue
        require_range(data, payload_offset, payload_size, "CodeView payload")
        payload = data[payload_offset : payload_offset + payload_size]
        if len(payload) < 24 or payload[:4] != b"RSDS":
            raise SystemExit("retail Starfield.exe has an unsupported CodeView payload")
        guid = str(uuid.UUID(bytes_le=payload[4:20]))
        age = struct.unpack_from("<I", payload, 20)[0]
        path_bytes = payload[24:].split(b"\0", 1)[0]
        pdb_path = path_bytes.decode("utf-8", errors="strict")
        codeview.append((guid, age, pdb_path))
    if len(codeview) != 1:
        raise SystemExit(
            f"expected one retail Starfield.exe CodeView entry, found {len(codeview)}"
        )

    fixed_signature = struct.pack("<I", 0xFEEF04BD)
    fixed_versions: list[tuple[int, int, int, int]] = []
    cursor = 0
    while True:
        cursor = data.find(fixed_signature, cursor)
        if cursor < 0:
            break
        if cursor + 52 <= len(data):
            fields = struct.unpack_from("<13I", data, cursor)
            if fields[1] == 0x00010000:
                file_version_ms, file_version_ls = fields[2], fields[3]
                fixed_versions.append(
                    (
                        file_version_ms >> 16,
                        file_version_ms & 0xFFFF,
                        file_version_ls >> 16,
                        file_version_ls & 0xFFFF,
                    )
                )
        cursor += 4
    if len(fixed_versions) != 1:
        raise SystemExit(
            "expected one VS_FIXEDFILEINFO in retail Starfield.exe, found "
            f"{len(fixed_versions)}"
        )

    pdb_guid, pdb_age, pdb_path = codeview[0]
    return RetailPeMetadata(
        fixed_versions[0], coff_timestamp, pdb_guid, pdb_age, pdb_path
    )


def read_retail_esm_header(path: Path) -> RetailEsmHeader:
    with path.open("rb") as stream:
        header = stream.read(42)
    if len(header) != 42 or header[:4] != b"TES4":
        raise SystemExit(f"pinned retail master has no 24-byte TES4 header: {path}")
    if header[24:28] != b"HEDR" or struct.unpack_from("<H", header, 28)[0] != 12:
        raise SystemExit(f"pinned retail master has no 12-byte leading HEDR: {path}")
    version_bits, record_count, next_object_id = struct.unpack_from("<III", header, 30)
    return RetailEsmHeader(version_bits, record_count, next_object_id)


def scan_retail_ctda(path: Path) -> RetailCtdaCensus:
    record_count = 0
    group_count = 0
    compressed_record_count = 0
    compressed_ctda_count = 0
    widths: collections.Counter[int] = collections.Counter()
    observed_indices: set[int] = set()
    parameter3: collections.Counter[int] = collections.Counter()

    with path.open("rb") as stream, mmap.mmap(
        stream.fileno(), 0, access=mmap.ACCESS_READ
    ) as data:
        position = 0
        group_ends = [len(data)]
        while group_ends:
            while group_ends and position == group_ends[-1]:
                group_ends.pop()
            if not group_ends:
                break
            enclosing_end = group_ends[-1]
            if position > enclosing_end or position + 24 > enclosing_end:
                raise SystemExit(
                    f"invalid record/group boundary in {path} at 0x{position:X}"
                )

            signature = data[position : position + 4]
            data_size = struct.unpack_from("<I", data, position + 4)[0]
            if signature == b"GRUP":
                group_end = position + data_size
                if data_size < 24 or group_end > enclosing_end:
                    raise SystemExit(
                        f"invalid GRUP size in {path} at 0x{position:X}: {data_size}"
                    )
                group_count += 1
                group_ends.append(group_end)
                position += 24
                continue

            record_end = position + 24 + data_size
            if record_end > enclosing_end:
                raise SystemExit(
                    f"record {signature!r} overruns its group in {path} "
                    f"at 0x{position:X}"
                )
            record_count += 1
            flags = struct.unpack_from("<I", data, position + 8)[0]
            record_data = data[position + 24 : record_end]
            is_compressed = (flags & 0x00040000) != 0
            if is_compressed:
                compressed_record_count += 1
                if len(record_data) < 4:
                    raise SystemExit(
                        f"compressed record is too short in {path} at 0x{position:X}"
                    )
                expected_size = struct.unpack_from("<I", record_data, 0)[0]
                compressed_payload = record_data[4:]
                decoder = zlib.decompressobj()
                try:
                    record_data = decoder.decompress(compressed_payload) + decoder.flush()
                except zlib.error as error:
                    raise SystemExit(
                        f"cannot decompress record in {path} at 0x{position:X}: {error}"
                    ) from error
                if not decoder.eof or decoder.unconsumed_tail or decoder.unused_data:
                    raise SystemExit(
                        f"invalid zlib framing in {path} at 0x{position:X}: "
                        f"eof={decoder.eof}, unconsumed={len(decoder.unconsumed_tail)}, "
                        f"trailing={len(decoder.unused_data)}"
                    )
                if len(record_data) != expected_size:
                    raise SystemExit(
                        f"decompressed-size mismatch in {path} at 0x{position:X}: "
                        f"expected {expected_size}, found {len(record_data)}"
                    )

            cursor = 0
            extended_size: int | None = None
            while cursor < len(record_data):
                if cursor + 6 > len(record_data):
                    raise SystemExit(
                        f"truncated subrecord header in {path} record {signature!r} "
                        f"at 0x{position:X}"
                    )
                subrecord_signature = record_data[cursor : cursor + 4]
                subrecord_size = struct.unpack_from("<H", record_data, cursor + 4)[0]
                cursor += 6
                if subrecord_signature == b"XXXX":
                    if extended_size is not None or subrecord_size != 4:
                        raise SystemExit(
                            f"invalid XXXX subrecord in {path} record {signature!r} "
                            f"at 0x{position:X}"
                        )
                    if cursor + 4 > len(record_data):
                        raise SystemExit(
                            f"truncated XXXX body in {path} at 0x{position:X}"
                        )
                    extended_size = struct.unpack_from("<I", record_data, cursor)[0]
                    cursor += 4
                    continue

                physical_size = (
                    extended_size if extended_size is not None else subrecord_size
                )
                extended_size = None
                body_end = cursor + physical_size
                if body_end > len(record_data):
                    raise SystemExit(
                        f"subrecord {subrecord_signature!r} overruns record "
                        f"{signature!r} in {path} at 0x{position:X}"
                    )
                if subrecord_signature == b"CTDA":
                    widths[physical_size] += 1
                    if is_compressed:
                        compressed_ctda_count += 1
                    body = record_data[cursor:body_end]
                    if physical_size >= 10:
                        observed_indices.add(struct.unpack_from("<H", body, 8)[0])
                    if physical_size >= 32:
                        parameter3[struct.unpack_from("<i", body, 28)[0]] += 1
                cursor = body_end
            if extended_size is not None:
                raise SystemExit(
                    f"dangling XXXX subrecord in {path} at record 0x{position:X}"
                )
            position = record_end

    minimum = min(parameter3) if parameter3 else None
    maximum = max(parameter3) if parameter3 else None
    return RetailCtdaCensus(
        record_count,
        group_count,
        compressed_record_count,
        compressed_ctda_count,
        tuple(sorted(widths.items())),
        frozenset(observed_indices),
        tuple(sorted(parameter3.items())),
        minimum,
        maximum,
    )


def combine_censuses(censuses: list[RetailCtdaCensus]) -> RetailCtdaCensus:
    widths: collections.Counter[int] = collections.Counter()
    parameter3: collections.Counter[int] = collections.Counter()
    observed_indices: set[int] = set()
    for census in censuses:
        widths.update(dict(census.width_counts))
        parameter3.update(dict(census.parameter3_counts))
        observed_indices.update(census.observed_indices)
    return RetailCtdaCensus(
        sum(item.record_count for item in censuses),
        sum(item.group_count for item in censuses),
        sum(item.compressed_record_count for item in censuses),
        sum(item.compressed_ctda_count for item in censuses),
        tuple(sorted(widths.items())),
        frozenset(observed_indices),
        tuple(sorted(parameter3.items())),
        min(parameter3) if parameter3 else None,
        max(parameter3) if parameter3 else None,
    )


def require_retail_census(
    label: str,
    census: RetailCtdaCensus,
    expected: dict[str, int],
    xedit_indices: set[int],
) -> None:
    parameter3 = dict(census.parameter3_counts)
    actual = {
        "records": census.record_count,
        "groups": census.group_count,
        "compressed_records": census.compressed_record_count,
        "ctda": census.ctda_count,
        "distinct_indices": len(census.observed_indices),
        "maximum_index": max(census.observed_indices),
        "parameter3_minus_one": parameter3.get(-1, 0),
        "parameter3_zero": parameter3.get(0, 0),
        "parameter3_other": census.ctda_count
        - parameter3.get(-1, 0)
        - parameter3.get(0, 0),
        "parameter3_minimum": census.parameter3_minimum,
        "parameter3_maximum": census.parameter3_maximum,
    }
    if actual != expected:
        raise SystemExit(
            f"unexpected {label} retail CTDA census: expected {expected}, found {actual}"
        )
    if dict(census.width_counts) != {32: expected["ctda"]}:
        raise SystemExit(
            f"{label} retail CTDA widths are not exclusively 32 bytes: "
            f"{dict(census.width_counts)}"
        )
    if census.compressed_ctda_count != 0:
        raise SystemExit(
            f"{label} unexpectedly contains {census.compressed_ctda_count} CTDAs "
            "inside compressed records"
        )
    unexpected_indices = sorted(census.observed_indices - xedit_indices)
    if unexpected_indices:
        raise SystemExit(
            f"{label} retail CTDAs use indices absent from the pinned xEdit table: "
            f"{unexpected_indices}"
        )


def require_retail_name_evidence(
    executable: bytes, conditions: list[ConditionFunction]
) -> None:
    def contains_standalone_ascii_string(value: str) -> bool:
        token = value.encode("ascii") + b"\0"
        offset = 0
        while True:
            offset = executable.find(token, offset)
            if offset < 0:
                return False
            if offset == 0 or executable[offset - 1] == 0:
                return True
            offset += 1

    missing_exact = {
        condition.index
        for condition in conditions
        if not contains_standalone_ascii_string(condition.name)
    }
    if missing_exact != {954, 961}:
        raise SystemExit(
            "unexpected retail Starfield.exe exact condition-name coverage: "
            f"missing raw xEdit rows {sorted(missing_exact)}"
        )
    if b"GetQuestStarting >> %0.2f\0" not in executable:
        raise SystemExit("retail Starfield.exe lost the GetQuestStarting diagnostic string")
    if b"GetGamePlayOptionCurrentValue" in executable:
        raise SystemExit(
            "retail Starfield.exe unexpectedly contains xEdit's GamePlay spelling"
        )
    if not contains_standalone_ascii_string("GetGameplayOptionCurrentValue"):
        raise SystemExit(
            "retail Starfield.exe lost its Gameplay spelling of the condition name"
        )


def verify_retail_install(
    install: Path,
    steam_manifest: Path | None,
    verify_corpus: bool,
    conditions: list[ConditionFunction],
) -> None:
    install = install.resolve()
    executable_path = install / "Starfield.exe"
    data_directory = install / "Data"
    base_master_path = data_directory / "Starfield.esm"
    manifest_path = (
        steam_manifest.resolve()
        if steam_manifest is not None
        else install.parent.parent / f"appmanifest_{EXPECTED_RETAIL_STEAM_APP_ID}.acf"
    )

    app_id = read_appmanifest_value(manifest_path, "appid")
    build_id = read_appmanifest_value(manifest_path, "buildid")
    if app_id != EXPECTED_RETAIL_STEAM_APP_ID:
        raise SystemExit(
            f"unexpected Steam app id in {manifest_path}: expected "
            f"{EXPECTED_RETAIL_STEAM_APP_ID}, found {app_id}"
        )
    if build_id != EXPECTED_RETAIL_STEAM_BUILD_ID:
        raise SystemExit(
            f"unexpected Steam build id in {manifest_path}: expected "
            f"{EXPECTED_RETAIL_STEAM_BUILD_ID}, found {build_id}"
        )

    require_retail_artifact(executable_path, EXPECTED_RETAIL_EXE)
    require_retail_artifact(
        base_master_path, EXPECTED_RETAIL_CORPUS_ARTIFACTS["Starfield.esm"]
    )
    executable = executable_path.read_bytes()
    pe = read_retail_pe_metadata(executable)
    expected_pe = RetailPeMetadata(
        EXPECTED_RETAIL_EXE_VERSION,
        EXPECTED_RETAIL_EXE_COFF_TIMESTAMP,
        EXPECTED_RETAIL_PDB_GUID,
        EXPECTED_RETAIL_PDB_AGE,
        EXPECTED_RETAIL_PDB_PATH,
    )
    if pe != expected_pe:
        raise SystemExit(
            f"unexpected pinned retail Starfield.exe metadata: expected {expected_pe}, "
            f"found {pe}"
        )
    require_retail_name_evidence(executable, conditions)

    esm_header = read_retail_esm_header(base_master_path)
    if esm_header.version_bits != EXPECTED_RETAIL_ESM_HEDR_VERSION_BITS:
        raise SystemExit(
            "unexpected retail Starfield.esm HEDR version bits: expected "
            f"0x{EXPECTED_RETAIL_ESM_HEDR_VERSION_BITS:08X}, found "
            f"0x{esm_header.version_bits:08X}"
        )
    xedit_indices = {condition.index for condition in conditions}
    base_census = scan_retail_ctda(base_master_path)
    if esm_header.declared_record_count != base_census.record_count:
        raise SystemExit(
            "retail Starfield.esm HEDR record count does not match structural census: "
            f"{esm_header.declared_record_count} vs {base_census.record_count}"
        )
    require_retail_census(
        "base Starfield.esm",
        base_census,
        EXPECTED_RETAIL_BASE_CENSUS,
        xedit_indices,
    )
    if 961 in base_census.observed_indices:
        raise SystemExit(
            "retail base unexpectedly exercises raw 961; revisit the conservative "
            "xEdit/retail display-name policy"
        )
    print(
        "PASS: retail Starfield.exe 1.16.244.0 / Steam build 23518663; "
        "Starfield.esm has 96,486 exact 32-byte CTDAs, 303 observed xEdit-subset "
        "indices, and max raw index 966"
    )

    symbol_names = {"starfield.pdb", "starfield.map"}
    matching_symbols = sorted(
        path
        for path in install.rglob("*")
        if path.is_file() and path.name.casefold() in symbol_names
    )
    if matching_symbols:
        print(
            "INFO: matching local symbol artifact(s) are present but are outside this "
            f"hash-pinned contract: {matching_symbols}"
        )
    else:
        print(
            "INFO: no Starfield.pdb or Starfield.map is present; the PE retains only "
            f"CodeView {EXPECTED_RETAIL_PDB_GUID}, age {EXPECTED_RETAIL_PDB_AGE}"
        )

    if not verify_corpus:
        return

    censuses: list[RetailCtdaCensus] = []
    for name, expected_artifact in EXPECTED_RETAIL_CORPUS_ARTIFACTS.items():
        path = data_directory / name
        if name == "Starfield.esm":
            census = base_census
        else:
            require_retail_artifact(path, expected_artifact)
            census = scan_retail_ctda(path)
        censuses.append(census)
    corpus_census = combine_censuses(censuses)
    require_retail_census(
        "14-master installed corpus",
        corpus_census,
        EXPECTED_RETAIL_CORPUS_CENSUS,
        xedit_indices,
    )
    if 961 in corpus_census.observed_indices:
        raise SystemExit(
            "retail corpus unexpectedly exercises raw 961; revisit the conservative "
            "xEdit/retail display-name policy"
        )
    print(
        "PASS: pinned 14-master corpus has 124,096 exact 32-byte CTDAs, "
        "310 observed xEdit-subset indices, and max raw index 966"
    )


def parse_xedit_conditions(path: Path) -> list[ConditionFunction]:
    text = path.read_text(encoding="utf-8-sig", errors="strict")
    if "Mozilla Public License" not in text or "v. 2.0" not in text:
        raise SystemExit("xEdit SF1 source no longer carries its MPL-2.0 notice")
    if "{6}  wbFormID('Form')" not in text:
        raise SystemExit("xEdit SF1 ptForm is no longer represented by a FormID union arm")
    if "{30} wbFormIDCkNoReach('Actor Value', [AVIF])" not in text:
        raise SystemExit("xEdit SF1 ptActorValue is no longer represented by an AVIF FormID arm")

    enum_start = text.index("TConditionParameterType = (")
    enum_end = text.index(");", enum_start)
    parameter_types = tuple(
        re.findall(r"\bpt[A-Za-z0-9_]+\b", text[enum_start:enum_end])
    )
    if parameter_types != EXPECTED_PARAMETER_TYPES:
        raise SystemExit(
            "xEdit SF1 condition-parameter taxonomy changed: expected "
            f"{EXPECTED_PARAMETER_TYPES}, found {parameter_types}"
        )

    table_start = text.index("wbConditionFunctions : array[0..609]")
    table_end = text.index("function wbConditionDescFromIndex", table_start)
    table = text[table_start:table_end]
    entry_pattern = re.compile(
        r"\(Index:\s*(\d+);\s*Name:\s*'((?:''|[^'])+)'(?P<body>[^\r\n]*)\)",
        re.IGNORECASE,
    )
    parameter_pattern = re.compile(
        r"ParamType([123])\s*:\s*(pt[A-Za-z0-9_]+)", re.IGNORECASE
    )
    conditions: list[ConditionFunction] = []
    for match in entry_pattern.finditer(table):
        declared = ["ptNone", "ptNone", "ptNone"]
        for parameter_match in parameter_pattern.finditer(match.group("body")):
            declared[int(parameter_match.group(1)) - 1] = parameter_match.group(2)
        conditions.append(
            ConditionFunction(
                int(match.group(1)),
                match.group(2).replace("''", "'"),
                tuple(declared),
            )
        )

    if len(conditions) != CONDITION_FUNCTION_COUNT:
        raise SystemExit(
            f"expected {CONDITION_FUNCTION_COUNT} xEdit conditions, found {len(conditions)}"
        )
    if len({condition.index for condition in conditions}) != len(conditions):
        raise SystemExit("xEdit SF1 conditions contain duplicate raw indices")
    if len({condition.name.casefold() for condition in conditions}) != len(conditions):
        raise SystemExit("xEdit SF1 conditions contain duplicate names")
    if conditions != sorted(conditions, key=lambda item: item.index):
        raise SystemExit("xEdit SF1 conditions are not sorted by raw index")
    if max(condition.index for condition in conditions) != MAX_CONDITION_INDEX:
        raise SystemExit("unexpected maximum SF1 condition index")
    if any(condition.index >= 0x1000 for condition in conditions):
        raise SystemExit("unexpected SF1 raw condition index at or above 0x1000")

    used_types = {
        item.casefold() for condition in conditions for item in condition.param_types
    }
    expected_types = {item.casefold() for item in EXPECTED_PARAMETER_TYPES}
    if used_types != expected_types:
        raise SystemExit(f"unexpected used SF1 parameter types: {sorted(used_types)}")

    eligible_functions = sum(
        any(item.casefold() in TYPE_OVERRIDE_ELIGIBLE_TYPES for item in condition.param_types[:2])
        for condition in conditions
    )
    eligible_slots = sum(
        item.casefold() in TYPE_OVERRIDE_ELIGIBLE_TYPES
        for condition in conditions
        for item in condition.param_types[:2]
    )
    if eligible_functions != TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT:
        raise SystemExit(
            f"expected {TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT} Type-override functions, "
            f"found {eligible_functions}"
        )
    if eligible_slots != TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT:
        raise SystemExit(
            f"expected {TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT} Type-override slots, "
            f"found {eligible_slots}"
        )

    by_index = {condition.index: condition for condition in conditions}
    expected_controls = {
        1: ("GetDistance", ("ptReference", "ptNone", "ptNone")),
        14: ("GetValue", ("ptActorValue", "ptNone", "ptNone")),
        161: ("GetIsCurrentPackage", ("ptPackage", "ptNone", "ptNone")),
        407: ("GetVATSValue", ("ptInteger", "ptInteger", "ptNone")),
        576: ("GetEventData", ("ptEvent", "ptEventData", "ptNone")),
        819: ("GetActionDataForm", ("ptForm", "ptNone", "ptNone")),
        904: ("IsInsidePrimitiveTopAndBottom", ("ptKeyword", "ptNone", "ptNone")),
        966: ("AreVehiclesUnlocked", ("ptNone", "ptNone", "ptNone")),
    }
    actual_controls = {
        index: (by_index[index].name, by_index[index].param_types)
        for index in expected_controls
    }
    if actual_controls != expected_controls:
        raise SystemExit(
            f"unexpected SF1 condition controls: expected {expected_controls}, "
            f"found {actual_controls}"
        )
    return conditions


def condition_kind(parameter_type: str) -> str | None:
    normalized = parameter_type.casefold()
    if normalized == "ptnone":
        return None
    if normalized in NUMERIC_TYPES:
        return "ConditionParamKind.Numeric"
    if normalized in FORM_ID_TYPES:
        return "ConditionParamKind.FormId"
    raise ValueError(parameter_type)


def csharp_string(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\r", "\\r")
        .replace("\n", "\\n")
    )


def generate_csharp(conditions: list[ConditionFunction]) -> str:
    lines = [
        "// <auto-generated>",
        "// Generated by tools/extract_starfield_condition_functions.py from a pinned community oracle.",
        f"// xEdit source commit: {XEDIT_SOURCE_COMMIT}",
        f"// xEdit wbDefinitionsSF1.pas SHA-256: {EXPECTED_XEDIT_SHA256}",
        "// Provenance: xEdit source under MPL-2.0. These 610 rows provide community condition",
        "// names and coarse CTDA parameter-storage kinds; no engine command/callback identity is claimed.",
        "// Retail layout/usage cross-check: Steam build 23518663; Starfield.exe 1.16.244.0;",
        "// EXE SHA-256: 7E9ADB1414A8E1B325E5E1F097B9B17B78DEB7EEBEDA37A333351A43A60F9D28.",
        "// Starfield.esm SHA-256: 1DABED00C3F4282DD3BB54D2E9601E40B577D8742D078B7CCEF203ADBFEF0DA7;",
        "// HEDR 0.96; its 96,486 CTDAs, and all 124,096 CTDAs in the pinned 14-master",
        "// corpus, are 32 bytes.",
        "// The corpus exercises 310 raw indices, all an xEdit-table subset, including max index 966.",
        "// It does not prove the 300 unobserved rows, names/types, command membership, or callbacks.",
        "// No matching PDB/map was installed; the EXE retains CodeView GUID",
        "// f663cdf9-e091-4639-8547-2af3e0fcebf8 age 1 for Starfield.pdb.",
        "// Retail spells GetGameplayOptionCurrentValue; this table conservatively retains xEdit's",
        "// GetGamePlayOptionCurrentValue because executable string presence does not prove raw index 961.",
        "// This raw-index condition table is not a Starfield script-opcode or Papyrus command table.",
        "// </auto-generated>",
        "",
        "using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;",
        "",
        "namespace BethesdaMultitool.Core.Formats.Esm.Script;",
        "",
        "internal static class StarfieldConditionFunctionTable",
        "{",
        f"    internal const int ConditionFunctionCount = {CONDITION_FUNCTION_COUNT};",
        f"    internal const int ParameterTypeCount = {PARAMETER_TYPE_COUNT};",
        f"    internal const int TypeOverrideEligibleFunctionCount = {TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT};",
        f"    internal const int TypeOverrideEligibleSlotCount = {TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT};",
        f"    internal const ushort MaximumRawIndex = {MAX_CONDITION_INDEX};",
        "",
        "    // Raw CTDA index -> xEdit-facing condition definition. Script-call metadata remains empty",
        "    // because this source does not prove a Starfield command table or engine callback subset.",
        "    internal static readonly Dictionary<ushort, ScriptFunctionDef> ConditionFunctions = new()",
        "    {",
    ]
    for condition in conditions:
        lines.append(
            f'        [0x{condition.index:04X}] = new("{csharp_string(condition.name)}", "", '
            "false, [], true),"
        )
    lines.extend(
        [
            "    };",
            "",
            "    // Base storage kinds from xEdit's concrete union arms. Null slots fail closed raw.",
            "    internal static readonly Dictionary<ushort, ConditionParamKind?[]> ConditionParamKinds = new()",
            "    {",
        ]
    )
    for condition in conditions:
        kinds = [condition_kind(item) for item in condition.param_types[:2]]
        last = max((index for index, item in enumerate(kinds) if item is not None), default=-1)
        rendered = ", ".join(item or "null" for item in kinds[: last + 1])
        lines.append(f"        [0x{condition.index:04X}] = [{rendered}],")
    lines.extend(
        [
            "    };",
            "",
            "    // SF1 xEdit applies Type.UseAliases/UsePackdata only to declared Reference, Actor,",
            "    // or Package slots. This raw-index metadata is separate from the base FormID kind.",
            "    internal static readonly Dictionary<ushort, bool[]> ConditionTypeOverrideEligibility = new()",
            "    {",
        ]
    )
    for condition in conditions:
        eligible = [
            item.casefold() in TYPE_OVERRIDE_ELIGIBLE_TYPES
            for item in condition.param_types[:2]
        ]
        if not any(eligible):
            continue
        last = max(index for index, item in enumerate(eligible) if item)
        rendered = ", ".join("true" if item else "false" for item in eligible[: last + 1])
        lines.append(f"        [0x{condition.index:04X}] = [{rendered}],")
    lines.extend(["    };", "}", ""])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--xedit", type=Path, default=DEFAULT_XEDIT)
    parser.add_argument("-o", "--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify-only", action="store_true")
    parser.add_argument(
        "--verify-retail",
        type=Path,
        metavar="INSTALL",
        help=(
            "read-only, hash-gated verification of Starfield.exe and "
            "Data/Starfield.esm; implies generated-output verification"
        ),
    )
    parser.add_argument(
        "--verify-retail-corpus",
        action="store_true",
        help="also hash and structurally scan the pinned 14-master installed corpus",
    )
    parser.add_argument(
        "--steam-manifest",
        type=Path,
        help=(
            "Steam appmanifest_1716740.acf path; inferred from INSTALL when omitted"
        ),
    )
    args = parser.parse_args()
    if args.verify_retail_corpus and args.verify_retail is None:
        parser.error("--verify-retail-corpus requires --verify-retail")
    if args.steam_manifest is not None and args.verify_retail is None:
        parser.error("--steam-manifest requires --verify-retail")

    require_xedit_hash(args.xedit)
    conditions = parse_xedit_conditions(args.xedit)
    generated = generate_csharp(conditions)

    if args.verify_only or args.verify_retail is not None:
        if not args.output.is_file():
            raise SystemExit(f"generated output is missing: {args.output}")
        actual = args.output.read_text(encoding="utf-8")
        if actual != generated:
            raise SystemExit(f"generated output is stale: {args.output}")
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(generated, encoding="utf-8", newline="\n")

    print("PASS: 610 xEdit SF1 condition rows; 67 parameter types; max raw index 966")
    if args.verify_retail is not None:
        verify_retail_install(
            args.verify_retail,
            args.steam_manifest,
            args.verify_retail_corpus,
            conditions,
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
