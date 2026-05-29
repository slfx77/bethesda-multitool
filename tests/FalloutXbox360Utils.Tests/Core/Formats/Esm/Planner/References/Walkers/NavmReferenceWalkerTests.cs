using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class NavmReferenceWalkerTests
{
    [Fact]
    public void Yields_Empty_When_No_Nvex()
    {
        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = 0x000ABCDE,
            RawSubrecords = [new NavMeshSubrecord("DATA", [1, 2, 3, 4])],
        };
        var walker = new NavmReferenceWalker();

        Assert.Empty(walker.Walk(navm));
    }

    [Fact]
    public void Parses_All_Nvex_Entries()
    {
        // Two 10-byte NVEX entries pointing at 0xAA000010 and 0xAA000020.
        var nvexBytes = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(nvexBytes.AsSpan(0, 4), 0xAA000010u);
        BinaryPrimitives.WriteUInt32LittleEndian(nvexBytes.AsSpan(10, 4), 0xAA000020u);

        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = 0x000ABCDE,
            RawSubrecords = [new NavMeshSubrecord("NVEX", nvexBytes)],
        };
        var walker = new NavmReferenceWalker();

        var refs = walker.Walk(navm).ToList();

        Assert.Equal(2, refs.Count);
        Assert.Equal("NVEX[0]", refs[0].FieldPath);
        Assert.Equal(0xAA000010u, refs[0].FormId);
        Assert.Equal("NVEX[1]", refs[1].FieldPath);
        Assert.Equal(0xAA000020u, refs[1].FormId);
    }

    [Fact]
    public void Truncates_On_Partial_Tail_Entry()
    {
        // 15 bytes — one full entry plus a 5-byte tail that should be discarded.
        var nvexBytes = new byte[15];
        BinaryPrimitives.WriteUInt32LittleEndian(nvexBytes.AsSpan(0, 4), 0xAA000010u);

        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = 0x000ABCDE,
            RawSubrecords = [new NavMeshSubrecord("NVEX", nvexBytes)],
        };
        var walker = new NavmReferenceWalker();

        var refs = walker.Walk(navm).ToList();

        Assert.Single(refs);
    }
}
