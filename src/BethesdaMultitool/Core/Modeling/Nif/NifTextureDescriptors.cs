using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Images;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Builds <see cref="SceneTextureDescriptor" />s without decoding pixels (plan section 4, "Descriptors"): from Shared's
///     DDS inspection, from BMT's minimal DDS header (cube maps and formats inspection refuses), and from a DDX header.
/// </summary>
/// <remarks>
///     Mip declarations: level 0 of an inspected DDS is Authored (inspection validated its extent); a lower level of a
///     block-compressed DDS is Authored when the file holds its bytes and Missing when it does not; lower levels of packed
///     formats and every level of a header-only descriptor are Unknown (their storage pitch is not established here). A
///     DDX declares mip-max + 1 levels; they are Authored when the gate passed (the relayout produced each one from stored
///     data) and Unknown otherwise. Color space is declared only where the format itself says sRGB.
/// </remarks>
internal static class NifTextureDescriptors
{
    /// <summary>The descriptor of an inspected DDS.</summary>
    public static SceneTextureDescriptor FromDdsInfo(DdsImageInfo info, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(info);
        var levels = (int)Math.Clamp(info.DeclaredMipCount, 1, SceneTextureDescriptor.MaximumMipLevels);
        // Every block compression Shared's inspection admits (its layout says BC1 through BC5, BC7, or none).
        // BC6H never reaches this method: inspection refuses it, so a BC6H file takes FromDdsHeader instead.
        int? blockBytes = info.Compression switch
        {
            "BC1" or "BC4" => 8,
            "BC2" or "BC3" or "BC5" or "BC7" => 16,
            _ => null
        };
        var mips = new List<SceneTextureMip>(levels);
        long position = info.EncodedOffset;
        for (var level = 0; level < levels; level++)
        {
            var width = Math.Max(1, info.Width >> level);
            var height = Math.Max(1, info.Height >> level);
            SceneTextureMipPresence presence;
            if (level == 0)
            {
                presence = SceneTextureMipPresence.Authored;
                position += info.EncodedLength;
            }
            else if (blockBytes is { } bytes)
            {
                var size = NifDdsHeader.LevelBytes(info.Width, info.Height, level, bytes);
                presence = position + size <= content.Length
                    ? SceneTextureMipPresence.Authored
                    : SceneTextureMipPresence.Missing;
                position += size;
            }
            else
            {
                presence = SceneTextureMipPresence.Unknown;
            }

            mips.Add(new SceneTextureMip(level, presence, width, height));
        }

        var (formatNamespace, formatCode) = DdsFormatCode(content, info.FormatId);
        var srgb = info.ColorSpace == SceneColorSpace.Srgb;
        return new SceneTextureDescriptor(info.Width, info.Height, null, null, false, mips, formatNamespace, formatCode,
            info.BitsPerPixel, Channels(info.StoredChannels), info.Compression, info.ColorSpace,
            srgb ? SceneValueProvenance.Authored : SceneValueProvenance.Unknown,
            srgb ? "the DXGI format is an sRGB format" : null, info.AlphaMode, "linear");
    }

    /// <summary>The descriptor BMT's minimal header supports (a cube, or a format inspection refused).</summary>
    public static SceneTextureDescriptor FromDdsHeader(NifDdsHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var levels = Math.Clamp(header.Levels, 1, SceneTextureDescriptor.MaximumMipLevels);
        var mips = new List<SceneTextureMip>(levels);
        for (var level = 0; level < levels; level++)
        {
            mips.Add(new SceneTextureMip(level, SceneTextureMipPresence.Unknown, Math.Max(1, header.Width >> level),
                Math.Max(1, header.Height >> level)));
        }

        string? formatNamespace = null;
        string? formatCode = null;
        if (header.DxgiFormat is { } dxgi)
        {
            (formatNamespace, formatCode) = ("DXGI", dxgi.ToString(CultureInfo.InvariantCulture));
        }
        else if (header.FourCc is { } fourCc)
        {
            (formatNamespace, formatCode) = ("FourCC", fourCc);
        }

        var channels = header.Compression switch
        {
            "BC2" or "BC3" => SceneTextureChannels.Rgba,
            "BC4" => SceneTextureChannels.R,
            "BC5" => SceneTextureChannels.Rg,
            _ => SceneTextureChannels.Unknown
        };
        double? bits = header.BlockBytes is { } blockBytes ? blockBytes / 2 : null;
        return new SceneTextureDescriptor(header.Width, header.Height, header.Depth > 1 ? header.Depth : null,
            header.ArraySize is > 0 ? (int)Math.Min(header.ArraySize.Value, int.MaxValue) : null, header.IsCube, mips,
            formatNamespace, formatCode, bits, channels, header.Compression, layout: "linear");
    }

