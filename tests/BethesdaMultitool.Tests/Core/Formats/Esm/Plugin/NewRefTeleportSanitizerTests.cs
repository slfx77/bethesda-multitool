using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

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

        var result = PlacedReferenceAnalysis.IsRuntimeStructuralMarkerPlacement(
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

        var result = PlacedReferenceAnalysis.IsRuntimeStructuralMarkerPlacement(
            placed,
            records,
            out var baseEditorId);

        Assert.False(result);
        Assert.Null(baseEditorId);
    }

    // Legacy door-teleport repair/sanitizer tests (TryRepairStaticDoorTeleport,
    // SanitizeNewRefTeleport) were removed with retirement Stage F (2026-08-11) together
    // with the legacy cell-merge loop that owned them. The planner equivalents are
    // Planner/Cells/DoorTeleportTargetRescueTests and PlacedRefTeleportSanitizerTests.
    // The structural-marker cases above target PlacedReferenceAnalysis, which survives.

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
                Data = Encoding.ASCII.GetBytes(editorId + '\0')
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
                Data = Encoding.ASCII.GetBytes(modelPath + '\0')
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
}