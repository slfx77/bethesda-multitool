using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.Magic;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime.Synthetic;

public sealed class RuntimePerkEntryReaderTests
{
    private const uint EntryVa = 0x40200000;
    private const uint FunctionVa = 0x40300000;
    private const uint TargetVa = 0x40400000;
    private const uint EntryVtable = 0x82000100;
    private const uint FunctionVtable = 0x82001100;

    [Theory]
    [InlineData(1, "BGSEntryPointFunctionDataOneValue", 8)]
    [InlineData(2, "BGSEntryPointFunctionDataTwoValue", 12)]
    [InlineData(3, "BGSEntryPointFunctionDataLeveledList", 8)]
    public void Function_class_controls_payload_and_survives_reports_and_encoding(byte kind, string className, int size)
    {
        var fixture = new Capture();
        fixture.AddRtti(EntryVtable, "BGSEntryPointPerkEntry");
        fixture.AddRtti(FunctionVtable, className);
        fixture.Add(EntryVa, Entry(20, FunctionVa));
        var function = new byte[size];
        U32(function, 0, FunctionVtable);
        if (kind == 3) { U32(function, 4, TargetVa); fixture.Add(TargetVa, Form(0x123456, 0x34)); }
        else { F32(function, 4, 1.5f); }
        if (kind == 2) { F32(function, 8, -2.25f); }
        fixture.Add(FunctionVa, function);

        var entry = Assert.IsType<PerkEntry>(fixture.Reader().ReadEntry(EntryVa));
        Assert.Equal((byte)2, entry.Type);
        Assert.Equal((byte)2, entry.EntryPoint);
        Assert.Equal((byte)3, entry.EntryPointFunction);
        Assert.Equal(kind, entry.FunctionType);
        Assert.Empty(entry.RecoveryIssues);
        Assert.Contains("not independently verified", entry.RuntimeLayoutStatus);
        Assert.Contains("earlier executable layouts may differ", entry.RuntimeLayoutBasis);
        Assert.Equal(function, entry.RuntimeFunctionData);
        Assert.Null(entry.RawFunctionData);
        if (kind == 3) { Assert.Equal(0x123456u, entry.EffectFormId); Assert.Null(entry.EffectValue); }
        else { Assert.Equal(1.5f, entry.EffectValue); }
        if (kind == 2) { Assert.Equal(-2.25f, entry.EffectValue2); }

        var perk = new PerkRecord { EditorId = "Calibrated", Entries = [entry] };
        var text = GeckEffectsWriter.GeneratePerksReport([perk]);
        var csv = CsvMiscWriter.GeneratePerksCsv([perk], FormIdResolver.Empty);
        Assert.Contains(className, text);
        Assert.Contains(Convert.ToHexString(function), text);
        Assert.Contains(className, csv);
        Assert.Contains(Convert.ToHexString(function), csv);
        var encoded = PerkEncoder.EncodeNew(perk);
        var epfd = Assert.Single(encoded.Subrecords, item => item.Signature == "EPFD").Bytes;
        Assert.Equal(kind == 2 ? 8 : 4, epfd.Length);
        if (kind == 3) { Assert.Equal(0x123456u, BinaryPrimitives.ReadUInt32LittleEndian(epfd)); }
        else { Assert.Equal(1.5f, BitConverter.ToSingle(epfd)); }
        if (kind == 2) { Assert.Equal(-2.25f, BitConverter.ToSingle(epfd, 4)); }
    }

