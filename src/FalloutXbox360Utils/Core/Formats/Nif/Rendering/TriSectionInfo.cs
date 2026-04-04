namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal readonly record struct TriSectionInfo(
    string Name,
    int Offset,
    int Length,
    int ElementCount,
    int ElementSize);
