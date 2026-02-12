namespace FalloutXbox360Utils.Core.Formats.Ddx;

/// <summary>
///     Shared utilities for DDXConv batch conversion workflows.
///     Used by memory carving, BSA extraction, and BSA conversion pipelines.
/// </summary>
internal static class DdxBatchHelper
{
    /// <summary>
    ///     Merge DDXConv batch output back into the extraction directory.
    ///     Moves converted files from ddxOutputDir into extractDir
    ///     (preserving relative paths) and deletes original .ddx files.
    /// </summary>
    /// <param name="extractDir">The original extraction directory containing .ddx files.</param>
    /// <param name="ddxOutputDir">The DDXConv output directory containing converted .dds files.</param>
    /// <returns>Number of files merged.</returns>
    public static int MergeConversions(string extractDir, string ddxOutputDir)
    {
        var merged = 0;

        // Move all converted files from ddxOutputDir into extractDir
        foreach (var file in Directory.EnumerateFiles(ddxOutputDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(ddxOutputDir, file);
            var targetPath = Path.Combine(extractDir, relativePath);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Move(file, targetPath, true);
            merged++;
        }

        // Delete original DDX files from extractDir
        foreach (var ddxFile in Directory.GetFiles(extractDir, "*.ddx", SearchOption.AllDirectories))
        {
            File.Delete(ddxFile);
        }

        return merged;
    }
}
