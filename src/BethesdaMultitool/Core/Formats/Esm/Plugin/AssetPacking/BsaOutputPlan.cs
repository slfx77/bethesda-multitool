namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

internal sealed record BsaOutputPlan(
    string OutputPath,
    AssetPackBucket Bucket,
    string BucketLabel,
    int ChunkIndex,
    IReadOnlyList<(string Path, byte[] Data)> Files);
