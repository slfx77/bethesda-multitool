"""Extract Fallout 4's legacy script/condition command table.

The official Fallout 4 PDB supplies the data-symbol locations and the exact x64
``SCRIPT_FUNCTION`` / ``SCRIPT_PARAMETER`` layouts.  The matching executable
supplies the initialized structs and strings.  xEdit is used only for the
condition-specific parameter interpretation: a few CTDA encodings do not map
one-for-one to the script compiler's parameter array (notably GetEventData).
This extracts the full ``scriptFunctions`` game-command table and its exact
condition-handler subset. Compiled record scripts consume the opcode table;
CTDA lookup consumes the separately emitted raw-index subset and xEdit storage
metadata. The separate 522-slot ``scriptConsole`` table is out of scope and
must not be implied by the generated counts.

Inputs are ignored local research files.  The generated C# file is the only
runtime input and is intentionally checked into source control.

Usage:
    python tools/extract_fo4_script_functions.py
    python tools/extract_fo4_script_functions.py --verify-only
"""

from __future__ import annotations

import argparse
import hashlib
import re
import struct
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_EXE = REPO_ROOT / "Sample" / "Fallout 4" / "Fallout4.exe"
DEFAULT_PDB = REPO_ROOT / "Sample" / "Fallout 4" / "Fallout4.pdb"
DEFAULT_XEDIT = (
    REPO_ROOT
    / "Sample"
    / "Reference_Code"
    / "TES5Edit"
    / "Core"
    / "wbDefinitionsFO4.pas"
)
DEFAULT_CVDUMP = REPO_ROOT / "tools" / "microsoft-pdb" / "cvdump" / "cvdump.exe"
DEFAULT_OUTPUT = (
    REPO_ROOT
    / "src"
    / "BethesdaMultitool"
    / "Core"
    / "Formats"
    / "Esm"
    / "Script"
    / "Fallout4ScriptFunctionTable.Generated.cs"
)

EXPECTED_SHA256 = {
    "exe": "886D67FC955BE02DA0D79F0953D12D2BFEAD5017A69EB42E346D0194BE1D524D",
    "pdb": "6C5DB527EAA981C1C258B4523C21B6774332B2FE520BB5FA25AE96E2DDE01917",
    "xedit": "BE7DE41B01DA67DD66DFEB6935AD2FE82274B5B9977B87B7D673BC531F7C9797",
}
XEDIT_SOURCE_COMMIT = "e0e529a2d473756520f2d41f72c24dea0cf5ee0d"

SCRIPT_FUNCTION_TYPE = "0x00044963"
SCRIPT_FUNCTION_FIELDS = "0x00044962"
SCRIPT_PARAMETER_TYPE = "0x0004450e"
SCRIPT_PARAMETER_FIELDS = "0x0004450d"
SCRIPT_PARAM_ENUM = "0x00017cf2"
SCRIPT_PARAM_ENUM_FIELDS = "0x00017cf1"
SCRIPT_FUNCTION_ARRAY_TYPE = "0x00071bb7"

FUNCTION_SIZE = 80
PARAMETER_SIZE = 16
EXPECTED_ARRAY_BYTES = 65_520
EXPECTED_SLOT_COUNT = EXPECTED_ARRAY_BYTES // FUNCTION_SIZE
EXPECTED_NAMED_COUNT = 810
EXPECTED_CONDITION_COUNT = 479
EXPECTED_TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT = 63
EXPECTED_TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT = 64
EXPECTED_UNRESOLVED_PARAM_FUNCTIONS = {0x1245: "UnlockWord", 0x1246: "TeachWord"}