    /// <summary>The descriptor of a DDX original, from its header.</summary>
    /// <param name="header">The DDX header.</param>
    /// <param name="channels">The channel organization, when the relayout's inspection established it.</param>
    /// <param name="presence">Authored when the gate passed, Unknown otherwise.</param>
    public static SceneTextureDescriptor FromDdxHeader(NifDdxHeader header, SceneTextureChannels? channels,
        SceneTextureMipPresence presence)
    {
        ArgumentNullException.ThrowIfNull(header);
        var levels = Math.Clamp(header.DeclaredMipCount, 1, SceneTextureDescriptor.MaximumMipLevels);
        var mips = new List<SceneTextureMip>(levels);
        for (var level = 0; level < levels; level++)
        {
            mips.Add(new SceneTextureMip(level, presence, Math.Max(1, header.Width >> level),
                Math.Max(1, header.Height >> level)));
        }

        var (compression, bits, formatChannels) = header.FormatByte switch
        {
            0x52 => ("BC1", 4d, SceneTextureChannels.Unknown),
            0x53 => ("BC2", 8d, SceneTextureChannels.Rgba),
            0x54 => ("BC3", 8d, SceneTextureChannels.Rgba),
            0x71 => ("BC5", 8d, SceneTextureChannels.Rg),
            0x7B => ("BC4", 4d, SceneTextureChannels.R),
            _ => ((string?)null, (double?)null, SceneTextureChannels.Unknown)
        };
        return new SceneTextureDescriptor(header.Width, header.Height, null, null, null, mips, "Xenos",
            NifDdxHeader.Hex(header.FormatByte), bits, channels ?? formatChannels, compression,
            layout: header.IsThreeXdr ? "xenos-tiled-3xdr" : "xenos-tiled-3xdo");
    }

    /// <summary>Maps Shared's stored-channel text to the channel enum.</summary>
    public static SceneTextureChannels Channels(string storedChannels)
    {
        return storedChannels switch
        {
            "R" => SceneTextureChannels.R,
            "RG" => SceneTextureChannels.Rg,
            "RGB" => SceneTextureChannels.Rgb,
            "RGBA" => SceneTextureChannels.Rgba,
            "L" => SceneTextureChannels.Luminance,
            "LA" => SceneTextureChannels.LuminanceAlpha,
            _ => SceneTextureChannels.Unknown
        };
    }

    /// <summary>
    ///     The pixel-format namespace and code: the FourCC characters, the DX10 DXGI number, or Shared's layout identity
    ///     for a mask-described format.
    /// </summary>
    private static (string Namespace, string Code) DdsFormatCode(ReadOnlySpan<byte> content, string formatId)
    {
        if (content.Length >= 88 && (BinaryPrimitives.ReadUInt32LittleEndian(content[80..]) & 0x4) != 0)
        {
            var code = content.Slice(84, 4);
            if (code.SequenceEqual("DX10"u8) && content.Length >= 132)
            {
                return ("DXGI",
                    BinaryPrimitives.ReadUInt32LittleEndian(content[128..]).ToString(CultureInfo.InvariantCulture));
            }

            if (code.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0)
            {
                return ("FourCC", Encoding.ASCII.GetString(code));
            }
        }

        return ("DDS", formatId);
    }
}
