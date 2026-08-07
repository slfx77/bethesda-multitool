using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

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
        var bundle = MakeMinimalBundle(0x123, 1, 0);

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
        Assert.Equal(new[] { (byte)'C', (byte)'E', (byte)'L', (byte)'L' }, label);

        var groupType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        Assert.Equal(0u, groupType);
    }

    [Fact]
    public void BuildInteriorCellGrup_NestedGrupHierarchy_IsCellThenBlockThenSubblockThenCell()
    {
        var bundle = MakeMinimalBundle(0xABC, 1, 0);

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
        var bundle = MakeMinimalBundle(0x42, 2, 0);

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
        var bundle = MakeMinimalBundle(0x42, 0, 0);

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
            Subrecords =
            [
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
            Context = MakeInteriorContext(cellFormId, 0, 0),
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
                new ParsedSubrecord { Signature = "DATA", Data = new byte[24] }
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
            Subrecords = [new ParsedSubrecord { Signature = "EDID", Data = "TestWrld\0"u8.ToArray() }]
        };
        var pcRecords = new Dictionary<uint, ParsedMainRecord> { [wrldFormId] = wrldRecord };

        var bundle = new CellOverrideBundle
        {
            CellFormId = cellFormId,
            Context = MakeExteriorContext(cellFormId, wrldFormId, 0x1234, 0x5678),
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
        Assert.Equal(new[] { (byte)'W', (byte)'R', (byte)'L', (byte)'D' },
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
            Subrecords = [new ParsedSubrecord { Signature = "EDID", Data = "Wld\0"u8.ToArray() }]
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
            Context = MakeExteriorContext(0xC0, 0x999, 1, 2),
            CellRecordBytes = MakeMinimalCellBytes(0xC0),
            PersistentChildRecords = [],
            TemporaryChildRecords = [MakeMinimalRefrRecord(0xC1)]
        };

        var pcRecords = new Dictionary<uint, ParsedMainRecord>(); // WRLD 0x999 not present
        var bytes = CellGrupBuilder.BuildCellSection([bundle], pcRecords);
        Assert.Null(bytes);
    }

    [Fact]
    public void BuildCellSection_WrldAnchorWithoutBounds_DropsMasterOfstPayload()
    {
        // The master WRLD carries OFST: per-file byte offsets into the MASTER. The FNV
        // runtime consults each ESM-flagged file's own OFST as the exterior-cell fast
        // path, so a master OFST cloned into this plugin makes the engine seek THIS file
        // at master offsets and every loaded exterior cell fails temporary-data load.
        // The master's bytes must never survive. This anchor has no NAM0/NAM9, so no
        // replacement table can be sized either and the record ends up with no OFST —
        // see BuildCellSection_WrldWithBounds_EmitsRebuiltOfstTable for the normal case.
        const uint wrldFormId = 0x60;
        var ofstData = new byte[70_000]; // >64KB so the XXXX-extended path is exercised too
        ofstData.AsSpan().Fill(0xAB);
        var wrldRecord = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "WRLD", FormId = wrldFormId, Version = 0x000F },
            Subrecords =
            [
                new ParsedSubrecord { Signature = "EDID", Data = "TestWrld\0"u8.ToArray() },
                new ParsedSubrecord { Signature = "OFST", Data = ofstData }
            ]
        };
        var pcRecords = new Dictionary<uint, ParsedMainRecord> { [wrldFormId] = wrldRecord };

        var bundle = new CellOverrideBundle
        {
            CellFormId = 0xC0,
            Context = MakeExteriorContext(0xC0, wrldFormId, 0x1234, 0x5678),
            CellRecordBytes = MakeMinimalCellBytes(0xC0),
            PersistentChildRecords = [],
            TemporaryChildRecords = [MakeMinimalRefrRecord(0xC1)]
        };

        var bytes = CellGrupBuilder.BuildCellSection([bundle], pcRecords)!;
        Assert.NotNull(bytes);

        // Neither the OFST signature nor its XXXX size-extension prefix may survive.
        Assert.Equal(-1, IndexOfSignature(bytes, "OFST"));
        Assert.Equal(-1, IndexOfSignature(bytes, "XXXX"));

        // The anchor's dataSize must reflect the strip: EDID (6 + 9) only.
        var wrldOffset = IndexOfSignature(bytes, "WRLD", 24); // skip top GRUP label
        Assert.True(wrldOffset >= 0);
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(wrldOffset + 4, 4));
        Assert.Equal(15u, dataSize);
    }

    private static int IndexOfSignature(byte[] bytes, string signature, int searchFrom = 0)
    {
        for (var i = searchFrom; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] == signature[0] && bytes[i + 1] == signature[1]
                                         && bytes[i + 2] == signature[2] && bytes[i + 3] == signature[3])
            {
                return i;
            }
        }

        return -1;
    }

    private static byte[] MakeMinimalCellBytes(uint cellFormId)
    {
        var cell = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "CELL", FormId = cellFormId, Version = 0x000F },
            Subrecords = [new ParsedSubrecord { Signature = "EDID", Data = "MyCell\0"u8.ToArray() }]
        };
        return CellGrupBuilder.ReconstructRecordBytes(cell);
    }

    private static byte[] MakeCellBytesAt(uint cellFormId, int gridX, int gridY, uint headerFlags = 0)
    {
        var xclc = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(xclc.AsSpan(0, 4), gridX);
        BinaryPrimitives.WriteInt32LittleEndian(xclc.AsSpan(4, 4), gridY);
        var cell = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "CELL", FormId = cellFormId, Version = 0x000F, Flags = headerFlags
            },
            Subrecords =
            [
                new ParsedSubrecord { Signature = "DATA", Data = [0x02] },
                new ParsedSubrecord { Signature = "XCLC", Data = xclc }
            ]
        };
        return CellGrupBuilder.ReconstructRecordBytes(cell);
    }

    /// <summary>
    ///     A WRLD anchor whose NAM0/NAM9 object bounds describe a grid spanning
    ///     [minX..maxX] x [minY..maxY] cells, matching how the GECK writes them.
    /// </summary>
    private static ParsedMainRecord MakeWrldWithBounds(
        uint wrldFormId, int minX, int minY, int maxX, int maxY)
    {
        static byte[] Corner(int cellX, int cellY)
        {
            var buffer = new byte[8];
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(0, 4), cellX * 4096f);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4, 4), cellY * 4096f);
            return buffer;
        }

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "WRLD", FormId = wrldFormId, Version = 0x000F },
            Subrecords =
            [
                new ParsedSubrecord { Signature = "EDID", Data = "BoundedWrld\0"u8.ToArray() },
                new ParsedSubrecord { Signature = "NAM0", Data = Corner(minX, minY) },
                new ParsedSubrecord { Signature = "NAM9", Data = Corner(maxX, maxY) }
            ]
        };
    }

    /// <summary>
    ///     Reads the OFST table out of the single WRLD record in a built cell section, along
    ///     with the record's own offset (OFST entries are WRLD-relative).
    /// </summary>
    private static (int WrldOffset, uint[] Table) ReadOfstTable(byte[] bytes)
    {
        var wrldOffset = IndexOfSignature(bytes, "WRLD", 24);
        Assert.True(wrldOffset >= 0);

        var payloadOffset = IndexOfSignature(bytes, "OFST", wrldOffset) + 6;
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(wrldOffset + 4, 4));
        var recordEnd = wrldOffset + 24 + (int)declaredSize;
        var table = new uint[(recordEnd - payloadOffset) / 4];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(payloadOffset + (i * 4), 4));
        }

        return (wrldOffset, table);
    }

    [Fact]
    public void BuildCellSection_WrldWithBounds_EmitsRebuiltOfstTable()
    {
        // Every WRLD record in every shipped FNV/FO3 DLC plugin carries an OFST — 63 of 63,
        // covering both master-worldspace overrides and the plugins' own new worldspaces.
        // The table is sized from the worldspace grid and holds the offset of each CELL this
        // file contributes, relative to the start of the WRLD record.
        const uint wrldFormId = 0x60;
        const uint cellFormId = 0xC0;
        var pcRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [wrldFormId] = MakeWrldWithBounds(wrldFormId, -2, -3, 1, 2)
        };

        var bundle = new CellOverrideBundle
        {
            CellFormId = cellFormId,
            Context = MakeExteriorContext(cellFormId, wrldFormId, 0x1234, 0x5678),
            CellRecordBytes = MakeCellBytesAt(cellFormId, gridX: 1, gridY: -1),
            PersistentChildRecords = [],
            TemporaryChildRecords = [MakeMinimalRefrRecord(0xC1)]
        };

        var bytes = CellGrupBuilder.BuildCellSection([bundle], pcRecords)!;
        var (wrldOffset, table) = ReadOfstTable(bytes);

        // Grid is 4 columns (-2..1) x 6 rows (-3..2) = 24 entries.
        Assert.Equal(24, table.Length);

        // The cell at (1, -1) lands at row (-1 - -3) = 2, column (1 - -2) = 3 → index 11.
        const int expectedIndex = (2 * 4) + 3;
        Assert.All(
            Enumerable.Range(0, table.Length).Where(i => i != expectedIndex),
            i => Assert.Equal(0u, table[i]));

        // ...and its value must be the CELL record's offset relative to the WRLD record.
        var cellOffset = IndexOfSignature(bytes, "CELL", wrldOffset);
        Assert.True(cellOffset > wrldOffset);
        Assert.Equal((uint)(cellOffset - wrldOffset), table[expectedIndex]);
    }

    [Fact]
    public void BuildCellSection_PersistentContainer_IsNotIndexedIntoOfst()
    {
        // Regression: the Freeside "Wilderness cell Attaching" access violation. A worldspace's
        // persistent ref container carries XCLC (0,0) — colliding with a real master grid cell —
        // but it is not an exterior grid cell. Indexing it into OFST lets the engine serve it
        // into a grid slot, where TESObjectCELL::GetLandRecord returns NULL for persistent cells
        // and GridCellArray::LoadCell dereferences it unchecked.
        const uint wrldFormId = 0x60;
        const uint containerFormId = 0xC0;
        var pcRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [wrldFormId] = MakeWrldWithBounds(wrldFormId, -2, -2, 2, 2)
        };

        var bundle = new CellOverrideBundle
        {
            CellFormId = containerFormId,
            Context = new PcEsmCellContext
            {
                CellFormId = containerFormId,
                IsInterior = false,
                WorldspaceFormId = wrldFormId,
                BlockLabel = null,
                SubblockLabel = null,
                BlockGroupType = 0,
                SubblockGroupType = 0
            },
            CellRecordBytes = MakeCellBytesAt(containerFormId, 0, 0, headerFlags: 0x400),
            PersistentChildRecords = [MakeMinimalRefrRecord(0xC1)],
            TemporaryChildRecords = []
        };

        var bytes = CellGrupBuilder.BuildCellSection([bundle], pcRecords)!;
        var (_, table) = ReadOfstTable(bytes);

        // The table is present and correctly sized, but entirely zero — exactly the shape
        // DeadMoney.esm ships for WastelandNV, whose emission this mirrors.
        Assert.Equal(25, table.Length);
        Assert.All(table, entry => Assert.Equal(0u, entry));
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
        var bundle = MakeMinimalBundle(0x66, 1, 1);

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
            Context = MakeInteriorContext(cellFormId, 0, 0),
            CellRecordBytes = MakeMinimalCellBytes(cellFormId),
            PersistentChildRecords = [refRecord],
            VwdChildRecords = [refRecord],
            TemporaryChildRecords = [refRecord]
        };
    }
}