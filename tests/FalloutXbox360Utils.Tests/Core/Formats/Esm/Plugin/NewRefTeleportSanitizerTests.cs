using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class NewRefTeleportSanitizerTests
{
    [Fact]
    public void IsRuntimeStructuralMarkerPlacement_DetectsRoomMarkerBase()
    {
        var records = new Dictionary<uint, ParsedMainRecord>
        {
            [0x0000001F] = Record("STAT", 0x0000001F, "RoomMarker")
        };
        var placed = new PlacedReference
        {
            FormId = 0x01003772,
            RecordType = "REFR",
            BaseFormId = 0x0000001F
        };

        var result = PluginBuilder.IsRuntimeStructuralMarkerPlacement(
            placed,
            records,
            out var baseEditorId);

        Assert.True(result);
        Assert.Equal("RoomMarker", baseEditorId);
    }

    [Fact]
    public void IsRuntimeStructuralMarkerPlacement_IgnoresMapMarkerBase()
    {
        var records = new Dictionary<uint, ParsedMainRecord>
        {
            [0x00000010] = Record("STAT", 0x00000010, "MapMarker",
                modelPath: "Marker_Map.NIF")
        };
        var placed = new PlacedReference
        {
            FormId = 0x01003773,
            RecordType = "REFR",
            BaseFormId = 0x00000010
        };

        var result = PluginBuilder.IsRuntimeStructuralMarkerPlacement(
            placed,
            records,
            out var baseEditorId);

        Assert.False(result);
        Assert.Null(baseEditorId);
    }

    [Fact]
    public void TryRepairStaticDoorTeleport_SynthesizesDoorAndRetargetsSource()
    {
        var builder = CreateBuilder();
        var allocator = new FormIdAllocator();
        var sourceRefFormId = allocator.Allocate();
        var records = new Dictionary<uint, ParsedMainRecord>
        {
            [0x000914E1] = Record("DOOR", 0x000914E1, "WastelandhomeextDoor",
                modelPath: "Architecture\\Wasteland\\WastelandhomeextDoor.NIF"),
            [0x00105D49] = Record("REFR", 0x00105D49, nameFormId: 0x0017A671,
                data: new PositionSubrecord(1, 2, 3, 4, 5, 6, 0, false)),
            [0x0017A671] = Record("STAT", 0x0017A671, "WastelandHomeExtDoorIntSTATIC",
                modelPath: "architecture\\wasteland\\wastelandhomeextdoorintstatic.nif")
        };
        var childLocations = new Dictionary<uint, MasterChildLocation>
        {
            [0x00105D49] = new(0x00105CF7, 9, "REFR")
        };
        var rerouted = new Dictionary<uint, PluginBuilder.CellChildRecordBuckets>();
        var topLevelGroups = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var repairs = new Dictionary<uint, PluginBuilder.DoorTeleportRepair>();
        var placed = NewDoorRef(0x01003756, 0x00105D49) with
        {
            X = 10,
            Y = 20,
            Z = 30,
            RotX = 0.1f,
            RotY = 0.2f,
            RotZ = 0.3f
        };

        var repaired = builder.TryRepairStaticDoorTeleport(
            placed,
            sourceRefFormId,
            records,
            childLocations,
            rerouted,
            topLevelGroups,
            repairs,
            allocator,
            new PluginBuildOptions(),
            new ConversionPipelineStats(),
            out var repairedPlaced);

        Assert.True(repaired);
        Assert.Equal(0x01000802u, repairedPlaced.DestinationDoorFormId);
        Assert.Equal(0x01000802u, repairs[0x00105D49].ReplacementRefFormId);
        Assert.True(topLevelGroups.ContainsKey("DOOR"));

        var doorRecord = SingleRecordFromGrup(topLevelGroups["DOOR"]);
        Assert.Equal("DOOR", doorRecord.Header.Signature);
        Assert.Equal(0x01000801u, doorRecord.Header.FormId);
        Assert.Equal("DmpDoorStatic00105D49", doorRecord.EditorId);
        Assert.Equal("architecture\\wasteland\\wastelandhomeextdoorintstatic.nif",
            doorRecord.Subrecords.Single(s => s.Signature == "MODL").DataAsString);

        var buckets = rerouted[0x00105CF7];
        var replacementRef = SingleRecordFromBytes(buckets.Persistent.Single());
        Assert.Equal(0x01000802u, replacementRef.Header.FormId);
        Assert.Equal(0x01000801u, ReadFormIdSubrecord(replacementRef, "NAME"));
        Assert.Equal(sourceRefFormId, ReadFormIdSubrecord(replacementRef, "XTEL"));

        var deletedStatic = SingleRecordFromBytes(buckets.Temporary.Single());
        Assert.Equal(0x00105D49u, deletedStatic.Header.FormId);
        Assert.True(deletedStatic.Header.IsDeleted);
    }

    [Fact]
    public void SanitizeNewRefTeleport_DropsTargetWhoseBaseIsStatic()
    {
        var builder = CreateBuilder();
        var records = new Dictionary<uint, ParsedMainRecord>
        {
            [0x000914E1] = Record("DOOR", 0x000914E1, "WastelandhomeextDoor"),
            [0x00105D49] = Record("REFR", 0x00105D49, nameFormId: 0x0017A671),
            [0x0017A671] = Record("STAT", 0x0017A671, "WastelandHomeExtDoorIntSTATIC")
        };
        var placed = NewDoorRef(0x01003756, 0x00105D49);

        var sanitized = builder.SanitizeNewRefTeleport(
            placed,
            records,
            new PluginBuildOptions(),
            new ConversionPipelineStats());

        Assert.Null(sanitized.DestinationDoorFormId);
        Assert.Null(sanitized.TeleportPosRot);
        Assert.Null(sanitized.TeleportFlags);
    }

    [Fact]
    public void SanitizeNewRefTeleport_DropsMissingTarget()
    {
        var builder = CreateBuilder();
        var records = new Dictionary<uint, ParsedMainRecord>
        {
            [0x000914E1] = Record("DOOR", 0x000914E1, "WastelandhomeextDoor")
        };
        var placed = NewDoorRef(0x010035F2, 0x0010BDF7);

        var sanitized = builder.SanitizeNewRefTeleport(
            placed,
            records,
            new PluginBuildOptions(),
            new ConversionPipelineStats());

        Assert.Null(sanitized.DestinationDoorFormId);
    }

    [Fact]
    public void SanitizeNewRefTeleport_KeepsValidMasterDoorTarget()
    {
        var builder = CreateBuilder();
        var records = new Dictionary<uint, ParsedMainRecord>
        {
            [0x000914E1] = Record("DOOR", 0x000914E1, "WastelandhomeextDoor"),
            [0x00105D48] = Record("REFR", 0x00105D48, nameFormId: 0x0006638D),
            [0x0006638D] = Record("DOOR", 0x0006638D, "WastelandhomeextDoorInt")
        };
        var placed = NewDoorRef(0x01003739, 0x00105D48);

        var sanitized = builder.SanitizeNewRefTeleport(
            placed,
            records,
            new PluginBuildOptions(),
            new ConversionPipelineStats());

        Assert.Equal(0x00105D48u, sanitized.DestinationDoorFormId);
        Assert.Equal((byte?)7, sanitized.TeleportFlags);
    }

    private static PlacedReference NewDoorRef(uint formId, uint destinationDoorFormId)
    {
        return new PlacedReference
        {
            FormId = formId,
            RecordType = "REFR",
            BaseFormId = 0x000914E1,
            DestinationDoorFormId = destinationDoorFormId,
            TeleportFlags = 7
        };
    }

    private static ParsedMainRecord Record(
        string signature,
        uint formId,
        string? editorId = null,
        uint? nameFormId = null,
        string? modelPath = null,
        PositionSubrecord? data = null)
    {
        var subrecords = new List<ParsedSubrecord>();
        if (editorId is not null)
        {
            subrecords.Add(new ParsedSubrecord
            {
                Signature = "EDID",
                Data = System.Text.Encoding.ASCII.GetBytes(editorId + '\0')
            });
        }

        if (nameFormId.HasValue)
        {
            var nameBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(nameBytes, nameFormId.Value);
            subrecords.Add(new ParsedSubrecord
            {
                Signature = "NAME",
                Data = nameBytes
            });
        }

        if (modelPath is not null)
        {
            subrecords.Add(new ParsedSubrecord
            {
                Signature = "MODL",
                Data = System.Text.Encoding.ASCII.GetBytes(modelPath + '\0')
            });
        }

        if (data is not null)
        {
            var bytes = new byte[24];
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), data.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), data.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8, 4), data.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12, 4), data.RotX);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16, 4), data.RotY);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20, 4), data.RotZ);
            subrecords.Add(new ParsedSubrecord
            {
                Signature = "DATA",
                Data = bytes
            });
        }

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId
            },
            Subrecords = subrecords
        };
    }

    private static PluginBuilder CreateBuilder()
    {
#pragma warning disable CS0618
        return new PluginBuilder(RecordEncoderRegistry.CreateDefault());
#pragma warning restore CS0618
    }

    private static ParsedMainRecord SingleRecordFromGrup(byte[] grupBytes)
    {
        var (records, _) = EsmParser.EnumerateRecordsWithGrups(BuildPluginBytes(grupBytes));
        return Assert.Single(records.Where(r => r.Header.Signature != "TES4"));
    }

    private static ParsedMainRecord SingleRecordFromBytes(byte[] recordBytes)
    {
        var header = EsmParser.ParseRecordHeader(recordBytes, false)!;
        var subrecords = EsmParser.ParseSubrecords(
            recordBytes.AsSpan(EsmParser.MainRecordHeaderSize, (int)header.DataSize),
            false);
        return new ParsedMainRecord
        {
            Header = header,
            Subrecords = subrecords
        };
    }

    private static byte[] BuildPluginBytes(byte[] body)
    {
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var bytes = new byte[tes4.Length + body.Length];
        tes4.CopyTo(bytes, 0);
        body.CopyTo(bytes, tes4.Length);
        return bytes;
    }

    private static uint ReadFormIdSubrecord(ParsedMainRecord record, string signature)
    {
        var subrecord = Assert.Single(record.Subrecords.Where(s => s.Signature == signature));
        return BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data);
    }
}