PARAM_IDENTIFIERS = {
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
    27: "WorldOrList",
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
    46: "EventFunction",
    47: "EventFunctionMember",
    48: "EventFunctionData",
    49: "VoiceType",
    50: "EncounterZone",
    51: "IdleForm",
    52: "Message",
    53: "InvObjectOrFormList",
    54: "Alignment",
    55: "EquipType",
    56: "ObjectOrFormList",
    57: "Music",
    58: "CritStage",
    59: "Keyword",
    60: "RefType",
    61: "Location",
    62: "Form",
    63: "Alias",
    64: "UnusedShout",
    65: "UnusedWordOfPower",
    66: "RelationshipRank",
    67: "Scene",
    68: "CastingSource",
    69: "AssociationType",
    70: "WardState",
    71: "PackageDataCanBeNull",
    72: "PackageDataNumeric",
    73: "PackageDataReference",
    74: "VmScriptVar",
    75: "ReferenceEffect",
    76: "PackageDataLocation",
    77: "SoundCategory",
    78: "KnowableForm",
    79: "Region",
    80: "Action",
    81: "MovementIdleFromState",
    82: "MovementIdleToState",
    83: "VmRefOrAliasScript",
    84: "DamageType",
    85: "SceneAction",
    86: "KeywordOrFormList",
    87: "FurnitureEntryType",
    88: "Count",
}

EXPECTED_PARAM_ENUM_NAMES = {
    0: "SCRIPT_PARAM_CHAR",
    1: "SCRIPT_PARAM_INT",
    2: "SCRIPT_PARAM_FLOAT",
    3: "SCRIPT_PARAM_INVENTORY_OBJECT",
    4: "SCRIPT_PARAM_OBJECTREF",
    5: "SCRIPT_PARAM_ACTOR_VALUE",
    6: "SCRIPT_PARAM_ACTOR",
    7: "SCRIPT_PARAM_SPELL_ITEM",
    8: "SCRIPT_PARAM_AXIS",
    9: "SCRIPT_PARAM_CELL",
    10: "SCRIPT_PARAM_ANIM_GROUP",
    11: "SCRIPT_PARAM_MAGIC_ITEM",
    12: "SCRIPT_PARAM_SOUND",
    13: "SCRIPT_PARAM_TOPIC",
    14: "SCRIPT_PARAM_QUEST",
    15: "SCRIPT_PARAM_RACE",
    16: "SCRIPT_PARAM_CLASS",
    17: "SCRIPT_PARAM_FACTION",
    18: "SCRIPT_PARAM_SEX",
    19: "SCRIPT_PARAM_GLOBAL",
    20: "SCRIPT_PARAM_FURNITURE_OR_FORMLIST",
    21: "SCRIPT_PARAM_OBJECT",
    22: "SCRIPT_PARAM_SCRIPT_VAR",
    23: "SCRIPT_PARAM_STAGE",
    24: "SCRIPT_PARAM_MAP_MARKER",
    25: "SCRIPT_PARAM_ACTOR_BASE",
    26: "SCRIPT_PARAM_CONTAINER_REF",
    27: "SCRIPT_PARAM_WORLD_OR_LIST",
    28: "SCRIPT_PARAM_CRIME_TYPE",
    29: "SCRIPT_PARAM_PACKAGE",
    30: "SCRIPT_PARAM_COMBAT_STYLE",
    31: "SCRIPT_PARAM_MAGIC_EFFECT",
    32: "SCRIPT_PARAM_FORM_TYPE",
    33: "SCRIPT_PARAM_WEATHER",
    34: "SCRIPT_PARAM_NPC",
    35: "SCRIPT_PARAM_OWNER",
    36: "SCRIPT_PARAM_SHADER_EFFECT",
    37: "SCRIPT_PARAM_FORMLIST",
    38: "SCRIPT_PARAM_MENUICON",
    39: "SCRIPT_PARAM_PERK",
    40: "SCRIPT_PARAM_NOTE",
    41: "SCRIPT_PARAM_MISC_STAT",
    42: "SCRIPT_PARAM_IMAGESPACEMOD",
    43: "SCRIPT_PARAM_IMAGESPACE",
    44: "SCRIPT_PARAM_VATS_VALUE",
    45: "SCRIPT_PARAM_VATS_VALUE_DATA",
    46: "SCRIPT_PARAM_EVENT_FUNCTION",
    47: "SCRIPT_PARAM_EVENT_FUNCTION_MEMBER",
    48: "SCRIPT_PARAM_EVENT_FUNCTION_DATA",
    49: "SCRIPT_PARAM_VOICE_TYPE",
    50: "SCRIPT_PARAM_ENCOUNTERZONE",
    51: "SCRIPT_PARAM_IDLE_FORM",
    52: "SCRIPT_PARAM_MESSAGE",
    53: "SCRIPT_PARAM_INVOBJECT_OR_FORMLIST",
    54: "SCRIPT_PARAM_ALIGNMENT",
    55: "SCRIPT_PARAM_EQUIPTYPE",
    56: "SCRIPT_PARAM_OBJECT_OR_FORMLIST",
    57: "SCRIPT_PARAM_MUSIC",
    58: "SCRIPT_PARAM_CRITSTAGE",
    59: "SCRIPT_PARAM_KEYWORD",
    60: "SCRIPT_PARAM_REFTYPE",
    61: "SCRIPT_PARAM_LOCATION",
    62: "SCRIPT_PARAM_FORM",
    63: "SCRIPT_PARAM_ALIAS",
    64: "UNUSED_SCRIPT_PARAM_SHOUT",
    65: "UNUSED_SCRIPT_PARAM_WORD_OF_POWER",
    66: "SCRIPT_PARAM_RELATIONSHIP_RANK",
    67: "SCRIPT_PARAM_BGSSCENE",
    68: "SCRIPT_PARAM_CASTING_SOURCE",
    69: "SCRIPT_PARAM_ASSOCIATION_TYPE",
    70: "SCRIPT_PARAM_WARD_STATE",
    71: "SCRIPT_PARAM_PACKAGE_DATA_CAN_BE_NULL",
    72: "SCRIPT_PARAM_PACKAGE_DATA_NUMERIC",
    73: "SCRIPT_PARAM_PACKAGE_DATA_REFERENCE",
    74: "SCRIPT_PARAM_VM_SCRIPT_VAR",
    75: "SCRIPT_PARAM_REFERENCE_EFFECT",
    76: "SCRIPT_PARAM_PACKAGE_DATA_LOCATION",
    77: "SCRIPT_PARAM_SOUND_CATEGORY",
    78: "SCRIPT_PARAM_KNOWABLE_FORM",
    79: "SCRIPT_PARAM_REGION",
    80: "SCRIPT_PARAM_ACTION",
    81: "SCRIPT_PARAM_MOVEMENT_IDLE_FROM_STATE",
    82: "SCRIPT_PARAM_MOVEMENT_IDLE_TO_STATE",
    83: "SCRIPT_PARAM_VM_REF_OR_ALIAS_SCRIPT",
    84: "SCRIPT_PARAM_DAMAGE_TYPE",
    85: "SCRIPT_PARAM_SCENE_ACTION",
    86: "SCRIPT_PARAM_KEYWORD_OR_FORMLIST",
    87: "SCRIPT_PARAM_FURNENTRYTYPE",
    88: "SCRIPT_PARAM_COUNT",
}

