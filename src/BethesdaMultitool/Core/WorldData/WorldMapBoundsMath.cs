using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     Pure bounds math shared by the world-map hit tester. Resolves an object's clamped half-extents
///     from the OBND bounds index in a single dictionary lookup, so the hit tester can resolve once and
///     feed both the hit test and the large/small area classification — previously two separate
///     <c>BoundsIndex.TryGetValue</c> lookups per object on every PointerMoved event.
///     Lives in the shared (non-GUI) compile set so it can be unit tested on the net10.0 TFM.
/// </summary>
internal static class WorldMapBoundsMath
{
    /// <summary>Maximum half-extent for hit testing (matches the rendering clamp).</summary>
    internal const float MaxHalfExtent = 2048f;

    /// <summary>
    ///     Resolves an object's clamped half-extents from the bounds index with a single lookup.
    ///     Returns <see cref="ClampedExtents.None" /> when the object has no bounds entry.
    /// </summary>
    internal static ClampedExtents Resolve(PlacedReference obj, IReadOnlyDictionary<uint, ObjectBounds> boundsIndex)
    {
        if (boundsIndex.TryGetValue(obj.BaseFormId, out var bounds))
        {
            var halfW = Math.Min((bounds.X2 - bounds.X1) * 0.5f * obj.Scale, MaxHalfExtent);
            var halfH = Math.Min((bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale, MaxHalfExtent);
            return new ClampedExtents(true, halfW, halfH);
        }

        return ClampedExtents.None;
    }

    /// <summary>Clamped half-extents resolved from an object's OBND bounds (if any).</summary>
    internal readonly record struct ClampedExtents(bool HasBounds, float HalfW, float HalfH)
    {
        internal static readonly ClampedExtents None = new(false, 0f, 0f);

        /// <summary>Bounding area (halfW * halfH); 0 when there are no usable bounds.</summary>
        internal float Area => HasBounds ? HalfW * HalfH : 0f;
    }
}
