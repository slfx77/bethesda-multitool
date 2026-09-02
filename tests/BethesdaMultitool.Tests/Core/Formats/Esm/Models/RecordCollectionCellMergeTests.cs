using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

/// <summary>
///     Engine load-order CELL merge semantics: an override record in a later file replaces header
///     fields but its CHILDREN (placed references, LAND) merge with the base file's. FO4's DLCs
///     re-ship thousands of Commonwealth CELL headers (precombine regeneration) with no LAND and no
///     base REFRs — whole-record replacement erased downtown Boston when all DLC ESMs were loaded.
/// </summary>
public class RecordCollectionCellMergeTests
{
    private const uint CommonwealthWs = 0x3C;

    private static LandHeightmap Land(float offset = 1000f)
    {
        return new LandHeightmap
        {
            HeightOffset = offset,
            HeightDeltas = new sbyte[33 * 33]
        };
    }

    private static CellRecord Cell(
        uint formId,
        LandHeightmap? land,
        params PlacedReference[] refs)
    {
        return new CellRecord
        {
            FormId = formId,
            WorldspaceFormId = CommonwealthWs,
            GridX = 1,
            GridY = 2,
            Heightmap = land,
            PlacedObjects = [.. refs]
        };
    }

    private static PlacedReference Ref(uint formId, uint baseFormId)
    {
        return new PlacedReference
        {
            FormId = formId,
            BaseFormId = baseFormId
        };
    }

    [Fact]
    public void MergeWith_LandlessCellOverride_KeepsBaseLandAndRefs()
    {
        // Base cell: terrain + two refs. DLC override: no LAND, one added ref, one re-shipped ref.
        var baseCell = Cell(0x10, Land(), Ref(0x100, 0xA), Ref(0x101, 0xB));
        var overrideCell = Cell(0x10, null, Ref(0x101, 0xBB), Ref(0x900, 0xC));

        var merged = new RecordCollection { Cells = [baseCell] }
            .MergeWith(new RecordCollection { Cells = [overrideCell] });

        var cell = Assert.Single(merged.Cells);
        Assert.NotNull(cell.Heightmap); // base LAND survives
        Assert.Equal(3, cell.PlacedObjects.Count); // 0x100 kept, 0x101 overridden, 0x900 added
        Assert.Equal(0xAu, cell.PlacedObjects.Single(r => r.FormId == 0x100).BaseFormId);
        Assert.Equal(0xBBu, cell.PlacedObjects.Single(r => r.FormId == 0x101).BaseFormId);
        Assert.Contains(cell.PlacedObjects, r => r.FormId == 0x900);
    }

    [Fact]
    public void MergeWith_CellOverrideWithLand_OverrideLandWins()
    {
        var baseCell = Cell(0x10, Land());
        var overrideCell = Cell(0x10, Land(2000f));

        var merged = new RecordCollection { Cells = [baseCell] }
            .MergeWith(new RecordCollection { Cells = [overrideCell] });

        Assert.Equal(2000f, Assert.Single(merged.Cells).Heightmap!.HeightOffset);
    }

    [Fact]
    public void MergeWith_CellOverride_BackfillsMissingHeaderFields()
    {
        var baseCell = Cell(0x10, Land()) with
        {
            EditorId = "BaseCell",
            WaterHeight = 5f,
            WaterFormId = 0x4321,
            ClimateFormId = 0x1234
        };
        var overrideCell = new CellRecord { FormId = 0x10, FullName = "Renamed" };

        var merged = new RecordCollection { Cells = [baseCell] }
            .MergeWith(new RecordCollection { Cells = [overrideCell] });

        var cell = Assert.Single(merged.Cells);
        Assert.Equal("Renamed", cell.FullName); // override header field wins
        Assert.Equal("BaseCell", cell.EditorId); // absent fields backfill from base
        Assert.Equal(5f, cell.WaterHeight);
        Assert.Equal(0x4321u, cell.WaterFormId);
        Assert.Equal(0x1234u, cell.ClimateFormId);
        Assert.Equal(CommonwealthWs, cell.WorldspaceFormId);
        Assert.Equal(1, cell.GridX);
    }

