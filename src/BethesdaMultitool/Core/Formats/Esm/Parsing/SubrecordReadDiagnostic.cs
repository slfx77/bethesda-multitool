namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

public sealed record SubrecordReadDiagnostic(string? Signature, int PayloadOffset, uint DeclaredLength,
    int AvailableLength, bool IsBigEndian, string Reason);
