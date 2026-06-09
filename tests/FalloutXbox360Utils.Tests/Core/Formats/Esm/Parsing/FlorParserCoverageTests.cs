using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordBuilder;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     FLOR (Flora) ESM-side parse coverage. FLOR is parsed through the generic record path
///     (MiscRecordHandler.ParseGenericRecords) — identity + EDID/FULL/MODL/OBND as named
///     properties, and PFIG (ingredient), SNAM (sound), SCRI (script) FormIDs via the subrecord
///     schema. Pins that a synthetic FLOR round-trips in both endiannesses so the ESM-only path
///     (no DMP) keeps carrying flora through.
/// </summary>
public class FlorParserCoverageTests
{
    [Fact]
    public void ParseGenericRecords_Flor_LittleEndian_CapturesIdentityAndFormIds()
    {
        AssertFlorParses(false);
    }

    [Fact]
    public void ParseGenericRecords_Flor_BigEndian_CapturesIdentityAndFormIds()
    {
        AssertFlorParses(true);
    }

    private static void AssertFlorParses(bool bigEndian)
    {
        const uint florFormId = 0x000F1000;
        const uint ingredientFormId = 0x000F2000;
        const uint soundFormId = 0x000F3000;
        const uint scriptFormId = 0x000F4000;

        var florBytes = BuildRecordBytes(
            florFormId, "FLOR", bigEndian,
            ("EDID", NullTermString("XanderRoot")),
            ("FULL", NullTermString("Xander Root")),
            ("MODL", NullTermString("Plants\\XanderRoot.NIF")),
            ("OBND", Obnd(-10, -11, -12, 20, 21, 22, bigEndian)),
            ("PFIG", FormIdBytes(ingredientFormId, bigEndian)),
            ("PFPC", [64, 100, 64, 0]), // per-season harvest chances (spring/summer/fall/winter)
            ("SCRI", FormIdBytes(scriptFormId, bigEndian)),
            ("SNAM", FormIdBytes(soundFormId, bigEndian)));

        var mainRecord = new DetectedMainRecord(
            "FLOR", (uint)(florBytes.Length - 24), 0, florFormId, 0, bigEndian);
        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, florBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, florBytes.Length);
        accessor.WriteArray(0, florBytes, 0, florBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: florBytes.Length);
        var flora = parser.ParseGenericRecords("FLOR");

        var flor = Assert.Single(flora);
        Assert.Equal("FLOR", flor.RecordType);
        Assert.Equal(florFormId, flor.FormId);
        Assert.Equal("XanderRoot", flor.EditorId);
        Assert.Equal("Xander Root", flor.FullName);
        Assert.Equal("Plants\\XanderRoot.NIF", flor.ModelPath);

        Assert.NotNull(flor.Bounds);
        Assert.Equal(-10, flor.Bounds!.X1);
        Assert.Equal(20, flor.Bounds.X2);

        // Schema-decoded FormID subrecords are stored as nested field dictionaries keyed by the
        // schema field name (see SubrecordCellAndMiscSchemas / SubrecordSchemaRegistry).
        Assert.Equal(ingredientFormId, NestedFormId(flor.Fields, "PFIG", "Ingredient FormID"));
        Assert.Equal(soundFormId, NestedFormId(flor.Fields, "SNAM", "Sound FormID"));
        Assert.Equal(scriptFormId, NestedFormId(flor.Fields, "SCRI", "Script FormID"));

        // PFPC carries a ByteArray schema with no named field, so the subrecord is seen (key present)
        // but its bytes aren't projected through the schema view. Pin that it is at least captured.
        Assert.True(flor.Fields.ContainsKey("PFPC"));
    }

    private static uint NestedFormId(Dictionary<string, object?> fields, string subrecord, string fieldName)
    {
        var nested = Assert.IsType<Dictionary<string, object?>>(fields[subrecord]);
        return Assert.IsType<uint>(nested[fieldName]);
    }

    private static byte[] FormIdBytes(uint value, bool bigEndian)
    {
        var bytes = new byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        return bytes;
    }

    private static byte[] Obnd(short x1, short y1, short z1, short x2, short y2, short z2, bool bigEndian)
    {
        var bytes = new byte[12];
        short[] values = [x1, y1, z1, x2, y2, z2];
        for (var i = 0; i < values.Length; i++)
        {
            var span = bytes.AsSpan(i * 2);
            if (bigEndian)
            {
                BinaryPrimitives.WriteInt16BigEndian(span, values[i]);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(span, values[i]);
            }
        }

        return bytes;
    }
}