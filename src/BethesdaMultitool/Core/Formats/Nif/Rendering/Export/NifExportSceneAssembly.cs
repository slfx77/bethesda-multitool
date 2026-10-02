using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Parses a NIF for export and assembles the <see cref="GlbScene" /> the CLI <c>export nif</c> route hands to a
///     GLB writer.
/// </summary>
/// <remarks>
///     These bodies were moved out of the CLI pipeline unchanged so the same assembly can be reached from Core (the
///     writer router and its corpus gate) without depending on the CLI layer. The pipeline delegates here and keeps
///     its console reporting, output directory creation and writer call.
/// </remarks>
internal static class NifExportSceneAssembly
{
    /// <summary>Parses raw NIF bytes, converting an Xbox 360 big-endian stream to the PC layout first.</summary>
    /// <param name="raw">The file bytes as read from disk or an archive.</param>
    /// <param name="data">The bytes to assemble from: the converted stream for a big-endian source, else <paramref name="raw" />.</param>
    /// <param name="nif">The parsed header of <paramref name="data" /> when this returns true.</param>
    /// <param name="error">The console message for the failed step when this returns false.</param>
    /// <returns>True when a parsed, little-endian NIF is available.</returns>
    internal static bool TryParseForExport(
        byte[] raw,
        out byte[] data,
        [NotNullWhen(true)] out NifInfo? nif,
        [NotNullWhen(false)] out string? error)
    {
        return TryParseForExport(raw, out data, out nif, out _, out error);
    }

    /// <summary>Parses raw NIF bytes and also reports whether the Xbox 360 big-endian conversion ran.</summary>
    /// <param name="raw">The file bytes as read from disk or an archive.</param>
    /// <param name="data">The bytes to assemble from: the converted stream for a big-endian source, else <paramref name="raw" />.</param>
    /// <param name="nif">The parsed header of <paramref name="data" /> when this returns true.</param>
    /// <param name="convertedFromBigEndian">
    ///     True when the source was a big-endian stream converted to the PC layout. The converted header reports
    ///     little-endian, so this is the only record of that fact after parsing.
    /// </param>
    /// <param name="error">The console message for the failed step when this returns false.</param>
    /// <returns>True when a parsed, little-endian NIF is available.</returns>
    internal static bool TryParseForExport(
        byte[] raw,
        out byte[] data,
        [NotNullWhen(true)] out NifInfo? nif,
        out bool convertedFromBigEndian,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(raw);

        data = raw;
        convertedFromBigEndian = false;
        error = null;

        nif = NifParser.Parse(data);
        if (nif == null)
        {
            error = "Failed to parse NIF file";
            return false;
        }

        if (nif.IsBigEndian)
        {
            var converted = NifConverter.Convert(data);
            if (!converted.Success || converted.OutputData == null)
            {
                nif = null;
                error = "Failed to convert Xbox NIF to PC format";
                return false;
            }

            data = converted.OutputData;
            convertedFromBigEndian = true;
            nif = NifParser.Parse(data);
            if (nif == null)
            {
                error = "Failed to parse converted NIF file";
                return false;
            }
        }

        return true;
    }

    /// <summary>Assembles the export scene exactly as the CLI <c>export nif</c> route always has.</summary>
    /// <param name="data">Little-endian NIF bytes from a successful <c>TryParseForExport</c>.</param>
    /// <param name="nif">The parsed header of <paramref name="data" />.</param>
    /// <param name="sourceLabel">The source label recorded on the scene, normally the input path.</param>
    /// <param name="textureResolver">Resolves external BGSM/BGEM material state for modern self-contained shapes.</param>
    /// <returns>The assembled scene, or null when no route produced one.</returns>
    internal static GlbScene? BuildForExport(
        byte[] data,
        NifInfo nif,
        string sourceLabel,
        NifTextureResolver textureResolver)
    {
        if (!nif.Blocks.Any(block => NifSceneGraphWalker.SelfContainedShapeTypes.Contains(block.TypeName)))
        {
            return NifExportSceneBuilder.Build(data, nif, sourceLabel);
        }

        // The renderer extractor owns external BGSM/BGEM resolution for modern self-contained
        // shapes. Keep its fully resolved material state even when FO76 skinning requires the
        // hierarchy exporter to own joints, weights, and inverse-bind matrices.
        var modernModel = NifGeometryExtractor.Extract(data, nif, textureResolver);
        var hasFo76SkinCandidate = Enumerable.Range(0, nif.Blocks.Count)
            .Any(shapeIndex => Fo76BsSkinBindingExtractor.IsCandidate(data, nif, shapeIndex));
        if (hasFo76SkinCandidate)
        {
            var hierarchyScene = NifExportSceneBuilder.Build(data, nif, sourceLabel);
            if (hierarchyScene is not null)
            {
                if (modernModel is not null)
                {
                    NifExportSceneBuilder.ApplyModernMaterialState(hierarchyScene, modernModel);
                }

                return hierarchyScene;
            }
        }

        // Rigid FO4/FO76 shapes (and unsupported hierarchy cases) retain the modern extractor's
        // transformed geometry and resolved material state instead of falling back to an empty GLB.
        return modernModel is null
            ? null
            : NifExportSceneBuilder.BuildRenderableModel(modernModel, sourceLabel);
    }
}
