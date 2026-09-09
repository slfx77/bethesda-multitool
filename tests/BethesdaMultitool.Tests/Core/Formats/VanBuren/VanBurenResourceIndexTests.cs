using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for <c>resource.rht</c>, the prototype's payload name index (measured 2026-09-08):
///     header, 20-byte records keyed by the OFFSET of the group's name, and two NUL-separated
///     string tables that tile the rest of the file.
/// </summary>
public sealed class VanBurenResourceIndexTests
{
    private static readonly string[] Groups = ["Tiles", "Maps", "Engine"];

    private static readonly (string, uint, uint, string)[] Records =
    [
        ("Maps", 1, VanBurenResourceIndex.MapType, "zz_TestMapsMarkTest"),
        ("Maps", 2, VanBurenResourceIndex.SceneType, "MarkTest"),
        ("Maps", 3, VanBurenResourceIndex.WalkGridType, "MarkTest"),
        ("Maps", 4, VanBurenResourceIndex.TextureType, "MarkTest_0"),
        ("Engine", 16, VanBurenResourceIndex.SceneType, "Default_StartMap"),
        ("Tiles", 0, VanBurenResourceIndex.MeshType, "Int_Cave1_Rock_Brown")
    ];

    private static byte[] Index(
        string[] groups,
        (string Group, uint Index, uint Type, string Name)[] records,
        uint firstDword = 1,
        bool dropTerminator = false)
    {
        var groupTable = new List<byte>();
        var groupOffsets = new Dictionary<string, uint>();
        foreach (var g in groups)
        {
            groupOffsets[g] = (uint)groupTable.Count;
            groupTable.AddRange(Encoding.ASCII.GetBytes(g));
            groupTable.Add(0);
        }

        var nameTable = new List<byte>();
        var nameOffsets = new Dictionary<string, uint>();
        foreach (var r in records)
        {
            if (nameOffsets.ContainsKey(r.Name))
            {
                continue;
            }

            nameOffsets[r.Name] = (uint)nameTable.Count;
            nameTable.AddRange(Encoding.ASCII.GetBytes(r.Name));
            nameTable.Add(0);
        }

        if (dropTerminator)
        {
            nameTable.RemoveAt(nameTable.Count - 1);
        }

        var recordArea = 20 + records.Length * 20;
        var b = new byte[recordArea + groupTable.Count + nameTable.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(b, firstDword);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)recordArea);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)(recordArea + groupTable.Count));
        for (var i = 0; i < records.Length; i++)
        {
            var at = 20 + i * 20;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), records[i].Index);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), records[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 12), nameOffsets[records[i].Name]);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 16), groupOffsets[records[i].Group]);
        }

        groupTable.CopyTo(b, recordArea);
        nameTable.CopyTo(b, recordArea + groupTable.Count);
        return b;
    }

    [Fact]
    public void KeysAreTheGroupNameOffsetAndTheEntryIndex()
    {
        var index = VanBurenResourceIndex.Parse(Index(Groups, Records), "resource.rht");

        Assert.Equal(Groups, index.Groups);
        Assert.Equal(6, index.Entries.Count);
        Assert.Equal("MarkTest", index.Lookup("Maps", 2)!.Value.Name);
        Assert.Equal(VanBurenResourceIndex.WalkGridType, index.Lookup("maps", 3)!.Value.TypeId);
        Assert.Null(index.Lookup("Maps", 99));
        Assert.Null(index.Lookup("Sounds", 2));
    }

    [Fact]
    public void FindReturnsTheEntriesOfOneNameAndType()
    {
        // The same NAME appears under two types — the scene and the walk grid share the map's stem
        // — so a lookup by name alone is ambiguous and the type is part of the key.
        var index = VanBurenResourceIndex.Parse(Index(Groups, Records), "resource.rht");

        Assert.Equal([2], index.Find("Maps", "marktest", VanBurenResourceIndex.SceneType).Select(e => e.Index));
        Assert.Equal([3], index.Find("Maps", "MarkTest", VanBurenResourceIndex.WalkGridType).Select(e => e.Index));
        Assert.Empty(index.Find("Engine", "MarkTest", VanBurenResourceIndex.SceneType));
    }

    [Fact]
    public void ANameOffsetInsideAStringIsRefused()
    {
        // ⚑ Every offset must land on a string START; one that points mid-string reads a suffix
        // that looks like a name, which is exactly the silent failure the check exists for.
        var b = Index(Groups, Records);
        var record1 = 20 + 20;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(record1 + 12),
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(record1 + 12)) + 3);

        Assert.False(VanBurenResourceIndex.TryParse(b, "shifted.rht", out _, out var error));
        Assert.Contains("string start", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateKeyIsRefused()
    {
        var records = Records.ToArray();
        records[1] = ("Maps", 1, VanBurenResourceIndex.SceneType, "MarkTest");

        Assert.Throws<InvalidDataException>(() => VanBurenResourceIndex.Parse(Index(Groups, records), "dup.rht"));
    }

    [Fact]
    public void TheProbeNeedsTheConstantsAndARecordAreaEndingOnTheGroupTable()
    {
        Assert.True(VanBurenResourceIndex.IsResourceIndex(Index(Groups, Records)));
        Assert.False(VanBurenResourceIndex.IsResourceIndex(Index(Groups, Records, 2)));

        var b = Index(Groups, Records);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 20 + 5 * 20);
        Assert.False(VanBurenResourceIndex.IsResourceIndex(b));
    }

    [Fact]
    public void ANameTableWithoutItsFinalTerminatorIsRefused()
    {
        Assert.False(VanBurenResourceIndex.TryParse(Index(Groups, Records, dropTerminator: true), "cut.rht", out _,
            out _));
    }
}