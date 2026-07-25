namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Resolves real-asset BSAs from the repo's Sample tree for Bucket B probes.
///     Checks <c>BETHESDA_TEST_DATA_ROOT</c> first (file directly under the root),
///     then walks up from <see cref="AppContext.BaseDirectory" /> to find the repo
///     checkout. Returns the repo-relative fallback path when nothing is found so
///     callers' <c>File.Exists</c> skip gates fire normally.
/// </summary>
internal static class SampleBsaLocator
{
    /// <summary>
    ///     The FNV PC Final meshes BSA
    ///     (Sample/Full_Builds/Fallout New Vegas (PC Final)/Data/Fallout - Meshes.bsa).
    /// </summary>
    public static string ResolveFnvMeshesBsa()
    {
        return Resolve(
            "Fallout - Meshes.bsa",
            Path.Combine("Sample", "Full_Builds", "Fallout New Vegas (PC Final)", "Data", "Fallout - Meshes.bsa"));
    }

    private static string Resolve(string fileName, string repoRelativePath)
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, fileName)))
        {
            return Path.Combine(root, fileName);
        }

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, repoRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return repoRelativePath;
    }
}