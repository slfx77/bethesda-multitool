using static SampleGenerator.SampleGeneratorConfig;
using static SampleGenerator.SampleGeneratorFileOperations;

namespace SampleGenerator;

/// <summary>
///     Copies a build's debug symbols and executables into
///     <c>Sample/DebugSymbols/&lt;build&gt;/</c>, leaving the originals in place.
///     <para>
///         Copied, not moved: a <c>.xex</c>/<c>.exe</c> is shipped game content and the build would
///         be unfaithful without it, and the leaked Xbox 360 prototypes ship their <c>.pdb</c>s in
///         the build tree too. This gives one place to look for every symbol in the corpus without
///         taking anything away from the builds.
///     </para>
/// </summary>
internal static class SampleGeneratorSymbolStage
{
    /// <summary>
    ///     What counts as a symbol or executable worth collecting.
    ///     <para>
    ///         ⚠ Deliberately NOT every <c>.exe</c> in the tree. A Steam install carries DOSBox,
    ///         installers, uninstallers and redistributables; sweeping those up would bury the four
    ///         executables anyone actually wants. Only the build's own executables qualify — the
    ///         Xbox 360 <c>.xex</c>s, and the <c>MainExecutable</c> the catalog names.
    ///     </para>
    /// </summary>
    private static readonly string[] SymbolExtensions = [".pdb", ".map", ".xex"];

    /// <summary>Outcome of collecting one build's symbols.</summary>
    internal readonly record struct SymbolResult(int Copied, long Bytes);

    internal static SymbolResult Apply(CatalogEntry entry, string buildDir, string buildName, bool dryRun)
    {
        if (!Directory.Exists(buildDir))
        {
            return default;
        }

        var wanted = new List<string>();

        foreach (var file in Directory.EnumerateFiles(buildDir, "*", SearchOption.AllDirectories))
        {
            if (SymbolExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                wanted.Add(file);
            }
        }

        if (entry.MainExecutable is not null)
        {
            wanted.AddRange(Directory.EnumerateFiles(buildDir, entry.MainExecutable, SearchOption.AllDirectories));
        }

        if (wanted.Count == 0)
        {
            return default;
        }

        var destinationRoot = SampleGeneratorPathSafety.ResolveDestinationPath(SampleDebugSymbols, buildName);
        var copied = 0;
        long bytes = 0;

        foreach (var file in wanted.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(buildDir, file);
            var target = SampleGeneratorPathSafety.ResolveDestinationPath(destinationRoot, relative);
            var length = new FileInfo(file).Length;

            // Same name and same size means this run already has it; symbols are large and a
            // re-copy of ~1.4 GB on every run would make the stage unusable.
            if (File.Exists(target) && new FileInfo(target).Length == length)
            {
                continue;
            }

            if (dryRun)
            {
                copied++;
                bytes += length;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.SetAttributes(target, FileAttributes.Normal);
            copied++;
            bytes += length;
        }

        return new SymbolResult(copied, bytes);
    }
}
