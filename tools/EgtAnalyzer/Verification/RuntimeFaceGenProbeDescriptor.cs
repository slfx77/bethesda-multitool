namespace EgtAnalyzer.Verification;

internal sealed record RuntimeFaceGenProbeDescriptor(
    string? DescriptorAddress,
    string? ValuesPointer,
    uint Count,
    uint Stride,
    bool Valid);
