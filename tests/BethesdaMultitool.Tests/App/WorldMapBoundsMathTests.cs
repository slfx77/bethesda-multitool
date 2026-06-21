using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Tests for <see cref="WorldMapBoundsMath" />, the pure bounds resolution extracted from
///     <c>WorldMapHitTester</c> so that an object's clamped half-extents (and derived area) are
///     computed from a single bounds-index lookup instead of two per object per pointer event.
/// </summary>
public sealed class WorldMapBoundsMathTests
{
    private static Dictionary<uint, ObjectBounds> Index(uint formId, ObjectBounds bounds)
    {
        return new Dictionary<uint, ObjectBounds> { [formId] = bounds };
    }

    [Fact]
    public void Resolve_NoBoundsEntry_ReturnsNone()
    {
        var obj = new PlacedReference { FormId = 1, BaseFormId = 0xABCD, Scale = 1f };

        var extents = WorldMapBoundsMath.Resolve(obj, new Dictionary<uint, ObjectBounds>());

        Assert.False(extents.HasBounds);
        Assert.Equal(0f, extents.Area);
        Assert.Equal(WorldMapBoundsMath.ClampedExtents.None, extents);
    }

    [Fact]
    public void Resolve_WithBounds_ComputesClampedHalfExtents()
    {
        var obj = new PlacedReference { FormId = 1, BaseFormId = 0x10, Scale = 1f };
        var bounds = new ObjectBounds { X1 = -100, X2 = 100, Y1 = -50, Y2 = 50 };

        var extents = WorldMapBoundsMath.Resolve(obj, Index(0x10, bounds));

        Assert.True(extents.HasBounds);
        Assert.Equal(100f, extents.HalfW); // (100 - -100) * 0.5 * 1
        Assert.Equal(50f, extents.HalfH);  // (50 - -50) * 0.5 * 1
        Assert.Equal(5000f, extents.Area); // 100 * 50
    }

    [Fact]
    public void Resolve_AppliesObjectScaleBeforeClamp()
    {
        var obj = new PlacedReference { FormId = 1, BaseFormId = 0x10, Scale = 2f };
        var bounds = new ObjectBounds { X1 = -100, X2 = 100, Y1 = -100, Y2 = 100 };

        var extents = WorldMapBoundsMath.Resolve(obj, Index(0x10, bounds));

        Assert.Equal(200f, extents.HalfW); // 200 * 0.5 * 2
        Assert.Equal(200f, extents.HalfH);
    }

    [Fact]
    public void Resolve_ClampsToMaxHalfExtent()
    {
        var obj = new PlacedReference { FormId = 1, BaseFormId = 0x10, Scale = 1f };
        var bounds = new ObjectBounds { X1 = -30000, X2 = 30000, Y1 = -30000, Y2 = 30000 };

        var extents = WorldMapBoundsMath.Resolve(obj, Index(0x10, bounds));

        Assert.Equal(WorldMapBoundsMath.MaxHalfExtent, extents.HalfW);
        Assert.Equal(WorldMapBoundsMath.MaxHalfExtent, extents.HalfH);
    }
}
