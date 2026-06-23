using EgtAnalyzer.Verification;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering;

namespace EgtAnalyzer.Commands;

/// <summary>
///     Raw-fit neighbor and provenance-family diagnostics for <c>verify-egt</c>. Builds the
///     authored-NPC candidate set, ranks same-race/same-sex neighbours against a quantized raw
///     fit, and drives the provenance-family PCA dump.
/// </summary>
internal static class VerifyEgtRawFitReporter
{
    private const int RawFitNeighborCount = 8;

    internal static IReadOnlyList<RawFitNeighborCandidate> BuildRawFitNeighborCandidates(
        NpcAppearanceResolver resolver,
        string pluginName)
    {
        var candidates = new List<RawFitNeighborCandidate>();
        foreach (var (formId, npc) in resolver.GetAllNpcs())
        {
            var appearance = resolver.ResolveHeadOnly(formId, pluginName);
            var coeffs = appearance?.FaceGenTextureCoeffs;
            if (coeffs is not { Length: > 0 })
            {
                continue;
            }

            candidates.Add(new RawFitNeighborCandidate(
                formId,
                npc.EditorId,
                npc.FullName,
                npc.IsFemale,
                npc.RaceFormId,
                npc.TemplateFormId,
                npc.TemplateFlags,
                coeffs));
        }

        return candidates;
    }

    internal static void PrintRawFitNeighborSummary(
        NpcAppearanceResolver resolver,
        NpcAppearance appearance,
        NpcFaceGenTextureVerificationDetail verification,
        IReadOnlyList<RawFitNeighborCandidate> candidates)
    {
        var quantizedFit = verification.RawFitQuantizedCoefficient256;
        if (quantizedFit is not { Length: > 0 })
        {
            return;
        }

        if (!resolver.TryGetNpc(appearance.NpcFormId, out var targetNpc))
        {
            return;
        }

        var fitCoefficients = quantizedFit
            .Select(value => value / 256f)
            .ToArray();

        var family = candidates
            .Where(candidate =>
                candidate.IsFemale == targetNpc.IsFemale &&
                candidate.RaceFormId == targetNpc.RaceFormId &&
                candidate.TextureCoefficients.Length == fitCoefficients.Length)
            .Select(candidate => new RawFitNeighborMatch(
                candidate,
                CompareCoefficientVectors(fitCoefficients, candidate.TextureCoefficients)))
            .OrderBy(match => match.Metrics.MeanAbsoluteDifference)
            .ThenBy(match => match.Metrics.RootMeanSquareDifference)
            .ThenBy(match => match.Candidate.FormId)
            .ToList();

        if (family.Count == 0)
        {
            return;
        }

        var selfIndex = family.FindIndex(match => match.Candidate.FormId == appearance.NpcFormId);
        var selfLabel = selfIndex >= 0
            ? $"selfRank={selfIndex + 1}/{family.Count} selfMAE={family[selfIndex].Metrics.MeanAbsoluteDifference:F4} selfRMSE={family[selfIndex].Metrics.RootMeanSquareDifference:F4}"
            : $"selfRank=missing/{family.Count}";
        Console.WriteLine(
            $"  RAWFIT-NEIGHBORS 0x{appearance.NpcFormId:X8}: " +
            $"race=0x{targetNpc.RaceFormId.GetValueOrDefault():X8} sex={(targetNpc.IsFemale ? 'F' : 'M')} {selfLabel}");

        foreach (var (match, rank) in family
                     .Take(RawFitNeighborCount)
                     .Select((match, index) => (match, index + 1)))
        {
            var label = match.Candidate.EditorId ?? match.Candidate.FullName ?? "<unnamed>";
            var selfTag = match.Candidate.FormId == appearance.NpcFormId ? " self" : string.Empty;
            var templateTag = match.Candidate.TemplateFormId.HasValue
                ? $" tmpl=0x{match.Candidate.TemplateFormId.Value:X8} flags=0x{match.Candidate.TemplateFlags:X4}"
                : string.Empty;
            Console.WriteLine(
                $"    [{rank}] 0x{match.Candidate.FormId:X8} " +
                $"{label} mae={match.Metrics.MeanAbsoluteDifference:F4} " +
                $"rmse={match.Metrics.RootMeanSquareDifference:F4} " +
                $"max={match.Metrics.MaxAbsoluteDifference:F4}{selfTag}{templateTag}");
        }

        if (selfIndex >= RawFitNeighborCount)
        {
            var self = family[selfIndex];
            Console.WriteLine(
                $"    [self] 0x{self.Candidate.FormId:X8} " +
                $"{self.Candidate.EditorId ?? self.Candidate.FullName ?? "<unnamed>"} " +
                $"mae={self.Metrics.MeanAbsoluteDifference:F4} " +
                $"rmse={self.Metrics.RootMeanSquareDifference:F4} " +
                $"max={self.Metrics.MaxAbsoluteDifference:F4}");
        }
    }

