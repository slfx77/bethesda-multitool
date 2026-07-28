namespace BethesdaMultitool.Core.Formats.Esm.Analysis.FileAnalysis;

internal sealed record EsmBuildDateExtractionResult(
    DateTime BuildDateUtc,
    string Source,
    bool IsFallback);
