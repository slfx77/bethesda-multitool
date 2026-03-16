namespace FalloutXbox360Utils.CLI;

internal sealed class NifRenderSettings
{
    public required string Path { get; init; }
    public required string OutputDir { get; init; }
    public required RenderParams Render { get; init; }
    public string? BsaPath { get; init; }
    public int Parallelism { get; init; }
    public string[] TexturesBsaPaths { get; init; } = [];
    public string? EsmPath { get; init; }
    public CameraConfig Camera { get; init; } = new();
    public int? FixedSize { get; init; }
    public bool ForceGpu { get; init; }
    public bool ForceCpu { get; init; }
}