    [Fact]
    public void MergeWith_TerrainCarryDisabled_OverrideKeepsOnlyItsOwnTerrain()
    {
        // The DMP viewer's "dump-preserved terrain only" mode (Master ESM terrain toggle OFF):
        // the override (dump) cell must NOT inherit the base (master) cell's terrain, while ref
        // merging and header backfill keep engine semantics.
        var baseCell = Cell(0x10, Land(), Ref(0x100, 0xA)) with
        {
            EditorId = "MasterCell",
            LandVisualData = new LandVisualData { Source = VisualDataSource.MasterEsm },
            RuntimeTerrainMesh = new RuntimeTerrainMesh { Vertices = new float[3] }
        };
        var overrideCell = Cell(0x10, null, Ref(0x900, 0xC));

        var merged = new RecordCollection { Cells = [baseCell] }
            .MergeWith(new RecordCollection { Cells = [overrideCell] }, carryBaseTerrainIntoCells: false);

        var cell = Assert.Single(merged.Cells);
        Assert.Null(cell.Heightmap);
        Assert.Null(cell.LandVisualData);
        Assert.Null(cell.RuntimeTerrainMesh);
        Assert.Equal("MasterCell", cell.EditorId); // header backfill unaffected
        Assert.Equal(2, cell.PlacedObjects.Count); // ref merge unaffected
    }

    [Fact]
    public void MergeWith_TerrainCarryDisabled_OverridesOwnTerrainSurvives()
    {
        var baseCell = Cell(0x10, Land());
        var overrideCell = Cell(0x10, Land(2000f));

        var merged = new RecordCollection { Cells = [baseCell] }
            .MergeWith(new RecordCollection { Cells = [overrideCell] }, carryBaseTerrainIntoCells: false);

        Assert.Equal(2000f, Assert.Single(merged.Cells).Heightmap!.HeightOffset);
    }

    [Fact]
    public void MergeWorldspaces_FoldsOverriddenCellChildren_WithoutRelink()
    {
        // The renderer-profiler path reads ws.Cells directly (it never calls RelinkWorldspaceCells),
        // so the worldspace-level cell stitch must apply the same child merge.
        var baseCell = Cell(0x10, Land(), Ref(0x100, 0xA));
        var baseWs = new WorldspaceRecord { FormId = CommonwealthWs, Cells = [baseCell] };
        var baseRc = new RecordCollection { Worldspaces = [baseWs], Cells = [baseCell] };

        var overrideCell = Cell(0x10, null);
        var overlayWs = new WorldspaceRecord { FormId = CommonwealthWs, Cells = [overrideCell] };
        var overlayRc = new RecordCollection { Worldspaces = [overlayWs], Cells = [overrideCell] };

        var merged = baseRc.MergeWith(overlayRc);

        var wsCell = Assert.Single(merged.Worldspaces.Single().Cells);
        Assert.NotNull(wsCell.Heightmap);
        Assert.Single(wsCell.PlacedObjects);
    }
}

