using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class CellStructuralReferencePreserverTests
{
    [Theory]
    [InlineData(0x00000015u)] // MultiBoundMarker
    [InlineData(0x00000017u)] // OcclusionMarker
    [InlineData(0x0000001Fu)] // RoomMarker
    [InlineData(0x00000020u)] // PortalMarker
    public void IsRenderCullingMarker_EngineMarkerBaseFormId_ReturnsTrue(uint baseFormId)
    {
        var refr = MakeRefr(0x0010CC01, baseFormId);

        Assert.True(CellStructuralReferencePreserver.IsRenderCullingMarker(
            refr, new Dictionary<uint, ParsedMainRecord>()));
    }

    [Fact]
    public void IsRenderCullingMarker_CollisionMarkerBase_ReturnsFalse()
    {
        // Collision marker (0x21) drives physics/navigation, not rendering — must be kept.
        var refr = MakeRefr(0x0010CC02, 0x00000021u);

        Assert.False(CellStructuralReferencePreserver.IsRenderCullingMarker(
            refr, new Dictionary<uint, ParsedMainRecord>()));
    }

    [Fact]
    public void IsRenderCullingMarker_OrdinaryStaticBase_ReturnsFalse()
    {
        var refr = MakeRefr(0x0010CC03, 0x000ABCDEu);

        Assert.False(CellStructuralReferencePreserver.IsRenderCullingMarker(
            refr, new Dictionary<uint, ParsedMainRecord>()));
    }

    [Fact]
    public void IsRenderCullingMarker_NonCanonicalBaseWithMarkerEditorId_ReturnsTrue()
    {
        // A base record (not an engine default-object FormID) whose editor ID names a marker.
        var baseRecord = MakeBase("STAT", 0x000ABCDE, "PortalMarker");
        var refr = MakeRefr(0x0010CC04, 0x000ABCDEu);

        Assert.True(CellStructuralReferencePreserver.IsRenderCullingMarker(
            refr, new Dictionary<uint, ParsedMainRecord> { [0x000ABCDE] = baseRecord }));
    }

    [Fact]
    public void IsRenderCullingMarker_NonCanonicalCollisionEditorId_ReturnsFalse()
    {
        var baseRecord = MakeBase("STAT", 0x000ABCDF, "CollisionMarker");
        var refr = MakeRefr(0x0010CC05, 0x000ABCDFu);

        Assert.False(CellStructuralReferencePreserver.IsRenderCullingMarker(
            refr, new Dictionary<uint, ParsedMainRecord> { [0x000ABCDF] = baseRecord }));
    }

    [Fact]
    public void IsRenderCullingMarker_NonRefrSignature_ReturnsFalse()
    {
        var achr = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "ACHR", FormId = 0x0010CC06, Version = 0x000F },
            Subrecords = [MakeName(0x0000001Fu)]
        };

        Assert.False(CellStructuralReferencePreserver.IsRenderCullingMarker(
            achr, new Dictionary<uint, ParsedMainRecord>()));
    }

    private static ParsedMainRecord MakeRefr(uint formId, uint baseFormId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "REFR", FormId = formId, Version = 0x000F },
            Subrecords = [MakeName(baseFormId)]
        };
    }

    private static ParsedSubrecord MakeName(uint baseFormId)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, baseFormId);
        return new ParsedSubrecord { Signature = "NAME", Data = data };
    }

    private static ParsedMainRecord MakeBase(string signature, uint formId, string editorId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = signature, FormId = formId, Version = 0x000F },
            Subrecords =
            [
                new ParsedSubrecord
                {
                    Signature = "EDID",
                    Data = System.Text.Encoding.Latin1.GetBytes(editorId + "\0")
                }
            ]
        };
    }
}
