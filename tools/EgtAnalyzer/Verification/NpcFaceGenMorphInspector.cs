using System.Buffers.Binary;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using static EgtAnalyzer.Verification.CrossNpcRowAnalyzer;
using static EgtAnalyzer.Verification.DeltaTextureHelpers;
using static EgtAnalyzer.Verification.ExternalEgtDonorAnalyzer;
using static EgtAnalyzer.Verification.MorphPlausibilityAnalyzer;
using static EgtAnalyzer.Verification.MorphRowSimilarityAnalyzer;
using static EgtAnalyzer.Verification.MorphStructureAnalyzer;

namespace EgtAnalyzer.Verification;

/// <summary>
/// Morph inspection and cross-NPC required-row analysis for FaceGen texture verification.
/// Owns the per-run inspection state captured while morph indices are inspected and
/// prints the cross-NPC and external-head-EGT required-row similarity summaries.
/// </summary>
internal static class NpcFaceGenMorphInspector
{
    private static readonly int[] LateHotspotFamilyIndices = [35, 36, 37, 38, 39, 40, 41, 42, 43, 45, 46, 49];
    private static readonly Dictionary<uint, Dictionary<int, CrossNpcRequiredRow>> InspectRequiredRows = [];
    private static readonly Dictionary<uint, InspectNpcState> InspectNpcStates = [];
    private static readonly Dictionary<uint, string> InspectCurrentEgtPaths = [];

    internal static void ResetInspectMorphRunState()
    {
        InspectRequiredRows.Clear();
        InspectNpcStates.Clear();
        InspectCurrentEgtPaths.Clear();
    }