XEDIT_NUMERIC_TYPES = {
    "ptFloat",
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
    "ptFormType",
    "ptMiscStat",
    "ptSex",
    "ptWardState",
}

XEDIT_FORM_ID_TYPES = {
    "ptActor",
    "ptActorBase",
    "ptActorValue",
    "ptAssociationType",
    "ptBaseEffect",
    "ptBaseObject",
    "ptCell",
    "ptClass",
    "ptDamageType",
    "ptEffectItem",
    "ptEncounterZone",
    "ptEquipType",
    "ptEventData",
    "ptFaction",
    "ptFactionNull",
    "ptFormList",
    "ptFurniture",
    "ptGlobal",
    "ptIdleForm",
    "ptKeyword",
    "ptLocation",
    "ptLocationRefType",
    "ptOwner",
    "ptPackage",
    "ptPerk",
    "ptQuest",
    "ptRace",
    "ptReference",
    "ptRegion",
    "ptScene",
    "ptVoiceType",
    "ptWeather",
    "ptWorldspace",
}

XEDIT_NUMERIC_TYPES_NORMALIZED = {item.casefold() for item in XEDIT_NUMERIC_TYPES}
XEDIT_FORM_ID_TYPES_NORMALIZED = {item.casefold() for item in XEDIT_FORM_ID_TYPES}
XEDIT_TYPE_OVERRIDE_ELIGIBLE = {"ptreference", "ptactor", "ptpackage"}


@dataclass(frozen=True)
class Section:
    number: int
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
    is_condition: bool
    unresolved_parameters: bool


