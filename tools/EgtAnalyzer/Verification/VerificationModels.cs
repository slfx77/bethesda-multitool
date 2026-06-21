using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;

namespace EgtAnalyzer.Verification;

internal sealed record MorphAblationRow(
    int MorphIndex,
    float Coefficient,
    float Scale,
    double Mae,
    double DeltaMae);

internal sealed record ResidualProjectionRow(
    int MorphIndex,
    int Current256,
    int WholeDelta256,
    int EyesDelta256,
    int MouthDelta256)
{
    public int MaxAbsDelta256 =>
        Math.Max(
            Math.Abs(WholeDelta256),
            Math.Max(Math.Abs(EyesDelta256), Math.Abs(MouthDelta256)));

    public string DominantRegion
    {
        get
        {
            var wholeAbs = Math.Abs(WholeDelta256);
            var eyesAbs = Math.Abs(EyesDelta256);
            var mouthAbs = Math.Abs(MouthDelta256);
            if (wholeAbs >= eyesAbs && wholeAbs >= mouthAbs)
            {
                return "whole";
            }

            return eyesAbs >= mouthAbs ? "eyes" : "mouth";
        }
    }
}

internal sealed record NpcFaceGenTextureVerificationResult
{
    public required uint FormId { get; init; }
    public required string PluginName { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public required string ShippedTexturePath { get; init; }
    public string? ShippedSourcePath { get; init; }
    public string? ShippedSourceFormat { get; init; }
    public string? BaseTexturePath { get; init; }
    public string? EgtPath { get; init; }
    public string? ComparisonMode { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double MeanAbsoluteRgbError { get; init; }
    public double RootMeanSquareRgbError { get; init; }
    public int MaxAbsoluteRgbError { get; init; }
    public int PixelsWithAnyRgbDifference { get; init; }
    public int PixelsWithRgbErrorAbove1 { get; init; }
    public int PixelsWithRgbErrorAbove2 { get; init; }
    public int PixelsWithRgbErrorAbove4 { get; init; }
    public int PixelsWithRgbErrorAbove8 { get; init; }
    public double SsimLuminance { get; init; }
    public double SsimRgbMean { get; init; }
    public double SsimNormalizedLuminance { get; init; }
    public double SsimNormalizedRgbMean { get; init; }
    public double SsimMaxSatRgbMean { get; init; }
    public double AffineFitMeanAbsoluteRgbError { get; init; }
    public double AffineFitRootMeanSquareRgbError { get; init; }
    public int AffineFitMaxAbsoluteRgbError { get; init; }
    public double AffineFitScaleRed { get; init; }
    public double AffineFitScaleGreen { get; init; }
    public double AffineFitScaleBlue { get; init; }
    public double AffineFitBiasRed { get; init; }
    public double AffineFitBiasGreen { get; init; }
    public double AffineFitBiasBlue { get; init; }
    public string? FailureReason { get; init; }

    public bool Verified => FailureReason == null;
    public bool ExactMatch => Verified && PixelsWithAnyRgbDifference == 0;
}

internal sealed record NpcFaceGenTextureVerificationDetail(
    NpcFaceGenTextureVerificationResult Result,
    DecodedTexture? GeneratedTexture,
    DecodedTexture? ShippedTexture,
    IReadOnlyList<DiagnosticVariantMetric>? DiagnosticVariants = null,
    DecodedTexture? AffineFitTexture = null,
    int[]? RawFitQuantizedCoefficient256 = null);

internal sealed record DiagnosticVariantMetric(
    string Mode,
    double MeanAbsoluteRgbError,
    double RootMeanSquareRgbError,
    int MaxAbsoluteRgbError);

internal sealed record FloatDeltaRgbComparisonMetrics(
    double MeanAbsoluteRgbError,
    double RootMeanSquareRgbError,
    float MaxAbsoluteRgbError,
    double MeanSignedRedError,
    double MeanSignedGreenError,
    double MeanSignedBlueError);

internal sealed record MorphContributionStats(
    float WholeMeanAbsR,
    float WholeMeanAbsG,
    float WholeMeanAbsB,
    float WholeMaxAbsR,
    float WholeMaxAbsG,
    float WholeMaxAbsB,
    float EyesMeanAbsRgb,
    float MouthMeanAbsRgb);

internal sealed record ResidualProjectionStats(
    double Projection256,
    double Cosine);

internal sealed record MorphResidualAlignmentStats(
    double WholeProjection256,
    double EyesProjection256,
    double MouthProjection256,
    double WholeCosine,
    double EyesCosine,
    double MouthCosine);

internal sealed record MorphStructureRow(
    int Index,
    int Current256,
    int Scale256,
    float WholeAbsMeanRgb,
    float EyesAbsMeanRgb,
    float MouthAbsMeanRgb,
    float NoseAbsMeanRgb,
    float ForeheadAbsMeanRgb,
    double WholeProjection256,
    double EyesProjection256,
    double MouthProjection256,
    double WholeCosine,
    double EyesCosine,
    double MouthCosine)
{
    public double FaceLocalizedRatio =>
        WholeAbsMeanRgb <= 0f ? 0d : (EyesAbsMeanRgb + MouthAbsMeanRgb) / WholeAbsMeanRgb;
}

internal sealed record RawDeltaCoefficientFitResult(
    int[] QuantizedCoefficient256,
    FloatDeltaRgbComparisonMetrics FittedRawMetrics,
    FloatDeltaRgbComparisonMetrics FloatOracleRawMetrics,
    RawDeltaPixelBuffers FloatOracleBuffers);

internal sealed record RawDeltaLinearFitSolution(
    float[][] Basis,
    double[] SolvedCoefficient256);

internal sealed record RawDeltaPixelBuffers(
    float[] R,
    float[] G,
    float[] B);

internal sealed record RawDeltaResidualSubspaceRow(
    int Index,
    int Current256,
    int Fit256,
    int Delta256,
    float CurrentCoeff,
    float FitCoeff);

internal sealed record RawDeltaResidualSubspaceFitResult(
    float[] AbsoluteCoefficients,
    FloatDeltaRgbComparisonMetrics FittedRawMetrics,
    IReadOnlyList<RawDeltaResidualSubspaceRow> Rows);

internal sealed record HotspotDeltaFitResult(
    int[] DeltaCoefficient256,
    FloatDeltaRgbComparisonMetrics FittedResidualMetrics);

internal sealed record MorphContentPlausibilityStats(
    float Factor,
    double InRangePercent,
    double MeanAbsRequiredByteDelta,
    float MaxAbsRequiredByteDelta,
    double MeanAbsClipByte,
    float MaxAbsClipByte,
    FloatDeltaRgbComparisonMetrics CorrectedRawMetrics,
    double CorrectedEyesRawMae,
    double CorrectedMouthRawMae);

internal sealed record MorphGainPlausibilityStats(
    double Gain,
    double InRangePercent,
    double MeanAbsByteDelta,
    float MaxAbsByteDelta,
    double MeanAbsClipByte,
    float MaxAbsClipByte,
    FloatDeltaRgbComparisonMetrics CorrectedRawMetrics,
    double CorrectedEyesRawMae,
    double CorrectedMouthRawMae);

internal sealed record MorphAffinePlausibilityStats(
    double Scale,
    double Bias,
    double InRangePercent,
    double MeanAbsByteDelta,
    float MaxAbsByteDelta,
    double MeanAbsClipByte,
    float MaxAbsClipByte,
    FloatDeltaRgbComparisonMetrics CorrectedRawMetrics,
    double CorrectedEyesRawMae,
    double CorrectedMouthRawMae);

internal sealed record MorphRowSimilarityStats(
    double Cosine,
    double Correlation,
    double TargetMae,
    double GainFitMae,
    double AffineFitMae,
    double GainExplainedPercent,
    double AffineExplainedPercent,
    double Gain,
    double AffineScale,
    double AffineBias);

internal sealed record MorphNearestOtherRowStats(
    int MorphIndex,
    MorphRowSimilarityStats Stats);

internal sealed record MorphChannelSimilarityStats(
    double Cosine,
    double Correlation,
    double TargetMae,
    double AffineFitMae,
    double AffineExplainedPercent,
    double AffineScale,
    double AffineBias);

internal sealed record MorphNearestOtherChannelCandidate(
    int MorphIndex,
    MorphChannelSimilarityStats Stats,
    double VsSelfPercent);

internal sealed record MorphNearestOtherRowPerChannelStats(
    MorphNearestOtherChannelCandidate Red,
    MorphNearestOtherChannelCandidate Green,
    MorphNearestOtherChannelCandidate Blue,
    MorphRowSimilarityStats MixedStats);

internal sealed record CrossNpcRequiredRow(
    int MorphIndex,
    sbyte[] RequiredR,
    sbyte[] RequiredG,
    sbyte[] RequiredB,
    string? SourcePath = null);

internal sealed record CrossNpcRequiredRowSimilarity(
    double Cosine,
    double Correlation,
    double MeanAbsoluteDifference,
    double AffineFitMae,
    double AffineScale,
    double AffineBias);

internal sealed record ExternalHeadEgtCandidate(
    string Path,
    EgtParser Egt);

internal sealed record ExternalHeadEgtRowMatch(
    string Path,
    string FullPath,
    int MorphIndex,
    CrossNpcRequiredRowSimilarity Stats,
    EgtMorph Morph);

internal sealed record InspectNpcState(
    int Cols,
    int Rows,
    (float[] R, float[] G, float[] B) CurrentNative,
    (float[] R, float[] G, float[] B) ShippedDecoded,
    double CurrentRawMae,
    double CurrentEyesRawMae,
    double CurrentMouthRawMae,
    Dictionary<int, InspectMorphState> Morphs);

internal sealed record InspectMorphState(
    int MorphIndex,
    EgtMorph SourceMorph,
    float Factor);

internal sealed record ExternalDonorApplyStats(
    FloatDeltaRgbComparisonMetrics RawMetrics,
    double EyesRawMae,
    double MouthRawMae);

internal sealed record ExternalDonorBlendFit(
    double CoefficientA,
    double CoefficientB,
    double Bias,
    double RowMae,
    sbyte[] DeltaR,
    sbyte[] DeltaG,
    sbyte[] DeltaB);

internal sealed record ExternalDonorBlendStats(
    double CoefficientA,
    double CoefficientB,
    double Bias,
    double RowMae,
    ExternalDonorApplyStats ApplyStats);

internal sealed record PrincipalComponentSet(
    double[] Eigenvalues,
    double[][] Eigenvectors);

internal sealed record AxisProjectionRange(
    double Min,
    double Max);

internal sealed record RawDeltaChannelFreeFitResult(
    int[] QuantizedCoefficient256R,
    int[] QuantizedCoefficient256G,
    int[] QuantizedCoefficient256B,
    float[] FittedR,
    float[] FittedG,
    float[] FittedB,
    FloatDeltaRgbComparisonMetrics FittedRawMetrics);

internal sealed record ShippedNpcFaceTexture(
    uint FormId,
    string PluginName,
    string VirtualPath,
    string? ArchivePath);
