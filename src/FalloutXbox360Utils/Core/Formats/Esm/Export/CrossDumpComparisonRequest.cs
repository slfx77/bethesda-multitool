namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal sealed record CrossDumpComparisonRequest
{
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
    public string OutputPath { get; init; } = "";
    public string? BaseDirectoryPath { get; init; }
    public string? TypeFilter { get; init; }
    public string OutputFormat { get; init; } = "html";
    public bool Verbose { get; init; }
}
