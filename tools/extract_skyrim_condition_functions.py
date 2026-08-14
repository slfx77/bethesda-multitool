"""Generate Skyrim's raw-index CTDA condition-function table.

The pinned Skyrim LE executable provides a 40-byte SCRIPT_FUNCTION array. Its
non-null condition callbacks are the engine-backed condition subset; the
matching linker map independently names the callback addresses. The pinned
xEdit TES5 definition supplies the user-facing names and CTDA parameter storage
kinds, plus rows absent from the LE artifact (including an explicit SKSE block).

This deliberately emits a condition-only raw-index table. It does not expose
xEdit community rows as a script-opcode or Papyrus command table.

Usage:
    python tools/extract_skyrim_condition_functions.py
    python tools/extract_skyrim_condition_functions.py --verify-only
"""

from __future__ import annotations

import argparse
import hashlib
import re
import struct
from dataclasses import dataclass
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_EXE = REPO_ROOT / "Sample" / "Skyrim" / "TESV.exe"
DEFAULT_MAP = REPO_ROOT / "Sample" / "Skyrim" / "TESV.map"
DEFAULT_XEDIT = (
    REPO_ROOT
    / "Sample"
    / "Reference_Code"
    / "TES5Edit"
    / "Core"
    / "wbDefinitionsTES5.pas"
)
DEFAULT_OUTPUT = (
    REPO_ROOT
    / "src"
    / "BethesdaMultitool"
    / "Core"
    / "Formats"
    / "Esm"
    / "Script"
    / "SkyrimConditionFunctionTable.Generated.cs"
)

EXPECTED_SHA256 = {
    "exe": "311E71737B597DDC02A8D26D83BB5B0B2896C9041A69F580E1B4DE875C4BB8BD",
    "map": "FED7F0B964EA752FE677F4C413C37C97B5CAD21541755C69C701A66423B288B2",
    "xedit": "621697E36E806C6308B11E3FE125C0BBB8CE783BCC7704DBD05A7B1BF9E40390",
}
XEDIT_SOURCE_COMMIT = "e0e529a2d473756520f2d41f72c24dea0cf5ee0d"

SCRIPT_FUNCTION_TABLE_VA = 0x01580BD0
SCRIPT_FUNCTION_SIZE = 40
SCRIPT_PARAMETER_SIZE = 12
ENGINE_COMMAND_COUNT = 727
ENGINE_SENTINEL_INDEX = 727
ENGINE_SLOT_COUNT = ENGINE_COMMAND_COUNT + 1
ENGINE_SENTINEL_NAME = "ADD NEW FUNCTIONS BEFORE THIS ONE!!!"
ENGINE_CONDITION_COUNT = 391
ENGINE_UNIQUE_CONDITION_HANDLER_COUNT = 379
ENGINE_PARAMETER_COUNT = 704
ENGINE_MAX_PARAMETER_TYPE = 82
MAP_CONDITION_SYMBOL_COUNT = 387
XEDIT_CONDITION_COUNT = 402
POST_ARTIFACT_INDICES = frozenset(range(730, 736))
SKSE_INDICES = frozenset(range(1024, 1029))
TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT = 49
TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT = 50

# These are display-name aliases at the same raw index, not membership disputes.
# Generated UI names intentionally follow xEdit; the executable spelling is kept
# here as a pinned engine cross-check.
EXPECTED_ENGINE_NAME_ALIASES = {
    327: ("IsRidingHorse", "IsRidingMount"),
    339: ("IsPlayersLastRiddenHorse", "IsPlayersLastRiddenMount"),
    519: ("GetIsLockBroken", "GetLockIsBroken"),
    680: ("GetActivationHeight", "GetActivatorHeight"),
    681: ("EPModSkillUsage_IsAdvanceSkill", "EPMagic_IsAdvanceSkill"),
    725: ("GetKnockStateEnum", "GetKnockedStateEnum"),
}

