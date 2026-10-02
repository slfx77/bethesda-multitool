using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of NiSourceTexture at 20.2.0.7 (nif.xml:12218-12265): Use External, the File Name (a FilePath, which
///     from 20.1.0.3 is a header-string index), the Pixel Data link and the Format Prefs (nif.xml:12200-12216), all kept in
///     native state. An internal texture (Use External 0) names embedded NiPixelData, which is later-cut(2).
/// </summary>
internal sealed class NifSourceTextureView
{
    private NifSourceTextureView(byte useExternal, NifStringValue? fileName, int pixelData)
    {
        UseExternal = useExternal;
        FileName = fileName;
        PixelData = pixelData;
    }

    /// <summary>The stored Use External byte.</summary>
    public byte UseExternal { get; }

    /// <summary>True when the texture is an external file.</summary>
    public bool IsExternal => UseExternal != 0;

    /// <summary>The File Name string, when decoded.</summary>
    public NifStringValue? FileName { get; }

    /// <summary>The Pixel Data link (-1 for none).</summary>
    public int PixelData { get; }

    /// <summary>The authored file name as Latin-1 text, or empty for none or unresolved.</summary>
    public string FileNameText => FileName?.Text ?? "";

    /// <summary>Reads the view.</summary>
    public static NifSourceTextureView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var root = block.Root;
        var useExternal = (byte)NifModelPropertyFields.Integer(block, root, "Use External").RawBits;
        NifStringValue? fileName = root.TryGet("File Name", out var value) ? value as NifStringValue : null;
        var pixelData = root.TryGet("Pixel Data", out var link) && link is NifRefValue reference ? reference.Index : -1;
        return new NifSourceTextureView(useExternal, fileName, pixelData);
    }

    /// <summary>The decoded members for native state.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["useExternal"] = UseExternal,
            ["fileName"] = FileName is { IsResolved: true } name ? NifModelNativeValues.Text(name.RawBytes.Span) : null,
            ["fileNameIndex"] = FileName?.Index,
            ["pixelData"] = PixelData
        };
    }
}
