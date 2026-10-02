namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>The result of exporting one selected virtual asset.</summary>
/// <param name="VirtualPath">The source-relative input identity.</param>
/// <param name="OutputPath">The destination path when planned, otherwise null.</param>
/// <param name="Success">Whether the output was published.</param>
/// <param name="Error">The failure reason, or null on success.</param>
public sealed record AssetExportResult(string VirtualPath, string? OutputPath, bool Success, string? Error);