XEDIT_NUMERIC_TYPES = {
    "ptFloat",
    "ptInteger",
    "ptString",
    "ptAlias",
    "ptEvent",
    "ptPackdata",
    "ptQuestStage",
    "ptActorValue",
    "ptAlignment",
    "ptAxis",
    "ptCastingSource",
    "ptCrimeType",
    "ptCriticalStage",
    "ptFormType",
    "ptFurnitureAnim",
    "ptFurnitureEntry",
    "ptMiscStat",
    "ptPlayerAction",
    "ptSex",
    "ptVATSValueFunction",
    "ptWardState",
}

XEDIT_FORM_ID_TYPES = {
    "ptActor",
    "ptActorBase",
    "ptAssociationType",
    "ptBaseObject",
    "ptCell",
    "ptClass",
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
    "ptInventoryObject",
    "ptKeyword",
    "ptKnowable",
    "ptLocation",
    "ptLocationRefType",
    "ptMagicEffect",
    "ptOwner",
    "ptPackage",
    "ptPerk",
    "ptQuest",
    "ptRace",
    "ptReference",
    "ptRegion",
    "ptScene",
    "ptShout",
    "ptVoiceType",
    "ptWeather",
    "ptWorldspace",
}

XEDIT_NUMERIC_TYPES_NORMALIZED = {item.casefold() for item in XEDIT_NUMERIC_TYPES}
XEDIT_FORM_ID_TYPES_NORMALIZED = {item.casefold() for item in XEDIT_FORM_ID_TYPES}
XEDIT_TYPE_OVERRIDE_ELIGIBLE = {"ptreference", "ptactor", "ptpackage"}


@dataclass(frozen=True)
class Section:
    virtual_address: int
    virtual_size: int
    raw_pointer: int
    raw_size: int


@dataclass(frozen=True)
class EngineFunction:
    index: int
    name: str
    is_reference: bool
    condition_pointer: int


@dataclass(frozen=True)
class XEditCondition:
    index: int
    name: str
    param_types: tuple[str, str, str]


class PeImage:
    def __init__(self, path: Path) -> None:
        self.data = path.read_bytes()
        if self.data[:2] != b"MZ":
            raise SystemExit(f"not a PE image: {path}")
        pe_offset = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[pe_offset : pe_offset + 4] != b"PE\0\0":
            raise SystemExit(f"missing PE signature: {path}")
        coff = pe_offset + 4
        section_count = struct.unpack_from("<H", self.data, coff + 2)[0]
        optional_size = struct.unpack_from("<H", self.data, coff + 16)[0]
        optional = coff + 20
        if struct.unpack_from("<H", self.data, optional)[0] != 0x10B:
            raise SystemExit("Skyrim oracle must be a 32-bit PE image")
        self.image_base = struct.unpack_from("<I", self.data, optional + 28)[0]
        section_table = optional + optional_size
        self.sections: list[Section] = []
        for index in range(section_count):
            offset = section_table + index * 40
            virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from(
                "<IIII", self.data, offset + 8
            )
            self.sections.append(
                Section(virtual_address, virtual_size, raw_pointer, raw_size)
            )

    def va_to_offset(self, va: int) -> int | None:
        rva = va - self.image_base
        for section in self.sections:
            span = max(section.virtual_size, section.raw_size)
            if section.virtual_address <= rva < section.virtual_address + span:
                offset = section.raw_pointer + rva - section.virtual_address
                return offset if 0 <= offset < len(self.data) else None
        return None

    def require_va(self, va: int, size: int, label: str) -> int:
        offset = self.va_to_offset(va)
        if offset is None or offset + size > len(self.data):
            raise SystemExit(f"unmapped {label} VA 0x{va:08X} ({size} bytes)")
        return offset

    def read_c_string(self, va: int, label: str) -> str:
        offset = self.require_va(va, 1, label)
        end = self.data.find(b"\0", offset, min(len(self.data), offset + 512))
        if end < 0:
            raise SystemExit(f"unterminated {label} string at VA 0x{va:08X}")
        try:
            value = self.data[offset:end].decode("ascii")
        except UnicodeDecodeError as exc:
            raise SystemExit(f"non-ASCII {label} string at VA 0x{va:08X}") from exc
        if not value:
            raise SystemExit(f"empty {label} string at VA 0x{va:08X}")
        return value


