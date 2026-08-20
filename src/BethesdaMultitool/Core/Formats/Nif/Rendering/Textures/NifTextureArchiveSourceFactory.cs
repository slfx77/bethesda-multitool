using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Builds indexed texture archive sources for one or more texture archives. Each path may be a
///     loose directory, a BSA (Morrowind→Skyrim/FNV), or a BA2 (Fallout 4 / Fallout 76) — dispatched
///     by magic, not extension — so the resolver loads textures uniformly across formats.
///     <para>
///         By default archives open through <see cref="ArchiveHandleRegistry.Shared" />, so the CPU
///         resolver, both GPU resolvers, and the 2D map palette share one parse + memory map per
///         archive instead of each opening their own (the same texture BSAs used to be opened up to
///         4× per GUI session). Sources dispose their lease; the shared reader closes when its last
///         holder releases. Pass <c>registry: null</c> for private, exclusively owned opens.
///     </para>
/// </summary>
internal static class NifTextureArchiveSourceFactory
{
    internal static List<INifTextureSource> Create(params string[] textureSourcePaths)
    {
        return Create(ArchiveHandleRegistry.Shared, textureSourcePaths);
    }

    internal static List<INifTextureSource> Create(
        ArchiveHandleRegistry? registry, params string[] textureSourcePaths)
    {
        var sources = new List<INifTextureSource>(textureSourcePaths.Length);
        try
        {
            foreach (var sourcePath in textureSourcePaths)
            {
                if (Directory.Exists(sourcePath))
                {
                    sources.Add(new NifTextureDirectorySource(sourcePath));
                    continue;
                }

                sources.Add(registry is null
                    ? CreatePrivateSource(sourcePath)
                    : CreateSharedSource(registry, sourcePath));
            }
        }
        catch
        {
            // A failed open mid-list must release the sources already built: registry leases are
            // rooted in the process-wide registry and would otherwise pin their archives forever.
            foreach (var source in sources)
            {
                source.Dispose();
            }

            throw;
        }

        return sources;
    }

    private static INifTextureSource CreateSharedSource(ArchiveHandleRegistry registry, string sourcePath)
    {
        var lease = registry.Acquire(sourcePath);
        try
        {
            // ArchiveReader dispatched BSA/BA2 by magic at open; the source disposes the LEASE —
            // the reader itself is shared state owned by the registry.
            return lease.Reader.AsBsaExtractor is { } bsaExtractor
                ? new NifTextureArchiveSource(bsaExtractor, BuildBsaIndex(bsaExtractor), lease)
                : new Ba2TextureArchiveSource(lease.Reader.AsBa2Extractor!, BuildBa2Index(lease.Reader.AsBa2Extractor!),
                    lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static INifTextureSource CreatePrivateSource(string sourcePath)
    {
        return Ba2Parser.IsBa2File(sourcePath) ? CreateBa2Source(sourcePath) : CreateBsaSource(sourcePath);
    }

    private static NifTextureArchiveSource CreateBsaSource(string sourcePath)
    {
        // The BsaExtractor ctor parses the archive once; reuse its Archive for the path index
        // (this used to run a second, redundant BsaParser.Parse).
        var extractor = new BsaExtractor(sourcePath);
        return new NifTextureArchiveSource(extractor, BuildBsaIndex(extractor), extractor);
    }

    private static Ba2TextureArchiveSource CreateBa2Source(string sourcePath)
    {
        // The Ba2Extractor ctor parses the archive once; reuse its Archive for the path index.
        var extractor = new Ba2Extractor(sourcePath);
        return new Ba2TextureArchiveSource(extractor, BuildBa2Index(extractor), extractor);
    }

    private static Dictionary<string, BsaFileRecord> BuildBsaIndex(BsaExtractor extractor)
    {
        var fileIndex = new Dictionary<string, BsaFileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in extractor.Archive.AllFiles)
        {
            var path = file.FullPath;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            fileIndex[path.Replace('/', '\\')] = file;
        }

        return fileIndex;
    }

    private static Dictionary<string, Ba2FileRecord> BuildBa2Index(Ba2Extractor extractor)
    {
        var fileIndex = new Dictionary<string, Ba2FileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in extractor.Archive.AllFiles)
        {
            var path = file.FullPath;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            fileIndex[path.Replace('/', '\\')] = file;
        }

        return fileIndex;
    }
}