    [Theory]
    [InlineData("BGSQuestPerkEntry", 0, 16)]
    [InlineData("BGSAbilityPerkEntry", 1, 12)]
    public void Referenced_forms_are_classified_by_entry_RTTI_and_quest_stage_is_a_byte(string className, byte type, int size)
    {
        var fixture = new Capture();
        fixture.AddRtti(EntryVtable, className);
        var data = Entry(size, 0);
        U32(data, 8, TargetVa);
        if (type == 0) { data[12] = 200; data[13] = 0xCB; data[14] = 0xCB; data[15] = 0xCB; }
        fixture.Add(EntryVa, data);
        // The early build's QUST byte need not equal a later PDB's FormType numbering.
        fixture.Add(TargetVa, Form(0xABCD, type == 0 ? (byte)0x48 : (byte)0x14));
        var entry = Assert.IsType<PerkEntry>(fixture.Reader().ReadEntry(EntryVa));
        Assert.Equal(type, entry.Type);
        Assert.Equal((byte)7, entry.Rank);
        Assert.Equal((byte)42, entry.Priority);
        if (type == 0) { Assert.Equal(0xABCDu, entry.QuestFormId); Assert.Equal(200, entry.QuestStage); }
        else { Assert.Equal(0xABCDu, entry.AbilityFormId); Assert.Null(entry.QuestStage); }
    }

    [Theory]
    [InlineData(false, 8, false)]
    [InlineData(true, 8, false)]
    [InlineData(true, 20, false)]
    [InlineData(true, 20, true)]
    public void Uncaptured_class_payload_or_function_remains_explicit_and_never_becomes_a_default_effect(
        bool rtti, int entryLength, bool functionHeader)
    {
        var fixture = new Capture();
        if (rtti) { fixture.AddRtti(EntryVtable, "BGSEntryPointPerkEntry"); }
        fixture.Add(EntryVa, Entry(entryLength, FunctionVa));
        if (functionHeader) { var bytes = new byte[4]; U32(bytes, 0, FunctionVtable); fixture.Add(FunctionVa, bytes); }
        var entry = Assert.IsType<PerkEntry>(fixture.Reader().ReadEntry(EntryVa));
        Assert.Equal((byte)7, entry.Rank);
        Assert.Equal(rtti ? (byte)2 : byte.MaxValue, entry.Type);
        Assert.Null(entry.FunctionType);
        Assert.Null(entry.EffectValue);
        Assert.NotEmpty(entry.RecoveryIssues);
        Assert.NotNull(entry.RuntimeRawData);
        var encoded = PerkEncoder.EncodeNew(new PerkRecord { Entries = [entry] });
        Assert.DoesNotContain(encoded.Subrecords, item => item.Signature == "PRKE");
        Assert.Contains(encoded.Warnings, warning => warning.Contains("was not emitted"));
    }

