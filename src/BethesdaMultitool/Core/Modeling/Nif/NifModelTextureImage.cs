using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One document image the texture source produced, with the facts its <c>bmt.nif.texture</c> native row records:
///     every request that resolved to it (authored name, lookup key, role, source block), the resolution (resolved
///     occurrence, its layer provenance, the extension fallback, the basename retry), and the DDS or DDX facts.
/// </summary>
internal sealed class NifModelTextureImage
{
    /// <summary>Creates the record of one image.</summary>
    public NifModelTextureImage(int index, SceneImage image, string container, SceneImageOrigin origin)
    {
        Index = index;
        Image = image;
        Container = container;
        Origin = origin;
    }

    /// <summary>The document image index.</summary>
    public int Index { get; }

    /// <summary>The document image.</summary>
    public SceneImage Image { get; }

    /// <summary>The original container label (<c>dds</c>, <c>ddx</c>, <c>png</c>, ...).</summary>
    public string Container { get; }

    /// <summary>How the image entered the document.</summary>
    public SceneImageOrigin Origin { get; }

    /// <summary>The resolved occurrence, or null for a missing image.</summary>
    public AssetReference? Reference { get; init; }

    /// <summary>The first block whose request produced the image (for a missing image's source location).</summary>
    public int FirstSourceBlock { get; init; } = -1;

    /// <summary>The DDX header's format byte, for a DDX original.</summary>
    public byte? DdxFormatByte { get; init; }

    /// <summary>The DDS compression Shared's inspection or BMT's header named, for a DDS original.</summary>
    public string? DdsCompression { get; init; }

    /// <summary>The native facts about resolution and content.</summary>
    public JsonObject Facts { get; } = new();

    /// <summary>Every request that resolved to this image.</summary>
    public JsonArray Requests { get; } = new();

    /// <summary>Records one more request, bounded to 64 inline entries plus a count.</summary>
    public void AddRequest(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestCount++;
        if (Requests.Count < NifModelNativeValues.MaximumInlineElements)
        {
            Requests.Add(request);
        }
    }

    /// <summary>The number of requests that resolved to this image.</summary>
    public int RequestCount { get; private set; }
}
