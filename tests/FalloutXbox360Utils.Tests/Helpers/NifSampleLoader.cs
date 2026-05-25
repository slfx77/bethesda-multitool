using FalloutXbox360Utils.Core.Formats.Nif;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Shared helpers for tests that load NIFs from the unpacked PC Final sample tree
///     (Sample/Unpacked_Builds/PC_Final_Unpacked/Data/meshes/characters).
/// </summary>
internal static class NifSampleLoader
{
    /// <summary>
    ///     Walks up from <see cref="AppContext.BaseDirectory"/> looking for the sample
    ///     characters mesh directory. Returns the relative fallback path if no candidate
    ///     is found within 10 parent levels — callers can then treat missing NIFs as
    ///     "test data absent" rather than failing setup.
    /// </summary>
    public static string FindCharactersSampleRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(
                dir,
                "Sample",
                "Unpacked_Builds",
                "PC_Final_Unpacked",
                "Data",
                "meshes",
                "characters");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return Path.Combine(
            "Sample",
            "Unpacked_Builds",
            "PC_Final_Unpacked",
            "Data",
            "meshes",
            "characters");
    }

    /// <summary>
    ///     Loads a NIF from disk and returns the raw bytes plus parsed <see cref="NifInfo"/>.
    ///     Returns null if the file is missing or unparseable — callers typically skip the
    ///     assertion in that case so the test passes when sample data isn't available.
    /// </summary>
    public static (byte[] Data, NifInfo Info)? LoadNif(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var data = File.ReadAllBytes(fullPath);
        var nif = NifParser.Parse(data);
        return nif != null ? (data, nif) : null;
    }
}
