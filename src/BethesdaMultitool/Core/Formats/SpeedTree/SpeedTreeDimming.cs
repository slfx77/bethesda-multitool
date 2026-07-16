namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     Engine render parameters carried by TREE CNAM. The first two fields are canopy-depth dimming;
///     Rock/Rustle are per-tree phase multipliers consumed by SpeedTreeLeafShader::SetupGeometry.
///     Optional defaults preserve source compatibility for callers that only supplied dimming.
/// </summary>
public readonly record struct SpeedTreeDimming(
    float Leaf,
    float Branch,
    float RockSpeed = 1f,
    float RustleSpeed = 1f);