    [Fact]
    public void Ordered_tabs_retain_empty_lists_flags_global_and_reference_and_bound_cycles()
    {
        const uint tabs = 0x40500000, conditionA = 0x40501000, conditionB = 0x40502000, node = 0x40503000;
        const uint global = 0x40504000, reference = 0x40505000;
        var fixture = new Capture();
        fixture.AddRtti(EntryVtable, "BGSEntryPointPerkEntry");
        var entryBytes = Entry(20, 0); entryBytes[10] = 3; U32(entryBytes, 16, tabs);
        fixture.Add(EntryVa, entryBytes);
        var heads = new byte[16]; U32(heads, 0, conditionA); U32(heads, 4, node); fixture.Add(tabs, heads);
        var listNode = new byte[8]; U32(listNode, 0, conditionB); U32(listNode, 4, node); fixture.Add(node, listNode);
        var a = new byte[28]; a[0] = 0x65; U32(a, 4, global);
        BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(8), 449); U32(a, 12, TargetVa); U32(a, 20, 2); U32(a, 24, reference);
        fixture.Add(conditionA, a);
        var b = new byte[28]; b[0] = 0x60; F32(b, 4, 35); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), 14); U32(b, 12, 5);
        fixture.Add(conditionB, b);
        fixture.Add(TargetVa, Form(0xABCD, 0x57)); fixture.Add(global, Form(0xBCDE, 6)); fixture.Add(reference, Form(0xCDEF, 0x3A));

        var entry = Assert.IsType<PerkEntry>(fixture.Reader().ReadEntry(EntryVa));
        Assert.Equal(3, entry.ConditionGroups.Count);
        var group = entry.ConditionGroups[0];
        Assert.Equal(2, group.Conditions.Count);
        Assert.Contains(group.RecoveryIssues, issue => issue.Contains("Cycle"));
        Assert.Empty(entry.ConditionGroups[1].Conditions);
        Assert.Empty(entry.ConditionGroups[1].RecoveryIssues);
        Assert.Contains(entry.ConditionGroups[2].RecoveryIssues, issue => issue.Contains("Unavailable"));
        var condition = group.Conditions[0];
        Assert.Equal((byte)0x65, condition.Flags);
        Assert.Equal(2u, condition.RunOn);
        Assert.Equal(0xABCDu, condition.Parameter1FormId);
        Assert.Equal(0xBCDEu, condition.ComparisonGlobalFormId);
        Assert.Equal(0xCDEFu, condition.ReferenceFormId);
        Assert.Equal(a, condition.RuntimeRawData);
        Assert.Equal(35f, group.Conditions[1].ComparisonValue);
    }

    [Fact]
    public void Activation_keeps_label_flags_inline_script_and_original_runtime_bytes()
    {
        var fixture = new Capture();
        fixture.AddRtti(EntryVtable, "BGSEntryPointPerkEntry");
        fixture.AddRtti(FunctionVtable, "BGSEntryPointFunctionDataActivateChoice");
        fixture.Add(EntryVa, Entry(20, FunctionVa));
        var function = new byte[112]; U32(function, 0, FunctionVtable); U32(function, 4, TargetVa);
        BinaryPrimitives.WriteUInt16BigEndian(function.AsSpan(108), 0x1234);
        fixture.Add(FunctionVa, function); fixture.Add(TargetVa, "Use terminal\0"u8.ToArray());
        var entry = Assert.IsType<PerkEntry>(fixture.Reader().ReadEntry(EntryVa));
        Assert.Equal((byte)4, entry.FunctionType);
        Assert.Equal("Use terminal", entry.ActivationLabel);
        Assert.Equal((ushort)0x1234, entry.ActivationFlags);
        Assert.Equal(function, entry.RuntimeFunctionData);
        Assert.Null(entry.RawFunctionData);
        var encoded = PerkEncoder.EncodeNew(new PerkRecord { Entries = [entry] });
        Assert.DoesNotContain(encoded.Subrecords, item => item.Signature == "PRKE");
    }

    private static byte[] Entry(int size, uint function)
    {
        var bytes = new byte[size]; U32(bytes, 0, EntryVtable); bytes[4] = 7; bytes[5] = 42;
        if (size >= 20) { bytes[8] = 2; bytes[9] = 3; U32(bytes, 12, function); }
        return bytes;
    }

    private static byte[] Form(uint id, byte type) { var bytes = new byte[16]; bytes[4] = type; U32(bytes, 12, id); return bytes; }
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
    private static void F32(byte[] bytes, int offset, float value) => U32(bytes, offset, BitConverter.SingleToUInt32Bits(value));

    private sealed class Capture
    {
        private readonly SparseMemoryAccessor _accessor = new();
        private readonly List<MinidumpMemoryRegion> _regions = [];
        private long _length;
        public void Add(uint address, byte[] bytes)
        {
            _accessor.AddRange(_length, bytes);
            _regions.Add(new MinidumpMemoryRegion { VirtualAddress = Xbox360MemoryUtils.VaToLong(address), FileOffset = _length, Size = bytes.Length });
            _length += bytes.Length;
        }
        public void AddRtti(uint vtable, string name)
        {
            var bytes = new byte[512]; var start = vtable - 4;
            U32(bytes, 0, start + 64); U32(bytes, 64 + 12, start + 128);
            Encoding.ASCII.GetBytes(".?AV" + name + "@@\0").CopyTo(bytes, 128 + 8);
            Add(start, bytes);
        }
        public RuntimePerkEntryReader Reader() => new(new RuntimeMemoryContext(_accessor, _length,
            new MinidumpInfo { IsValid = true, ProcessorArchitecture = 3, MemoryRegions = _regions }));
    }
}
