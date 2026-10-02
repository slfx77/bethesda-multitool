using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of BSShaderTextureSet (nif.xml:14189-14205): Num Textures inline SizedStrings. At FO3-era BS versions
///     slots 0-5 are diffuse, normal (with the PC gloss in alpha), glow, parallax, environment cube and environment mask;
///     the count itself and any slot beyond 5 are native state only.
/// </summary>
internal sealed class NifTextureSetView
{
    private NifTextureSetView(IReadOnlyList<NifSizedStringValue> textures)
    {
        Textures = textures;
    }

    /// <summary>The stored texture paths, in slot order (empty strings included).</summary>
    public IReadOnlyList<NifSizedStringValue> Textures { get; }

    /// <summary>Reads the view.</summary>
    /// <exception cref="InvalidDataException">The Textures array did not decode as SizedStrings.</exception>
    public static NifTextureSetView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (!block.Root.TryGet("Textures", out var value) || value is not NifArrayValue array)
        {
            throw new InvalidDataException($"NIF block {block.Index} ({block.Type}) did not decode its Textures array.");
        }

        var textures = new List<NifSizedStringValue>(array.Count);
        foreach (var item in array.Items)
        {
            textures.Add(item as NifSizedStringValue ?? throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) decoded a texture path as {item.Kind}, not a SizedString."));
        }

        return new NifTextureSetView(textures.AsReadOnly());
    }
}
