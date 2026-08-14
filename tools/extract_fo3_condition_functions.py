"""Generate Fallout 3's exact retail CTDA condition-function map.

The hash-pinned PC executable contains one sequential 0x28-byte game-command
array.  A row is CTDA-capable only when its callback pointer at
``SCRIPT_FUNCTION + 0x20`` is non-null.  That field name/layout is corroborated
by the FNV PDB; no FO3 PDB is used.  Names, short names, reference flags,
parameter display names, raw parameter types, and optional flags are read from
that Fallout 3 row; the output is not produced by filtering the FNV table.

The pinned xEdit FO3 definition is an independent community cross-check: its
237 base-game rows must match the executable's exact keys, names, and first two
parameter kinds.  Its separately labeled seven-row FOSE block is validated and
excluded.

The local retail executable is intentionally not distributable.  This
generator and its deterministic C# output are tracked, so normal builds do not
depend on the oracle.

Usage:
    python tools/extract_fo3_condition_functions.py
    python tools/extract_fo3_condition_functions.py --verify-only
    python tools/extract_fo3_condition_functions.py --exe <Fallout3.exe>
"""

from __future__ import annotations

import argparse
import hashlib
import re
import struct
import sys
import uuid
from dataclasses import dataclass
from pathlib import Path, PureWindowsPath


REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_EXE = (
    REPO_ROOT
    / "Sample"
    / "Full_Builds"
    / "Fallout 3 (PC Final)"
    / "Fallout3.exe"
)
DEFAULT_OUTPUT = (
    REPO_ROOT
    / "src"
    / "BethesdaMultitool"
    / "Core"
    / "Formats"
    / "Esm"
    / "Script"
    / "Fallout3ConditionFunctionTable.Generated.cs"
)
DEFAULT_XEDIT = (
    REPO_ROOT
    / "Sample"
    / "Reference_Code"
    / "TES5Edit"
    / "Core"
    / "wbDefinitionsFO3.pas"
)

EXPECTED_EXE_SHA256 = (
    "C3F97C2255FA041A851C17CF372D69AAADD8694E2DC4230BA556001BBFBD2F3E"
)
EXPECTED_EXE_SIZE = 16_855_040
EXPECTED_COFF_TIMESTAMP = 0x60A80E56
EXPECTED_FILE_VERSION = "1.7.0.4"
EXPECTED_IMAGE_BASE = 0x00400000
EXPECTED_GAME_ARRAY_FILE_OFFSET = 0x00D09D88
EXPECTED_CODEVIEW_PDB_NAME = "Fallout.pdb"
EXPECTED_CODEVIEW_GUID = "fa958b2a-dde8-42d1-b407-b864abf11685"
EXPECTED_CODEVIEW_AGE = 2

XEDIT_SOURCE_COMMIT = "e0e529a2d473756520f2d41f72c24dea0cf5ee0d"
EXPECTED_XEDIT_SHA256 = (
    "EF6F8DF070B5E7C7B4A551AD2A633A329DA9BEEFE72A995DACA61F8404A16A96"
)

FUNCTION_SIZE = 0x28
PARAMETER_SIZE = 0x0C
GAME_OPCODE_BASE = 0x1000
EXPECTED_GAME_COMMAND_COUNT = 568
EXPECTED_SENTINEL_SLOT_COUNT = 1
EXPECTED_ENGINE_SLOT_COUNT = EXPECTED_GAME_COMMAND_COUNT + EXPECTED_SENTINEL_SLOT_COUNT
EXPECTED_SENTINEL_OPCODE = GAME_OPCODE_BASE + EXPECTED_GAME_COMMAND_COUNT
EXPECTED_SENTINEL_NAME = "ADD NEW FUNCTIONS BEFORE THIS ONE!!!"
EXPECTED_CONDITION_COUNT = 237
EXPECTED_MAX_CONDITION_INDEX = 0x022E
EXPECTED_FOSE_FUNCTIONS = {
    1024: ("GetFOSEVersion", None, None),
    1025: ("GetFOSERevision", None, None),
    1028: ("GetWeight", "ptInventoryObject", None),
    1082: ("IsKeyPressed", "ptInteger", None),
    1165: ("GetWeaponHasScope", "ptInventoryObject", None),
    1166: ("IsControlPressed", "ptInteger", None),
    1213: ("GetFOSEBeta", None, None),
}

