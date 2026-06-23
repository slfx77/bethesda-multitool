using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public class NavInfoMapBuilderTests
{
    [Fact]
    public void Master_cell_navm_augmentation_is_enabled_by_default()
    {
        // When a master cell is overridden for new content, the proto's NAVM rides along by
        // default. Engine RE (memory/navm_engine_load_mechanism.md) proves master's own NAVMs
        // survive the override via the cell's TESForm file-list merge, so no verbatim copy is
        // emitted. Guards against silently reverting the default flip back to off.
        Assert.True(new PluginBuildOptions().EmitMasterCellNavmAugmentation);
    }

    [Fact]
    public void BuildNvmi_layout_matches_canonical_byte_offsets()
    {
        var entry = new NewNavmEntry(
            0x01003F30,
            0x01000801,
            false,
            -3,
            -1,
            null);

        var bytes = NavInfoMapBuilder.BuildNvmi(entry);

        Assert.Equal(32, bytes.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4))); // flags
        Assert.Equal(0x01003F30u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4))); // navmFormId
        Assert.Equal(0x01000801u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4))); // locationFormId
        Assert.Equal((short)-1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(12, 2))); // gridY
        Assert.Equal((short)-3, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(14, 2))); // gridX
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(28, 4))); // preferred%

        // Grid-center fallback when NVVX is absent.
        Assert.Equal(-3 * 4096f + 2048f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(-1 * 4096f + 2048f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void BuildNvmi_interior_zeros_grid_and_uses_origin_centroid()
    {
        var entry = new NewNavmEntry(
            0x01003FAB,
            0x01003F00,
            true,
            5,
            -10,
            null);

        var bytes = NavInfoMapBuilder.BuildNvmi(entry);

        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(12, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(14, 2)));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void BuildNvmi_with_nvvx_computes_centroid_from_vertices()
    {
        // Three vertices in LE float Vec3 form: (10,20,30), (40,50,60), (70,80,90). Centroid (40,50,60).
        var nvvx = new byte[3 * 12];
        WriteVec3(nvvx.AsSpan(0, 12), 10f, 20f, 30f);
        WriteVec3(nvvx.AsSpan(12, 12), 40f, 50f, 60f);
        WriteVec3(nvvx.AsSpan(24, 12), 70f, 80f, 90f);

        var entry = new NewNavmEntry(
            0x01000001,
            0x01000801,
            false,
            0,
            0,
            nvvx);

        var bytes = NavInfoMapBuilder.BuildNvmi(entry);

        Assert.Equal(40f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(50f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(60f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void BuildNvci_is_sixteen_bytes_navmFormId_then_three_zero_counts()
    {
        var entry = new NewNavmEntry(
            0x01003F30,
            0,
            false,
            0,
            0,
            null);

        var bytes = NavInfoMapBuilder.BuildNvci(entry);

        Assert.Equal(16, bytes.Length);
        Assert.Equal(0x01003F30u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void BuildNaviOverride_splices_new_NVMIs_into_master_NVMI_run_and_new_NVCIs_into_master_NVCI_run()
    {
        // Master NAVI is structured as NVER + [all NVMI] + [all NVCI] (verified against
        // FalloutNV.esm). FNVEdit flags any out-of-group occurrence as a structural error,
        // so new NVMIs must splice into the master NVMI run and new NVCIs into the master
        // NVCI run — NOT appended after the NVCI run ends.
        var master = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "NAVI",
                FormId = NavInfoMapBuilder.MasterNaviFormId,
                Flags = 0,
                Version = 0x000F
            },
            Subrecords =
            [
                new ParsedSubrecord { Signature = "NVER", Data = new byte[] { 0x0E, 0, 0, 0 } },
                new ParsedSubrecord { Signature = "NVMI", Data = new byte[32] },
                new ParsedSubrecord { Signature = "NVMI", Data = new byte[32] },
                new ParsedSubrecord { Signature = "NVCI", Data = new byte[16] },
                new ParsedSubrecord { Signature = "NVCI", Data = new byte[16] }
            ]
        };

        var newEntries = new List<NewNavmEntry>
        {
            new(0x01003F30, 0x01000801, false, -3, -1, null),
            new(0x01003F31, 0x01000801, false, 2, 5, null)
        };

        var recordBytes = NavInfoMapBuilder.BuildNaviOverride(
            master,
            newEntries,
            new PluginBuildOptions { CompressRecords = false });

        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(recordBytes.AsSpan(4, 4));
        var body = recordBytes.AsSpan(EsmParser.MainRecordHeaderSize, (int)dataSize);

        var sigOrder = new List<string>();
        var j = 0;
        while (j + 6 <= body.Length)
        {
            var sig = Encoding.ASCII.GetString(body.Slice(j, 4));
            var ssize = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(j + 4, 2));
            sigOrder.Add(sig);
            j += 6 + ssize;
        }

        // Counts: 1 NVER, 2 master + 2 new = 4 NVMI, 2 master + 2 new = 4 NVCI.
        Assert.Equal(1, sigOrder.Count(s => s == "NVER"));
        Assert.Equal(4, sigOrder.Count(s => s == "NVMI"));
        Assert.Equal(4, sigOrder.Count(s => s == "NVCI"));

        // CRITICAL: order must be NVER + [all NVMI grouped] + [all NVCI grouped]. New NVMIs
        // come AFTER master NVMIs but BEFORE master NVCIs.
        Assert.Equal(["NVER", "NVMI", "NVMI", "NVMI", "NVMI", "NVCI", "NVCI", "NVCI", "NVCI"], sigOrder);
    }

    private static void WriteVec3(Span<byte> dest, float x, float y, float z)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dest[..4], x);
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(4, 4), y);
        BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(8, 4), z);
    }
}
