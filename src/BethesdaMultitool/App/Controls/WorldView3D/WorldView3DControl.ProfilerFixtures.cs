using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

/// <summary>
///     Runtime evidence emitted when the profiler replaces only the water visibility set with a
///     single retail-authored CELL. The underlying terrain remains the ordinary selected-worldspace
///     renderer input, so its height range proves that the opaque scene has geometry below the plane.
/// </summary>
internal sealed record WorldView3DProfilerSyntheticWaterFixture(
    uint SourceCellFormId,
    int GridX,
    int GridY,
    uint WaterFormId,
    string? WaterEditorId,
    float PlaneHeight,
    float TerrainMinimumHeight,
    float TerrainMaximumHeight,
    int TerrainSamplesBelowPlane);

public sealed partial class WorldView3DControl
{
    /// <summary>Actual device sample count used by live and offscreen scene targets.</summary>
    internal int Profiler_SceneSampleCount => _gpu12?.SceneSampleCount ?? 1;

    /// <summary>
    ///     Installs the bounded positive WATER001 fixture without replacing the retail scene. The
    ///     terrain/reference renderers keep their normal WastelandNV data; only WaterRenderer12 gets
    ///     a one-cell dictionary and no broad spatial index. Every source invariant is checked before
    ///     mutation so a changed master fails the profiler scenario instead of silently weakening it.
    /// </summary>
    internal WorldView3DProfilerSyntheticWaterFixture? Profiler_ApplyFnvWater001SyntheticFixture(
        string worldspaceEditorId,
        uint sourceCellFormId,
        int gridX,
        int gridY,
        uint waterFormId,
        float planeHeight)
    {
        if (_data?.Game != BethesdaGame.FalloutNewVegas ||
            _water is null ||
            _selectedInterior is not null ||
            !float.IsFinite(planeHeight))
        {
            return null;
        }

        var worldspace = _data.Worldspaces.FirstOrDefault(candidate =>
            string.Equals(candidate.EditorId, worldspaceEditorId, StringComparison.OrdinalIgnoreCase));
        var sourceCell = worldspace?.Cells.FirstOrDefault(candidate => candidate.FormId == sourceCellFormId);
        if (worldspace is null ||
            sourceCell is null ||
            sourceCell.GridX != gridX ||
            sourceCell.GridY != gridY ||
            sourceCell.WaterFormId != waterFormId ||
            sourceCell.WaterHeight is not { } authoredPlaneHeight ||
            !float.IsFinite(authoredPlaneHeight) ||
            MathF.Abs(authoredPlaneHeight - planeHeight) > 0.001f ||
            sourceCell.Heightmap is null ||
            !_data.WatersByFormId.TryGetValue(waterFormId, out var water))
        {
            return null;
        }

        var appearance = WaterAppearance.FromWaterRecord(water);
        if (appearance is null ||
            appearance.IsLava ||
            !appearance.Surface.HasAuthoredClassicRefractionInputs)
        {
            return null;
        }

        var terrainMinimum = float.PositiveInfinity;
        var terrainMaximum = float.NegativeInfinity;
        var terrainSamplesBelowPlane = 0;
        foreach (var height in sourceCell.Heightmap.CalculateHeights())
        {
            if (!float.IsFinite(height))
            {
                continue;
            }

            terrainMinimum = MathF.Min(terrainMinimum, height);
            terrainMaximum = MathF.Max(terrainMaximum, height);
            if (height < planeHeight)
            {
                terrainSamplesBelowPlane++;
            }
        }

        if (!float.IsFinite(terrainMinimum) ||
            !float.IsFinite(terrainMaximum) ||
            terrainSamplesBelowPlane == 0)
        {
            return null;
        }

        var selection = WaterAppearanceSelectionResolver.Resolve(
            sourceCell,
            worldspace,
            _data.WatersByFormId);
        if (selection.WaterFormId != waterFormId || selection.Water is null)
        {
            return null;
        }

        // Do not replace _cellGridLookup or _spatialIndex: terrain and references must still render
        // the ordinary opaque retail scene. Passing a null water spatial index is intentional; it
        // makes WaterRenderer12 build exactly one generated packet from this dictionary.
        var syntheticCell = sourceCell with
        {
            WaterHeight = planeHeight,
            WaterFormId = waterFormId
        };
        var syntheticWaterCells = new Dictionary<(int gx, int gy), CellRecord>
        {
            [(gridX, gridY)] = syntheticCell
        };
        _water.LoadData(
            syntheticWaterCells,
            worldspaceDefaultWaterHeight: null,
            spatialIndex: null,
            appearance: appearance,
            normalMapBindlessIndices: ResolveWaterNormalIndices(appearance));
        _water.SetFnvWater001WaterTypeContext(waterFormId, worldspace.WaterFormId);

        // Profiler_CaptureSceneAsync refreshes the camera CELL material before rendering. Pinning the
        // same retained selection lets that refresh take its no-change path and preserve this one-cell
        // geometry fixture while keeping the capture's WATR/CELL telemetry fully retail-authored.
        _waterAppearanceSelection = selection;
        _boundWaterAppearanceFormId = selection.WaterFormId;
        _hasBoundWaterAppearance = true;

        return new WorldView3DProfilerSyntheticWaterFixture(
            sourceCell.FormId,
            gridX,
            gridY,
            waterFormId,
            water.EditorId,
            planeHeight,
            terrainMinimum,
            terrainMaximum,
            terrainSamplesBelowPlane);
    }
}