def require_hash(path: Path, kind: str) -> None:
    if not path.is_file():
        raise SystemExit(f"missing {kind} input: {path}")
    actual = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    expected = EXPECTED_SHA256[kind]
    if actual != expected:
        raise SystemExit(
            f"unexpected {kind} SHA-256 for {path}: expected {expected}, found {actual}"
        )


def parse_engine_functions(path: Path) -> list[EngineFunction]:
    pe = PeImage(path)
    table_offset = pe.require_va(
        SCRIPT_FUNCTION_TABLE_VA,
        ENGINE_SLOT_COUNT * SCRIPT_FUNCTION_SIZE,
        "scriptFunctions table",
    )
    functions: list[EngineFunction] = []
    parameter_count = 0
    max_parameter_type = 0
    for index in range(ENGINE_SLOT_COUNT):
        offset = table_offset + index * SCRIPT_FUNCTION_SIZE
        (
            name_pointer,
            _short_name_pointer,
            opcode,
            _help_pointer,
            packed_signature,
            parameters_pointer,
            _execute_pointer,
            _compile_pointer,
            condition_pointer,
            _flags,
        ) = struct.unpack_from("<10I", pe.data, offset)
        if opcode != 0x1000 + index:
            raise SystemExit(
                f"non-sequential scriptFunctions opcode at slot {index}: 0x{opcode:04X}"
            )
        name = pe.read_c_string(name_pointer, f"scriptFunctions[{index}].name")
        reference_flag = packed_signature & 0xFFFF
        declared_parameter_count = packed_signature >> 16
        if reference_flag not in (0, 1):
            raise SystemExit(
                f"unexpected reference flag {reference_flag} at scriptFunctions[{index}]"
            )
        if (declared_parameter_count == 0) != (parameters_pointer == 0):
            raise SystemExit(f"parameter pointer/count mismatch at scriptFunctions[{index}]")
        if declared_parameter_count:
            parameters_offset = pe.require_va(
                parameters_pointer,
                declared_parameter_count * SCRIPT_PARAMETER_SIZE,
                f"scriptFunctions[{index}].parameters",
            )
            for parameter_index in range(declared_parameter_count):
                parameter_offset = parameters_offset + parameter_index * SCRIPT_PARAMETER_SIZE
                parameter_name_pointer, raw_type, optional = struct.unpack_from(
                    "<III", pe.data, parameter_offset
                )
                pe.read_c_string(
                    parameter_name_pointer,
                    f"scriptFunctions[{index}].parameters[{parameter_index}].name",
                )
                if optional not in (0, 1):
                    raise SystemExit(
                        f"unexpected optional flag {optional} at "
                        f"scriptFunctions[{index}].parameters[{parameter_index}]"
                    )
                max_parameter_type = max(max_parameter_type, raw_type)
                parameter_count += 1
        if condition_pointer and pe.va_to_offset(condition_pointer) is None:
            raise SystemExit(
                f"unmapped condition callback 0x{condition_pointer:08X} at raw index {index}"
            )
        functions.append(
            EngineFunction(index, name, reference_flag == 1, condition_pointer)
        )

    sentinel = functions[ENGINE_SENTINEL_INDEX]
    if sentinel.name != ENGINE_SENTINEL_NAME or sentinel.condition_pointer != 0:
        raise SystemExit(f"unexpected Skyrim command-table sentinel: {sentinel}")
    if parameter_count != ENGINE_PARAMETER_COUNT:
        raise SystemExit(
            f"expected {ENGINE_PARAMETER_COUNT} engine parameters, found {parameter_count}"
        )
    if max_parameter_type != ENGINE_MAX_PARAMETER_TYPE:
        raise SystemExit(
            f"expected maximum engine parameter type {ENGINE_MAX_PARAMETER_TYPE}, "
            f"found {max_parameter_type}"
        )
    return functions[:ENGINE_COMMAND_COUNT]


