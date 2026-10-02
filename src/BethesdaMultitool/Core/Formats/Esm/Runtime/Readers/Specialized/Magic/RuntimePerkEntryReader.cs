using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.Magic;

/// <summary>
/// Reads polymorphic perk entries using captured RTTI, never the value of a union as a type tag.
/// Layouts agree in the supplied July 2010, August 2010 and February 2011 Xbox PDBs.
/// See docs/new_vegas_runtime_perks.md for calibration and partial-capture boundaries.
/// </summary>
internal sealed class RuntimePerkEntryReader(RuntimeMemoryContext context)
{
    private const int MaxNodes = 256;
    private const string LayoutBasis = "Xbox 360 class layouts agree in July/August 2010 and February 2011 PDBs; earlier executable layouts may differ.";
    private readonly Dictionary<uint, string?> _classes = [];

    public List<PerkEntry> ReadEntries(byte[] owner, int offset, List<string>? issues = null)
    {
        var result = new List<PerkEntry>();
        foreach (var pointer in WalkList(owner, offset, issues, "Entry"))
        {
            if (ReadEntry(pointer) is { } entry) { result.Add(entry); }
            else { issues?.Add($"Entry 0x{pointer:X8}: Unavailable."); }
        }
        return result;
    }

    internal PerkEntry? ReadEntry(uint address)
    {
        var header = Read(address, 8);
        if (header is null) { return null; }
        var className = ClassName(header);
        var type = className switch
        {
            "BGSQuestPerkEntry" => (byte)0,
            "BGSAbilityPerkEntry" => (byte)1,
            "BGSEntryPointPerkEntry" => (byte)2,
            _ => byte.MaxValue
        };
        var size = type switch { 0 => 16, 1 => 12, 2 => 20, _ => 8 };
        var bytes = Read(address, size);
        var entry = new PerkEntry
        {
            Type = type, Rank = header[4], Priority = header[5],
            RuntimeAddress = address, RuntimeClassName = className,
            RuntimeLayoutBasis = LayoutBasis,
            RuntimeLayoutStatus = type == byte.MaxValue
                ? "RTTI class unavailable or unsupported; base offsets are inferred."
                : "Captured RTTI class matched; field offsets are not independently verified against this executable.",
            RuntimeRawData = bytes ?? header
        };
        if (type == byte.MaxValue)
        {
            return entry with { RecoveryIssues = ["Entry class: Unavailable or unsupported; only the captured base header was decoded."] };
        }
        if (bytes is null)
        {
            return entry with { RecoveryIssues = ["Entry payload: Unavailable; the derived structure is not fully captured."] };
        }
        if (type is 0 or 1)
        {
            var pointer = BinaryUtils.ReadUInt32BE(bytes, 8);
            var formId = pointer == 0 ? null : context.FollowPointerVaToFormId(pointer);
            var issues = pointer != 0 && formId is null
                ? new List<string> { "Referenced form: Unavailable; pointer target is not captured or invalid." }
                : [];
            return type == 0
                ? entry with { QuestFormId = formId, QuestStage = bytes[12], RecoveryIssues = issues }
                : entry with { AbilityFormId = formId, RecoveryIssues = issues };
        }

        var diagnostics = new List<string>();
        var groups = ReadGroups(BinaryUtils.ReadUInt32BE(bytes, 16), bytes[10], diagnostics);
        entry = entry with
        {
            EntryPoint = bytes[8], EntryPointFunction = bytes[9], PerkConditionTabCount = bytes[10],
            ConditionGroups = groups, RecoveryIssues = diagnostics
        };
        return ReadFunctionData(entry, BinaryUtils.ReadUInt32BE(bytes, 12), diagnostics);
    }

