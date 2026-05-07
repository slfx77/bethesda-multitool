namespace FalloutXbox360Utils.Core.Formats.Esm;

internal sealed record EsmBuildDateExtractionResult(
    DateTime BuildDateUtc,
    string Source,
    bool IsFallback);
