namespace FalloutXbox360Utils.CLI.Rendering.Nif;

internal sealed class NifExportSettings
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public string[]? DataRoots { get; init; }
    public string[]? TextureSourcePaths { get; init; }
}
