namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>The stream family an export source belongs to, as used by normalized-writer admission.</summary>
/// <remarks>
///     A family is a NIF stream generation read from the header (<see cref="NifExportFamilies.FromHeader" />), not a
///     claim about which game shipped the file. <see cref="SpeedTree" /> is never read from a NIF header; SpeedTree
///     export requests name it directly.
/// </remarks>
internal enum NifExportFamily
{
    /// <summary>A header outside the Bethesda version table.</summary>
    Unknown = 0,

    /// <summary>NetImmerse 4.0.0.2.</summary>
    Morrowind = 1,

    /// <summary>TES4-era Gamebryo, above 4.2.2.0 through 20.0.0.5.</summary>
    Oblivion = 2,

    /// <summary>20.2.0.7 with BS stream 14 through 34.</summary>
    Fo3Fnv = 3,

    /// <summary>20.2.0.7 with BS stream 83.</summary>
    Skyrim = 4,

    /// <summary>20.2.0.7 with BS stream 100.</summary>
    SkyrimSe = 5,

    /// <summary>20.2.0.7 with BS stream 130 through 139.</summary>
    Fo4 = 6,

    /// <summary>20.2.0.7 with BS stream 155.</summary>
    Fo76 = 7,

    /// <summary>20.2.0.7 with BS stream 170 or later.</summary>
    Starfield = 8,

    /// <summary>A SpeedTree (.spt) source; never produced from a NIF header.</summary>
    SpeedTree = 9
}
