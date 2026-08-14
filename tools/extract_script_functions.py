"""Generate the pinned final Xbox 360 Fallout: New Vegas script tables.

The PDB supplies the authoritative ``SCRIPT_FUNCTION`` layout and the exact
``scriptConsole``/``scriptFunctions`` locations.  The executable supplies the
definitions themselves.  A game command is CTDA-capable only when its
``pConditionFunction`` pointer at ``SCRIPT_FUNCTION + 32`` is non-null.

The local research inputs are intentionally not distributable; the generated
C# output and this hash-pinned generator are tracked.

Usage:
    python tools/extract_script_functions.py
    python tools/extract_script_functions.py --verify-only
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
DEFAULT_BUILD = (
    REPO_ROOT
    / "Sample"
    / "Full_Builds"
    / "Fallout New Vegas (Aug 22, 2010)"
    / "Diskuild_1.0.0.252"
)
DEFAULT_EXE = DEFAULT_BUILD / "Fallout.exe"
DEFAULT_PDB = DEFAULT_BUILD / "Fallout.pdb"
DEFAULT_CVDUMP = REPO_ROOT / "tools" / "microsoft-pdb" / "cvdump" / "cvdump.exe"
DEFAULT_OUTPUT = (
    REPO_ROOT
    / "src"
    / "BethesdaMultitool"
    / "Core"
    / "Formats"
    / "Esm"
    / "Script"
    / "ScriptFunctionTable.Generated.cs"
)

EXPECTED_SHA256 = {
    "exe": "A43DFE9025A0FACD0EF862E89A83C05C11A5CB3A9FE53EFEEC18C0278F75F0A6",
    "pdb": "EC702DC52F42A9C14037B800871D727AF2B54ED191D68AE5FCDE3BFCE65E6F00",
}

XEX_IMAGE_BASE = 0x82000000
FUNCTION_SIZE = 40
PARAMETER_SIZE = 12
CONSOLE_OPCODE_BASE = 0x0100
GAME_OPCODE_BASE = 0x1000
EXPECTED_CONSOLE_COUNT = 205
EXPECTED_GAME_COUNT = 624
EXPECTED_ENGINE_SLOT_COUNT = 625
EXPECTED_CONDITION_COUNT = 250
EXPECTED_SENTINEL_OPCODE = GAME_OPCODE_BASE + EXPECTED_GAME_COUNT
EXPECTED_SENTINEL_NAME = "ADD NEW FUNCTIONS BEFORE THIS ONE!!!"
EXPECTED_PDB_LOCATIONS = {
    "scriptConsole": (7, 0x27008),
    "scriptFunctions": (7, 0x29038),
}

# Type records in the pinned final PDB.  The input hash makes these identifiers
# stable; validating the records prevents the extraction offsets from becoming
# unaudited magic numbers.
SCRIPT_FUNCTION_TYPE = 0x0001FE1B
SCRIPT_FUNCTION_FIELDS = 0x0001FE1A
SCRIPT_PARAMETER_TYPE = 0x0001E8ED
SCRIPT_PARAMETER_FIELDS = 0x0001E8EC

# SCRIPT_PARAM_TYPE enum names from the pinned PDB.  Keep this classic FNV enum
# separate from the game-specific TES4/FO4 parameter domains.
PARAM_TYPE_NAMES = {
    0: "Char", 1: "Int", 2: "Float", 3: "InventoryObject", 4: "ObjectRef",
    5: "ActorValue", 6: "Actor", 7: "SpellItem", 8: "Axis", 9: "Cell",
    10: "AnimGroup", 11: "MagicItem", 12: "Sound", 13: "Topic", 14: "Quest",
    15: "Race", 16: "Class", 17: "Faction", 18: "Sex", 19: "Global",
    20: "FurnitureOrFormList", 21: "Object", 22: "ScriptVar", 23: "Stage",
    24: "MapMarker", 25: "ActorBase", 26: "ContainerRef", 27: "World",
    28: "CrimeType", 29: "Package", 30: "CombatStyle", 31: "MagicEffect",
    32: "FormType", 33: "Weather", 34: "Npc", 35: "Owner",
    36: "ShaderEffect", 37: "FormList", 38: "MenuIcon", 39: "Perk",
    40: "Note", 41: "MiscStat", 42: "ImageSpaceMod", 43: "ImageSpace",
    44: "VatsValue", 45: "VatsValueData", 46: "VoiceType", 47: "EncounterZone",
    48: "IdleForm", 49: "Message", 50: "InvObjectOrFormList", 51: "Alignment",
    52: "EquipType", 53: "ObjectOrFormList", 54: "Music", 55: "CritStage",
    56: "NpcOrLevChar", 57: "CreaOrLevCrea", 58: "LevChar", 59: "LevCrea",
    60: "LevItem", 61: "Form", 62: "Reputation", 63: "Casino",
    64: "CasinoChip", 65: "Challenge", 66: "CaravanMoney", 67: "CaravanCard",
    68: "CaravanDeck", 69: "Region",
}


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


def collect_type_blocks(cvdump: Path, pdb: Path) -> dict[int, list[str]]:
    wanted = {
        SCRIPT_FUNCTION_TYPE,
        SCRIPT_FUNCTION_FIELDS,
        SCRIPT_PARAMETER_TYPE,
        SCRIPT_PARAMETER_FIELDS,
    }
    blocks: dict[int, list[str]] = {}
    current: int | None = None
    type_start = re.compile(r"^\s*(0x[0-9a-fA-F]+)\s*:")
    for line in cvdump_lines(cvdump, "-t", pdb):
        match = type_start.match(line)
        if match:
            current = int(match.group(1), 16)
            if current in wanted:
                blocks[current] = [line]
            continue
        if current in wanted:
            blocks[current].append(line)

    missing = wanted - blocks.keys()
    if missing:
        raise SystemExit(f"PDB type records missing: {[hex(item) for item in sorted(missing)]}")
    return blocks


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


def validate_pdb_types(blocks: dict[int, list[str]]) -> None:
    function_text = "".join(blocks[SCRIPT_FUNCTION_TYPE])
    parameter_text = "".join(blocks[SCRIPT_PARAMETER_TYPE])
    if "Size = 40, class name = SCRIPT_FUNCTION" not in function_text:
        raise SystemExit("PDB SCRIPT_FUNCTION is not the expected 40-byte structure")
    if "# members = 12" not in function_text:
        raise SystemExit("PDB SCRIPT_FUNCTION does not have the expected 12 fields")
    if "Size = 12, class name = SCRIPT_PARAMETER" not in parameter_text:
        raise SystemExit("PDB SCRIPT_PARAMETER is not the expected 12-byte structure")

    validate_field_offsets(
        "".join(blocks[SCRIPT_FUNCTION_FIELDS]),
        {
            "pFunctionName": 0,
            "pShortName": 4,
            "eOutput": 8,
            "pHelpString": 12,
            "bReferenceFunction": 16,
            "sParamCount": 18,
            "pParameters": 20,
            "pExecuteFunction": 24,
            "pCompileFunction": 28,
            "pConditionFunction": 32,
            "bEditorFilter": 36,
            "bInvalidatesCellList": 37,
        },
        "SCRIPT_FUNCTION",
    )
    validate_field_offsets(
        "".join(blocks[SCRIPT_PARAMETER_FIELDS]),
        {"pParamName": 0, "eParamType": 4, "bOptional": 8},
        "SCRIPT_PARAMETER",
    )


def find_pdb_locations(cvdump: Path, pdb: Path) -> dict[str, tuple[int, int]]:
    matches = {name: set() for name in EXPECTED_PDB_LOCATIONS}
    pattern = re.compile(
        r"S_GDATA32:\s*\[(\d+):([0-9A-Fa-f]+)\],.*?\b"
        r"(scriptConsole|scriptFunctions)\s*$",
        re.IGNORECASE,
    )
    for line in cvdump_lines(cvdump, "-g", pdb):
        match = pattern.search(line)
        if match:
            canonical_name = next(
                name for name in matches if name.casefold() == match.group(3).casefold()
            )
            matches[canonical_name].add((int(match.group(1)), int(match.group(2), 16)))

    locations: dict[str, tuple[int, int]] = {}
    for name, expected in EXPECTED_PDB_LOCATIONS.items():
        if matches[name] != {expected}:
            raise SystemExit(
                f"unexpected PDB {name} locations: expected {expected}, "
                f"found {sorted(matches[name])}"
            )
        locations[name] = expected
    return locations


class PeImage:
    def __init__(self, path: Path):
        self.data = path.read_bytes()
        try:
            pe_offset = struct.unpack_from("<I", self.data, 0x3C)[0]
            if self.data[pe_offset : pe_offset + 4] != b"PE\0\0":
                raise SystemExit(f"not a PE image: {path}")
            machine, section_count = struct.unpack_from("<HH", self.data, pe_offset + 4)
            optional_size = struct.unpack_from("<H", self.data, pe_offset + 20)[0]
            optional_offset = pe_offset + 24
            magic = struct.unpack_from("<H", self.data, optional_offset)[0]
            self.image_base = struct.unpack_from("<I", self.data, optional_offset + 28)[0]
        except struct.error as error:
            raise SystemExit(f"truncated PE headers: {path}") from error

        if machine != 0x01F2 or magic != 0x010B or self.image_base != XEX_IMAGE_BASE:
            raise SystemExit(
                "Fallout.exe must be the expected Xbox 360 PowerPC PE32 image "
                f"(machine=0x{machine:X}, magic=0x{magic:X}, base=0x{self.image_base:X})"
            )

        self.sections: list[Section] = []
        section_offset = optional_offset + optional_size
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
                Section(index + 1, name, virtual_size, virtual_address, raw_size, raw_pointer)
            )

    def section_offset(self, section_number: int, offset: int) -> int:
        section = next((item for item in self.sections if item.number == section_number), None)
        if section is None or offset < 0 or offset >= section.raw_size:
            raise SystemExit(f"invalid section-relative address [{section_number}:{offset:X}]")
        return section.raw_pointer + offset

    def va_to_offset(self, address: int) -> int | None:
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
                return offset if offset < len(self.data) else None
        return None

    def read_u16(self, offset: int) -> int:
        try:
            return struct.unpack_from(">H", self.data, offset)[0]
        except struct.error as error:
            raise SystemExit(f"out-of-range u16 read at file offset 0x{offset:X}") from error

    def read_u32(self, offset: int) -> int:
        try:
            return struct.unpack_from(">I", self.data, offset)[0]
        except struct.error as error:
            raise SystemExit(f"out-of-range u32 read at file offset 0x{offset:X}") from error

    def read_c_string(self, address: int, *, maximum: int = 1024) -> str:
        if address == 0:
            return ""
        offset = self.va_to_offset(address)
        if offset is None:
            raise SystemExit(f"unmapped string pointer 0x{address:08X}")
        end = self.data.find(b"\0", offset, min(len(self.data), offset + maximum))
        if end < 0:
            raise SystemExit(f"unterminated string pointer 0x{address:08X}")
        value = self.data[offset:end]
        if any(byte < 0x20 or byte > 0x7E for byte in value):
            raise SystemExit(f"non-ASCII string pointer 0x{address:08X}")
        return value.decode("ascii")


def extract_function(pe: PeImage, offset: int, expected_opcode: int) -> Function:
    name_pointer = pe.read_u32(offset)
    short_name_pointer = pe.read_u32(offset + 4)
    opcode = pe.read_u32(offset + 8)
    if opcode != expected_opcode:
        raise SystemExit(
            f"non-sequential function table at opcode 0x{expected_opcode:04X}: "
            f"found 0x{opcode:04X}"
        )

    name = pe.read_c_string(name_pointer)
    if not name:
        raise SystemExit(f"empty function name at opcode 0x{opcode:04X}")
    short_name = pe.read_c_string(short_name_pointer) if short_name_pointer else ""
    is_reference = pe.data[offset + 16] != 0
    parameter_count = pe.read_u16(offset + 18)
    parameter_pointer = pe.read_u32(offset + 20)

    # Pinned final-PDB field: SCRIPT_FUNCTION::pConditionFunction at byte 32.
    condition_pointer = pe.read_u32(offset + 32)
    if condition_pointer and pe.va_to_offset(condition_pointer) is None:
        raise SystemExit(
            f"unmapped condition callback 0x{condition_pointer:08X} "
            f"for opcode 0x{opcode:04X}"
        )

    if parameter_count > 32:
        raise SystemExit(f"implausible parameter count {parameter_count} at opcode 0x{opcode:04X}")
    if parameter_count and not parameter_pointer:
        raise SystemExit(f"null parameter pointer at opcode 0x{opcode:04X}")

    parameters: list[Parameter] = []
    if parameter_count:
        parameter_offset = pe.va_to_offset(parameter_pointer)
        if parameter_offset is None:
            raise SystemExit(
                f"unmapped parameter pointer 0x{parameter_pointer:08X} at opcode 0x{opcode:04X}"
            )
        for parameter_index in range(parameter_count):
            current = parameter_offset + parameter_index * PARAMETER_SIZE
            parameter_name_pointer = pe.read_u32(current)
            raw_type = pe.read_u32(current + 4)
            optional = pe.read_u32(current + 8) != 0
            if raw_type not in PARAM_TYPE_NAMES:
                raise SystemExit(
                    f"unknown SCRIPT_PARAM_TYPE {raw_type} at opcode 0x{opcode:04X} "
                    f"parameter {parameter_index}"
                )
            parameters.append(
                Parameter(pe.read_c_string(parameter_name_pointer), raw_type, optional)
            )

    return Function(
        opcode,
        name,
        short_name,
        is_reference,
        tuple(parameters),
        condition_pointer,
    )


def extract_tables(
    pe: PeImage, locations: dict[str, tuple[int, int]]
) -> tuple[list[Function], list[Function]]:
    console_start = pe.section_offset(*locations["scriptConsole"])
    console = [
        extract_function(pe, console_start + slot * FUNCTION_SIZE, CONSOLE_OPCODE_BASE + slot)
        for slot in range(EXPECTED_CONSOLE_COUNT)
    ]

    game_start = pe.section_offset(*locations["scriptFunctions"])
    game = [
        extract_function(pe, game_start + slot * FUNCTION_SIZE, GAME_OPCODE_BASE + slot)
        for slot in range(EXPECTED_GAME_COUNT)
    ]
    sentinel = extract_function(
        pe,
        game_start + EXPECTED_GAME_COUNT * FUNCTION_SIZE,
        EXPECTED_SENTINEL_OPCODE,
    )
    if sentinel.name != EXPECTED_SENTINEL_NAME or sentinel.parameters or sentinel.is_condition:
        raise SystemExit(
            "unexpected terminal scriptFunctions slot: "
            f"name={sentinel.name!r}, params={len(sentinel.parameters)}, "
            f"condition=0x{sentinel.condition_pointer:08X}"
        )
    return console, game


def validate_tables(console: list[Function], game: list[Function]) -> list[Function]:
    if len(console) != EXPECTED_CONSOLE_COUNT:
        raise SystemExit(
            f"expected {EXPECTED_CONSOLE_COUNT} console functions, found {len(console)}"
        )
    if len(game) != EXPECTED_GAME_COUNT:
        raise SystemExit(f"expected {EXPECTED_GAME_COUNT} game commands, found {len(game)}")
    if any(function.is_condition for function in console):
        raise SystemExit("console table unexpectedly contains condition callbacks")

    conditions = [function for function in game if function.is_condition]
    if len(conditions) != EXPECTED_CONDITION_COUNT:
        raise SystemExit(
            f"expected {EXPECTED_CONDITION_COUNT} condition callbacks, found {len(conditions)}"
        )

    by_raw_index = {function.opcode - GAME_OPCODE_BASE: function for function in game}
    if by_raw_index[0].name != "UnusedFunction0" or by_raw_index[0].is_condition:
        raise SystemExit("raw condition index 0 is not the expected non-condition sentinel command")
    if by_raw_index[1].name != "GetDistance" or not by_raw_index[1].is_condition:
        raise SystemExit("raw condition index 1 is not the expected GetDistance callback")
    if by_raw_index[2].name != "AddItem" or by_raw_index[2].is_condition:
        raise SystemExit("raw condition index 2 is not the expected script-only AddItem command")
    return conditions


def csharp_string(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\r", "\\r")
        .replace("\n", "\\n")
    )


def generate_csharp(
    console: list[Function], game: list[Function], conditions: list[Function]
) -> str:
    all_functions = console + game
    lines = [
        "// <auto-generated>",
        "// Generated by tools/extract_script_functions.py from the pinned final Xbox 360 FNV PDB/EXE.",
        f"// EXE SHA-256: {EXPECTED_SHA256['exe']}",
        f"// PDB SHA-256: {EXPECTED_SHA256['pdb']}",
        "// PDB SCRIPT_FUNCTION layout: 0x28 bytes; pConditionFunction is the pointer at +0x20.",
        f"// {len(console)} console definitions; {len(game)} game commands; "
        f"{len(conditions)} non-null retail condition callbacks.",
        "// The raw CTDA map is keyed by (game opcode - 0x1000) and reuses the command objects.",
        "// </auto-generated>",
        "",
        "namespace BethesdaMultitool.Core.Formats.Esm.Script;",
        "",
        "public static partial class ScriptFunctionTable",
        "{",
        f"    internal const int ConsoleFunctionCount = {EXPECTED_CONSOLE_COUNT};",
        f"    internal const int EngineSlotCount = {EXPECTED_ENGINE_SLOT_COUNT};",
        f"    internal const int GameCommandCount = {EXPECTED_GAME_COUNT};",
        f"    internal const int FunctionCount = {len(all_functions)};",
        f"    internal const int ConditionFunctionCount = {EXPECTED_CONDITION_COUNT};",
        "",
        "    private static readonly Dictionary<ushort, ScriptFunctionDef> _functions = new()",
        "    {",
    ]

    for function in all_functions:
        rendered_parameters = ", ".join(
            f'new("{csharp_string(parameter.name)}", '
            f"ScriptParamType.{PARAM_TYPE_NAMES[parameter.raw_type]}, "
            f"{'true' if parameter.optional else 'false'})"
            for parameter in function.parameters
        )
        lines.append(
            f'        [0x{function.opcode:04X}] = new("{csharp_string(function.name)}", '
            f'"{csharp_string(function.short_name)}", '
            f"{'true' if function.is_reference else 'false'}, [{rendered_parameters}], "
            f"{'true' if function.is_condition else 'false'}),"
        )

    lines.extend(
        [
            "    };",
            "",
            "    // Raw CTDA function index -> the matching game-command definition. A null",
            "    // pConditionFunction is deliberately absent even when a script command exists.",
            "    internal static readonly Dictionary<ushort, ScriptFunctionDef> ConditionFunctions = new()",
            "    {",
        ]
    )
    for function in conditions:
        raw_index = function.opcode - GAME_OPCODE_BASE
        lines.append(
            f"        [0x{raw_index:04X}] = _functions[0x{function.opcode:04X}],"
        )
    lines.extend(["    };", "}", ""])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--pdb", type=Path, default=DEFAULT_PDB)
    parser.add_argument("--cvdump", type=Path, default=DEFAULT_CVDUMP)
    parser.add_argument("-o", "--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()

    require_authoritative_input(args.exe, "exe")
    require_authoritative_input(args.pdb, "pdb")
    if not args.cvdump.is_file():
        raise SystemExit(f"missing cvdump: {args.cvdump}")

    blocks = collect_type_blocks(args.cvdump, args.pdb)
    validate_pdb_types(blocks)
    locations = find_pdb_locations(args.cvdump, args.pdb)
    console, game = extract_tables(PeImage(args.exe), locations)
    conditions = validate_tables(console, game)
    generated = generate_csharp(console, game, conditions)

    if args.verify_only:
        if not args.output.is_file() or args.output.read_text(encoding="utf-8") != generated:
            raise SystemExit(f"generated output is stale: {args.output}")
        print(
            f"PASS: {len(console)} console definitions; {len(game)} game commands; "
            f"{len(conditions)} condition callbacks"
        )
        return 0

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(generated, encoding="utf-8", newline="\n")
    print(f"Wrote {args.output}")
    print(
        f"  {len(console)} console definitions; {len(game)} game commands; "
        f"{len(conditions)} condition callbacks"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