    private PerkEntry ReadFunctionData(PerkEntry entry, uint address, List<string> issues)
    {
        // A null function-data object represents NONE, distinct from an uncaptured non-null pointer.
        if (address == 0) { return entry with { FunctionType = 0 }; }
        var header = Read(address, 4);
        var className = header is null ? null : ClassName(header);
        byte? kind = className switch
        {
            "BGSEntryPointFunctionDataOneValue" => 1,
            "BGSEntryPointFunctionDataTwoValue" => 2,
            "BGSEntryPointFunctionDataLeveledList" => 3,
            "BGSEntryPointFunctionDataActivateChoice" => 4,
            _ => null
        };
        var size = kind switch { 1 or 3 => 8, 2 => 12, 4 => 112, _ => 4 };
        var bytes = Read(address, size);
        entry = entry with
        {
            FunctionType = kind, RuntimeFunctionAddress = address,
            RuntimeFunctionClassName = className, RuntimeFunctionData = bytes ?? header
        };
        if (kind is null || bytes is null)
        {
            issues.Add("Function data: Unavailable; class or complete payload is not captured/supported.");
            return entry;
        }
        if (kind is 1 or 2)
        {
            var first = ReadFiniteFloat(bytes, 4, issues);
            var second = kind == 2 ? ReadFiniteFloat(bytes, 8, issues) : null;
            return entry with { EffectValue = first, EffectValue2 = second };
        }
        if (kind == 3)
        {
            var pointer = BinaryUtils.ReadUInt32BE(bytes, 4);
            var formId = pointer == 0 ? null : context.FollowPointerVaToFormId(pointer);
            if (pointer != 0 && formId is null) { issues.Add("Leveled list: Unavailable; referenced form is not captured or invalid."); }
            return entry with { EffectFormId = formId };
        }
        var labelPointer = BinaryUtils.ReadUInt32BE(bytes, 4);
        var label = context.ReadNullTerminatedAsciiString(labelPointer, 4096);
        if (labelPointer != 0 && label is null) { issues.Add("Activation label: Unavailable or not printable ASCII."); }
        var script = new RuntimeScriptReader(context).ReadInlineResultScript(address + 8);
        if (script is null || script.IsIncompleteExecutableBundle) { issues.Add("Activation script: Unavailable or incomplete."); }
        return entry with
        {
            ActivationLabel = label, ActivationFlags = BinaryUtils.ReadUInt16BE(bytes, 108),
            ActivationScript = script
        };
    }

    private List<PerkConditionGroup> ReadGroups(uint address, byte count, List<string> issues)
    {
        var result = new List<PerkConditionGroup>();
        for (var index = 0; index < count; index++)
        {
            var va64 = (ulong)address + (uint)index * 8;
            var va = va64 <= uint.MaxValue ? (uint)va64 : 0;
            var head = address != 0 ? Read(va, 8) : null;
            var groupIssues = new List<string>();
            var conditions = head is null ? [] : ReadConditions(head, 0, groupIssues);
            if (head is null) { groupIssues.Add("Condition list head: Unavailable."); }
            result.Add(new PerkConditionGroup
            {
                RunOn = unchecked((sbyte)index), RuntimeAddress = va,
                Conditions = conditions, RecoveryIssues = groupIssues
            });
            if (groupIssues.Count > 0) { issues.Add($"Condition tab {index}: Incomplete; see group diagnostics."); }
        }
        return result;
    }

    public List<PerkCondition> ReadConditions(byte[] owner, int offset, List<string>? issues = null)
    {
        var result = new List<PerkCondition>();
        foreach (var item in WalkList(owner, offset, issues, "Condition"))
        {
            if (ReadCondition(item) is { } condition) { result.Add(condition); }
            else { issues?.Add($"Condition 0x{item:X8}: Unavailable."); }
        }
        return result;
    }

    private IEnumerable<uint> WalkList(byte[] owner, int offset, List<string>? issues, string kind)
    {
        if (offset < 0 || offset > owner.Length - 8) { issues?.Add($"{kind} list head: Unavailable."); yield break; }
        var node = owner.AsSpan(offset, 8).ToArray();
        var visited = new HashSet<uint>();
        for (var count = 0; count < MaxNodes; count++)
        {
            var item = BinaryUtils.ReadUInt32BE(node);
            if (item != 0) { yield return item; }
            var next = BinaryUtils.ReadUInt32BE(node, 4);
            if (next == 0) { yield break; }
            if (!visited.Add(next)) { issues?.Add($"{kind} list: Cycle detected."); yield break; }
            var nextNode = Read(next, 8);
            if (nextNode is null) { issues?.Add($"{kind} list: Next node unavailable."); yield break; }
            node = nextNode;
        }
        issues?.Add($"{kind} list: Limited to {MaxNodes} nodes.");
    }