    internal static void DumpMorphInspection(
        NpcAppearance appearance,
        EgtParser egt,
        string egtPath,
        MeshArchiveSet meshArchives,
        float[] currentCoefficients,
        IReadOnlyList<int> morphIndices,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        InspectCurrentEgtPaths[appearance.NpcFormId] = egtPath;
        var namedRegions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = namedRegions["eyes"];
        var mouth = namedRegions["mouth"];
        var currentRawMae = CompareFloatDeltaRgb(currentNative, shippedDecoded).MeanAbsoluteRgbError;
        var currentEyesRawMae = GetRegionRawMae(currentNative, shippedDecoded, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H);
        var currentMouthRawMae = GetRegionRawMae(currentNative, shippedDecoded, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H);
        var npcState = new InspectNpcState(
            egt.Cols, egt.Rows, currentNative, shippedDecoded,
            currentRawMae, currentEyesRawMae, currentMouthRawMae,
            new Dictionary<int, InspectMorphState>());
        InspectNpcStates[appearance.NpcFormId] = npcState;

        if (NpcFaceGenTextureVerifier.EnableInspectMorphSummaryOnly)
        {
            foreach (var morphIndex in morphIndices.Where(index => index >= 0).Distinct().OrderBy(index => index))
            {
                if (morphIndex >= egt.SymmetricMorphs.Length) continue;
                var morph = egt.SymmetricMorphs[morphIndex];
                var coeff = morphIndex < currentCoefficients.Length ? currentCoefficients[morphIndex] : 0f;
                var coeff256 = (int)(coeff * 256f);
                var scale256 = (int)(morph.Scale * 256f);
                var contributionFactor = coeff256 * scale256 / 65536f;
                npcState.Morphs[morphIndex] = new InspectMorphState(morphIndex, morph, contributionFactor);
                CrossSearchRequiredRows(egt, morphIndex, morph, coeff256, currentNative, shippedDecoded, appearance.NpcFormId);
            }
            return;
        }

        if (!meshArchives.TryExtractFile(egtPath, out var rawEgtData, out var rawArchivePath))
        {
            Console.WriteLine($"  MORPH-INSPECT 0x{appearance.NpcFormId:X8}: raw EGT extract failed for {egtPath}");
            return;
        }

        var rowStride = AlignTo(egt.Cols, 8);
        var channelSize = rowStride * egt.Rows;
        var morphSize = 4 + (3 * channelSize);

        Console.WriteLine(
            $"  MORPH-INSPECT 0x{appearance.NpcFormId:X8}: source={Path.GetFileName(rawArchivePath)} " +
            $"egt={egtPath} cols={egt.Cols} rows={egt.Rows} rowStride={rowStride}");

        foreach (var morphIndex in morphIndices.Where(index => index >= 0).Distinct().OrderBy(index => index))
        {
            if (morphIndex >= egt.SymmetricMorphs.Length)
            {
                Console.WriteLine($"    [{morphIndex:D2}] skipped: index outside symmetric range ({egt.SymmetricMorphs.Length})");
                continue;
            }

            var morphDataOffset = 64 + (morphIndex * morphSize);
            if (morphDataOffset + morphSize > rawEgtData.Length)
            {
                Console.WriteLine($"    [{morphIndex:D2}] skipped: raw offset 0x{morphDataOffset:X} exceeds file length {rawEgtData.Length}");
                continue;
            }

            var morph = egt.SymmetricMorphs[morphIndex];
            var rawScale = BinaryPrimitives.ReadSingleLittleEndian(rawEgtData.AsSpan(morphDataOffset, 4));
            var coeff = morphIndex < currentCoefficients.Length ? currentCoefficients[morphIndex] : 0f;
            var coeff256 = (int)(coeff * 256f);
            var scale256 = (int)(morph.Scale * 256f);
            var contributionFactor = coeff256 * scale256 / 65536f;
            npcState.Morphs[morphIndex] = new InspectMorphState(morphIndex, morph, contributionFactor);
            var stats = ComputeMorphContributionStats(egt, morph, contributionFactor);
            var residualAlignment = ComputeMorphResidualAlignment(egt, morph, currentNative, shippedDecoded);

            Console.WriteLine(
                $"    [{morphIndex:D2}] coeff={coeff,9:F4} coeff256={coeff256,6} " +
                $"scale={morph.Scale,9:F6} rawScale={rawScale,9:F6} scale256={scale256,6} " +
                $"factor={contributionFactor,10:F6}");
            Console.WriteLine(
                $"         wholeAbsMean=({stats.WholeMeanAbsR:F4}, {stats.WholeMeanAbsG:F4}, {stats.WholeMeanAbsB:F4}) " +
                $"wholeMax=({stats.WholeMaxAbsR:F2}, {stats.WholeMaxAbsG:F2}, {stats.WholeMaxAbsB:F2}) " +
                $"eyesAbsMean={stats.EyesMeanAbsRgb:F4} mouthAbsMean={stats.MouthMeanAbsRgb:F4}");
            Console.WriteLine(
                $"         residualProj256 whole={residualAlignment.WholeProjection256,8:F2} " +
                $"eyes={residualAlignment.EyesProjection256,8:F2} mouth={residualAlignment.MouthProjection256,8:F2} " +
                $"cos whole={residualAlignment.WholeCosine,7:F4} eyes={residualAlignment.EyesCosine,7:F4} mouth={residualAlignment.MouthCosine,7:F4}");

            var contentPlausibility = ComputeMorphContentPlausibility(egt, morph, coeff256, currentNative, shippedDecoded);
            var gainPlausibility = ComputeMorphGainPlausibility(egt, morph, coeff256, currentNative, shippedDecoded);
            var affinePlausibility = ComputeMorphAffinePlausibility(egt, morph, coeff256, currentNative, shippedDecoded);
            var rowSimilarity = ComputeMorphRowSimilarityStats(egt, morph, coeff256, currentNative, shippedDecoded);
            var nearestOtherRow = ComputeMorphNearestOtherRowStats(egt, morphIndex, morph, coeff256, currentNative, shippedDecoded);
            var nearestOtherRowRgb = ComputeMorphNearestOtherRowPerChannelStats(egt, morphIndex, morph, coeff256, currentNative, shippedDecoded);

            if (contentPlausibility != null)
            {
                Console.WriteLine(
                    $"         rowBacksolve factor={contentPlausibility.Factor,10:F6} inRange={contentPlausibility.InRangePercent,6:F1}% " +
                    $"mean|Δrow|={contentPlausibility.MeanAbsRequiredByteDelta,7:F2} max|Δrow|={contentPlausibility.MaxAbsRequiredByteDelta,7:F2} " +
                    $"meanClip={contentPlausibility.MeanAbsClipByte,7:F2} maxClip={contentPlausibility.MaxAbsClipByte,7:F2}");
                Console.WriteLine(
                    $"         rowClampRawMAE={contentPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError:F4} " +
                    $"(Δ={contentPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError - currentRawMae:+0.0000;-0.0000}) " +
                    $"eyesRawMAE={contentPlausibility.CorrectedEyesRawMae:F4} (Δ={contentPlausibility.CorrectedEyesRawMae - currentEyesRawMae:+0.0000;-0.0000}) " +
                    $"mouthRawMAE={contentPlausibility.CorrectedMouthRawMae:F4} (Δ={contentPlausibility.CorrectedMouthRawMae - currentMouthRawMae:+0.0000;-0.0000})");
            }
            if (gainPlausibility != null)
            {
                Console.WriteLine(
                    $"         gainFit gain={gainPlausibility.Gain,11:F6} inRange={gainPlausibility.InRangePercent,6:F1}% " +
                    $"mean|Δrow|={gainPlausibility.MeanAbsByteDelta,7:F2} max|Δrow|={gainPlausibility.MaxAbsByteDelta,7:F2} " +
                    $"meanClip={gainPlausibility.MeanAbsClipByte,7:F2} maxClip={gainPlausibility.MaxAbsClipByte,7:F2}");
                Console.WriteLine(
                    $"         gainRawMAE={gainPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError:F4} " +
                    $"(Δ={gainPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError - currentRawMae:+0.0000;-0.0000}) " +
                    $"eyesRawMAE={gainPlausibility.CorrectedEyesRawMae:F4} (Δ={gainPlausibility.CorrectedEyesRawMae - currentEyesRawMae:+0.0000;-0.0000}) " +
                    $"mouthRawMAE={gainPlausibility.CorrectedMouthRawMae:F4} (Δ={gainPlausibility.CorrectedMouthRawMae - currentMouthRawMae:+0.0000;-0.0000})");
            }
            if (affinePlausibility != null)
            {
                Console.WriteLine(
                    $"         affineFit a={affinePlausibility.Scale,11:F6} b={affinePlausibility.Bias,8:F3} " +
                    $"inRange={affinePlausibility.InRangePercent,6:F1}% mean|Δrow|={affinePlausibility.MeanAbsByteDelta,7:F2} " +
                    $"max|Δrow|={affinePlausibility.MaxAbsByteDelta,7:F2} meanClip={affinePlausibility.MeanAbsClipByte,7:F2} maxClip={affinePlausibility.MaxAbsClipByte,7:F2}");
                Console.WriteLine(
                    $"         affineRawMAE={affinePlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError:F4} " +
                    $"(Δ={affinePlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError - currentRawMae:+0.0000;-0.0000}) " +
                    $"eyesRawMAE={affinePlausibility.CorrectedEyesRawMae:F4} (Δ={affinePlausibility.CorrectedEyesRawMae - currentEyesRawMae:+0.0000;-0.0000}) " +
                    $"mouthRawMAE={affinePlausibility.CorrectedMouthRawMae:F4} (Δ={affinePlausibility.CorrectedMouthRawMae - currentMouthRawMae:+0.0000;-0.0000})");
            }
            if (rowSimilarity != null)
            {
                Console.WriteLine(
                    $"         rowSpace cos={rowSimilarity.Cosine,7:F4} corr={rowSimilarity.Correlation,7:F4} " +
                    $"targetMAE={rowSimilarity.TargetMae,7:F2} gainFitMAE={rowSimilarity.GainFitMae,7:F2} " +
                    $"affineFitMAE={rowSimilarity.AffineFitMae,7:F2} gainExpl={rowSimilarity.GainExplainedPercent,6:F1}% " +
                    $"affExpl={rowSimilarity.AffineExplainedPercent,6:F1}%");
            }
            if (rowSimilarity != null && nearestOtherRow != null)
            {
                var affineVsSelfPercent = rowSimilarity.AffineFitMae <= 1e-9
                    ? 0d : Math.Max(0d, 100d * (1d - (nearestOtherRow.Stats.AffineFitMae / rowSimilarity.AffineFitMae)));
                Console.WriteLine(
                    $"         rowNearest other=[{nearestOtherRow.MorphIndex:D2}] cos={nearestOtherRow.Stats.Cosine,7:F4} " +
                    $"corr={nearestOtherRow.Stats.Correlation,7:F4} affineFitMAE={nearestOtherRow.Stats.AffineFitMae,7:F2} " +
                    $"affExpl={nearestOtherRow.Stats.AffineExplainedPercent,6:F1}% vsSelf={affineVsSelfPercent,6:F1}% " +
                    $"a={nearestOtherRow.Stats.AffineScale,8:F3} b={nearestOtherRow.Stats.AffineBias,8:F3}");
            }
            if (rowSimilarity != null && nearestOtherRowRgb != null)
            {
                var mixVsSelfPercent = rowSimilarity.AffineFitMae <= 1e-9
                    ? 0d : Math.Max(0d, 100d * (1d - (nearestOtherRowRgb.MixedStats.AffineFitMae / rowSimilarity.AffineFitMae)));
                var mixVsWholePercent = nearestOtherRow == null || nearestOtherRow.Stats.AffineFitMae <= 1e-9
                    ? 0d : Math.Max(0d, 100d * (1d - (nearestOtherRowRgb.MixedStats.AffineFitMae / nearestOtherRow.Stats.AffineFitMae)));
                var split = new HashSet<int>
                {
                    nearestOtherRowRgb.Red.MorphIndex,
                    nearestOtherRowRgb.Green.MorphIndex,
                    nearestOtherRowRgb.Blue.MorphIndex
                }.Count;
                Console.WriteLine(
                    $"         rowNearestRGB " +
                    $"R=[{nearestOtherRowRgb.Red.MorphIndex:D2}] mae={nearestOtherRowRgb.Red.Stats.AffineFitMae,6:F2} vsSelf={nearestOtherRowRgb.Red.VsSelfPercent,5:F1}% | " +
                    $"G=[{nearestOtherRowRgb.Green.MorphIndex:D2}] mae={nearestOtherRowRgb.Green.Stats.AffineFitMae,6:F2} vsSelf={nearestOtherRowRgb.Green.VsSelfPercent,5:F1}% | " +
                    $"B=[{nearestOtherRowRgb.Blue.MorphIndex:D2}] mae={nearestOtherRowRgb.Blue.Stats.AffineFitMae,6:F2} vsSelf={nearestOtherRowRgb.Blue.VsSelfPercent,5:F1}% | " +
                    $"mixAffineMAE={nearestOtherRowRgb.MixedStats.AffineFitMae,6:F2} mixVsSelf={mixVsSelfPercent,5:F1}% " +
                    $"vsWhole={mixVsWholePercent,5:F1}% split={split}");
            }

            var rOffset = morphDataOffset + 4;
            var gOffset = rOffset + channelSize;
            var bOffset = gOffset + channelSize;
            DumpMorphChannelInspection("R", rawEgtData, rOffset, rowStride, egt.Cols, egt.Rows, morph.DeltaR);
            DumpMorphChannelInspection("G", rawEgtData, gOffset, rowStride, egt.Cols, egt.Rows, morph.DeltaG);
            DumpMorphChannelInspection("B", rawEgtData, bOffset, rowStride, egt.Cols, egt.Rows, morph.DeltaB);

            CrossSearchRequiredRows(egt, morphIndex, morph, coeff256, currentNative, shippedDecoded, appearance.NpcFormId);
        }
    }