    internal static void PrintRawFitProvenancePcaSummary(
        NpcAppearanceResolver resolver,
        NpcAppearance appearance,
        NpcFaceGenTextureVerificationDetail verification,
        IReadOnlyList<RawFitNeighborCandidate> candidates,
        MeshArchiveSet meshArchives,
        Dictionary<string, EgtParser?> egtCache)
    {
        if (!resolver.TryGetNpc(appearance.NpcFormId, out var targetNpc))
        {
            return;
        }

        var currentCoeffs = appearance.FaceGenTextureCoeffs;
        if (currentCoeffs is not { Length: > 0 })
        {
            return;
        }

        if (verification.ShippedTexture == null)
        {
            return;
        }

        var egtPath = verification.Result.EgtPath;
        if (string.IsNullOrWhiteSpace(egtPath))
        {
            return;
        }

        if (!egtCache.TryGetValue(egtPath, out var egt))
        {
            egt = NpcMeshHelpers.LoadEgtFromBsa(egtPath, meshArchives);
            egtCache[egtPath] = egt;
        }

        if (egt == null)
        {
            return;
        }

        var family = candidates
            .Where(candidate =>
                candidate.IsFemale == targetNpc.IsFemale &&
                candidate.RaceFormId == targetNpc.RaceFormId &&
                candidate.TextureCoefficients.Length >= currentCoeffs.Length)
            .Select(candidate => candidate.TextureCoefficients)
            .ToArray();
        if (family.Length < 2)
        {
            return;
        }

        RawDeltaFitDumper.DumpRawFitProvenancePcaSummary(
            appearance,
            egt,
            verification.ShippedTexture,
            family,
            verification.RawFitQuantizedCoefficient256);
    }

    private static CoefficientDistanceMetrics CompareCoefficientVectors(
        float[] left,
        float[] right)
    {
        var count = Math.Min(left.Length, right.Length);
        if (count == 0)
        {
            return new CoefficientDistanceMetrics(0, 0, 0);
        }

        double sumAbs = 0;
        double sumSq = 0;
        var maxAbs = 0f;
        for (var index = 0; index < count; index++)
        {
            var diff = left[index] - right[index];
            var abs = Math.Abs(diff);
            sumAbs += abs;
            sumSq += diff * diff;
            if (abs > maxAbs)
            {
                maxAbs = abs;
            }
        }

        return new CoefficientDistanceMetrics(
            sumAbs / count,
            Math.Sqrt(sumSq / count),
            maxAbs);
    }

    internal sealed record RawFitNeighborCandidate(
        uint FormId,
        string? EditorId,
        string? FullName,
        bool IsFemale,
        uint? RaceFormId,
        uint? TemplateFormId,
        ushort TemplateFlags,
        float[] TextureCoefficients);

    private sealed record RawFitNeighborMatch(
        RawFitNeighborCandidate Candidate,
        CoefficientDistanceMetrics Metrics);

    private sealed record CoefficientDistanceMetrics(
        double MeanAbsoluteDifference,
        double RootMeanSquareDifference,
        float MaxAbsoluteDifference);
}

