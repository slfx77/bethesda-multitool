using BethesdaMultitool.Core.Formats.Esm.Plugin;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for <see cref="EngineDefaultObjectMarkers" /> — the table that maps engine
///     default-object marker editor IDs to their canonical hardcoded FormIDs so DMP captures
///     of PortalMarker/OcclusionMarker (engine-only forms with no ESM record) are aliased
///     back to the real marker instead of emitted as duplicate STATs that break the
///     room/portal/occlusion graph.
/// </summary>
public class EngineDefaultObjectMarkersTests
{
    [Theory]
    [InlineData("PortalMarker", 0x00000020u)]
    [InlineData("OcclusionMarker", 0x00000017u)]
    [InlineData("RoomMarker", 0x0000001Fu)]
    [InlineData("CollisionMarker", 0x00000021u)]
    [InlineData("MultiBoundMarker", 0x00000015u)]
    public void TryGetCanonicalFormId_KnownMarker_ReturnsCanonicalEngineFormId(string editorId, uint expected)
    {
        Assert.True(EngineDefaultObjectMarkers.TryGetCanonicalFormId(editorId, out var formId));
        Assert.Equal(expected, formId);
    }

    [Fact]
    public void TryGetCanonicalFormId_IsCaseInsensitive()
    {
        Assert.True(EngineDefaultObjectMarkers.TryGetCanonicalFormId("portalmarker", out var formId));
        Assert.Equal(0x00000020u, formId);
    }

    [Theory]
    [InlineData("SomeProtoStatic")]
    [InlineData("DealerMarker01")] // a real FURN editor id — must NOT collide with the marker table
    [InlineData("")]
    [InlineData(null)]
    public void TryGetCanonicalFormId_NonMarker_ReturnsFalse(string? editorId)
    {
        Assert.False(EngineDefaultObjectMarkers.TryGetCanonicalFormId(editorId, out var formId));
        Assert.Equal(0u, formId);
    }
}