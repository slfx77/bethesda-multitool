using System.Runtime.CompilerServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>The authored record that supplied the current water appearance.</summary>
internal enum WaterAppearanceSelectionSource : uint
{
    Unavailable = 0,
    CellXcwt = 1,
    WorldspaceNam2 = 2,
    EngineDefault = 3
}

/// <summary>
///     Pure result of resolving a CELL's XCWT override against its WRLD NAM2 fallback.
///     The selected record is retained so renderer and telemetry use the same lookup result.
/// </summary>
internal readonly record struct ResolvedWaterAppearanceSelection(
    WaterRecord? Water,
    WaterAppearanceSelectionSource Source,
    uint? CellFormId,
    uint? WorldspaceFormId)
{
    internal uint? WaterFormId => Water?.FormId;

    internal string SourceTelemetry => Source switch
    {
        WaterAppearanceSelectionSource.CellXcwt => "cell-xcwt",
        WaterAppearanceSelectionSource.WorldspaceNam2 => "worldspace-nam2",
        WaterAppearanceSelectionSource.EngineDefault => "engine-default",
        _ => "unavailable"
    };
}

/// <summary>
///     Resolves the usable WATR for the current cell. A non-zero, retained CELL XCWT wins; missing,
///     zero, or unresolved XCWT falls back to the retained WRLD NAM2 record, and finally — for FO3/FNV
///     — to the engine's own default WATR forms.
///     <para>
///         That last tier exists because interiors have no worldspace, so a watery interior with no
///         XCWT previously resolved to nothing and the renderer substituted the FNV <em>exterior lake</em>
///         preset: blue-lake tints, <c>WaterSurfaceParams.Default</c> (the NVCleanWater exterior
///         preset) and no NNAM at all. Measured across the watery FNV interiors sampled, ~46% carry no
///         XCWT (<c>1ECisternA</c>, <c>Vault11c</c>, <c>Vault22b</c>, <c>TechatticupMineInterior</c>, …),
///         so nearly half of them rendered as open lake water.
///     </para>
///     <para>
///         Retail does NOT branch water shader selection on cell type —
///         <c>WaterShaderProperty::GetRenderPasses</c> picks the permutation from a
///         reflection/refraction-availability predicate and per-water capability bits, and the only
///         context switch it makes is camera submersion. So the interior/exterior difference is
///         entirely WATR <em>data</em>, which is what this tier supplies.
///     </para>
/// </summary>
internal static class WaterAppearanceSelectionResolver
{
    // Retail FO3/FNV engine-default water forms. Looked up by FormID first (stable when the game
    // master loads at index 0, which is the only configuration these defaults are meaningful in) and
    // by EditorID as a fallback for renumbered or remastered data.
    private const uint InteriorDefaultFormId = 0x0000421E;
    private const uint ExteriorDefaultFormId = 0x00000018;
    private const string InteriorDefaultEditorId = "DefaultInteriorWater";
    private const string ExteriorDefaultEditorId = "DefaultWater";

    // The EditorID fallback is a linear scan, and Resolve runs per frame. Memoize per water table —
    // the table is rebuilt on data load, so a weak keying retires the entry with it.
    private static readonly ConditionalWeakTable<
        IReadOnlyDictionary<uint, WaterRecord>, EngineDefaultWaters> EngineDefaultCache = new();

    internal static ResolvedWaterAppearanceSelection Resolve(
        CellRecord? cell,
        WorldspaceRecord? worldspace,
        IReadOnlyDictionary<uint, WaterRecord>? watersByFormId,
        BethesdaGame game = BethesdaGame.Unknown,
        bool isInterior = false)
    {
        if (TryResolve(cell?.WaterFormId, watersByFormId, out var cellWater))
        {
            return new ResolvedWaterAppearanceSelection(
                cellWater,
                WaterAppearanceSelectionSource.CellXcwt,
                cell?.FormId,
                worldspace?.FormId);
        }

        if (TryResolve(worldspace?.WaterFormId, watersByFormId, out var worldspaceWater))
        {
            return new ResolvedWaterAppearanceSelection(
                worldspaceWater,
                WaterAppearanceSelectionSource.WorldspaceNam2,
                cell?.FormId,
                worldspace?.FormId);
        }

        // Scoped to the two games confirmed to ship these forms. Every other game keeps the previous
        // behaviour until its own engine defaults are established from its binary.
        if (game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 &&
            watersByFormId is not null)
        {
            var defaults = EngineDefaultCache.GetValue(watersByFormId, ResolveEngineDefaults);
            var engineDefault = isInterior ? defaults.Interior : defaults.Exterior;
            if (engineDefault is not null)
            {
                return new ResolvedWaterAppearanceSelection(
                    engineDefault,
                    WaterAppearanceSelectionSource.EngineDefault,
                    cell?.FormId,
                    worldspace?.FormId);
            }
        }

        return new ResolvedWaterAppearanceSelection(
            null,
            WaterAppearanceSelectionSource.Unavailable,
            cell?.FormId,
            worldspace?.FormId);
    }

    private static EngineDefaultWaters ResolveEngineDefaults(
        IReadOnlyDictionary<uint, WaterRecord> watersByFormId)
    {
        return new EngineDefaultWaters(
            Find(watersByFormId, InteriorDefaultFormId, InteriorDefaultEditorId),
            Find(watersByFormId, ExteriorDefaultFormId, ExteriorDefaultEditorId));
    }

    private static WaterRecord? Find(
        IReadOnlyDictionary<uint, WaterRecord> watersByFormId, uint formId, string editorId)
    {
        if (watersByFormId.TryGetValue(formId, out var byFormId) &&
            string.Equals(byFormId.EditorId, editorId, StringComparison.OrdinalIgnoreCase))
        {
            return byFormId;
        }

        foreach (var water in watersByFormId.Values)
        {
            if (string.Equals(water.EditorId, editorId, StringComparison.OrdinalIgnoreCase))
            {
                return water;
            }
        }

        // A FormID hit whose EditorID did not match is still better than nothing: renumbered data can
        // rename the record, but a plugin overriding this exact form is overriding the engine default.
        return byFormId;
    }

    private static bool TryResolve(
        uint? formId,
        IReadOnlyDictionary<uint, WaterRecord>? watersByFormId,
        out WaterRecord? water)
    {
        water = null;
        return formId is > 0 && watersByFormId is not null &&
               watersByFormId.TryGetValue(formId.Value, out water);
    }

    private sealed record EngineDefaultWaters(WaterRecord? Interior, WaterRecord? Exterior);
}