    private PerkCondition? ReadCondition(uint address)
    {
        var bytes = Read(address, 28);
        if (bytes is null) { return null; }
        var flags = bytes[0];
        var function = BinaryUtils.ReadUInt16BE(bytes, 8);
        var issues = new List<string>();
        if ((flags >> 5) > 5) { issues.Add("Comparison operator: Unsupported; raw type byte retained."); }
        var first = ResolveParameter(BinaryUtils.ReadUInt32BE(bytes, 12), function, 0, issues);
        var second = ResolveParameter(BinaryUtils.ReadUInt32BE(bytes, 16), function, 1, issues);
        var p1 = PerkConditionParameterResolver.ResolveParameter(function, 0, first.Value);
        var p2 = PerkConditionParameterResolver.ResolveParameter(function, 1, second.Value);
        var runOn = BinaryUtils.ReadUInt32BE(bytes, 20);
        var reference = DialogueConditionReferencePolicy.IsSemanticReferenceSlot(function, runOn, BethesdaGame.FalloutNewVegas)
            ? context.FollowPointerVaToFormId(BinaryUtils.ReadUInt32BE(bytes, 24)) : null;
        if (reference is null && BinaryUtils.ReadUInt32BE(bytes, 24) != 0 &&
            DialogueConditionReferencePolicy.IsSemanticReferenceSlot(function, runOn, BethesdaGame.FalloutNewVegas))
        {
            issues.Add("Run-on reference: Unavailable; raw pointer retained.");
        }
        uint? global = null;
        float value = 0;
        if ((flags & 4) != 0)
        {
            global = context.FollowPointerVaToFormId(BinaryUtils.ReadUInt32BE(bytes, 4));
            if (global is null) { issues.Add("Comparison global: Unavailable."); }
        }
        else { value = ReadFiniteFloat(bytes, 4, issues) ?? 0; }
        return new PerkCondition
        {
            FunctionIndex = function, FunctionName = PerkConditionParameterResolver.ResolveScriptFunctionName(function),
            Parameter1 = first.Value, Parameter1FormId = first.FormId, Parameter1Display = p1.Display,
            Parameter2 = second.Value, Parameter2FormId = second.FormId, Parameter2Display = p2.Display,
            ComparisonOperator = (byte)(flags >> 5), ComparisonValue = value, Flags = flags,
            RunOn = runOn, ReferenceFormId = reference, ComparisonGlobalFormId = global,
            RuntimeAddress = address, RuntimeRawData = bytes, RecoveryIssues = issues,
            RuntimeLayoutBasis = LayoutBasis + " CONDITION_ITEM_DATA interpreted as 28 bytes."
        };
    }

    private (uint Value, uint? FormId) ResolveParameter(uint raw, ushort function, int index, List<string> issues)
    {
        if (!PerkConditionParameterResolver.IsFormParameter(function, index)) { return (raw, null); }
        if (raw == 0) { return (0, null); }
        var id = context.FollowPointerVaToFormId(raw);
        if (id is null) { issues.Add($"Parameter {index + 1}: Referenced form unavailable; raw pointer retained."); }
        return (id ?? raw, id);
    }

    private static float? ReadFiniteFloat(byte[] bytes, int offset, List<string> issues)
    {
        var value = BinaryUtils.ReadFloatBE(bytes, offset);
        if (float.IsFinite(value)) { return value; }
        issues.Add($"Float at +{offset}: Non-finite; raw bytes retained.");
        return null;
    }

    private byte[]? Read(uint address, int size) => address == 0 ? null : context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(address), size);

    private string? ClassName(byte[] header)
    {
        var vtable = BinaryUtils.ReadUInt32BE(header);
        if (_classes.TryGetValue(vtable, out var cached)) { return cached; }
        string? name = null;
        if (Xbox360MemoryUtils.IsModulePointer(vtable) && Read(vtable - 4, 4) is { } slot)
        {
            var locator = BinaryUtils.ReadUInt32BE(slot);
            if (Xbox360MemoryUtils.IsModulePointer(locator) && Read(locator, 20) is { } col &&
                BinaryUtils.ReadUInt32BE(col) == 0 && BinaryUtils.ReadUInt32BE(col, 4) == 0 &&
                BinaryUtils.ReadUInt32BE(col, 8) == 0)
            {
                var descriptor = BinaryUtils.ReadUInt32BE(col, 12);
                if (Xbox360MemoryUtils.IsModulePointer(descriptor) && descriptor <= uint.MaxValue - 8)
                {
                    var mangled = context.ReadNullTerminatedAsciiString(descriptor + 8, 256);
                    if (mangled is not null) { name = RttiReader.DemangleName(mangled); }
                }
            }
        }
        _classes[vtable] = name;
        return name;
    }
}
