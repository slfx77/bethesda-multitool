namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Normalizes texture paths into the canonical BSA lookup format.
/// </summary>
internal static class NifTexturePathUtility
{
    /// <summary>
    ///     Whether an already-lowercased, backslash-normalized path names a material file rather than
    ///     a texture. Extension is the only reliable signal at this point: the caller may be a mesh
    ///     shader Name, a landscape LTEX BNAM, or a material swap, and only some of those arrive
    ///     rooted.
    /// </summary>
    /// <summary>
    ///     The Data-relative roots archives index by. Order is irrelevant — the EARLIEST occurrence in
    ///     the path wins, so a build path that happens to contain more than one still cuts at the
    ///     outermost real root.
    /// </summary>
    private static readonly string[] KnownAssetRoots =
        ["materials\\", "textures\\", "meshes\\", "geometries\\"];

    private static bool StartsWithKnownRoot(string normalized)
    {
        foreach (var root in KnownAssetRoots)
        {
            if (normalized.StartsWith(root, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMaterialExtension(string normalized)
    {
        // .bgsm/.bgem ONLY — deliberately NOT Starfield's .mat.
        //
        // A .bgsm is a FILE fetched from an archive, so a wrong root makes the lookup miss. A .mat is
        // resolved by NAME out of the compiled material database (materialsbeta.cdb), which does not
        // care what root the path carries — which is exactly why Starfield's terrain renders today
        // while FO76's does not, despite both arriving through the same branch of ResolveDiffuse
        // (added together in 589a831d, the Starfield feature commit). Re-rooting .mat would change a
        // path that currently works, for no benefit.
        return normalized.EndsWith(".bgsm", StringComparison.Ordinal)
               || normalized.EndsWith(".bgem", StringComparison.Ordinal);
    }

    internal static string Normalize(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant().Trim();

        // Peel any path prefix down to the Data-relative portion that archives index by. Two forms:
        //  • A leading "data\" step — vanilla FNV's WATR DefaultWater NNAM authors the path relative
        //    to the game directory (parent of Data\), e.g. "data\textures\water\genaratednoise01.dds".
        //  • An absolute developer build path baked into the asset — extremely common on FO4/FO76,
        //    where a NIF's BSLightingShaderProperty Name (and some material texture entries) ships as
        //    e.g. "C:\Projects\Fallout4\Build\PC\Data\Materials\Architecture\X.bgsm". Without peeling,
        //    the lookup misses and every such FO4/FO76 shape renders untextured (white).
        // Archive entries are stored relative to Data\, so strip everything up to and including the
        // "...\data\" segment (or the leading "data\"). The engine roots at Data\ the same way.
        var dataSegment = normalized.IndexOf("\\data\\", StringComparison.Ordinal);
        if (dataSegment >= 0)
        {
            normalized = normalized[(dataSegment + "\\data\\".Length)..];
        }
        else if (normalized.StartsWith("data\\", StringComparison.Ordinal))
        {
            normalized = normalized[5..];
        }

        // Fallout 76 bakes absolute build paths with NO "Data" step at all, e.g.
        // "c:\projects\76\build\pc\materials\landscape\ground\x.bgsm" — where Fallout 4's had one
        // ("...\Build\PC\Data\Materials\..."). The \data\ peel above cannot see those, so fall back
        // to cutting at the first recognised asset root. Found by the resolve-failure logging on its
        // first run: seven of eight named misses were this exact shape, silently unresolved before.
        if (!StartsWithKnownRoot(normalized))
        {
            var cut = -1;
            foreach (var root in KnownAssetRoots)
            {
                var index = normalized.IndexOf('\\' + root, StringComparison.Ordinal);
                if (index >= 0 && (cut < 0 || index < cut))
                {
                    cut = index;
                }
            }

            if (cut >= 0)
            {
                normalized = normalized[(cut + 1)..];
            }
        }

        // Fallout 4 / Fallout 76 material files (.bgsm/.bgem) and Starfield's (.mat) live under
        // materials\, not textures\ — the BSLightingShaderProperty Name points at one, and so does a
        // landscape LTEX's BNAM. Leave an already-rooted path alone; otherwise root by EXTENSION.
        //
        // Rooting a rootless material at textures\ was a real, whole-worldspace bug: FO76's LTEX BNAM
        // ships WITHOUT the materials\ prefix (TerrainTextureRecordHandler stores it verbatim), so
        // every Appalachia landscape material was looked up at "textures\landscape\...\x.bgsm" — a
        // path in no archive — and the entire worldspace rendered on the untextured fallback with
        // nothing logged. The prose above already claimed this behaviour; only the code disagreed.
        //
        // Safe by construction: a rootless material path currently resolves to NOTHING, so the only
        // paths whose behaviour changes here are ones that are already failing.
        // Uses the same root set as the peel above: a path already rooted at ANY recognised asset
        // root is left alone. Checking only textures\/materials\ here meant a peeled meshes\ path
        // came back out as "textures\meshes\..." — caught by this file's own test.
        if (!StartsWithKnownRoot(normalized))
        {
            normalized = IsMaterialExtension(normalized)
                ? "materials\\" + normalized
                : "textures\\" + normalized;
        }

        return normalized;
    }

    /// <summary>
    ///     Produces the <c>.dds</c> variant of a texture path for the loader's extension fallback.
    ///     Morrowind (and some Oblivion) NIFs reference textures by their authoring extension
    ///     (<c>.tga</c> / <c>.bmp</c>) while archives store the compiled <c>.dds</c>. Returns false
    ///     when the path has no extension or is already <c>.dds</c> (nothing to swap).
    /// </summary>
    internal static bool TrySwapToDdsExtension(string path, out string ddsPath)
    {
        ddsPath = path;
        if (path.EndsWith(".dds", StringComparison.Ordinal))
        {
            return false;
        }

        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('\\');
        if (dot <= slash || dot < 0)
        {
            return false; // no extension (the last '.' is in a directory name, or absent)
        }

        ddsPath = string.Concat(path.AsSpan(0, dot), ".dds");
        return true;
    }
}
