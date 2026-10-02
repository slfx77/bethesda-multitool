using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ImageMagick;
using SharpGLTF.Schema2;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Hashes a decoded glTF material's rendered content, independent of its name and table position.</summary>
/// <remarks>
///     Moved here from <c>NifCorpusExportAssertions</c> so the v2 oracle and the fixture wrapper share one
///     definition. It hashes the alpha policy, every channel's parameters, texture coordinate set and sampler, and
///     each bound image's independently decoded RGBA pixels. Parameters are formatted exactly (round-trip) rather
///     than quantized: both writers copy the same prepared single-precision values, so any difference is real.
/// </remarks>
internal static class NifGlbMaterialSignature
{
    /// <summary>The signature recorded for a primitive with no material.</summary>
    internal const string NoMaterial = "no-material";

    /// <summary>Computes one material's content signature.</summary>
    /// <param name="material">The decoded material.</param>
    /// <param name="pixelHashes">A cache from encoded image SHA-256 to decoded pixel identity, shared by both GLBs.</param>
    /// <param name="cancellationToken">Cancels between channels.</param>
    /// <param name="hasTextureTransform">Whether any channel carries a texture transform, which neither writer emits.</param>
    /// <returns>An uppercase hexadecimal SHA-256 of the material content.</returns>
    internal static string Compute(Material material, Dictionary<string, string> pixelHashes,
        CancellationToken cancellationToken, out bool hasTextureTransform)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(pixelHashes);
        hasTextureTransform = false;
        var signature = new StringBuilder();
        signature.Append(material.Alpha).Append('|').Append(Number(material.AlphaCutoff))
            .Append('|').Append(material.DoubleSided).Append('|').Append(material.Unlit);
        foreach (var channel in material.Channels.OrderBy(static value => value.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            signature.Append('|').Append(channel.Key);
            foreach (var parameter in channel.Parameters.OrderBy(static value => value.Name, StringComparer.Ordinal))
            {
                signature.Append('|').Append(parameter.Name).Append('=').Append(Value(parameter.Value));
            }

            signature.Append('|').Append(channel.TextureCoordinate);
            var sampler = channel.TextureSampler;
            signature.Append('|').Append(sampler?.WrapS).Append('|').Append(sampler?.WrapT)
                .Append('|').Append(sampler?.MinFilter).Append('|').Append(sampler?.MagFilter);
            if (channel.Texture is not { } texture)
            {
                signature.Append("|no-image");
                continue;
            }

            if (channel.TextureTransform is not null)
            {
                hasTextureTransform = true;
                signature.Append("|texture-transform");
            }

            signature.Append('|').Append(Pixels(texture.PrimaryImage.Content.Content.ToArray(), pixelHashes));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    /// <summary>A short prefix of a signature, for failure messages.</summary>
    /// <param name="signature">A signature from <see cref="Compute" />.</param>
    /// <returns>The first twelve characters.</returns>
    internal static string Short(string signature) => signature.Length <= 12 ? signature : signature[..12];

    /// <summary>Decodes an encoded image once per distinct content and returns its size and RGBA hash.</summary>
    private static string Pixels(byte[] encoded, Dictionary<string, string> pixelHashes)
    {
        var key = Convert.ToHexString(SHA256.HashData(encoded));
        if (pixelHashes.TryGetValue(key, out var identity))
        {
            return identity;
        }

        using var image = new MagickImage(encoded);
        using var pixelView = image.GetPixels();
        var rgba = pixelView.ToByteArray(PixelMapping.RGBA) ?? [];
        identity = string.Create(CultureInfo.InvariantCulture,
            $"{image.Width}x{image.Height}|{Convert.ToHexString(SHA256.HashData(rgba))}");
        pixelHashes.Add(key, identity);
        return identity;
    }

    /// <summary>Formats the glTF parameter value types consistently on every host culture.</summary>
    private static string Value(object value) => value switch
    {
        float scalar => Number(scalar),
        Vector2 vector => Number(vector.X) + "," + Number(vector.Y),
        Vector3 vector => Number(vector.X) + "," + Number(vector.Y) + "," + Number(vector.Z),
        Vector4 vector => Number(vector.X) + "," + Number(vector.Y) + "," + Number(vector.Z) + "," + Number(vector.W),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>Formats a single-precision value exactly, treating signed zero identically.</summary>
    private static string Number(float value) =>
        value == 0f ? "0" : value.ToString("R", CultureInfo.InvariantCulture);
}
