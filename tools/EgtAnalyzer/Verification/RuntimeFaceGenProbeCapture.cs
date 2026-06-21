namespace EgtAnalyzer.Verification;

internal sealed record RuntimeFaceGenProbeCapture
{
    public required string CaptureDirectory { get; init; }
    public required string CaptureDirectoryName { get; init; }
    public required uint BaseNpcFormId { get; init; }
    public required uint RaceFormId { get; init; }
    public required bool IsFemale { get; init; }
    public string? MatchedFormType { get; init; }
    public required RuntimeFaceGenProbeDescriptor NpcTextureDescriptor { get; init; }
    public required RuntimeFaceGenProbeDescriptor RaceTextureDescriptor { get; init; }
    public string? SelectedRacePage { get; init; }
    public RuntimeFaceGenProbeDescriptor? RaceTexturePageADescriptor { get; init; }
    public RuntimeFaceGenProbeDescriptor? RaceTexturePageBDescriptor { get; init; }
    public required float[] NpcTextureCoefficients { get; init; }
    public required float[] RaceTextureCoefficients { get; init; }
    public required float[] MergedTextureCoefficients { get; init; }
    public float[] RaceTexturePageACoefficients { get; init; } = [];
    public float[] RaceTexturePageBCoefficients { get; init; } = [];
    public float[] MergedTexturePageACoefficients { get; init; } = [];
    public float[] MergedTexturePageBCoefficients { get; init; } = [];
}
