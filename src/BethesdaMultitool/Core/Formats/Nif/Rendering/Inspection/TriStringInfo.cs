namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>An ASCII string found while scanning a FaceGen TRI file, with its location and whether it looks identifier-like.</summary>
internal readonly record struct TriStringInfo(
    string Value,
    int Offset,
    int Length,
    bool IsIdentifierLike);
