using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class PerkParameterDataTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public void EPFT_selects_parameter_shape_independently_of_DATA_result_function(bool bigEndian, byte kind)
    {
        var payload = new byte[kind == 2 ? 8 : 4];
        Write(payload, 0, kind == 3 ? 0x00001234u : BitConverter.SingleToUInt32Bits(1.5f), bigEndian);
        if (kind == 2) { Write(payload, 4, BitConverter.SingleToUInt32Bits(-2.25f), bigEndian); }
        var bytes = BuildRecordBytes(0x800, "PERK", bigEndian,
            ("EDID", NullTermString("ParameterShape")), ("DATA", new byte[5]),
            ("PRKE", new byte[] { 2, 0, 0 }), ("DATA", new byte[] { 2, 3, 0 }),
            ("EPFT", new byte[] { kind }), ("EPFD", payload), ("PRKF", []));
        var scan = MakeScanResult([new DetectedMainRecord("PERK", (uint)(bytes.Length - 24), 0, 0x800, 0, bigEndian)]);
        using var map = MemoryMappedFile.CreateNew(null, bytes.Length);
        using var accessor = map.CreateViewAccessor(0, bytes.Length);
        accessor.WriteArray(0, bytes, 0, bytes.Length);
        var perk = Assert.Single(new RecordParser(scan, accessor: accessor, fileSize: bytes.Length).ParseAll().Perks);
        var entry = Assert.Single(perk.Entries);
        Assert.Equal((byte)3, entry.EntryPointFunction);
        Assert.Equal(kind, entry.FunctionType);
        Assert.Equal(payload, entry.RawFunctionData);
        if (kind == 3) { Assert.Equal(0x1234u, entry.EffectFormId); Assert.Null(entry.EffectValue); }
        else { Assert.Equal(1.5f, entry.EffectValue); }
        if (kind == 2) { Assert.Equal(-2.25f, entry.EffectValue2); }
        var encoded = PerkEncoder.EncodeNew(perk);
        var result = Assert.Single(encoded.Subrecords, item => item.Signature == "EPFD").Bytes;
        Assert.Equal(payload.Length, result.Length);
        Assert.Equal(kind == 3 ? 0x1234u : BitConverter.SingleToUInt32Bits(1.5f), BinaryPrimitives.ReadUInt32LittleEndian(result));
        if (kind == 2) { Assert.Equal(-2.25f, BitConverter.ToSingle(result, 4)); }
    }

    private static void Write(byte[] bytes, int offset, uint value, bool bigEndian)
    {
        if (bigEndian) { BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value); }
        else { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value); }
    }

    [Fact]
    public void Perk_condition_flags_run_on_reference_and_global_survive_remapping_and_encoding()
    {
        var perk = new PerkRecord
        {
            EditorId = "ConditionFields",
            Conditions = [new PerkCondition
            {
                FunctionIndex = 14, Parameter1 = 5, ComparisonOperator = 3,
                Flags = 0x65, RunOn = 2, ReferenceFormId = 0x222, ComparisonGlobalFormId = 0x333
            }]
        };
        var encoded = PerkEncoder.EncodeNew(perk, new HashSet<uint> { 0x422, 0x433 },
            new Dictionary<uint, uint> { [0x222] = 0x422, [0x333] = 0x433 });
        var condition = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "CTDA").Bytes;
        Assert.Equal((byte)0x65, condition[0]);
        Assert.Equal(0x433u, BinaryPrimitives.ReadUInt32LittleEndian(condition.AsSpan(4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(condition.AsSpan(20)));
        Assert.Equal(0x422u, BinaryPrimitives.ReadUInt32LittleEndian(condition.AsSpan(24)));
    }
}
