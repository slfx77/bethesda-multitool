using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     Decides whether a selected placement owns output the 3D viewer can actually preview, and
///     what its authored enabled-state is.
///     <para>
///         The point is to avoid offering a success-looking no-op: actor refs, ordinary refs with no
///         model, and malformed placements have nothing for a visibility toggle to act on, so the
///         UI must not present one.
///     </para>
///     <para>
///         In <c>Core/</c> because every input is already a Core type — the rules were previously
///         methods on the WinUI control, where the only reachable coverage was asserting the
///         renderer source still contained <c>"RenderableReference.TryBuild("</c>.
///     </para>
/// </summary>
internal static class ReferencePreviewEligibility
{
    /// <summary>
    ///     True when the placement resolves to a renderable mesh (including any embedded water) or
    ///     to a placed-LIGH emitter that actually emits.
    /// </summary>
    public static bool CanPreview(PlacedReference reference, WorldViewData? data)
    {
        if (reference.FormId == 0 || data is null)
        {
            return false;
        }

        var xespDisabled = data.XespDisabledRefs.Contains(reference.FormId);
        var category = data.CategoryIndex.GetValueOrDefault(
            reference.BaseFormId, PlacedObjectCategory.Unknown);

        if (RenderableReference.TryBuild(
                reference, category, xespDisabled: xespDisabled, game: data.Game) is not null)
        {
            return true;
        }

        return data.LightsByFormId.TryGetValue(reference.BaseFormId, out var light)
               && PlacedLight.TryBuild(reference, light, xespDisabled, data.Game) is { HasEmission: true };
    }

    /// <summary>
    ///     The resolved authored placement state used by reference meshes: the REFR's own flag or
    ///     the already-resolved XESP parent chain.
    ///     <para>
    ///         A base LIGH's Off By Default bit governs its emitter separately and must not be used
    ///         to claim an attached lantern/model is hidden — see
    ///         <see cref="IsBaseLightAuthoredEnabled" />. XSRF Imposter refs are NOT
    ///         authored-hidden: the 2026-08-10 census showed FNV's imposter population is the
    ///         retail-rendered Vegas skyline set, not vantage-only content. Deliberately ignores UI
    ///         preview overrides.
    ///     </para>
    /// </summary>
    public static bool IsAuthoredEnabled(PlacedReference reference, WorldViewData? data)
    {
        return !reference.IsInitiallyDisabled
               && data?.XespDisabledRefs.Contains(reference.FormId) != true;
    }

    /// <summary>
    ///     The base LIGH emitter's independent authored state, or <c>null</c> when the placement is
    ///     not backed by a parsed LIGH record.
    /// </summary>
    public static bool? IsBaseLightAuthoredEnabled(PlacedReference reference, WorldViewData? data)
    {
        return data?.LightsByFormId.TryGetValue(reference.BaseFormId, out var light) == true
            ? (light.Flags & PlacedLight.OffByDefaultFlag) == 0
            : null;
    }
}
