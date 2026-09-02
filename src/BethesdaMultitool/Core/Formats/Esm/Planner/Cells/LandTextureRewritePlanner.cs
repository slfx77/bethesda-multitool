using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Phase-F terrain reference rewrite. Runs after top-level allocation so LAND layers
///     and LTEX grass links see the final plugin FormIDs and never serialize a known
///     prototype-only dangling target.
/// </summary>
public static class LandTextureRewritePlanner
{
    public static EmitPlan Apply(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(masterByFormId);

        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();
        var emittedTypes = BuildEmittedTypeIndex(plan, masterByFormId);
        var cells = RewriteCells(plan, emittedTypes, diagnostics);
        var records = RewriteLandscapeTextures(plan, emittedTypes, diagnostics);

        return plan with
        {
            CellsByFormId = cells,
            Records = records,
            Diagnostics = plan.Diagnostics.AddRange(diagnostics)
        };
    }

    private static ImmutableDictionary<uint, CellPlan> RewriteCells(
        EmitPlan plan,
        IReadOnlyDictionary<uint, string> emittedTypes,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        var cells = plan.CellsByFormId.ToBuilder();
        foreach (var (cellId, cell) in plan.CellsByFormId)
        {
            var changed = false;
            var temporary = cell.TemporaryChildren.ToBuilder();
            for (var i = 0; i < temporary.Count; i++)
            {
                var child = temporary[i];
                if (child.Type != "LAND" || child.Model is not CellLandDecision land
                                         || land.VisualData is null)
                {
                    continue;
                }

                var visual = RewriteLandVisual(
                    land.VisualData, land.CellSourceFormId, plan.SourceToEmittedFormId,
                    emittedTypes, diagnostics);
                if (!ReferenceEquals(visual, land.VisualData))
                {
                    temporary[i] = child with { Model = land with { VisualData = visual } };
                    changed = true;
                }
            }

            if (changed)
            {
                cells[cellId] = cell with { TemporaryChildren = temporary.ToImmutable() };
            }
        }

        return cells.ToImmutable();
    }

    private static ImmutableArray<RecordPlan> RewriteLandscapeTextures(
        EmitPlan plan,
        IReadOnlyDictionary<uint, string> emittedTypes,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        var records = plan.Records.ToBuilder();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Type != "LTEX" || record.Model is not LandscapeTextureRecord ltex
                                      || record.Disposition == RecordDisposition.Skip)
            {
                continue;
            }

            List<uint>? rewrittenGrass = null;
            for (var grassIndex = 0; grassIndex < ltex.GrassFormIds.Count; grassIndex++)
            {
                var sourceGrass = ltex.GrassFormIds[grassIndex];
                if (sourceGrass == 0)
                {
                    rewrittenGrass ??= new List<uint>(ltex.GrassFormIds.Take(grassIndex));
                    continue;
                }

                if (TryResolveTyped(
                        sourceGrass, "GRAS", plan.SourceToEmittedFormId,
                        emittedTypes, out var emittedGrass))
                {
                    if (emittedGrass != sourceGrass)
                    {
                        rewrittenGrass ??= new List<uint>(ltex.GrassFormIds.Take(grassIndex));
                    }

                    rewrittenGrass?.Add(emittedGrass);
                    continue;
                }

                rewrittenGrass ??= new List<uint>(ltex.GrassFormIds.Take(grassIndex));
                diagnostics.Add(DanglingDiagnostic(
                    "land.ltex-grass-dropped", "LTEX", record.FormId, sourceGrass, "GRAS"));
            }

            if (rewrittenGrass is not null)
            {
                records[i] = record with { Model = ltex with { GrassFormIds = rewrittenGrass } };
            }
        }

        return records.ToImmutable();
    }

    private static LandVisualData RewriteLandVisual(
        LandVisualData visual,
        uint cellSourceFormId,
        ImmutableDictionary<uint, uint> remap,
        IReadOnlyDictionary<uint, string> emittedTypes,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        var changed = false;
        var layers = new List<LandTextureLayer>(visual.TextureLayers.Count);
        foreach (var layer in visual.TextureLayers)
        {
            // FormID 0 is not a dangling reference: retail authors ATXT/BTXT layers whose texture
            // is the engine-default land texture (FalloutNV.esm LAND 0x000DB102 quadrant 0 layer 1
            // paints one, VTXT and all). It needs no remap — 0 stays 0 — and dropping it deletes an
            // authored blend layer. This was the 794-vs-793 residual: master LANDs re-encoded for
            // runtime-heightmap cells lost their null-texture layers, while verbatim carry-forward
            // kept them.
            if (layer.TextureFormId == 0)
            {
                layers.Add(layer);
                continue;
            }

            if (TryResolveTyped(layer.TextureFormId, "LTEX", remap, emittedTypes, out var emittedLtex))
            {
                layers.Add(emittedLtex == layer.TextureFormId
                    ? layer
                    : layer with { TextureFormId = emittedLtex });
                changed |= emittedLtex != layer.TextureFormId;
                continue;
            }

            changed = true;
            diagnostics.Add(DanglingDiagnostic(
                "land.texture-layer-dropped", "CELL", cellSourceFormId,
                layer.TextureFormId, "LTEX"));
        }

        var indices = visual.TextureIndices;
        if (indices is { Length: > 0 })
        {
            var rewritten = new uint[indices.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                var source = indices[i];
                rewritten[i] = source;
                if (remap.ContainsKey(source)
                    && TryResolveTyped(source, "LTEX", remap, emittedTypes, out var emittedLtex))
                {
                    rewritten[i] = emittedLtex;
                    changed |= emittedLtex != source;
                }
            }

            if (changed || !rewritten.SequenceEqual(indices))
            {
                indices = rewritten;
            }
        }

        return changed
            ? visual with { TextureLayers = layers, TextureIndices = indices }
            : visual;
    }

    private static Dictionary<uint, string> BuildEmittedTypeIndex(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId)
    {
        var result = masterByFormId.ToDictionary(pair => pair.Key, pair => pair.Value.Header.Signature);
        foreach (var record in plan.Records)
        {
            if (record.Disposition != RecordDisposition.Skip)
            {
                result[record.FormId] = record.Type;
            }
        }

        return result;
    }

    private static bool TryResolveTyped(
        uint sourceFormId,
        string expectedType,
        IReadOnlyDictionary<uint, uint> remap,
        IReadOnlyDictionary<uint, string> emittedTypes,
        out uint emittedFormId)
    {
        emittedFormId = remap.GetValueOrDefault(sourceFormId, sourceFormId);
        return emittedFormId != 0
               && emittedTypes.TryGetValue(emittedFormId, out var type)
               && string.Equals(type, expectedType, StringComparison.Ordinal);
    }

    private static PlanDiagnostic DanglingDiagnostic(
        string code,
        string recordType,
        uint formId,
        uint target,
        string targetType)
    {
        return new PlanDiagnostic
        {
            Kind = PlanDiagnosticKind.Warning,
            Phase = "LandTextureRewrite",
            Code = code,
            RecordType = recordType,
            FormId = formId,
            Message =
                $"Dropped dangling prototype {targetType} reference 0x{target:X8} from {recordType} 0x{formId:X8}."
        };
    }
}
