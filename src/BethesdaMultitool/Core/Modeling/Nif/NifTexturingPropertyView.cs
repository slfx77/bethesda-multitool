using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of NiTexturingProperty from 20.1.0.2 (nif.xml:12466-12553): the TexturingFlags bitfield
///     (nif.xml:5499-5507: Multitexture bit 0, Apply Mode bits 1-3, Decal Count bits 4-11), Texture Count, and each map
///     whose Has flag is set, in stored order. The Bump Map (with its luma scale, offset and matrix) and the Shader Textures
///     have no typed carrier and are only counted here; the block's decoded fields keep them in native state.
/// </summary>
internal sealed class NifTexturingPropertyView
{
    /// <summary>The TexDesc fields in stored order, with the slot names the reader types.</summary>
    private static readonly (string Field, string Slot)[] MapFields =
    [
        ("Base Texture", "Base"),
        ("Dark Texture", "Dark"),
        ("Detail Texture", "Detail"),
        ("Gloss Texture", "Gloss"),
        ("Glow Texture", "Glow"),
        ("Bump Map Texture", "Bump Map"),
        ("Normal Texture", "Normal"),
        ("Parallax Texture", "Parallax"),
        ("Decal 0 Texture", "Decal 0"),
        ("Decal 1 Texture", "Decal 1"),
        ("Decal 2 Texture", "Decal 2"),
        ("Decal 3 Texture", "Decal 3")
    ];

    private NifTexturingPropertyView(ushort flags, uint textureCount, IReadOnlyList<NifTextureMapView> maps,
        int shaderTextureCount)
    {
        Flags = flags;
        TextureCount = textureCount;
        Maps = maps;
        ShaderTextureCount = shaderTextureCount;
    }

    /// <summary>The stored TexturingFlags.</summary>
    public ushort Flags { get; }

    /// <summary>Apply Mode (bits 1-3): ApplyMode (nif.xml:604-623), 0..4 defined.</summary>
    public int ApplyMode => (Flags >> 1) & 0x7;

    /// <summary>The stored Texture Count.</summary>
    public uint TextureCount { get; }

    /// <summary>The present maps (Has flag set), in stored order; the Bump Map is included with slot "Bump Map".</summary>
    public IReadOnlyList<NifTextureMapView> Maps { get; }

    /// <summary>The stored Num Shader Textures.</summary>
    public int ShaderTextureCount { get; }

    /// <summary>The highest texture-coordinate set any typed map (every map but the bump map) uses; 0 when none.</summary>
    public int MaximumUvSet => Maps.Where(m => m.Slot != "Bump Map" && m.Source >= 0).Select(m => m.UvSet)
        .DefaultIfEmpty(0).Max();

    /// <summary>Reads the view.</summary>
    public static NifTexturingPropertyView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var root = block.Root;
        var flags = (ushort)NifModelPropertyFields.Integer(block, root, "Flags").RawBits;
        var count = (uint)NifModelPropertyFields.Integer(block, root, "Texture Count").RawBits;
        var maps = new List<NifTextureMapView>();
        foreach (var (field, slot) in MapFields)
        {
            if (root.TryGet(field, out var value) && value is NifStructValue map)
            {
                maps.Add(NifTextureMapView.Read(block, slot, map));
            }
        }

        var shaderTextures = NifModelPropertyFields.OptionalInteger(root, "Num Shader Textures") is { } number
            ? (int)number.RawBits
            : 0;
        return new NifTexturingPropertyView(flags, count, maps.AsReadOnly(), shaderTextures);
    }

    /// <summary>The decoded members for native state.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["flags"] = Flags,
            ["multitexture"] = (Flags & 1) != 0,
            ["applyMode"] = ApplyMode,
            ["decalCount"] = (Flags >> 4) & 0xFF,
            ["textureCount"] = TextureCount,
            ["maps"] = new JsonArray(Maps.Select(m => (JsonNode?)m.ToJson()).ToArray()),
            ["shaderTextures"] = ShaderTextureCount
        };
    }
}