/// <summary>
///     TES4-family load-order FormID rebasing: inside a plugin, a FormID's high byte indexes the
///     file's OWN master list, so every DLC's new records ship raw 0x01-prefixed and would collide
///     across DLCs in a FormID-keyed merge. The mapper folds each file's local indices into shared
///     load-order slots (overrides keep targeting their master; own records get a disjoint block).
/// </summary>
public class Tes4LoadOrderFormIdMapperTests
{
    private static RecordCollection CellCollection(uint cellFormId, uint worldspaceFormId)
    {
        return new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = cellFormId,
                    WorldspaceFormId = worldspaceFormId,
                    PlacedObjects = [new PlacedReference { FormId = cellFormId + 1, BaseFormId = 0x00000A00 }]
                }
            ]
        };
    }

    private static Func<string, IReadOnlyList<string>> Masters(Dictionary<string, string[]> byName)
    {
        return path => byName.TryGetValue(Path.GetFileName(path), out var masters) ? masters : [];
    }

    [Fact]
    public void TryCreate_SingleSourceWithoutPrimary_ReturnsNull()
    {
        Assert.Null(Tes4LoadOrderFormIdMapper.TryCreate(["Fallout4.esm"]));
    }

    [Fact]
    public void Namespaced_BaseMaster_IsIdentity()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["Fallout4.esm", "DLCCoast.esm"],
            mastersReader: Masters(new Dictionary<string, string[]>
            {
                ["Fallout4.esm"] = [],
                ["DLCCoast.esm"] = ["Fallout4.esm"]
            }))!;

        var records = CellCollection(0x00000FC9, 0x3C);
        Assert.Same(records, mapper.Namespaced(records, "Fallout4.esm"));
    }

    [Fact]
    public void Namespaced_SecondDlc_MovesOwnRecordsToItsSlot_AndKeepsOverridesOnMaster()
    {
        var mastersReader = Masters(new Dictionary<string, string[]>
        {
            ["Fallout4.esm"] = [],
            ["DLCCoast.esm"] = ["Fallout4.esm"],
            ["DLCNukaWorld.esm"] = ["Fallout4.esm"]
        });
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["Fallout4.esm", "DLCCoast.esm", "DLCNukaWorld.esm"], mastersReader: mastersReader)!;

        // DLCCoast at slot 1: local 0x01 (own) already equals its slot → identity.
        var coast = CellCollection(0x01000B0F, 0x01000B0F);
        Assert.Same(coast, mapper.Namespaced(coast, "DLCCoast.esm"));

        // DLCNukaWorld at slot 2: own records move 0x01 → 0x02, master references stay 0x00.
        var nuka = new RecordCollection
        {
            Cells =
            [
                new CellRecord // its own new cell in its own worldspace
                {
                    FormId = 0x01000FEF,
                    WorldspaceFormId = 0x01000FE0,
                    PlacedObjects =
                    [
                        new PlacedReference { FormId = 0x01000FF0, BaseFormId = 0x00000A00 }
                    ]
                },
                new CellRecord { FormId = 0x00000FC9, WorldspaceFormId = 0x3C } // Commonwealth override
            ]
        };

        var rebased = mapper.Namespaced(nuka, "DLCNukaWorld.esm");
        Assert.NotSame(nuka, rebased);
        var own = rebased.Cells.Single(c => c.FormId >> 24 == 0x02);
        Assert.Equal(0x02000FEFu, own.FormId);
        Assert.Equal(0x02000FE0u, own.WorldspaceFormId);
        var ownRef = Assert.Single(own.PlacedObjects);
        Assert.Equal(0x02000FF0u, ownRef.FormId);
        Assert.Equal(0x00000A00u, ownRef.BaseFormId); // master reference untouched
        Assert.Contains(rebased.Cells, c => c.FormId == 0x00000FC9); // override still targets the base
    }

    [Fact]
    public void Namespaced_BaseGamePrimaryWithNoMasters_OccupiesSlotZero()
    {
        // Supplementary set [DLCCoast] with primary Fallout4.esm: a base-game primary has no MAST
        // list, so its own master count is 0 and it keeps slot 0 — that is the one shape where the
        // pre-2026-08-17 "primary owns slot 0" behavior was correct. Coast lands at slot 1, and its
        // master reference resolves to the primary's slot — overrides keep folding onto the base.
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["DLCCoast.esm"],
            "Fallout4.esm",
            Masters(new Dictionary<string, string[]>
            {
                ["Fallout4.esm"] = [],
                ["DLCCoast.esm"] = ["Fallout4.esm"]
            }))!;

        var records = CellCollection(0x00000FC9, 0x3C);
        Assert.Same(records, mapper.Namespaced(records, "DLCCoast.esm")); // identity: 0→0, own 1→1
    }

    [Fact]
    public void Namespaced_MissingMaster_GetsStableSharedSlot()
    {
        var mastersReader = Masters(new Dictionary<string, string[]>
        {
            ["A.esm"] = [],
            ["B.esm"] = ["Missing.esm"],
            ["C.esm"] = ["Missing.esm"]
        });
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["A.esm", "B.esm", "C.esm"], mastersReader: mastersReader)!;

        // Both B and C reference records of the absent master; the synthetic slot must match so
        // their overrides of it still fold together (slot 3 = first past the real files).
        var b = mapper.Namespaced(CellCollection(0x00001111, 0x3C), "B.esm");
        var c = mapper.Namespaced(CellCollection(0x00001111, 0x3C), "C.esm");
        Assert.Equal(0x03001111u, b.Cells[0].FormId);
        Assert.Equal(0x03001111u, c.Cells[0].FormId);
    }

    [Fact]
    public void Namespaced_NullAndSentinelFormIds_PassThrough()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["Fallout4.esm", "DLCNukaWorld.esm", "X.esm"],
            mastersReader: Masters(new Dictionary<string, string[]>
            {
                ["Fallout4.esm"] = [],
                ["DLCNukaWorld.esm"] = ["Fallout4.esm"],
                ["X.esm"] = ["Fallout4.esm"]
            }))!;

        var records = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x01001234,
                    PlacedObjects =
                    [
                        new PlacedReference { FormId = 0x01005678, BaseFormId = 0 },
                        new PlacedReference { FormId = 0xFFFFFFFF, BaseFormId = 0x0100AAAA }
                    ]
                }
            ]
        };

        var rebased = mapper.Namespaced(records, "X.esm");
        var cell = Assert.Single(rebased.Cells);
        Assert.Equal(0x02001234u, cell.FormId);
        Assert.Equal(0u, cell.PlacedObjects[0].BaseFormId); // null ref untouched
        Assert.Equal(0xFFFFFFFFu, cell.PlacedObjects[1].FormId); // sentinel untouched
        Assert.Equal(0x0200AAAAu, cell.PlacedObjects[1].BaseFormId); // own-record ref moved with the file
    }

    [Fact]
    public void Namespaced_NonPluginSource_IsIdentity()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["Fallout4.esm", "capture.dmp"],
            mastersReader: Masters(new Dictionary<string, string[]> { ["Fallout4.esm"] = [] }))!;

        var records = CellCollection(0x01001234, 0x3C);
        Assert.Same(records, mapper.Namespaced(records, "capture.dmp"));
    }

    /// <summary>
    ///     The user-reported case (2026-08-17): open a converted plugin, then add its master to the
    ///     Load Order to resolve the placements. The primary is merged UNSTAMPED, so it keeps raw
    ///     FormIDs — meaning the master must stay where the primary's references already point.
    ///     Slotting the primary at 0 pushed the master's records to 0x01xxxxxx, which both stranded
    ///     every reference AND collided with the primary's own 0x01 range.
    /// </summary>
    [Fact]
    public void Namespaced_MasterAddedBesideAnUnstampedPrimary_StaysWhereItsReferencesPoint()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["FalloutNV.esm"],
            "xex4.v152.esm",
            Masters(new Dictionary<string, string[]>
            {
                ["FalloutNV.esm"] = [],
                ["xex4.v152.esm"] = ["FalloutNV.esm"]
            }))!;

        // The plugin's REFRs name master bases as 0x00xxxxxx and are never rebased (unstamped),
        // so FalloutNV.esm's records have to stay at 0x00xxxxxx for the lookup to hit.
        var master = CellCollection(0x0008E665, 0x3C);
        Assert.Same(master, mapper.Namespaced(master, @"C:\FNV\Data\FalloutNV.esm"));
    }

    /// <summary>Two masters keep their MAST order, and the primary lands after both.</summary>
    [Fact]
    public void Namespaced_TwoMastersBesideAPrimary_BothStayIdentity()
    {
        var mastersReader = Masters(new Dictionary<string, string[]>
        {
            ["FalloutNV.esm"] = [],
            ["DeadMoney.esm"] = ["FalloutNV.esm"],
            ["proto.esp"] = ["FalloutNV.esm", "DeadMoney.esm"]
        });
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["FalloutNV.esm", "DeadMoney.esm"], "proto.esp", mastersReader)!;

        var nv = CellCollection(0x0008E665, 0x3C);
        Assert.Same(nv, mapper.Namespaced(nv, "FalloutNV.esm"));

        // DeadMoney is master index 1 in the primary's list and sits at slot 1, so its own
        // 0x01-prefixed records are already correct and its 0x00 refs still mean FalloutNV.
        var dm = CellCollection(0x01000B0F, 0x01000B0F);
        Assert.Same(dm, mapper.Namespaced(dm, "DeadMoney.esm"));
    }

    /// <summary>
    ///     A dump primary passes null and keeps the old numbering, because a dump is self-contained
    ///     and owns the unstamped 0x00 range itself.
    /// </summary>
    [Fact]
    public void Namespaced_NoPrimary_KeepsMasterAtSlotZero()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["FalloutNV.esm", "DeadMoney.esm"],
            mastersReader: Masters(new Dictionary<string, string[]>
            {
                ["FalloutNV.esm"] = [],
                ["DeadMoney.esm"] = ["FalloutNV.esm"]
            }))!;

        var nv = CellCollection(0x0008E665, 0x3C);
        Assert.Same(nv, mapper.Namespaced(nv, "FalloutNV.esm"));
    }

    /// <summary>
    ///     2026-08-17 adversarial review: pins the primary's OWN slot (= its master count), which no
    ///     earlier test observed — reverting the primary to slot 0 kept the whole class green. A
    ///     supplementary patch that lists the PRIMARY as a master must resolve those references into
    ///     the primary's raw block, and its own records must land past the anchored slots.
    /// </summary>
    [Fact]
    public void Namespaced_EntryListingThePrimaryAsMaster_ResolvesIntoThePrimarysSlot()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["FalloutNV.esm", "Patch.esp"],
            "xex4.v152.esm",
            Masters(new Dictionary<string, string[]>
            {
                ["FalloutNV.esm"] = [],
                ["xex4.v152.esm"] = ["FalloutNV.esm"],
                ["Patch.esp"] = ["FalloutNV.esm", "xex4.v152.esm"]
            }))!;

        // Slots: FalloutNV 0 (MAST anchor), primary 1 (its master count), Patch 2 (first free).
        var patch = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x02000FEF, // local 0x02 = Patch's own record
                    PlacedObjects =
                    [
                        // local 0x01 = the PRIMARY: must stay in its raw block (slot 1), not fall to 0.
                        new PlacedReference { FormId = 0x02000FF0, BaseFormId = 0x01000A00 }
                    ]
                }
            ]
        };

        var rebased = mapper.Namespaced(patch, "Patch.esp");
        var cell = Assert.Single(rebased.Cells);
        Assert.Equal(0x02000FEFu, cell.FormId);
        Assert.Equal(0x01000A00u, Assert.Single(cell.PlacedObjects).BaseFormId);
    }

    /// <summary>
    ///     2026-08-17 adversarial review: synthetic slots for absent masters must start past EVERY
    ///     occupied slot. Anchoring the primary's masters can occupy slots at or above the file
    ///     count, and a count-based seed then handed the absent master an OCCUPIED slot — aliasing
    ///     two files' records into one global block, where the FormID-keyed merge folds them.
    /// </summary>
    [Fact]
    public void Namespaced_MissingMasterBesideAnAnchoredPrimary_GetsAnUnoccupiedSlot()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["FalloutNV.esm", "SomePatch.esp"],
            "proto.esp",
            Masters(new Dictionary<string, string[]>
            {
                ["FalloutNV.esm"] = [],
                ["proto.esp"] = ["FalloutNV.esm", "DeadMoney.esm"],
                ["SomePatch.esp"] = ["FalloutNV.esm", "Missing.esm"]
            }))!;

        // Slots: FalloutNV 0 and DeadMoney 1 (anchored), proto 2, SomePatch 3 (first free).
        var patch = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x02000FEF, // local 0x02 = SomePatch's own record
                    PlacedObjects =
                    [
                        new PlacedReference { FormId = 0x02000FF0, BaseFormId = 0x01000A00 } // local 0x01 = Missing.esm
                    ]
                }
            ]
        };

        var rebased = mapper.Namespaced(patch, "SomePatch.esp");
        var cell = Assert.Single(rebased.Cells);
        Assert.Equal(0x03000FEFu, cell.FormId); // own records land at slot 3
        // Missing.esm gets 4 — past everything — never SomePatch's own slot 3 (the review bug).
        Assert.Equal(0x04000A00u, Assert.Single(cell.PlacedObjects).BaseFormId);
    }

    /// <summary>
    ///     2026-08-17 adversarial review: a duplicated (or case-differing) name in the primary's
    ///     MAST list must still RESERVE its index. The unstamped primary's raw references use that
    ///     high byte for the earlier occurrence, so handing the hole to an unrelated filler would
    ///     alias them onto the wrong file's records; reserved-but-vacant merely leaves them dangling.
    /// </summary>
    [Fact]
    public void Namespaced_DuplicateMasterInPrimaryList_ReservesItsSlot()
    {
        var mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            ["A.esm", "X.esp"],
            "P.esp",
            Masters(new Dictionary<string, string[]>
            {
                ["A.esm"] = [],
                ["P.esp"] = ["A.esm", "a.esm"], // duplicate differing only by case
                ["X.esp"] = ["A.esm"]
            }))!;

        // Slots: A 0, (reserved duplicate) 1, P 2 — X must land at 3, never in the vacant 1 where
        // the primary's raw 0x01 references (which mean A via the duplicate) would alias onto it.
        var x = CellCollection(0x01001234, 0x3C);
        var rebased = mapper.Namespaced(x, "X.esp");
        Assert.Equal(0x03001234u, rebased.Cells[0].FormId);
    }

    /// <summary>
    ///     Pins the REAL ReadMasters path, which every other test bypasses via the injected reader:
    ///     the primary's MAST list is read from its FULL PATH even while another handle (the GUI
    ///     session) holds the file open with write access. A regression to a FileShare.Read open
    ///     throws a sharing violation here that gets swallowed into an empty master list — which
    ///     silently reinstates the primary-at-slot-0 bug this class documents.
    /// </summary>
    [Fact]
    public void TryCreate_ReadsPrimaryMastersFromDisk_EvenWhileTheFileIsHeldOpenForWrite()
    {
        var tempDir = Directory.CreateTempSubdirectory("tes4mapper-").FullName;
        try
        {
            var masterPath = Path.Combine(tempDir, "FalloutNV.esm");
            File.WriteAllBytes(masterPath, BuildHeaderOnlyEsm());
            var primaryPath = Path.Combine(tempDir, "xex4.v152.esm");
            File.WriteAllBytes(primaryPath, BuildHeaderOnlyEsm("FalloutNV.esm"));

            using var held = new FileStream(
                primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

            var mapper = Tes4LoadOrderFormIdMapper.TryCreate([masterPath], primaryPath)!;

            // FalloutNV anchors at slot 0 (identity) only if the MAST list actually got read; a
            // swallowed read failure would put the primary at 0 and rebase the master to 0x01.
            var master = CellCollection(0x0008E665, 0x3C);
            Assert.Same(master, mapper.Namespaced(master, masterPath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static byte[] BuildHeaderOnlyEsm(params string[] masters)
    {
        var subrecords = new List<(string Signature, byte[] Data)> { ("HEDR", BuildHedr()) };
        foreach (var master in masters)
        {
            subrecords.Add(("MAST", Encoding.ASCII.GetBytes(master + "\0")));
            subrecords.Add(("DATA", new byte[8]));
        }

        var dataSize = subrecords.Sum(subrecord => 6 + subrecord.Data.Length);
        var data = new byte[24 + dataSize];
        Encoding.ASCII.GetBytes("TES4", data.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);

        var offset = 24;
        foreach (var (signature, bytes) in subrecords)
        {
            Encoding.ASCII.GetBytes(signature, data.AsSpan(offset, 4));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 4), (ushort)bytes.Length);
            bytes.CopyTo(data.AsSpan(offset + 6));
            offset += 6 + bytes.Length;
        }

        return data;
    }

    private static byte[] BuildHedr()
    {
        var hedr = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(hedr, 1.34f);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(8), 0x800);
        return hedr;
    }
}