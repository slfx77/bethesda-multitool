using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class CellGrupBuilderTests
{
    [Fact]
    public void BuildInteriorCellGrup_NoBundles_ReturnsEmpty()
    {
        var bytes = CellGrupBuilder.BuildInteriorCellGrup([]);
        Assert.Empty(bytes);
    }

    [Fact]
    public void BuildCellSection_NoBundles_ReturnsNull()
    {
        var bytes = CellGrupBuilder.BuildCellSection([], new Dictionary<uint, ParsedMainRecord>());
        Assert.Null(bytes);
    }

    [Fact]
    public void BuildInteriorCellGrup_TopLevelGrupHasCellLabel()
    {
        var bundle = MakeMinimalBundle(0x123, persistentCount: 1, temporaryCount: 0);

        var bytes = CellGrupBuilder.BuildInteriorCellGrup([bundle])!;

        // First 24 bytes are the top-level GRUP header.
        // Layout: GRUP(4) + Size(4) + Label(4) + GroupType(4) + Stamp(4) + Unknown(4)
        Assert.Equal((byte)'G', bytes[0]);
        Assert.Equal((byte)'R', bytes[1]);
        Assert.Equal((byte)'U', bytes[2]);
        Assert.Equal((byte)'P', bytes[3]);

        var grupSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        Assert.Equal((uint)bytes.Length, grupSize);

        var label = bytes.AsSpan(8, 4).ToArray();
        Assert.Equal(new byte[] { (byte)'C', (byte)'E', (byte)'L', (byte)'L' }, label);

        var groupType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        Assert.Equal(0u, groupType);
    }

    [Fact]
    public void BuildInteriorCellGrup_NestedGrupHierarchy_IsCellThenBlockThenSubblockThenCell()
    {
        var bundle = MakeMinimalBundle(0xABC, persistentCount: 1, temporaryCount: 0);

        var bytes = CellGrupBuilder.BuildInteriorCellGrup([bundle])!;

        // Walk the GRUP nesting. After the top CELL GRUP header (24 bytes), we expect
        // type 2 (block), then type 3 (subblock), then a CELL record (24+ bytes), then
        // type 6 (cell children), then type 8 (persistent children) with a REFR override.
        var offset = 24;

        // Block GRUP (type 2)
        Assert.Equal((byte)'G', bytes[offset]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12, 4)));
        offset += 24;

        // Subblock GRUP (type 3)
        Assert.Equal((byte)'G', bytes[offset]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12, 4)));
        offset += 24;

        // CELL record header
        Assert.Equal((byte)'C', bytes[offset]);
        Assert.Equal((byte)'E', bytes[offset + 1]);
        Assert.Equal((byte)'L', bytes[offset + 2]);
        Assert.Equal((byte)'L', bytes[offset + 3]);
    }

    [Fact]
    public void BuildInteriorCellGrup_PersistentChildrenWrappedInGroupType8()
    {
        var bundle = MakeMinimalBundle(0x42, persistentCount: 2, temporaryCount: 0);

        var bytes = CellGrupBuilder.BuildInteriorCellGrup([bundle])!;

        // Find the type-8 GRUP by searching for a GRUP header with GroupType==8.
        var found = false;
        for (var i = 0; i + 24 <= bytes.Length;)
        {
            if (bytes[i] == 'G' && bytes[i + 1] == 'R' && bytes[i + 2] == 'U' && bytes[i + 3] == 'P')
            {
                var groupType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 12, 4));
                if (groupType == 8)
                {
                    found = true;

                    // Label of the persistent children GRUP must be the cell FormID.
                    var label = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 8, 4));
                    Assert.Equal(0x42u, label);
                    break;
                }

                var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 4, 4));
                if (size <= 24)
                {
                    break;
                }

                i += 24;
            }
            else
            {
                i++;
            }
        }

        Assert.True(found, "Expected to find a GRUP type 8 (persistent children) inside the cell.");
    }

    [Fact]
    public void BuildInteriorCellGrup_NoChildren_OmitsChildGrup()
    {
        var bundle = MakeMinimalBundle(0x42, persistentCount: 0, temporaryCount: 0);

        var bytes = CellGrupBuilder.BuildInteriorCellGrup([bundle])!;

        // No type-6 children GRUP should be present.
        for (var i = 0; i + 24 <= bytes.Length; i++)
        {
            if (bytes[i] == 'G' && bytes[i + 1] == 'R' && bytes[i + 2] == 'U' && bytes[i + 3] == 'P')
            {
                var groupType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 12, 4));
                Assert.NotEqual(6u, groupType);
            }
        }
    }

    [Fact]
    public void ReconstructRecordBytes_ProducesParseableRecord()
    {
        var parsed = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "CELL",
                DataSize = 0,
                Flags = 0,
                FormId = 0x12345,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 0x000F
            },
            Subrecords = [
                new ParsedSubrecord { Signature = "EDID", Data = "TestCell\0"u8.ToArray() },
                new ParsedSubrecord { Signature = "DATA", Data = [0x01] }
            ]
        };

        var bytes = CellGrupBuilder.ReconstructRecordBytes(parsed);

        // First 4 bytes = "CELL" signature
        Assert.Equal((byte)'C', bytes[0]);

        // Bytes 4..7 = data size (uint32 LE)
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        // Two subrecords: EDID(6 + 9) + DATA(6 + 1) = 22
        Assert.Equal(22u, dataSize);

        // Total bytes = 24 (header) + 22 (subrecords)
        Assert.Equal(46, bytes.Length);

        // First subrecord starts at offset 24 (after header) and is "EDID"
        Assert.Equal((byte)'E', bytes[24]);
        Assert.Equal((byte)'D', bytes[25]);
        Assert.Equal((byte)'I', bytes[26]);
        Assert.Equal((byte)'D', bytes[27]);
    }

    [Fact]
    public void ReconstructRecordBytes_ClearsCompressedFlag()
    {
        var parsed = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "CELL",
                Flags = 0x00040000, // compressed flag set
                FormId = 0x42,
                Version = 0x000F
            },
            Subrecords = []
        };

        var bytes = CellGrupBuilder.ReconstructRecordBytes(parsed);

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        Assert.Equal(0u, flags & 0x00040000u);
    }

    private static CellOverrideBundle MakeMinimalBundle(uint cellFormId, int persistentCount, int temporaryCount)
    {
        // Build a minimal CELL record: header + EDID subrecord.
        var cellSubrecords = new List<ParsedSubrecord>
        {
            new() { Signature = "EDID", Data = "MyCell\0"u8.ToArray() }
        };
        var cell = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "CELL",
                FormId = cellFormId,
                Version = 0x000F
            },
            Subrecords = cellSubrecords
        };

        var refRecord = MakeMinimalRefrRecord(cellFormId + 1);
        var persistent = Enumerable.Range(0, persistentCount).Select(_ => refRecord).ToList();
        var temporary = Enumerable.Range(0, temporaryCount).Select(_ => refRecord).ToList();

        return new CellOverrideBundle
        {
            CellFormId = cellFormId,
            Context = MakeInteriorContext(cellFormId, blockNum: 0, subblockNum: 0),
            CellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(cell),
            PersistentChildRecords = persistent,
            TemporaryChildRecords = temporary
        };
    }

    private static PcEsmCellContext MakeInteriorContext(uint cellFormId, uint blockNum, uint subblockNum)
    {
        var blockLabel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(blockLabel, blockNum);
        var subblockLabel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(subblockLabel, subblockNum);
        return new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = true,
            WorldspaceFormId = null,
            BlockLabel = blockLabel,
            SubblockLabel = subblockLabel,
            BlockGroupType = 2,
            SubblockGroupType = 3
        };
    }

    private static byte[] MakeMinimalRefrRecord(uint formId)
    {
        var refr = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                FormId = formId,
                Version = 0x000F
            },
            Subrecords =
            [
                new() { Signature = "DATA", Data = new byte[24] }
            ]
        };
        return CellGrupBuilder.ReconstructRecordBytes(refr);
    }

    [Fact]
    public void BuildCellSection_ExteriorBundle_WrapsInWrldHierarchy()
    {
        const uint wrldFormId = 0x60;
        const uint cellFormId = 0xC0;

        var wrldRecord = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "WRLD", FormId = wrldFormId, Version = 0x000F },
            Subrecords = [new() { Signature = "EDID", Data = "TestWrld\0"u8.ToArray() }]
        };
        var pcRecords = new Dictionary<uint, ParsedMainRecord> { [wrldFormId] = wrldRecord };

        var bundle = new CellOverrideBundle
        {
            CellFormId = cellFormId,
            Context = MakeExteriorContext(cellFormId, wrldFormId, blockKey: 0x1234, subKey: 0x5678),
            CellRecordBytes = MakeMinimalCellBytes(cellFormId),
            PersistentChildRecords = [],
            TemporaryChildRecords = [MakeMinimalRefrRecord(0xC1)]
        };

        var bytes = CellGrupBuilder.BuildCellSection([bundle], pcRecords)!;
        Assert.NotNull(bytes);

        // Top-level GRUP must be "WRLD" (the bundle is exterior, so no top-level CELL GRUP).
        Assert.Equal((byte)'G', bytes[0]);
        Assert.Equal((byte)'R', bytes[1]);
        Assert.Equal((byte)'U', bytes[2]);
        Assert.Equal((byte)'P', bytes[3]);
        Assert.Equal(new byte[] { (byte)'W', (byte)'R', (byte)'L', (byte)'D' },
            bytes.AsSpan(8, 4).ToArray());
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));

        // Walk the GRUP types we expect to find: 0 (top WRLD), 1 (world children),
        // 4 (exterior block), 5 (exterior subblock), 6 (cell children), 9 (temporary).
        var groupTypesEncountered = ScanGroupTypes(bytes);
        Assert.Contains(0, groupTypesEncountered);
        Assert.Contains(1, groupTypesEncountered);
        Assert.Contains(4, groupTypesEncountered);
        Assert.Contains(5, groupTypesEncountered);
        Assert.Contains(6, groupTypesEncountered);
        Assert.Contains(9, groupTypesEncountered);
    }

    [Fact]
    public void BuildCellSection_PersistentCellContainer_SkipsBlockSubblockGroups()
    {
        const uint wrldFormId = 0x60;
        const uint persistentCell = 0xC0;

        var wrldRecord = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "WRLD", FormId = wrldFormId, Version = 0x000F },
            Subrecords = [new() { Signature = "EDID", Data = "Wld\0"u8.ToArray() }]
        };
        var pcRecords = new Dictionary<uint, ParsedMainRecord> { [wrldFormId] = wrldRecord };

        var persistentContext = new PcEsmCellContext
        {
            CellFormId = persistentCell,
            IsInterior = false,
            WorldspaceFormId = wrldFormId,
            BlockLabel = null,
            SubblockLabel = null,
            BlockGroupType = 0,
            SubblockGroupType = 0
        };

        var bundle = new CellOverrideBundle
        {
            CellFormId = persistentCell,
            Context = persistentContext,
            CellRecordBytes = MakeMinimalCellBytes(persistentCell),
            PersistentChildRecords = [MakeMinimalRefrRecord(0xC1)],
            TemporaryChildRecords = []
        };

        var bytes = CellGrupBuilder.BuildCellSection([bundle], pcRecords)!;

        // Persistent cells appear directly under the world children GRUP, with NO block (4) or
        // subblock (5) wrapper.
        var groupTypes = ScanGroupTypes(bytes);
        Assert.Contains(1, groupTypes);
        Assert.Contains(6, groupTypes);
        Assert.Contains(8, groupTypes);
        Assert.DoesNotContain(4, groupTypes);
        Assert.DoesNotContain(5, groupTypes);
    }

    [Fact]
    public void BuildCellSection_MissingWrldRecord_OmitsExteriorGrup()
    {
        // No WRLD record in the PC index → the WRLD GRUP can't be anchored, so the exterior
        // bundle is dropped entirely from the output.
        var bundle = new CellOverrideBundle
        {
            CellFormId = 0xC0,
            Context = MakeExteriorContext(0xC0, wrldFormId: 0x999, blockKey: 1, subKey: 2),
            CellRecordBytes = MakeMinimalCellBytes(0xC0),
            PersistentChildRecords = [],
            TemporaryChildRecords = [MakeMinimalRefrRecord(0xC1)]
        };

        var pcRecords = new Dictionary<uint, ParsedMainRecord>(); // WRLD 0x999 not present
        var bytes = CellGrupBuilder.BuildCellSection([bundle], pcRecords);
        Assert.Null(bytes);
    }

    private static byte[] MakeMinimalCellBytes(uint cellFormId)
    {
        var cell = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "CELL", FormId = cellFormId, Version = 0x000F },
            Subrecords = [new() { Signature = "EDID", Data = "MyCell\0"u8.ToArray() }]
        };
        return CellGrupBuilder.ReconstructRecordBytes(cell);
    }

    private static PcEsmCellContext MakeExteriorContext(uint cellFormId, uint wrldFormId, uint blockKey, uint subKey)
    {
        var blockLabel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(blockLabel, blockKey);
        var subblockLabel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(subblockLabel, subKey);
        return new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = false,
            WorldspaceFormId = wrldFormId,
            BlockLabel = blockLabel,
            SubblockLabel = subblockLabel,
            BlockGroupType = 4,
            SubblockGroupType = 5
        };
    }

    private static List<int> ScanGroupTypes(byte[] bytes)
    {
        var types = new List<int>();
        for (var i = 0; i + 24 <= bytes.Length; i++)
        {
            if (bytes[i] == 'G' && bytes[i + 1] == 'R' && bytes[i + 2] == 'U' && bytes[i + 3] == 'P')
            {
                types.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 12, 4)));
            }
        }

        return types;
    }

    [Fact]
    public void BuildInteriorCellGrup_VwdRecordsPresent_EmitsTypeOrder8Then10Then9()
    {
        // Build a bundle with all three sub-GRUPs populated; canonical order is 8 → 10 → 9.
        var bundle = MakeBundleWithAllChildren(0x55);

        var bytes = CellGrupBuilder.BuildInteriorCellGrup([bundle]);

        // Find the relative order of type-8, type-10, type-9 GRUPs in the byte stream.
        int? offset8 = null, offset10 = null, offset9 = null;
        for (var i = 0; i + 24 <= bytes.Length; i++)
        {
            if (bytes[i] == 'G' && bytes[i + 1] == 'R' && bytes[i + 2] == 'U' && bytes[i + 3] == 'P')
            {
                var type = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 12, 4));
                if (type == 8 && offset8 is null)
                {
                    offset8 = i;
                }
                else if (type == 10 && offset10 is null)
                {
                    offset10 = i;
                }
                else if (type == 9 && offset9 is null)
                {
                    offset9 = i;
                }
            }
        }

        Assert.NotNull(offset8);
        Assert.NotNull(offset10);
        Assert.NotNull(offset9);
        Assert.True(offset8 < offset10, "Persistent (8) should come before VWD (10).");
        Assert.True(offset10 < offset9, "VWD (10) should come before temporary (9).");
    }

    [Fact]
    public void BuildInteriorCellGrup_NoVwdRecords_OmitsType10Group()
    {
        // Default minimal bundle has no VWD children.
        var bundle = MakeMinimalBundle(0x66, persistentCount: 1, temporaryCount: 1);

        var bytes = CellGrupBuilder.BuildInteriorCellGrup([bundle]);

        var groupTypes = ScanGroupTypes(bytes);
        Assert.Contains(8, groupTypes);
        Assert.Contains(9, groupTypes);
        Assert.DoesNotContain(10, groupTypes);
    }

    private static CellOverrideBundle MakeBundleWithAllChildren(uint cellFormId)
    {
        var refRecord = MakeMinimalRefrRecord(cellFormId + 1);

        return new CellOverrideBundle
        {
            CellFormId = cellFormId,
            Context = MakeInteriorContext(cellFormId, blockNum: 0, subblockNum: 0),
            CellRecordBytes = MakeMinimalCellBytes(cellFormId),
            PersistentChildRecords = [refRecord],
            VwdChildRecords = [refRecord],
            TemporaryChildRecords = [refRecord]
        };
    }
}