# Raw classic SCRIPT_PARAM_TYPE ids.  The pinned FO3 executable supplies every
# emitted id and parameter display string.  Symbolic labels use the shared
# classic enum whose numbering is independently pinned by the FNV PDB.
PARAM_TYPE_NAMES = {
    0: "Char",
    1: "Int",
    2: "Float",
    3: "InventoryObject",
    4: "ObjectRef",
    5: "ActorValue",
    6: "Actor",
    7: "SpellItem",
    8: "Axis",
    9: "Cell",
    10: "AnimGroup",
    11: "MagicItem",
    12: "Sound",
    13: "Topic",
    14: "Quest",
    15: "Race",
    16: "Class",
    17: "Faction",
    18: "Sex",
    19: "Global",
    20: "FurnitureOrFormList",
    21: "Object",
    22: "ScriptVar",
    23: "Stage",
    24: "MapMarker",
    25: "ActorBase",
    26: "ContainerRef",
    27: "World",
    28: "CrimeType",
    29: "Package",
    30: "CombatStyle",
    31: "MagicEffect",
    32: "FormType",
    33: "Weather",
    34: "Npc",
    35: "Owner",
    36: "ShaderEffect",
    37: "FormList",
    38: "MenuIcon",
    39: "Perk",
    40: "Note",
    41: "MiscStat",
    42: "ImageSpaceMod",
    43: "ImageSpace",
    44: "VatsValue",
    45: "VatsValueData",
    46: "VoiceType",
    47: "EncounterZone",
    48: "IdleForm",
    49: "Message",
    50: "InvObjectOrFormList",
    51: "Alignment",
    52: "EquipType",
    53: "ObjectOrFormList",
    54: "Music",
    55: "CritStage",
    56: "NpcOrLevChar",
    57: "CreaOrLevCrea",
    58: "LevChar",
    59: "LevCrea",
    60: "LevItem",
    61: "Form",
}

EXPECTED_OBSERVED_PARAM_TYPES = frozenset(
    {
        0,
        1,
        2,
        3,
        4,
        5,
        6,
        7,
        8,
        9,
        10,
        11,
        12,
        13,
        14,
        15,
        16,
        17,
        18,
        19,
        20,
        21,
        22,
        23,
        24,
        25,
        26,
        27,
        28,
        29,
        30,
        31,
        32,
        33,
        35,
        36,
        37,
        39,
        40,
        41,
        42,
        43,
        46,
        47,
        48,
        49,
        50,
        51,
        52,
        53,
        54,
        55,
        56,
        57,
        58,
        59,
        60,
        61,
    }
)

# xEdit describes CTDA storage semantics rather than script-source signatures.
# Most kinds map one-to-one to a classic engine raw id; BaseObject legitimately
# corresponds to either Object or ObjectOrFormList in the executable.  The
# specialized xEdit enum selectors are stored through engine Int parameters.
XEDIT_PT_TO_ENGINE_RAW_TYPES = {
    "ptActor": frozenset({6}),
    "ptActorBase": frozenset({25}),
    "ptActorValue": frozenset({5}),
    "ptAlignment": frozenset({51}),
    "ptAxis": frozenset({8}),
    "ptBaseEffect": frozenset({31}),
    "ptBaseObject": frozenset({21, 53}),
    "ptBodyLocation": frozenset({1}),
    "ptCell": frozenset({9}),
    "ptClass": frozenset({16}),
    "ptCreatureType": frozenset({1}),
    "ptCrimeType": frozenset({28}),
    "ptCriticalStage": frozenset({55}),
    "ptEffectItem": frozenset({11}),
    "ptEncounterZone": frozenset({47}),
    "ptEquipType": frozenset({52}),
    "ptFaction": frozenset({17}),
    "ptFormList": frozenset({37}),
    "ptFormType": frozenset({32}),
    "ptFurniture": frozenset({20}),
    "ptGlobal": frozenset({19}),
    "ptIdleForm": frozenset({48}),
    "ptInteger": frozenset({1}),
    "ptInventoryObject": frozenset({50}),
    "ptMenuMode": frozenset({1}),
    "ptMiscStat": frozenset({41}),
    "ptNote": frozenset({40}),
    "ptOwner": frozenset({35}),
    "ptPackage": frozenset({29}),
    "ptPerk": frozenset({39}),
    "ptPlayerAction": frozenset({1}),
    "ptQuest": frozenset({14}),
    "ptQuestStage": frozenset({23}),
    "ptRace": frozenset({15}),
    "ptReference": frozenset({4}),
    "ptSex": frozenset({18}),
    "ptVATSValueFunction": frozenset({1}),
    "ptVATSValueParam": frozenset({1}),
    "ptVariableName": frozenset({22}),
    "ptVoiceType": frozenset({46}),
    "ptWeather": frozenset({33}),
    "ptWorldSpace": frozenset({27}),
}

