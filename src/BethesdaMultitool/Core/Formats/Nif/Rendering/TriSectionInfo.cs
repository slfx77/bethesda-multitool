namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>A named, fixed-width region within a FaceGen TRI file: its byte offset/length and element layout.</summary>
internal readonly record struct TriSectionInfo(
    string Name,
    int Offset,
    int Length,
    int ElementCount,
    int ElementSize);