def parse_map_condition_handlers(path: Path) -> list[tuple[str, int]]:
    text = path.read_text(encoding="latin-1", errors="strict")
    global_match = re.search(
        r"\?scriptFunctions@@3PAUSCRIPT_FUNCTION@@A\s+([0-9A-Fa-f]{8})\s+",
        text,
    )
    if not global_match or int(global_match.group(1), 16) != SCRIPT_FUNCTION_TABLE_VA:
        raise SystemExit("TESV.map does not pin scriptFunctions at VA 0x01580BD0")
    pattern = re.compile(
        r"^\s*[0-9A-Fa-f]{4}:[0-9A-Fa-f]{8}\s+"
        r"\?([A-Za-z0-9_$]+)ConditionFunction@(?:Script|FOScriptFunctions)@@\S*"
        r"\s+([0-9A-Fa-f]{8})\s+",
        re.MULTILINE,
    )
    handlers = [(name, int(va, 16)) for name, va in pattern.findall(text)]
    if len(handlers) != MAP_CONDITION_SYMBOL_COUNT:
        raise SystemExit(
            f"expected {MAP_CONDITION_SYMBOL_COUNT} map condition symbols, found {len(handlers)}"
        )
    if len({name.casefold() for name, _ in handlers}) != len(handlers):
        raise SystemExit("TESV.map condition symbols contain duplicate names")
    return handlers


def parse_xedit_conditions(path: Path) -> list[XEditCondition]:
    text = path.read_text(encoding="utf-8-sig", errors="strict")
    start = text.index("wbConditionFunctions : array[0..401]")
    end = text.index("function wbConditionDescFromIndex", start)
    table = text[start:end]
    if "// Added by SKSE" not in table:
        raise SystemExit("xEdit Skyrim table no longer contains the explicit SKSE provenance marker")
    entry_pattern = re.compile(
        r"\(Index:\s*(\d+);\s*Name:\s*'((?:''|[^'])+)'(?P<body>[^\r\n]*)\)",
        re.IGNORECASE,
    )
    parameter_pattern = re.compile(
        r"ParamType([123])\s*:\s*(pt[A-Za-z0-9_]+)", re.IGNORECASE
    )
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
    if len(conditions) != XEDIT_CONDITION_COUNT:
        raise SystemExit(
            f"expected {XEDIT_CONDITION_COUNT} xEdit conditions, found {len(conditions)}"
        )
    if len({condition.index for condition in conditions}) != len(conditions):
        raise SystemExit("xEdit condition functions contain duplicate raw indices")
    if conditions != sorted(conditions, key=lambda item: item.index):
        raise SystemExit("xEdit condition functions are not sorted by raw index")
    return conditions


