using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of one NiTexturingProperty map, the TexDesc struct from 20.1.0.3 (nif.xml:6498-6569): Source
///     (NiSourceTexture), the TexturingMapFlags bitfield (nif.xml:5509-5517) and the optional NiTextureTransform
///     (Translation, Scale, Rotation, Transform Method, Center).
/// </summary>
/// <remarks>
///     Flags members: Texture Index bits 0-7 (the texture-coordinate set; the separate UV Set field ends at 20.0.0.5),
///     Filter Mode bits 8-11 (TexFilterMode), Clamp Mode from bit 12. Clamp Mode's declared width 4 contradicts its mask
///     0x3000; it is decoded by width (bits 12-15), so a value above 3 is reported as undefined rather than silently
///     masked, and the raw flags stay in native state.
/// </remarks>
internal sealed class NifTextureMapView
{
    private NifTextureMapView(string slot, int source, ushort flags)
    {
        Slot = slot;
        Source = source;
        Flags = flags;
    }

    /// <summary>The map's slot name in NiTexturingProperty (Base, Dark, Detail, Gloss, Glow, Bump Map, Normal, ...).</summary>
    public string Slot { get; }

    /// <summary>The NiSourceTexture link (-1 for none).</summary>
    public int Source { get; }

    /// <summary>The stored TexturingMapFlags.</summary>
    public ushort Flags { get; }

    /// <summary>Texture Index (bits 0-7): the texture-coordinate set.</summary>
    public int UvSet => Flags & 0xFF;

    /// <summary>Filter Mode (bits 8-11).</summary>
    public int FilterMode => (Flags >> 8) & 0xF;

    /// <summary>Clamp Mode (bits 12-15, by the declared width 4).</summary>
    public int ClampMode => (Flags >> 12) & 0xF;

    /// <summary>Has Texture Transform.</summary>
    public bool HasTransform { get; private init; }

    /// <summary>The UV translation.</summary>
    public Vector2 Translation { get; private init; }

    /// <summary>The UV scale.</summary>
    public Vector2 Scale { get; private init; } = Vector2.One;

    /// <summary>The W-axis rotation (radians, Assumed).</summary>
    public float Rotation { get; private init; }

    /// <summary>The Transform Method (TransformMethod, nif.xml:6480-6496).</summary>
    public uint TransformMethod { get; private init; }

    /// <summary>The rotation center.</summary>
    public Vector2 Center { get; private init; }

    /// <summary>Reads one decoded TexDesc.</summary>
    public static NifTextureMapView Read(NifDecodedBlock block, string slot, NifStructValue map)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(map);
        var flags = (ushort)NifModelPropertyFields.Integer(block, map, "Flags").RawBits;
        var source = NifModelPropertyFields.Ref(block, map, "Source");
        var hasTransform = NifModelPropertyFields.OptionalInteger(map, "Has Texture Transform") is { RawBits: not 0 };
        if (!hasTransform)
        {
            return new NifTextureMapView(slot, source, flags);
        }

        return new NifTextureMapView(slot, source, flags)
        {
            HasTransform = true,
            Translation = NifModelPropertyFields.TexCoord(block, map, "Translation"),
            Scale = NifModelPropertyFields.TexCoord(block, map, "Scale"),
            Rotation = NifModelPropertyFields.Float(block, map, "Rotation"),
            TransformMethod = (uint)NifModelPropertyFields.Integer(block, map, "Transform Method").RawBits,
            Center = NifModelPropertyFields.TexCoord(block, map, "Center")
        };
    }

    /// <summary>The decoded members for native state.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["slot"] = Slot,
            ["source"] = Source,
            ["flags"] = Flags,
            ["uvSet"] = UvSet,
            ["filterMode"] = FilterMode,
            ["clampMode"] = ClampMode,
            ["hasTextureTransform"] = HasTransform
        };
        if (HasTransform)
        {
            json["transform"] = new JsonObject
            {
                ["translation"] = Pair(Translation),
                ["scale"] = Pair(Scale),
                ["rotation"] = NifModelNativeValues.Float(Rotation),
                ["method"] = TransformMethod,
                ["center"] = Pair(Center)
            };
        }

        return json;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Slot} map (source {Source}, UV set {UvSet})");
    }

    private static JsonArray Pair(Vector2 value)
    {
        return new JsonArray(NifModelNativeValues.Float(value.X), NifModelNativeValues.Float(value.Y));
    }
}