@dataclass(frozen=True)
class XEditCondition:
    index: int
    name: str
    param_types: tuple[str, str, str]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def require_authoritative_input(path: Path, key: str) -> None:
    if not path.is_file():
        raise SystemExit(f"missing {key} input: {path}")
    actual = sha256(path)
    expected = EXPECTED_SHA256[key]
    if actual != expected:
        raise SystemExit(
            f"unsupported {key} input hash for {path}:\n"
            f"  expected {expected}\n  actual   {actual}"
        )


def cvdump_lines(cvdump: Path, flag: str, pdb: Path):
    process = subprocess.Popen(
        [str(cvdump), flag, str(pdb)],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    assert process.stdout is not None
    yield from process.stdout
    return_code = process.wait()
    if return_code != 0:
        raise SystemExit(f"cvdump {flag} failed with exit code {return_code}")


def collect_type_blocks(cvdump: Path, pdb: Path) -> dict[str, list[str]]:
    wanted = {
        SCRIPT_FUNCTION_TYPE,
        SCRIPT_FUNCTION_FIELDS,
        SCRIPT_PARAMETER_TYPE,
        SCRIPT_PARAMETER_FIELDS,
        SCRIPT_PARAM_ENUM,
        SCRIPT_PARAM_ENUM_FIELDS,
        SCRIPT_FUNCTION_ARRAY_TYPE,
    }
    blocks: dict[str, list[str]] = {}
    current: str | None = None
    type_start = re.compile(r"^\s*(0x[0-9a-fA-F]+)\s*:")
    for line in cvdump_lines(cvdump, "-t", pdb):
        match = type_start.match(line)
        if match:
            current = match.group(1).lower()
            if current in wanted:
                blocks[current] = [line]
            continue
        if current in wanted:
            blocks[current].append(line)

    missing = wanted - blocks.keys()
    if missing:
        raise SystemExit(f"PDB type records missing: {sorted(missing)}")
    return blocks


def validate_pdb_types(blocks: dict[str, list[str]]) -> None:
    function_text = "".join(blocks[SCRIPT_FUNCTION_TYPE])
    parameter_text = "".join(blocks[SCRIPT_PARAMETER_TYPE])
    array_text = "".join(blocks[SCRIPT_FUNCTION_ARRAY_TYPE])
    enum_text = "".join(blocks[SCRIPT_PARAM_ENUM])
    function_fields = "".join(blocks[SCRIPT_FUNCTION_FIELDS])
    parameter_fields = "".join(blocks[SCRIPT_PARAMETER_FIELDS])

    required = [
        (function_text, "Size = 80, class name = SCRIPT_FUNCTION"),
        (parameter_text, "Size = 16, class name = SCRIPT_PARAMETER"),
        (array_text, f"length = (LF_USHORT) {EXPECTED_ARRAY_BYTES}"),
        (enum_text, "# members = 89"),
        (enum_text, "enum name = SCRIPT_PARAM_TYPE"),
    ]
    for text, needle in required:
        if needle.lower() not in text.lower():
            raise SystemExit(f"PDB layout assertion failed: {needle!r}")

    expected_function_fields = {
        "pFunctionName": 0,
        "pShortName": 8,
        "eOutput": 16,
        "pHelpString": 24,
        "bReferenceFunction": 32,
        "sParamCount": 34,
        "pParameters": 40,
        "pExecuteFunction": 48,
        "pCompileFunction": 56,
        "pConditionFunction": 64,
        "bEditorFilter": 72,
        "bInvalidatesCellList": 73,
    }
    expected_parameter_fields = {"pParamName": 0, "eParamType": 8, "bOptional": 12}
    validate_field_offsets(function_fields, expected_function_fields, "SCRIPT_FUNCTION")
    validate_field_offsets(parameter_fields, expected_parameter_fields, "SCRIPT_PARAMETER")

    enum_values: dict[int, str] = {}
    enum_pattern = re.compile(
        r"LF_ENUMERATE,\s+public,\s+value\s*=\s*(\d+),\s+name\s*=\s*'([^']+)'"
    )
    for match in enum_pattern.finditer("".join(blocks[SCRIPT_PARAM_ENUM_FIELDS])):
        enum_values[int(match.group(1))] = match.group(2)
    if enum_values != EXPECTED_PARAM_ENUM_NAMES:
        raise SystemExit("PDB SCRIPT_PARAM_TYPE values differ from the pinned FO4 enum")


def validate_field_offsets(text: str, expected: dict[str, int], type_name: str) -> None:
    found: dict[str, int] = {}
    lines = text.splitlines()
    for index, line in enumerate(lines[:-1]):
        offset_match = re.search(r"LF_MEMBER.*offset\s*=\s*(\d+)", line)
        name_match = re.search(r"member name\s*=\s*'([^']+)'", lines[index + 1])
        if offset_match and name_match:
            found[name_match.group(1)] = int(offset_match.group(1))
    if found != expected:
        raise SystemExit(f"{type_name} fields differ: expected {expected}, found {found}")


def find_pdb_global(cvdump: Path, pdb: Path) -> tuple[int, int]:
    matches: set[tuple[int, int]] = set()
    pattern = re.compile(
        rf"S_GDATA32:\s*\[(\d+):([0-9A-Fa-f]+)\],\s*"
        rf"Type:\s*{re.escape(SCRIPT_FUNCTION_ARRAY_TYPE)},\s*scriptFunctions\s*$",
        re.IGNORECASE,
    )
    for line in cvdump_lines(cvdump, "-g", pdb):
        match = pattern.search(line)
        if match:
            matches.add((int(match.group(1)), int(match.group(2), 16)))
    if len(matches) != 1:
        raise SystemExit(f"expected one scriptFunctions PDB global, found {sorted(matches)}")
    return next(iter(matches))


class PeImage:
    def __init__(self, path: Path):
        self.data = path.read_bytes()
        pe_offset = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[pe_offset : pe_offset + 4] != b"PE\0\0":
            raise SystemExit(f"not a PE image: {path}")
        machine, section_count = struct.unpack_from("<HH", self.data, pe_offset + 4)
        optional_size = struct.unpack_from("<H", self.data, pe_offset + 20)[0]
        optional_offset = pe_offset + 24
        magic = struct.unpack_from("<H", self.data, optional_offset)[0]
        if machine != 0x8664 or magic != 0x20B:
            raise SystemExit("Fallout4.exe must be an AMD64 PE32+ image")
        self.image_base = struct.unpack_from("<Q", self.data, optional_offset + 24)[0]
        self.sections: list[Section] = []
        section_offset = optional_offset + optional_size
        for index in range(section_count):
            offset = section_offset + index * 40
            name = self.data[offset : offset + 8].rstrip(b"\0").decode("ascii", "replace")
            virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from(
                "<IIII", self.data, offset + 8
            )
            self.sections.append(
                Section(index + 1, name, virtual_size, virtual_address, raw_size, raw_pointer)
            )

    def section_offset(self, section_number: int, offset: int) -> int:
        section = next((item for item in self.sections if item.number == section_number), None)
        if section is None or offset < 0 or offset >= section.raw_size:
            raise SystemExit(f"invalid section-relative address [{section_number}:{offset:X}]")
        return section.raw_pointer + offset

    def va_to_offset(self, address: int) -> int | None:
        relative = address - self.image_base
        for section in self.sections:
            if section.virtual_address <= relative < section.virtual_address + max(
                section.virtual_size, section.raw_size
            ):
                delta = relative - section.virtual_address
                return section.raw_pointer + delta if delta < section.raw_size else None
        return None

    def read_c_string(self, address: int, *, maximum: int = 1024) -> str:
        if address == 0:
            return ""
        offset = self.va_to_offset(address)
        if offset is None:
            raise ValueError(f"unmapped string pointer 0x{address:X}")
        end = self.data.find(b"\0", offset, min(len(self.data), offset + maximum))
        if end < 0:
            raise ValueError(f"unterminated string pointer 0x{address:X}")
        value = self.data[offset:end]
        if any(byte < 0x20 or byte > 0x7E for byte in value):
            raise ValueError(f"non-ASCII string pointer 0x{address:X}")
        return value.decode("ascii")


def extract_functions(pe: PeImage, section_number: int, section_offset: int) -> list[Function]:
    table_offset = pe.section_offset(section_number, section_offset)
    functions: list[Function] = []
    unresolved: dict[int, str] = {}
    for slot in range(EXPECTED_SLOT_COUNT):
        offset = table_offset + slot * FUNCTION_SIZE
        function_name_ptr, short_name_ptr = struct.unpack_from("<QQ", pe.data, offset)
        opcode = struct.unpack_from("<I", pe.data, offset + 16)[0]
        is_reference = pe.data[offset + 32] != 0
        parameter_count = struct.unpack_from("<H", pe.data, offset + 34)[0]
        parameters_ptr = struct.unpack_from("<Q", pe.data, offset + 40)[0]
        condition_ptr = struct.unpack_from("<Q", pe.data, offset + 64)[0]

        expected_opcode = 0x1000 + slot
        if opcode != expected_opcode:
            raise SystemExit(
                f"non-sequential game table at slot {slot}: 0x{opcode:X} != 0x{expected_opcode:X}"
            )
        if function_name_ptr == 0:
            continue
        name = pe.read_c_string(function_name_ptr)
        if not name:
            continue
        short_name = pe.read_c_string(short_name_ptr) if short_name_ptr else ""

        unresolved_parameters = False
        parameters: list[Parameter] = []
        if parameter_count:
            parameter_offset = pe.va_to_offset(parameters_ptr) if parameters_ptr else None
            if parameter_offset is None:
                unresolved_parameters = True
                unresolved[opcode] = name
            else:
                if parameter_count > 64:
                    raise SystemExit(f"implausible parameter count {parameter_count} for {name}")
                for parameter_index in range(parameter_count):
                    item_offset = parameter_offset + parameter_index * PARAMETER_SIZE
                    parameter_name_ptr = struct.unpack_from("<Q", pe.data, item_offset)[0]
                    raw_type = struct.unpack_from("<I", pe.data, item_offset + 8)[0]
                    optional = pe.data[item_offset + 12] != 0
                    if raw_type not in PARAM_IDENTIFIERS or raw_type == 88:
                        raise SystemExit(f"invalid parameter type {raw_type} for {name}")
                    parameters.append(
                        Parameter(pe.read_c_string(parameter_name_ptr), raw_type, optional)
                    )

        functions.append(
            Function(
                opcode,
                name,
                short_name,
                is_reference,
                tuple(parameters),
                condition_ptr != 0,
                unresolved_parameters,
            )
        )

    if len(functions) != EXPECTED_NAMED_COUNT:
        raise SystemExit(f"expected {EXPECTED_NAMED_COUNT} named functions, found {len(functions)}")
    if sum(function.is_condition for function in functions) != EXPECTED_CONDITION_COUNT:
        raise SystemExit(
            f"expected {EXPECTED_CONDITION_COUNT} condition handlers, found "
            f"{sum(function.is_condition for function in functions)}"
        )
    if unresolved != EXPECTED_UNRESOLVED_PARAM_FUNCTIONS:
        raise SystemExit(
            f"unexpected unresolved parameter pointers: expected "
            f"{EXPECTED_UNRESOLVED_PARAM_FUNCTIONS}, found {unresolved}"
        )
    return functions


def parse_xedit_conditions(path: Path) -> list[XEditCondition]:
    text = path.read_text(encoding="utf-8-sig", errors="strict")
    start = text.index("wbConditionFunctions : array[0..478]")
    end = text.index("function wbConditionDescFromIndex", start)
    table = text[start:end]
    entry_pattern = re.compile(
        r"\(Index:\s*(\d+);\s*Name:\s*'((?:''|[^'])+)'(?P<body>[^\r\n]*)\)",
        re.IGNORECASE,
    )
    parameter_pattern = re.compile(r"ParamType([123])\s*:\s*(pt[A-Za-z0-9_]+)", re.IGNORECASE)
    conditions: list[XEditCondition] = []
    for match in entry_pattern.finditer(table):
        parameter_types = ["ptNone", "ptNone", "ptNone"]
        for parameter_match in parameter_pattern.finditer(match.group("body")):
            parameter_types[int(parameter_match.group(1)) - 1] = parameter_match.group(2)
        conditions.append(
            XEditCondition(
                int(match.group(1)),
                match.group(2).replace("''", "'"),
                tuple(parameter_types),
            )
        )
    if len(conditions) != EXPECTED_CONDITION_COUNT:
        raise SystemExit(f"expected 479 xEdit conditions, found {len(conditions)}")
    if len({condition.index for condition in conditions}) != len(conditions):
        raise SystemExit("xEdit condition functions contain duplicate raw indices")
    if conditions != sorted(conditions, key=lambda item: item.index):
        raise SystemExit("xEdit condition functions are not sorted by index")
    return conditions


def validate_condition_crosscheck(
    functions: list[Function], conditions: list[XEditCondition]
) -> None:
    engine = {function.opcode - 0x1000: function for function in functions if function.is_condition}
    xedit = {condition.index: condition for condition in conditions}
    if engine.keys() != xedit.keys():
        raise SystemExit(
            f"condition index mismatch: engine-only={sorted(engine.keys() - xedit.keys())}, "
            f"xEdit-only={sorted(xedit.keys() - engine.keys())}"
        )
    name_mismatches = [
        (index, engine[index].name, xedit[index].name)
        for index in engine
        if engine[index].name.casefold() != xedit[index].name.casefold()
    ]
    if name_mismatches:
        raise SystemExit(f"condition name mismatch: {name_mismatches}")

    known_xedit_types = XEDIT_NUMERIC_TYPES_NORMALIZED | XEDIT_FORM_ID_TYPES_NORMALIZED | {"ptnone"}
    for condition in conditions:
        for parameter_type in condition.param_types:
            if parameter_type.casefold() not in known_xedit_types:
                raise SystemExit(f"unclassified xEdit condition type {parameter_type}")

    eligible_by_condition = [
        condition
        for condition in conditions
        if any(
            parameter_type.casefold() in XEDIT_TYPE_OVERRIDE_ELIGIBLE
            for parameter_type in condition.param_types[:2]
        )
    ]
    eligible_slots = sum(
        parameter_type.casefold() in XEDIT_TYPE_OVERRIDE_ELIGIBLE
        for condition in conditions
        for parameter_type in condition.param_types[:2]
    )
    if len(eligible_by_condition) != EXPECTED_TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT:
        raise SystemExit(
            f"expected {EXPECTED_TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT} conditions eligible for "
            f"Type overrides, found {len(eligible_by_condition)}"
        )
    if eligible_slots != EXPECTED_TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT:
        raise SystemExit(
            f"expected {EXPECTED_TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT} parameter slots eligible for "
            f"Type overrides, found {eligible_slots}"
        )


def condition_kind(parameter_type: str) -> str | None:
    normalized = parameter_type.casefold()
    if normalized == "ptnone":
        return None
    if normalized in XEDIT_NUMERIC_TYPES_NORMALIZED:
        return "ConditionParamKind.Numeric"
    if normalized in XEDIT_FORM_ID_TYPES_NORMALIZED:
        return "ConditionParamKind.FormId"
    raise ValueError(parameter_type)


def csharp_string(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\r", "\\r")
        .replace("\n", "\\n")
    )


def generate_csharp(functions: list[Function], conditions: list[XEditCondition]) -> str:
    lines = [
        "// <auto-generated>",
        "// Generated by tools/extract_fo4_script_functions.py from the official Fallout 4 PDB/EXE.",
        f"// EXE SHA-256: {EXPECTED_SHA256['exe']}",
        f"// PDB SHA-256: {EXPECTED_SHA256['pdb']}",
        f"// xEdit source commit: {XEDIT_SOURCE_COMMIT}",
        f"// xEdit wbDefinitionsFO4.pas SHA-256: {EXPECTED_SHA256['xedit']}",
        "// 819 sequential engine slots; 810 named game commands; 479 condition-capable commands.",
        "// The PDB/EXE supply the command definitions and non-null condition-handler subset.",
        "// Condition names are cross-checked against xEdit; CTDA parameter storage kinds and Type-override",
        "// eligibility are derived from the pinned wbDefinitionsFO4.pas input under MPL-2.0.",
        "// xEdit's single declared ParamType3 (GetPlayerControlsDisabled) is metadata only here:",
        "// the physical trailing FO4 CTDA Parameter #3 is selected by Run On, not by function.",
        "// </auto-generated>",
        "",
        "using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;",
        "",
        "namespace BethesdaMultitool.Core.Formats.Esm.Script;",
        "",
        "internal static class Fallout4ScriptFunctionTable",
        "{",
        f"    internal const int EngineSlotCount = {EXPECTED_SLOT_COUNT};",
        f"    internal const int NamedFunctionCount = {EXPECTED_NAMED_COUNT};",
        f"    internal const int ConditionFunctionCount = {EXPECTED_CONDITION_COUNT};",
        f"    internal const int UnresolvedParameterFunctionCount = {len(EXPECTED_UNRESOLVED_PARAM_FUNCTIONS)};",
        "    internal const int XEditDeclaredThirdParameterCount = 1;",
        f"    internal const int TypeOverrideEligibleFunctionCount = {EXPECTED_TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT};",
        f"    internal const int TypeOverrideEligibleSlotCount = {EXPECTED_TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT};",
        "",
        "    internal static readonly Dictionary<ushort, ScriptFunctionDef> Functions = new()",
        "    {",
    ]
    for function in functions:
        parameters = ", ".join(
            f'new("{csharp_string(parameter.name)}", '
            f"Fallout4ScriptParamType.{PARAM_IDENTIFIERS[parameter.raw_type]}, "
            f"{'true' if parameter.optional else 'false'})"
            for parameter in function.parameters
        )
        lines.append(
            f'        [0x{function.opcode:04X}] = new("{csharp_string(function.name)}", '
            f'"{csharp_string(function.short_name)}", '
            f"{'true' if function.is_reference else 'false'}, [{parameters}], "
            f"{'true' if function.is_condition else 'false'}, "
            f"{'true' if function.unresolved_parameters else 'false'}),"
        )
    lines.extend(
        [
            "    };",
            "",
            "    // Raw CTDA function index -> the matching engine command definition. Keeping this",
            "    // key space separate from bytecode opcodes prevents modern sparse-index collisions.",
            "    internal static readonly Dictionary<ushort, ScriptFunctionDef> ConditionFunctions = new()",
            "    {",
        ]
    )
    for function in functions:
        if function.is_condition:
            condition_index = function.opcode - 0x1000
            lines.append(
                f"        [0x{condition_index:04X}] = Functions[0x{function.opcode:04X}],"
            )
    lines.extend(
        [
            "    };",
            "",
            "    // Keys are raw CTDA function indices. Values are storage kinds from xEdit;",
            "    // null/omitted parameters fail closed as raw values.",
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
            "    // xEdit applies Type.UseAliases/UsePackdata only when the declared base CTDA kind is",
            "    // Reference, Actor, or Package. Preserve that exact per-slot eligibility rather than",
            "    // treating every FormID parameter as overrideable.",
            "    internal static readonly Dictionary<ushort, bool[]> ConditionTypeOverrideEligibility = new()",
            "    {",
        ]
    )
    for condition in conditions:
        eligible = [
            item.casefold() in XEDIT_TYPE_OVERRIDE_ELIGIBLE
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--pdb", type=Path, default=DEFAULT_PDB)
    parser.add_argument("--xedit", type=Path, default=DEFAULT_XEDIT)
    parser.add_argument("--cvdump", type=Path, default=DEFAULT_CVDUMP)
    parser.add_argument("-o", "--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()

    require_authoritative_input(args.exe, "exe")
    require_authoritative_input(args.pdb, "pdb")
    require_authoritative_input(args.xedit, "xedit")
    if not args.cvdump.is_file():
        raise SystemExit(f"missing cvdump: {args.cvdump}")

    blocks = collect_type_blocks(args.cvdump, args.pdb)
    validate_pdb_types(blocks)
    section_number, section_offset = find_pdb_global(args.cvdump, args.pdb)
    functions = extract_functions(PeImage(args.exe), section_number, section_offset)
    conditions = parse_xedit_conditions(args.xedit)
    validate_condition_crosscheck(functions, conditions)
    generated = generate_csharp(functions, conditions)

    if args.verify_only:
        if not args.output.is_file() or args.output.read_text(encoding="utf-8") != generated:
            raise SystemExit(f"generated output is stale: {args.output}")
        print(
            f"PASS: {len(functions)} named commands, "
            f"{sum(item.is_condition for item in functions)} condition handlers"
        )
        return 0

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(generated, encoding="utf-8", newline="\n")
    print(f"Wrote {args.output}")
    print(
        f"  {len(functions)} named game commands; "
        f"{sum(item.is_condition for item in functions)} condition handlers"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