    private static void CrossSearchRequiredRows(
        EgtParser egt,
        int sourceMorphIndex,
        EgtMorph sourceMorph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded,
        uint npcFormId)
    {
        var scale256 = (int)(sourceMorph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var requiredR = new sbyte[pixelCount];
        var requiredG = new sbyte[pixelCount];
        var requiredB = new sbyte[pixelCount];

        for (var i = 0; i < pixelCount; i++)
        {
            requiredR[i] = (sbyte)Math.Clamp(
                (int)MathF.Round(sourceMorph.DeltaR[i] + ((shippedDecoded.R[i] - currentNative.R[i]) / factor)),
                -128, 127);
            requiredG[i] = (sbyte)Math.Clamp(
                (int)MathF.Round(sourceMorph.DeltaG[i] + ((shippedDecoded.G[i] - currentNative.G[i]) / factor)),
                -128, 127);
            requiredB[i] = (sbyte)Math.Clamp(
                (int)MathF.Round(sourceMorph.DeltaB[i] + ((shippedDecoded.B[i] - currentNative.B[i]) / factor)),
                -128, 127);
        }

        if (!InspectRequiredRows.TryGetValue(npcFormId, out var npcRows))
        {
            npcRows = new Dictionary<int, CrossNpcRequiredRow>();
            InspectRequiredRows[npcFormId] = npcRows;
        }

        npcRows[sourceMorphIndex] = new CrossNpcRequiredRow(sourceMorphIndex, requiredR, requiredG, requiredB);

        if (NpcFaceGenTextureVerifier.EnableInspectMorphSummaryOnly)
        {
            return;
        }

        var channelNames = new[] { "R", "G", "B" };
        var requiredChannels = new[] { requiredR, requiredG, requiredB };

        for (var reqCh = 0; reqCh < 3; reqCh++)
        {
            var required = requiredChannels[reqCh];
            var bestMorphIndex = -1;
            var bestChannelIndex = -1;
            var bestMae = double.MaxValue;
            var bestCosine = 0d;
            var bestFlipped = false;

            for (var candidateMorphIdx = 0; candidateMorphIdx < egt.SymmetricMorphs.Length; candidateMorphIdx++)
            {
                var candidate = egt.SymmetricMorphs[candidateMorphIdx];
                var candidateChannels = new[] { candidate.DeltaR, candidate.DeltaG, candidate.DeltaB };

                for (var candCh = 0; candCh < 3; candCh++)
                {
                    var candData = candidateChannels[candCh];
                    CompareRowCandidate(required, candData, pixelCount, egt.Cols, egt.Rows, false,
                        out var mae, out var cosine);
                    if (mae < bestMae)
                    {
                        bestMae = mae; bestCosine = cosine;
                        bestMorphIndex = candidateMorphIdx; bestChannelIndex = candCh; bestFlipped = false;
                    }

                    CompareRowCandidate(required, candData, pixelCount, egt.Cols, egt.Rows, true,
                        out mae, out cosine);
                    if (mae < bestMae)
                    {
                        bestMae = mae; bestCosine = cosine;
                        bestMorphIndex = candidateMorphIdx; bestChannelIndex = candCh; bestFlipped = true;
                    }
                }
            }

            var currentChannel = reqCh switch
            {
                0 => sourceMorph.DeltaR,
                1 => sourceMorph.DeltaG,
                _ => sourceMorph.DeltaB,
            };
            var currentMae = ComputeSbyteMae(required, currentChannel, pixelCount);

            Console.WriteLine(
                $"         crossSearch {channelNames[reqCh]}: " +
                $"best=[{bestMorphIndex:D2}].{channelNames[bestChannelIndex]} " +
                $"mae={bestMae:F2} cos={bestCosine:F4} flip={bestFlipped} " +
                $"currentMae={currentMae:F2} " +
                $"isSelf={bestMorphIndex == sourceMorphIndex && bestChannelIndex == reqCh && !bestFlipped}");
        }
    }

    internal static void PrintCrossNpcRequiredRowSimilaritySummary()
    {
        if (InspectRequiredRows.Count < 2)
        {
            return;
        }

        var orderedNpcIds = InspectRequiredRows.Keys.OrderBy(id => id).ToArray();
        for (var leftIndex = 0; leftIndex < orderedNpcIds.Length; leftIndex++)
        {
            var leftNpcId = orderedNpcIds[leftIndex];
            var leftRows = InspectRequiredRows[leftNpcId];

            for (var rightIndex = leftIndex + 1; rightIndex < orderedNpcIds.Length; rightIndex++)
            {
                var rightNpcId = orderedNpcIds[rightIndex];
                var rightRows = InspectRequiredRows[rightNpcId];

                Console.WriteLine($"  TARGETROW-XNPC 0x{leftNpcId:X8} -> 0x{rightNpcId:X8}:");

                foreach (var sourceRow in leftRows.Values.OrderBy(row => row.MorphIndex))
                {
                    CrossNpcRequiredRowSimilarity? sameStats = null;
                    if (rightRows.TryGetValue(sourceRow.MorphIndex, out var sameTarget))
                    {
                        sameStats = ComputeCrossNpcRequiredRowSimilarity(sourceRow, sameTarget);
                    }

                    CrossNpcRequiredRow? bestTarget = null;
                    CrossNpcRequiredRowSimilarity? bestStats = null;
                    foreach (var candidate in rightRows.Values)
                    {
                        var candidateStats = ComputeCrossNpcRequiredRowSimilarity(sourceRow, candidate);
                        if (bestStats == null || candidateStats.AffineFitMae < bestStats.AffineFitMae)
                        {
                            bestTarget = candidate;
                            bestStats = candidateStats;
                        }
                    }

                    if (bestTarget == null || bestStats == null) continue;

                    var vsSame = sameStats == null || sameStats.AffineFitMae <= 1e-9
                        ? 0d : Math.Max(0d, 100d * (1d - (bestStats.AffineFitMae / sameStats.AffineFitMae)));
                    var sameText = sameStats == null
                        ? "same=[--] unavailable"
                        : $"same=[{sourceRow.MorphIndex:D2}] cos={sameStats.Cosine,7:F4} corr={sameStats.Correlation,7:F4} " +
                          $"mae={sameStats.MeanAbsoluteDifference,7:F2} affineMAE={sameStats.AffineFitMae,7:F2}";

                    Console.WriteLine(
                        $"    [{sourceRow.MorphIndex:D2}] {sameText} | " +
                        $"nearest=[{bestTarget.MorphIndex:D2}] cos={bestStats.Cosine,7:F4} corr={bestStats.Correlation,7:F4} " +
                        $"mae={bestStats.MeanAbsoluteDifference,7:F2} affineMAE={bestStats.AffineFitMae,7:F2} vsSame={vsSame,6:F1}%");
                }
            }
        }
    }

    internal static void PrintExternalHeadEgtRequiredRowSummary(
        MeshArchiveSet meshArchives,
        Dictionary<string, EgtParser?> egtCache)
    {
        if (InspectRequiredRows.Count == 0) return;

        var excludedPaths = InspectCurrentEgtPaths.Values
            .Select(NormalizeArchiveVirtualPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatePaths = EnumerateExternalHeadEgtPaths(meshArchives)
            .Where(path => !excludedPaths.Contains(path))
            .ToArray();
        if (candidatePaths.Length == 0)
        {
            Console.WriteLine("  TARGETROW-EGTEXT: no external head EGT candidates");
            return;
        }

        var candidates = new List<ExternalHeadEgtCandidate>(candidatePaths.Length);
        foreach (var candidatePath in candidatePaths)
        {
            if (!egtCache.TryGetValue(candidatePath, out var candidateEgt))
            {
                candidateEgt = NpcMeshHelpers.LoadEgtFromBsa(candidatePath, meshArchives);
                egtCache[candidatePath] = candidateEgt;
            }
            if (candidateEgt == null || candidateEgt.SymmetricMorphs.Length == 0) continue;
            candidates.Add(new ExternalHeadEgtCandidate(candidatePath, candidateEgt));
        }

        if (candidates.Count == 0)
        {
            Console.WriteLine("  TARGETROW-EGTEXT: external head EGT candidates failed to load");
            return;
        }

        Console.WriteLine($"  TARGETROW-EGTEXT candidates={candidates.Count} excluded={excludedPaths.Count}:");

        var sharedBlend2Fit = default(ExternalDonorBlendFit);
        var sharedBlend2BiasFit = default(ExternalDonorBlendFit);
        var sharedBlendMorphIndex = 37;
        var sharedBlendRows = InspectRequiredRows.Values
            .SelectMany(rows => rows.Values)
            .Where(row => row.MorphIndex == sharedBlendMorphIndex)
            .ToArray();
        var sharedBlendDonor37 = FindExternalHeadEgtCandidateByFileName(candidates, "headchildfemale.egt");
        var sharedBlendDonor41 = FindExternalHeadEgtCandidateByFileName(candidates, "headchild.egt");
        if (sharedBlendRows.Length >= 2 &&
            sharedBlendDonor37 != null && sharedBlendDonor41 != null &&
            sharedBlendMorphIndex >= 0 && sharedBlendMorphIndex < sharedBlendDonor37.Egt.SymmetricMorphs.Length &&
            41 < sharedBlendDonor41.Egt.SymmetricMorphs.Length)
        {
            sharedBlend2Fit = FitExternalDonorBlendRows(
                sharedBlendRows, sharedBlendDonor37.Egt.SymmetricMorphs[sharedBlendMorphIndex],
                sharedBlendDonor41.Egt.SymmetricMorphs[41], includeBias: false);
            sharedBlend2BiasFit = FitExternalDonorBlendRows(
                sharedBlendRows, sharedBlendDonor37.Egt.SymmetricMorphs[sharedBlendMorphIndex],
                sharedBlendDonor41.Egt.SymmetricMorphs[41], includeBias: true);
        }

        foreach (var npcFormId in InspectRequiredRows.Keys.OrderBy(id => id))
        {
            if (!InspectNpcStates.TryGetValue(npcFormId, out var npcState)) continue;
            var currentPath = InspectCurrentEgtPaths.TryGetValue(npcFormId, out var currentEgtPath) ? currentEgtPath : "<unknown>";
            Console.WriteLine($"    0x{npcFormId:X8} current={currentPath}");

            foreach (var sourceRow in InspectRequiredRows[npcFormId].Values.OrderBy(row => row.MorphIndex))
            {
                npcState.Morphs.TryGetValue(sourceRow.MorphIndex, out var sourceMorphState);
                var best37 = FindBestExternalHeadEgtRowMatch(sourceRow, candidates, 37);
                var bestLate = FindBestExternalHeadEgtRowMatch(sourceRow, candidates, LateHotspotFamilyIndices);
                var best37Apply = sourceMorphState == null || best37 == null ? null : ComputeExternalDonorApplyStats(npcState, sourceMorphState, best37.Morph);
                var bestLateApply = sourceMorphState == null || bestLate == null ? null : ComputeExternalDonorApplyStats(npcState, sourceMorphState, bestLate.Morph);
                var blend2 = sourceMorphState == null || best37 == null || bestLate == null ? null
                    : ComputeExternalDonorBlendStats(npcState, sourceMorphState, sourceRow, best37, bestLate, includeBias: false);
                var blend2Bias = sourceMorphState == null || best37 == null || bestLate == null ? null
                    : ComputeExternalDonorBlendStats(npcState, sourceMorphState, sourceRow, best37, bestLate, includeBias: true);
                var sharedBlend2 = sourceMorphState == null || sourceRow.MorphIndex != sharedBlendMorphIndex || sharedBlend2Fit == null ? null
                    : ComputeExternalDonorBlendApplyStats(npcState, sourceMorphState, sharedBlend2Fit);
                var sharedBlend2Bias = sourceMorphState == null || sourceRow.MorphIndex != sharedBlendMorphIndex || sharedBlend2BiasFit == null ? null
                    : ComputeExternalDonorBlendApplyStats(npcState, sourceMorphState, sharedBlend2BiasFit);

                var best37Text = best37 == null ? "best37=none"
                    : $"best37={best37.Path}[{best37.MorphIndex:D2}] cos={best37.Stats.Cosine,7:F4} corr={best37.Stats.Correlation,7:F4} affineMAE={best37.Stats.AffineFitMae,7:F2}";
                var bestLateText = bestLate == null ? "bestLate=none"
                    : $"bestLate={bestLate.Path}[{bestLate.MorphIndex:D2}] cos={bestLate.Stats.Cosine,7:F4} corr={bestLate.Stats.Correlation,7:F4} affineMAE={bestLate.Stats.AffineFitMae,7:F2}";
                var vs37 = best37 == null || best37.Stats.AffineFitMae <= 1e-9 || bestLate == null
                    ? 0d : Math.Max(0d, 100d * (1d - (bestLate.Stats.AffineFitMae / best37.Stats.AffineFitMae)));

                Console.WriteLine($"      [{sourceRow.MorphIndex:D2}] {best37Text} | {bestLateText} vs37={vs37,6:F1}%");
                if (best37Apply != null)
                    Console.WriteLine($"           apply37 rawMAE={best37Apply.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={best37Apply.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={best37Apply.EyesRawMae:F4} (Δ={best37Apply.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={best37Apply.MouthRawMae:F4} (Δ={best37Apply.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                if (bestLateApply != null)
                    Console.WriteLine($"           applyLate rawMAE={bestLateApply.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={bestLateApply.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={bestLateApply.EyesRawMae:F4} (Δ={bestLateApply.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={bestLateApply.MouthRawMae:F4} (Δ={bestLateApply.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                if (blend2 != null)
                {
                    Console.WriteLine($"           blend2 a={blend2.CoefficientA,7:F4} b={blend2.CoefficientB,7:F4} rowMAE={blend2.RowMae,7:F2}");
                    Console.WriteLine($"           applyBlend2 rawMAE={blend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={blend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={blend2.ApplyStats.EyesRawMae:F4} (Δ={blend2.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={blend2.ApplyStats.MouthRawMae:F4} (Δ={blend2.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }
                if (blend2Bias != null)
                {
                    Console.WriteLine($"           blend2b a={blend2Bias.CoefficientA,7:F4} b={blend2Bias.CoefficientB,7:F4} bias={blend2Bias.Bias,7:F4} rowMAE={blend2Bias.RowMae,7:F2}");
                    Console.WriteLine($"           applyBlend2b rawMAE={blend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={blend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={blend2Bias.ApplyStats.EyesRawMae:F4} (Δ={blend2Bias.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={blend2Bias.ApplyStats.MouthRawMae:F4} (Δ={blend2Bias.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }
                if (sharedBlend2 != null)
                {
                    Console.WriteLine($"           sharedBlend2 a={sharedBlend2.CoefficientA,7:F4} b={sharedBlend2.CoefficientB,7:F4} rowMAE={sharedBlend2.RowMae,7:F2}");
                    Console.WriteLine($"           applySharedBlend2 rawMAE={sharedBlend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={sharedBlend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={sharedBlend2.ApplyStats.EyesRawMae:F4} (Δ={sharedBlend2.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={sharedBlend2.ApplyStats.MouthRawMae:F4} (Δ={sharedBlend2.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }
                if (sharedBlend2Bias != null)
                {
                    Console.WriteLine($"           sharedBlend2b a={sharedBlend2Bias.CoefficientA,7:F4} b={sharedBlend2Bias.CoefficientB,7:F4} bias={sharedBlend2Bias.Bias,7:F4} rowMAE={sharedBlend2Bias.RowMae,7:F2}");
                    Console.WriteLine($"           applySharedBlend2b rawMAE={sharedBlend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={sharedBlend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={sharedBlend2Bias.ApplyStats.EyesRawMae:F4} (Δ={sharedBlend2Bias.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={sharedBlend2Bias.ApplyStats.MouthRawMae:F4} (Δ={sharedBlend2Bias.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }
            }
        }
    }
}

