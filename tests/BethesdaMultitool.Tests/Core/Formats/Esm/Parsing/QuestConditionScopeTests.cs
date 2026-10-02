using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class QuestConditionScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_parser_preserves_top_stage_and_objective_target_scopes(bool malformedMarker)
    {
        byte[] Condition(ushort function)
        {
            var bytes = new byte[28]; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), function); return bytes;
        }
        var parts = new List<(string Sig, byte[] Data)> { ("CTDA", Condition(1)) };
        if (malformedMarker) parts.Add(("INDX", [0]));
        parts.AddRange([("CTDA", Condition(2)), ("INDX", [10, 0]), ("QSDT", [0]), ("CTDA", Condition(3)),
            ("QOBJ", [20, 0, 0, 0]), ("CTDA", Condition(4)), ("QSTA", [0x14, 0, 0, 0, 0]), ("CTDA", Condition(5))]);
        var bytes = BuildRecordBytes(0x800, "QUST", false, parts.ToArray());
        using var memory = MemoryMappedFile.CreateNew(null, bytes.Length);
        using var accessor = memory.CreateViewAccessor(0, bytes.Length);
        accessor.WriteArray(0, bytes, 0, bytes.Length);
        var scan = MakeScanResult([new DetectedMainRecord("QUST", (uint)(bytes.Length - 24), 0, 0x800, 0, false)]);
        var quest = Assert.Single(new RecordParser(scan, accessor: accessor, fileSize: bytes.Length).ParseQuests());
        Assert.Equal<ushort>([1, 2], quest.Conditions.Select(row => row.FunctionIndex));
        Assert.Equal((ushort)3, Assert.Single(Assert.Single(quest.Stages).Conditions).FunctionIndex);
        Assert.Equal((ushort)5, Assert.Single(Assert.Single(Assert.Single(quest.Objectives).Targets).Conditions).FunctionIndex);
        var scope = new QuestConditionScope();
        foreach (var part in parts) scope.Advance(part.Sig, part.Data.Length);
        Assert.Equal(!malformedMarker, scope.Valid);
    }
}
