namespace BethesdaMultitool.Core.Formats.Esm.Analysis;

internal sealed record EsmBuildDateExtractionResult(
    DateTime BuildDateUtc,
    string Source,
    bool IsFallback);