def validate_crosscheck(
    functions: list[EngineFunction],
    handlers: list[tuple[str, int]],
    conditions: list[XEditCondition],
) -> None:
    engine = {function.index: function for function in functions if function.condition_pointer}
    if len(engine) != ENGINE_CONDITION_COUNT:
        raise SystemExit(
            f"expected {ENGINE_CONDITION_COUNT} engine condition entries, found {len(engine)}"
        )
    engine_handler_addresses = {function.condition_pointer for function in engine.values()}
    map_handler_addresses = {address for _, address in handlers}
    if len(engine_handler_addresses) != ENGINE_UNIQUE_CONDITION_HANDLER_COUNT:
        raise SystemExit(
            f"expected {ENGINE_UNIQUE_CONDITION_HANDLER_COUNT} unique engine handlers, "
            f"found {len(engine_handler_addresses)}"
        )
    if engine_handler_addresses != map_handler_addresses:
        raise SystemExit(
            "TESV.exe condition callback addresses do not exactly match TESV.map condition symbols"
        )

    xedit = {condition.index: condition for condition in conditions}
    expected_engine_indices = set(xedit) - POST_ARTIFACT_INDICES - SKSE_INDICES
    if set(engine) != expected_engine_indices:
        raise SystemExit(
            f"condition index mismatch: engine-only={sorted(set(engine) - expected_engine_indices)}, "
            f"xEdit-only-before-extension={sorted(expected_engine_indices - set(engine))}"
        )
    if {index for index in xedit if 730 <= index < 1024} != POST_ARTIFACT_INDICES:
        raise SystemExit("unexpected untagged xEdit rows after the pinned LE engine table")
    if {index for index in xedit if index >= 1024} != SKSE_INDICES:
        raise SystemExit("unexpected Skyrim extension rows outside the pinned SKSE set")

    aliases: dict[int, tuple[str, str]] = {}
    for index, function in engine.items():
        xedit_name = xedit[index].name
        if function.name.casefold() != xedit_name.casefold():
            aliases[index] = (function.name, xedit_name)
    if aliases != EXPECTED_ENGINE_NAME_ALIASES:
        raise SystemExit(
            f"unexpected engine/xEdit condition-name aliases: expected "
            f"{EXPECTED_ENGINE_NAME_ALIASES}, found {aliases}"
        )

    known_types = (
        XEDIT_NUMERIC_TYPES_NORMALIZED
        | XEDIT_FORM_ID_TYPES_NORMALIZED
        | {"ptnone", "ptvatsvalueparam"}
    )
    for condition in conditions:
        for parameter_type in condition.param_types:
            if parameter_type.casefold() not in known_types:
                raise SystemExit(f"unclassified xEdit condition type {parameter_type}")
    vats = xedit.get(407)
    if vats is None or vats.name != "GetVATSValue" or vats.param_types[:2] != (
        "ptVATSValueFunction",
        "ptVATSValueParam",
    ):
        raise SystemExit("unexpected Skyrim GetVATSValue dependent-parameter metadata")

    eligible_functions = sum(
        any(
            parameter_type.casefold() in XEDIT_TYPE_OVERRIDE_ELIGIBLE
            for parameter_type in condition.param_types[:2]
        )
        for condition in conditions
    )
    eligible_slots = sum(
        parameter_type.casefold() in XEDIT_TYPE_OVERRIDE_ELIGIBLE
        for condition in conditions
        for parameter_type in condition.param_types[:2]
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


def condition_kind(parameter_type: str) -> str | None:
    normalized = parameter_type.casefold()
    if normalized in {"ptnone", "ptvatsvalueparam"}:
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


def generate_csharp(
    functions: list[EngineFunction], conditions: list[XEditCondition]
) -> str:
    engine = {function.index: function for function in functions if function.condition_pointer}
    lines = [
        "// <auto-generated>",
        "// Generated by tools/extract_skyrim_condition_functions.py from pinned local oracles.",
        f"// Skyrim LE TESV.exe SHA-256: {EXPECTED_SHA256['exe']}",
        f"// Skyrim LE TESV.map SHA-256: {EXPECTED_SHA256['map']}",
        f"// xEdit source commit: {XEDIT_SOURCE_COMMIT}",
        f"// xEdit wbDefinitionsTES5.pas SHA-256: {EXPECTED_SHA256['xedit']}",
        "// The LE executable has 727 commands plus one sentinel and exactly 391 condition entries.",
        "// Their 379 unique callback addresses exactly match the map's 379 unique condition-handler VAs",
        "// (387 named symbols). xEdit display names match 385 raw indices and have six pinned aliases.",
        "// Community provenance (MPL-2.0): CTDA storage kinds, six untagged xEdit rows 730..735",
        "// absent from the pinned LE artifact, and five rows explicitly marked Added by SKSE (1024..1028).",
        "// This is a raw-index condition table, not a Skyrim script-opcode or Papyrus command table.",
        "// GetVATSValue param2 remains null here because runtime classification depends on param1's value.",
        "// </auto-generated>",
        "",
        "using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;",
        "",
        "namespace BethesdaMultitool.Core.Formats.Esm.Script;",
        "",
        "internal static class SkyrimConditionFunctionTable",
        "{",
        f"    internal const int LocalEngineCommandCount = {ENGINE_COMMAND_COUNT};",
        "    internal const int LocalEngineSentinelSlotCount = 1;",
        f"    internal const int LocalEngineConditionFunctionCount = {ENGINE_CONDITION_COUNT};",
        f"    internal const int LocalEngineUniqueConditionHandlerCount = {ENGINE_UNIQUE_CONDITION_HANDLER_COUNT};",
        f"    internal const int MapConditionSymbolCount = {MAP_CONDITION_SYMBOL_COUNT};",
        f"    internal const int LocalEngineExactDisplayNameCount = {ENGINE_CONDITION_COUNT - len(EXPECTED_ENGINE_NAME_ALIASES)};",
        f"    internal const int LocalEngineAliasDisplayNameCount = {len(EXPECTED_ENGINE_NAME_ALIASES)};",
        f"    internal const int XEditPostArtifactConditionCount = {len(POST_ARTIFACT_INDICES)};",
        f"    internal const int SkseExtensionConditionCount = {len(SKSE_INDICES)};",
        f"    internal const int ConditionFunctionCount = {XEDIT_CONDITION_COUNT};",
        f"    internal const int TypeOverrideEligibleFunctionCount = {TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT};",
        f"    internal const int TypeOverrideEligibleSlotCount = {TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT};",
        "    internal const ushort VatsValueFunctionIndex = 407;",
        "",
        "    // Raw CTDA index -> xEdit-facing condition definition. The 391 entries present in the",
        "    // pinned LE executable retain its reference-function bit; later/community-only rows do not",
        "    // invent script-call metadata. Parameter storage is authoritative in ConditionParamKinds.",
        "    internal static readonly Dictionary<ushort, ScriptFunctionDef> ConditionFunctions = new()",
        "    {",
    ]
    for condition in conditions:
        engine_function = engine.get(condition.index)
        is_reference = engine_function.is_reference if engine_function is not None else False
        lines.append(
            f'        [0x{condition.index:04X}] = new("{csharp_string(condition.name)}", "", '
            f"{'true' if is_reference else 'false'}, [], true),"
        )
    lines.extend(
        [
            "    };",
            "",
            "    // Base storage kinds from xEdit. Null/omitted slots fail closed as raw values.",
            "    // GetVATSValue param2 is deliberately null and classified from param1 at runtime.",
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
            "    // TES5 applies Type.UseAliases/UsePackdata only to declared Reference, Actor, or",
            "    // Package slots. This is raw-index metadata, separate from the base FormID kind.",
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
    parser.add_argument("--map", type=Path, default=DEFAULT_MAP)
    parser.add_argument("--xedit", type=Path, default=DEFAULT_XEDIT)
    parser.add_argument("-o", "--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()

    require_hash(args.exe, "exe")
    require_hash(args.map, "map")
    require_hash(args.xedit, "xedit")
    functions = parse_engine_functions(args.exe)
    handlers = parse_map_condition_handlers(args.map)
    conditions = parse_xedit_conditions(args.xedit)
    validate_crosscheck(functions, handlers, conditions)
    generated = generate_csharp(functions, conditions)

    if args.verify_only:
        if not args.output.is_file():
            raise SystemExit(f"generated output is missing: {args.output}")
        actual = args.output.read_text(encoding="utf-8")
        if actual != generated:
            raise SystemExit(f"generated output is stale: {args.output}")
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(generated, encoding="utf-8", newline="\n")

    print(
        "PASS: 727 Skyrim LE commands; 391 engine conditions / 379 handler VAs; "
        "402 xEdit rows (6 post-artifact + 5 SKSE)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
