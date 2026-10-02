namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>What a caller asks <see cref="NifGlbExport" /> to plan.</summary>
/// <param name="Name">The document label, normally the source stem.</param>
/// <param name="Family">The source's stream family, normally from <see cref="NifExportFamilies.FromHeader" />.</param>
/// <param name="ConvertedFromBigEndian">Whether the source was converted from an Xbox 360 big-endian stream.</param>
/// <param name="Preference">The writer preference, normally the call site's row in <see cref="NifGlbExportDefaults" />.</param>
internal sealed record NifGlbExportRequest(
    string Name,
    NifExportFamily Family,
    bool ConvertedFromBigEndian,
    NifGlbWriterPreference Preference);