XEDIT_CONDITION_ENTRY_RE = re.compile(
    r"\(Index:\s*(\d+);\s*Name:\s*'([^']+)'"
    r"(?:;\s*ParamType1:\s*(pt\w+))?"
    r"(?:;\s*ParamType2:\s*(pt\w+))?\s*\)",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class Section:
    name: str
    virtual_size: int
    virtual_address: int
    raw_size: int
    raw_pointer: int


@dataclass(frozen=True)
class Parameter:
    name: str
    raw_type: int
    optional: bool


@dataclass(frozen=True)
class Function:
    opcode: int
    name: str
    short_name: str
    is_reference: bool
    parameters: tuple[Parameter, ...]
    condition_pointer: int

    @property
    def is_condition(self) -> bool:
        return self.condition_pointer != 0


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def require_authoritative_input(path: Path) -> None:
    if not path.is_file():
        raise SystemExit(f"missing Fallout 3 retail executable: {path}")
    if path.stat().st_size != EXPECTED_EXE_SIZE:
        raise SystemExit(
            f"unsupported Fallout3.exe size for {path}:\n"
            f"  expected {EXPECTED_EXE_SIZE}\n  actual   {path.stat().st_size}"
        )
    actual = sha256(path)
    if actual != EXPECTED_EXE_SHA256:
        raise SystemExit(
            f"unsupported Fallout3.exe hash for {path}:\n"
            f"  expected {EXPECTED_EXE_SHA256}\n  actual   {actual}"
        )


def require_xedit_input(path: Path) -> None:
    if not path.is_file():
        raise SystemExit(f"missing xEdit FO3 definition: {path}")
    actual = sha256(path)
    if actual != EXPECTED_XEDIT_SHA256:
        raise SystemExit(
            f"unsupported wbDefinitionsFO3.pas hash for {path}:\n"
            f"  expected {EXPECTED_XEDIT_SHA256}\n  actual   {actual}"
        )


class PeImage:
    def __init__(self, path: Path):
        self.path = path
        self.data = path.read_bytes()
        try:
            pe_offset = struct.unpack_from("<I", self.data, 0x3C)[0]
            if self.data[pe_offset : pe_offset + 4] != b"PE\0\0":
                raise SystemExit(f"not a PE image: {path}")
            machine, section_count = struct.unpack_from("<HH", self.data, pe_offset + 4)
            timestamp = struct.unpack_from("<I", self.data, pe_offset + 8)[0]
            optional_size = struct.unpack_from("<H", self.data, pe_offset + 20)[0]
            self.optional_offset = pe_offset + 24
            magic = struct.unpack_from("<H", self.data, self.optional_offset)[0]
            self.image_base = struct.unpack_from("<I", self.data, self.optional_offset + 28)[0]
        except struct.error as error:
            raise SystemExit(f"truncated PE headers: {path}") from error

        if (
            machine != 0x014C
            or magic != 0x010B
            or self.image_base != EXPECTED_IMAGE_BASE
            or timestamp != EXPECTED_COFF_TIMESTAMP
        ):
            raise SystemExit(
                "Fallout3.exe is not the expected x86 PE32 image "
                f"(machine=0x{machine:X}, magic=0x{magic:X}, "
                f"base=0x{self.image_base:X}, timestamp=0x{timestamp:08X})"
            )

        self.sections: list[Section] = []
        section_offset = self.optional_offset + optional_size
        for index in range(section_count):
            offset = section_offset + index * 40
            try:
                name = self.data[offset : offset + 8].rstrip(b"\0").decode("ascii", "strict")
                virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from(
                    "<IIII", self.data, offset + 8
                )
            except (struct.error, UnicodeDecodeError) as error:
                raise SystemExit(f"invalid PE section header {index + 1}") from error
            if raw_pointer + raw_size > len(self.data):
                raise SystemExit(f"PE section {name!r} extends beyond the file")
            self.sections.append(
                Section(name, virtual_size, virtual_address, raw_size, raw_pointer)
            )

    def resolve_va(self, address: int) -> tuple[Section, int] | None:
        if address < self.image_base:
            return None
        relative = address - self.image_base
        for section in self.sections:
            extent = max(section.virtual_size, section.raw_size)
            if section.virtual_address <= relative < section.virtual_address + extent:
                delta = relative - section.virtual_address
                if delta >= section.raw_size:
                    return None
                offset = section.raw_pointer + delta
                if offset >= len(self.data):
                    return None
                return section, offset
        return None

    def read_u16(self, offset: int) -> int:
        try:
            return struct.unpack_from("<H", self.data, offset)[0]
        except struct.error as error:
            raise SystemExit(f"out-of-range u16 read at file offset 0x{offset:X}") from error

    def read_u32(self, offset: int) -> int:
        try:
            return struct.unpack_from("<I", self.data, offset)[0]
        except struct.error as error:
            raise SystemExit(f"out-of-range u32 read at file offset 0x{offset:X}") from error

    def read_c_string(self, address: int, *, maximum: int = 1024) -> str:
        if address == 0:
            return ""
        resolved = self.resolve_va(address)
        if resolved is None:
            raise SystemExit(f"unmapped string pointer 0x{address:08X}")
        _, offset = resolved
        end = self.data.find(b"\0", offset, min(len(self.data), offset + maximum))
        if end < 0:
            raise SystemExit(f"unterminated string pointer 0x{address:08X}")
        value = self.data[offset:end]
        if any(byte < 0x20 or byte > 0x7E for byte in value):
            raise SystemExit(f"non-ASCII string pointer 0x{address:08X}")
        return value.decode("ascii")

    def read_codeview_identity(self) -> tuple[str, str, int]:
        # PE32 data-directory index 6 is IMAGE_DIRECTORY_ENTRY_DEBUG.
        directory_offset = self.optional_offset + 96 + 6 * 8
        try:
            debug_rva, debug_size = struct.unpack_from("<II", self.data, directory_offset)
        except struct.error as error:
            raise SystemExit("truncated PE debug-directory entry") from error
        if debug_rva == 0 or debug_size == 0 or debug_size % 28 != 0:
            raise SystemExit(
                f"invalid PE debug directory RVA/size: 0x{debug_rva:X}/{debug_size}"
            )
        resolved_debug = self.resolve_va(self.image_base + debug_rva)
        if resolved_debug is None:
            raise SystemExit(f"unmapped PE debug directory RVA 0x{debug_rva:X}")
        _, debug_offset = resolved_debug

        identities: list[tuple[str, str, int]] = []
        for entry_offset in range(debug_offset, debug_offset + debug_size, 28):
            try:
                debug_type = struct.unpack_from("<I", self.data, entry_offset + 12)[0]
                data_size = struct.unpack_from("<I", self.data, entry_offset + 16)[0]
                raw_pointer = struct.unpack_from("<I", self.data, entry_offset + 24)[0]
            except struct.error as error:
                raise SystemExit("truncated IMAGE_DEBUG_DIRECTORY entry") from error
            if debug_type != 2:
                continue
            if raw_pointer + data_size > len(self.data) or data_size < 25:
                raise SystemExit("CodeView debug record extends beyond Fallout3.exe")
            record = self.data[raw_pointer : raw_pointer + data_size]
            if record[:4] != b"RSDS":
                raise SystemExit("Fallout3.exe CodeView record is not RSDS")
            guid = str(uuid.UUID(bytes_le=record[4:20]))
            age = struct.unpack_from("<I", record, 20)[0]
            path_end = record.find(b"\0", 24)
            if path_end < 0:
                raise SystemExit("unterminated CodeView PDB path")
            try:
                pdb_path = record[24:path_end].decode("ascii", "strict")
            except UnicodeDecodeError as error:
                raise SystemExit("non-ASCII CodeView PDB path") from error
            identities.append((PureWindowsPath(pdb_path).name, guid, age))

        if identities != [
            (EXPECTED_CODEVIEW_PDB_NAME, EXPECTED_CODEVIEW_GUID, EXPECTED_CODEVIEW_AGE)
        ]:
            raise SystemExit(
                "unexpected Fallout3.exe CodeView identity: "
                f"expected {EXPECTED_CODEVIEW_PDB_NAME}/"
                f"{EXPECTED_CODEVIEW_GUID}/age {EXPECTED_CODEVIEW_AGE}, "
                f"found {identities}"
            )
        return identities[0]


def find_game_array(image: PeImage) -> int:
    candidates: list[int] = []
    minimum_run = 8
    for section in image.sections:
        if section.name not in (".data", ".rdata"):
            continue
        start = section.raw_pointer
        limit = section.raw_pointer + section.raw_size - minimum_run * FUNCTION_SIZE
        for offset in range(start, limit + 1, 4):
            if all(
                image.read_u32(offset + slot * FUNCTION_SIZE + 8)
                == GAME_OPCODE_BASE + slot
                for slot in range(minimum_run)
            ):
                candidates.append(offset)

    if candidates != [EXPECTED_GAME_ARRAY_FILE_OFFSET]:
        raise SystemExit(
            "unexpected Fallout 3 game-command array candidates: "
            f"expected [0x{EXPECTED_GAME_ARRAY_FILE_OFFSET:X}], "
            f"found {[f'0x{item:X}' for item in candidates]}"
        )
    return candidates[0]


def extract_function(image: PeImage, offset: int, expected_opcode: int) -> Function:
    opcode = image.read_u32(offset + 8)
    if opcode != expected_opcode:
        raise SystemExit(
            f"non-sequential function table at opcode 0x{expected_opcode:04X}: "
            f"found 0x{opcode:04X}"
        )

    name = image.read_c_string(image.read_u32(offset))
    if not name:
        raise SystemExit(f"empty function name at opcode 0x{opcode:04X}")
    short_name_pointer = image.read_u32(offset + 4)
    short_name = image.read_c_string(short_name_pointer) if short_name_pointer else ""

    reference_raw = image.read_u16(offset + 0x10)
    if reference_raw not in (0, 1):
        raise SystemExit(
            f"invalid reference flag {reference_raw} at opcode 0x{opcode:04X}"
        )
    parameter_count = image.read_u16(offset + 0x12)
    parameter_pointer = image.read_u32(offset + 0x14)
    if parameter_count > 32:
        raise SystemExit(
            f"implausible parameter count {parameter_count} at opcode 0x{opcode:04X}"
        )
    if parameter_count and not parameter_pointer:
        raise SystemExit(f"null parameter pointer at opcode 0x{opcode:04X}")

    parameters: list[Parameter] = []
    if parameter_count:
        resolved_parameters = image.resolve_va(parameter_pointer)
        if resolved_parameters is None:
            raise SystemExit(
                f"unmapped parameter pointer 0x{parameter_pointer:08X} "
                f"at opcode 0x{opcode:04X}"
            )
        parameter_section, parameter_offset = resolved_parameters
        if parameter_section.name != ".data":
            raise SystemExit(
                f"parameter pointer for opcode 0x{opcode:04X} is in "
                f"{parameter_section.name}, not .data"
            )
        if parameter_offset + parameter_count * PARAMETER_SIZE > (
            parameter_section.raw_pointer + parameter_section.raw_size
        ):
            raise SystemExit(f"parameter array crosses .data at opcode 0x{opcode:04X}")

        for parameter_index in range(parameter_count):
            current = parameter_offset + parameter_index * PARAMETER_SIZE
            parameter_name = image.read_c_string(image.read_u32(current))
            if not parameter_name:
                raise SystemExit(
                    f"empty parameter name at opcode 0x{opcode:04X} "
                    f"parameter {parameter_index}"
                )
            raw_type = image.read_u32(current + 4)
            if raw_type not in PARAM_TYPE_NAMES:
                raise SystemExit(
                    f"unknown classic SCRIPT_PARAM_TYPE {raw_type} at opcode "
                    f"0x{opcode:04X} parameter {parameter_index}"
                )
            optional_raw = image.read_u32(current + 8)
            if optional_raw not in (0, 1):
                raise SystemExit(
                    f"invalid optional flag {optional_raw} at opcode 0x{opcode:04X} "
                    f"parameter {parameter_index}"
                )
            parameters.append(Parameter(parameter_name, raw_type, optional_raw != 0))

    condition_pointer = image.read_u32(offset + 0x20)
    if condition_pointer:
        resolved_condition = image.resolve_va(condition_pointer)
        if resolved_condition is None or resolved_condition[0].name != ".text":
            section_name = resolved_condition[0].name if resolved_condition else "unmapped"
            raise SystemExit(
                f"condition callback 0x{condition_pointer:08X} for opcode "
                f"0x{opcode:04X} is in {section_name}, not .text"
            )

    return Function(
        opcode=opcode,
        name=name,
        short_name=short_name,
        is_reference=reference_raw != 0,
        parameters=tuple(parameters),
        condition_pointer=condition_pointer,
    )


def extract_table(image: PeImage) -> tuple[list[Function], Function]:
    start = find_game_array(image)
    commands = [
        extract_function(image, start + slot * FUNCTION_SIZE, GAME_OPCODE_BASE + slot)
        for slot in range(EXPECTED_GAME_COMMAND_COUNT)
    ]
    sentinel_offset = start + EXPECTED_GAME_COMMAND_COUNT * FUNCTION_SIZE
    sentinel = extract_function(image, sentinel_offset, EXPECTED_SENTINEL_OPCODE)
    # The terminal row has pointers to its long-name and an empty short-name string only.
    # Its help, packed reference/count word, parameter, execute, compile, condition, and
    # trailing flag storage are all exactly zero in the pinned executable.
    zero_fields = (0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24)
    nonzero_fields = {
        field: image.read_u32(sentinel_offset + field)
        for field in zero_fields
        if image.read_u32(sentinel_offset + field) != 0
    }
    if nonzero_fields:
        raise SystemExit(
            "Fallout 3 terminal game-command row has nonzero storage: "
            f"{ {f'0x{field:X}': f'0x{value:08X}' for field, value in nonzero_fields.items()} }"
        )
    return commands, sentinel


def parse_xedit_entries(text: str) -> dict[int, tuple[str, str | None, str | None]]:
    # Remove Pascal brace comments so GetPlayerControlsDisabled's commented ParamType3..7
    # do not look like live members of the two-slot TConditionFunction record.
    without_brace_comments = re.sub(r"\{.*?\}", "", text, flags=re.DOTALL)
    entries: dict[int, tuple[str, str | None, str | None]] = {}
    for match in XEDIT_CONDITION_ENTRY_RE.finditer(without_brace_comments):
        index = int(match.group(1))
        if index in entries:
            raise SystemExit(f"duplicate xEdit FO3 condition index {index}")
        entries[index] = (match.group(2), match.group(3), match.group(4))
    return entries


def load_xedit_condition_functions(
    path: Path,
) -> tuple[
    dict[int, tuple[str, str | None, str | None]],
    dict[int, tuple[str, str | None, str | None]],
]:
    require_xedit_input(path)
    text = path.read_text(encoding="utf-8", errors="strict")
    array_start = text.find("wbConditionFunctions")
    if array_start < 0:
        raise SystemExit("wbConditionFunctions not found in wbDefinitionsFO3.pas")
    array_end = text.find("\n  );", array_start)
    if array_end < 0:
        raise SystemExit("unterminated wbConditionFunctions array in wbDefinitionsFO3.pas")
    fose_marker = text.find("// Added by FOSE:", array_start, array_end)
    if fose_marker < 0:
        raise SystemExit("xEdit FO3 condition array lacks its expected Added by FOSE block")

    base_entries = parse_xedit_entries(text[array_start:fose_marker])
    fose_entries = parse_xedit_entries(text[fose_marker:array_end])
    if fose_entries != EXPECTED_FOSE_FUNCTIONS:
        raise SystemExit(
            "xEdit Added by FOSE block differs from the pinned seven-row set: "
            f"expected {EXPECTED_FOSE_FUNCTIONS}, found {fose_entries}"
        )
    return base_entries, fose_entries


def validate_xedit_crosscheck(
    conditions: list[Function],
    base_entries: dict[int, tuple[str, str | None, str | None]],
) -> None:
    if len(base_entries) != EXPECTED_CONDITION_COUNT:
        raise SystemExit(
            f"expected {EXPECTED_CONDITION_COUNT} xEdit base-game rows, "
            f"found {len(base_entries)}"
        )
    engine_by_index = {
        function.opcode - GAME_OPCODE_BASE: function for function in conditions
    }
    if set(base_entries) != set(engine_by_index):
        missing = sorted(set(engine_by_index) - set(base_entries))
        extra = sorted(set(base_entries) - set(engine_by_index))
        raise SystemExit(
            "FO3 executable/xEdit condition-key mismatch: "
            f"missing from xEdit={missing}, extra in xEdit={extra}"
        )

    for index, (xedit_name, param1, param2) in base_entries.items():
        function = engine_by_index[index]
        if function.name != xedit_name:
            raise SystemExit(
                f"FO3 executable/xEdit name mismatch at raw index {index}: "
                f"engine={function.name!r}, xEdit={xedit_name!r}"
            )
        for parameter_index, xedit_type in enumerate((param1, param2)):
            engine_parameter = (
                function.parameters[parameter_index]
                if parameter_index < len(function.parameters)
                else None
            )
            if xedit_type is None:
                if engine_parameter is not None:
                    raise SystemExit(
                        f"xEdit omits FO3 raw index {index} parameter {parameter_index + 1}, "
                        f"but the executable declares raw type {engine_parameter.raw_type}"
                    )
                continue
            if engine_parameter is None:
                raise SystemExit(
                    f"xEdit declares FO3 raw index {index} parameter {parameter_index + 1} "
                    f"as {xedit_type}, but the executable has no slot"
                )
            allowed_raw_types = XEDIT_PT_TO_ENGINE_RAW_TYPES.get(xedit_type)
            if allowed_raw_types is None:
                raise SystemExit(f"unmapped xEdit FO3 parameter kind {xedit_type}")
            if engine_parameter.raw_type not in allowed_raw_types:
                raise SystemExit(
                    f"FO3 raw index {index} parameter {parameter_index + 1} kind mismatch: "
                    f"engine raw={engine_parameter.raw_type}, xEdit={xedit_type}, "
                    f"allowed engine raw={sorted(allowed_raw_types)}"
                )


def validate_table(commands: list[Function], sentinel: Function) -> list[Function]:
    if len(commands) != EXPECTED_GAME_COMMAND_COUNT:
        raise SystemExit(
            f"expected {EXPECTED_GAME_COMMAND_COUNT} game commands, found {len(commands)}"
        )
    if (
        sentinel.name != EXPECTED_SENTINEL_NAME
        or sentinel.short_name
        or sentinel.is_reference
        or sentinel.parameters
        or sentinel.is_condition
    ):
        raise SystemExit(
            "unexpected terminal game-command slot: "
            f"name={sentinel.name!r}, short={sentinel.short_name!r}, "
            f"reference={sentinel.is_reference}, params={len(sentinel.parameters)}, "
            f"condition=0x{sentinel.condition_pointer:08X}"
        )

    observed_param_types = {
        parameter.raw_type for command in commands for parameter in command.parameters
    }
    if observed_param_types != EXPECTED_OBSERVED_PARAM_TYPES:
        raise SystemExit(
            "Fallout 3 parameter-type domain drift: "
            f"expected {sorted(EXPECTED_OBSERVED_PARAM_TYPES)}, "
            f"found {sorted(observed_param_types)}"
        )

    conditions = [command for command in commands if command.is_condition]
    if len(conditions) != EXPECTED_CONDITION_COUNT:
        raise SystemExit(
            f"expected {EXPECTED_CONDITION_COUNT} condition callbacks, "
            f"found {len(conditions)}"
        )
    if max(command.opcode - GAME_OPCODE_BASE for command in conditions) != (
        EXPECTED_MAX_CONDITION_INDEX
    ):
        raise SystemExit("unexpected maximum Fallout 3 condition index")

    by_raw_index = {command.opcode - GAME_OPCODE_BASE: command for command in commands}
    if by_raw_index[0].name != "UnusedFunction0" or by_raw_index[0].is_condition:
        raise SystemExit("raw index 0 is not the expected non-condition UnusedFunction0 row")
    if by_raw_index[1].name != "GetDistance" or not by_raw_index[1].is_condition:
        raise SystemExit("raw index 1 is not the expected GetDistance condition row")
    if by_raw_index[2].name != "AddItem" or by_raw_index[2].is_condition:
        raise SystemExit("raw index 2 is not the expected script-only AddItem row")

    has_perk = by_raw_index[0x01C1]
    if (
        has_perk.name != "HasPerk"
        or not has_perk.is_condition
        or has_perk.parameters
        != (Parameter(name="Perk", raw_type=39, optional=False),)
    ):
        raise SystemExit(
            "Fallout 3 HasPerk no longer has its pinned one-parameter retail signature"
        )
    return conditions


def csharp_string(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\r", "\\r")
        .replace("\n", "\\n")
    )


def generate_csharp(conditions: list[Function]) -> str:
    lines = [
        "// <auto-generated>",
        "// Generated by tools/extract_fo3_condition_functions.py from the hash-pinned retail PC executable.",
        f"// Fallout3.exe {EXPECTED_FILE_VERSION}; {EXPECTED_EXE_SIZE:,} bytes; SHA-256: {EXPECTED_EXE_SHA256}",
        f"// PE COFF timestamp: 0x{EXPECTED_COFF_TIMESTAMP:08X}; image base: 0x{EXPECTED_IMAGE_BASE:08X}.",
        f"// CodeView: {EXPECTED_CODEVIEW_PDB_NAME}; GUID {EXPECTED_CODEVIEW_GUID}; age {EXPECTED_CODEVIEW_AGE}.",
        f"// One unique 0x{FUNCTION_SIZE:X}-byte array contains {EXPECTED_GAME_COMMAND_COUNT} sequential game commands",
        f"// plus one zero-tailed terminal sentinel. Its +0x20 callback pointer is non-null for exactly {len(conditions)} rows.",
        "// The +0x20 pConditionFunction field name/layout is FNV-PDB-corroborated, not FO3-PDB-proven;",
        "// the FO3 executable supplies the exact pointer state and requires every non-null target to map to .text.",
        "// Every emitted index, name, short name, reference flag, parameter display name/raw type/optional",
        "// flag, and callback-membership bit comes directly from that FO3 array and its ParamInfo records.",
        "// ScriptParamType labels interpret those raw classic ids using the FNV-PDB-pinned enum numbering;",
        "// this map is not generated by subtracting rows from or copying definitions out of the FNV table.",
        f"// xEdit source commit: {XEDIT_SOURCE_COMMIT}",
        f"// xEdit wbDefinitionsFO3.pas SHA-256: {EXPECTED_XEDIT_SHA256}",
        "// Community cross-check (MPL-2.0): its 237 base rows match the executable keys, names, and",
        "// first-two-slot kinds exactly. Seven separately labeled FOSE rows are validated but excluded.",
        "// The retail executable is an offline generation oracle; normal builds/runtime have no dependency on it.",
        "// </auto-generated>",
        "",
        "namespace BethesdaMultitool.Core.Formats.Esm.Script;",
        "",
        "internal static class Fallout3ConditionFunctionTable",
        "{",
        f"    internal const int LocalGameCommandCount = {EXPECTED_GAME_COMMAND_COUNT};",
        f"    internal const int LocalSentinelSlotCount = {EXPECTED_SENTINEL_SLOT_COUNT};",
        f"    internal const int ConditionFunctionCount = {EXPECTED_CONDITION_COUNT};",
        f"    internal const int ExcludedFoseConditionCount = {len(EXPECTED_FOSE_FUNCTIONS)};",
        "",
        "    internal static readonly Dictionary<ushort, ScriptFunctionDef> ConditionFunctions = new()",
        "    {",
    ]

    for function in conditions:
        rendered_parameters = ", ".join(
            f'new("{csharp_string(parameter.name)}", '
            f"ScriptParamType.{PARAM_TYPE_NAMES[parameter.raw_type]}, "
            f"{'true' if parameter.optional else 'false'})"
            for parameter in function.parameters
        )
        raw_index = function.opcode - GAME_OPCODE_BASE
        lines.append(
            f'        [0x{raw_index:04X}] = new("{csharp_string(function.name)}", '
            f'"{csharp_string(function.short_name)}", '
            f"{'true' if function.is_reference else 'false'}, "
            f"[{rendered_parameters}], true),"
        )

    lines.extend(["    };", "}", ""])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--xedit", type=Path, default=DEFAULT_XEDIT)
    parser.add_argument("-o", "--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()

    require_authoritative_input(args.exe)
    image = PeImage(args.exe)
    image.read_codeview_identity()
    commands, sentinel = extract_table(image)
    conditions = validate_table(commands, sentinel)
    xedit_base, _ = load_xedit_condition_functions(args.xedit)
    validate_xedit_crosscheck(conditions, xedit_base)
    generated = generate_csharp(conditions)

    if args.verify_only:
        if not args.output.is_file() or args.output.read_text(encoding="utf-8") != generated:
            raise SystemExit(f"generated output is stale: {args.output}")
        print(
            f"PASS: {len(commands)} FO3 game commands + one sentinel; "
            f"{len(conditions)} condition callbacks; xEdit base rows match; "
            f"{len(EXPECTED_FOSE_FUNCTIONS)} FOSE rows excluded"
        )
        return 0

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(generated, encoding="utf-8", newline="\n")
    print(f"Wrote {args.output}")
    print(
        f"  {len(commands)} FO3 game commands + one sentinel; "
        f"{len(conditions)} condition callbacks; xEdit base rows match; "
        f"{len(EXPECTED_FOSE_FUNCTIONS)} FOSE rows excluded"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
