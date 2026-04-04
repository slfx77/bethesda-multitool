namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal readonly record struct TriStringInfo(
    string Value,
    int Offset,
    int Length,
    bool IsIdentifierLike);
