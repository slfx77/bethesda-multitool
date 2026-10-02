using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Classifies a parsed NIF header into its <see cref="NifExportFamily" />.</summary>
/// <remarks>
///     The table follows the Bethesda rows of the repo's <c>nif.xml</c> version list: 4.0.0.2 Morrowind; the
///     TES4-era Gamebryo range (<see cref="NifVersions.IsTes4Era" />) Oblivion; and 20.2.0.7 split by BS stream
///     version (FO3/FNV 14-34, Skyrim 83, Skyrim SE 100, FO4 130-139 including the 132/139 development streams,
///     FO76 155, Starfield 170 and later per <c>#BS_GTE_STF#</c>). Anything else is
///     <see cref="NifExportFamily.Unknown" />. <c>nif.xml</c> lists 20.0.0.4 user 11 as shared by Oblivion and
///     Fallout 3; such a header classifies as Oblivion because the family is the stream generation, not the
///     shipping game. Big-endian conversion does not change either version field, so the original and the
///     converted header classify the same; the conversion itself is reported separately by
///     <see cref="NifExportSceneAssembly" />.
/// </remarks>
internal static class NifExportFamilies
{
    /// <summary>Reads the family from the binary and BS stream versions.</summary>
    /// <param name="nif">A parsed header.</param>
    /// <returns>The stream family, or <see cref="NifExportFamily.Unknown" />.</returns>
    /// <exception cref="ArgumentNullException">The header is null.</exception>
    public static NifExportFamily FromHeader(NifInfo nif)
    {
        ArgumentNullException.ThrowIfNull(nif);

        if (nif.BinaryVersion == NifVersions.NetImmerse4002)
        {
            return NifExportFamily.Morrowind;
        }

        if (NifVersions.IsTes4Era(nif.BinaryVersion))
        {
            return NifExportFamily.Oblivion;
        }

        if (nif.BinaryVersion != NifVersions.Gamebryo202007)
        {
            return NifExportFamily.Unknown;
        }

        return nif.BsVersion switch
        {
            >= 14 and <= 34 => NifExportFamily.Fo3Fnv,
            83 => NifExportFamily.Skyrim,
            100 => NifExportFamily.SkyrimSe,
            >= 130 and <= 139 => NifExportFamily.Fo4,
            155 => NifExportFamily.Fo76,
            >= 170 => NifExportFamily.Starfield,
            _ => NifExportFamily.Unknown
        };
    }
}
