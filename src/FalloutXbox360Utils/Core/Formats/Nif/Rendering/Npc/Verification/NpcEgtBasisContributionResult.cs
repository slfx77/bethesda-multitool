namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtBasisContributionResult
{
    public int MorphIndex { get; init; }
    public float MorphScale { get; init; }
    public int Scale256 { get; init; }
    public float NpcCoeff { get; init; }
    public int NpcCoeff256 { get; init; }
    public float RaceCoeff { get; init; }
    public int RaceCoeff256 { get; init; }
    public float MergedCoeff { get; init; }
    public int MergedCoeff256 { get; init; }
    public double NpcMeanAbsoluteRgbContribution { get; init; }
    public double RaceMeanAbsoluteRgbContribution { get; init; }
    public double MergedMeanAbsoluteRgbContribution { get; init; }
    public double MergedMeanSignedRedContribution { get; init; }
    public double MergedMeanSignedGreenContribution { get; init; }
    public double MergedMeanSignedBlueContribution { get; init; }
    public double ErrorAlignment { get; init; }
}