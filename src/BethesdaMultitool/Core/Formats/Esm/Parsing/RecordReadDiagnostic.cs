namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>A rejected record payload read.</summary>
public sealed record RecordReadDiagnostic(
    string Signature,
    uint FormId,
    long RecordOffset,
    long? DataOffset,
    uint DeclaredLength,
    long AvailableLength,
    bool IsBigEndian,
    string Stage,
    string Reason,
    string? SubrecordSignature = null,
    int? PayloadOffset = null,
    string Representation = "stored");
