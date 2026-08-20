using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     Exact snapshot of every non-spatial gate owned by <c>ReferenceRenderer12</c>. It is shared by
///     renderer-adjacent consumers whose content outlives one cull/build pass (placed-NIF water and
///     stationary shadow maps), so changing a visibility toggle cannot leave their cached content stale.
/// </summary>
internal readonly record struct ReferenceVisibilityKey(
    int EnabledOverrideVersion,
    ulong HiddenCategoryMask,
    bool ShowInitiallyDisabled,
    bool ShowGrass,
    bool ShowMarkers,
    bool ShowImposters,
    int DayNightVersion)
{
    /// <summary>Captures a deterministic key; category enumeration order does not affect it.</summary>
    internal static ReferenceVisibilityKey Capture(
        ReferenceEnabledOverrideStore enabledOverrides,
        IEnumerable<PlacedObjectCategory> hiddenCategories,
        bool showInitiallyDisabled,
        bool showGrass,
        bool showMarkers,
        bool showImposters,
        DayNightRefStateStore? dayNightStates = null)
    {
        ArgumentNullException.ThrowIfNull(enabledOverrides);
        ArgumentNullException.ThrowIfNull(hiddenCategories);

        return new ReferenceVisibilityKey(
            enabledOverrides.Version,
            BuildHiddenCategoryMask(hiddenCategories),
            showInitiallyDisabled,
            showGrass,
            showMarkers,
            showImposters,
            dayNightStates?.Version ?? 0);
    }

    /// <summary>
    ///     Applies the same independent authored, grass, marker, imposter, and category gates as the
    ///     reference cull. An explicit <see cref="ReferenceEnabledOverride.On" /> wins only over the
    ///     authored enabled state; it deliberately does not bypass the other gates. For references a
    ///     day/night script toggles, the current-hour state replaces the authored initial state.
    /// </summary>
    internal bool IsVisible(
        in RenderableReference reference,
        ReferenceEnabledOverrideStore enabledOverrides,
        DayNightRefStateStore? dayNightStates = null)
    {
        ArgumentNullException.ThrowIfNull(enabledOverrides);

        return enabledOverrides.IsVisible(
                   reference.FormId,
                   dayNightStates?.EffectiveDisabled(reference.FormId, reference.IsInitiallyDisabled)
                       ?? reference.IsInitiallyDisabled,
                   ShowInitiallyDisabled)
               && (ShowGrass || !reference.IsGrass)
               && (ShowMarkers || !reference.IsMarker)
               && (ShowImposters || !reference.IsImposter)
               && !IsCategoryHidden(reference.Category);
    }

    private bool IsCategoryHidden(PlacedObjectCategory category)
    {
        var value = (int)category;
        return value is >= 0 and < 64 && (HiddenCategoryMask & (1UL << value)) != 0;
    }

    internal static ulong BuildHiddenCategoryMask(IEnumerable<PlacedObjectCategory> hiddenCategories)
    {
        ArgumentNullException.ThrowIfNull(hiddenCategories);

        ulong mask = 0;
        foreach (var category in hiddenCategories)
        {
            var value = (int)category;
            if (value is < 0 or >= 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(hiddenCategories),
                    category,
                    "Placed-object visibility keys support category values from 0 through 63.");
            }

            mask |= 1UL << value;
        }

        return mask;
    }
}
