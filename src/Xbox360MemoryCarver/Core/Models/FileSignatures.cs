using System.Collections.Frozen;
using System.Text;

namespace Xbox360MemoryCarver.Core.Models;

/// <summary>
///     File signature information for carving.
/// </summary>
public class SignatureInfo
{
    public required byte[] Magic { get; init; }
    public required string Extension { get; init; }
    public string? Description { get; init; }
    public int MinSize { get; init; } = 16;
    public int MaxSize { get; init; } = 64 * 1024 * 1024;
    public string Folder { get; init; } = "";
}

/// <summary>
///     File signature definitions for carving various file types from memory dumps.
/// </summary>
public static class FileSignatures
{
    // Common magic bytes - use ReadOnlyMemory<byte> for immutability (S3887/S2386)
    private static readonly byte[] s_gamebryoMagic = Encoding.ASCII.GetBytes("Gamebryo File Format");
    private static readonly byte[] s_riffMagic = Encoding.ASCII.GetBytes("RIFF");
    private static readonly byte[] s_tes4Magic = Encoding.ASCII.GetBytes("TES4");
    private static readonly byte[] s_pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Xbox 360 DDX texture magic bytes
    private static readonly byte[] s_ddx3XdoMagic = Encoding.ASCII.GetBytes("3XDO");
    private static readonly byte[] s_ddx3XdrMagic = Encoding.ASCII.GetBytes("3XDR");

    // Xbox 360 specific formats
    private static readonly byte[] s_xex2Magic = Encoding.ASCII.GetBytes("XEX2");
    private static readonly byte[] s_xdbfMagic = Encoding.ASCII.GetBytes("XDBF");
    private static readonly byte[] s_xuisMagic = Encoding.ASCII.GetBytes("XUIS");
    private static readonly byte[] s_xuibMagic = Encoding.ASCII.GetBytes("XUIB");

    // Public read-only spans for external access
    public static ReadOnlySpan<byte> GamebryoMagic => s_gamebryoMagic;
    public static ReadOnlySpan<byte> RiffMagic => s_riffMagic;
    public static ReadOnlySpan<byte> Tes4Magic => s_tes4Magic;
    public static ReadOnlySpan<byte> PngMagic => s_pngMagic;
    public static ReadOnlySpan<byte> Ddx3XdoMagic => s_ddx3XdoMagic;
    public static ReadOnlySpan<byte> Ddx3XdrMagic => s_ddx3XdrMagic;
    public static ReadOnlySpan<byte> Xex2Magic => s_xex2Magic;
    public static ReadOnlySpan<byte> XdbfMagic => s_xdbfMagic;
    public static ReadOnlySpan<byte> XuisMagic => s_xuisMagic;
    public static ReadOnlySpan<byte> XuibMagic => s_xuibMagic;

    /// <summary>
    ///     All supported file signatures for carving.
    /// </summary>
    public static FrozenDictionary<string, SignatureInfo> Signatures { get; } = new Dictionary<string, SignatureInfo>
    {
        // Textures
        ["dds"] = new()
        {
            Magic = Encoding.ASCII.GetBytes("DDS "),
            Extension = ".dds",
            Description = "DirectDraw Surface texture",
            MinSize = 128,
            MaxSize = 50 * 1024 * 1024,
            Folder = "textures"
        },

        // Xbox 360 DDX textures
        ["ddx_3xdo"] =
            new()
            {
                Magic = s_ddx3XdoMagic,
                Extension = ".ddx",
                Description = "Xbox 360 DDX texture (3XDO format)",
                MinSize = 68,
                MaxSize = 50 * 1024 * 1024,
                Folder = "ddx"
            },
        ["ddx_3xdr"] = new()
        {
            Magic = s_ddx3XdrMagic,
            Extension = ".ddx",
            Description = "Xbox 360 DDX texture (3XDR engine-tiled format)",
            MinSize = 68,
            MaxSize = 50 * 1024 * 1024,
            Folder = "ddx"
        },

        // 3D Models
        ["nif"] = new()
        {
            Magic = s_gamebryoMagic,
            Extension = ".nif",
            Description = "NetImmerse/Gamebryo 3D model",
            MinSize = 100,
            MaxSize = 20 * 1024 * 1024,
            Folder = "models"
        },

        // Audio
        ["xma"] = new()
        {
            Magic = s_riffMagic,
            Extension = ".xma",
            Description = "Xbox Media Audio (RIFF/XMA)",
            MinSize = 44,
            MaxSize = 100 * 1024 * 1024,
            Folder = "audio"
        },
        ["lip"] = new()
        {
            Magic = Encoding.ASCII.GetBytes("LIPS"),
            Extension = ".lip",
            Description = "Lip-sync animation",
            MinSize = 20,
            MaxSize = 5 * 1024 * 1024,
            Folder = "lipsync"
        },

        // Scripts
        ["script_scn"] = new()
        {
            Magic = Encoding.ASCII.GetBytes("scn "),
            Extension = ".txt",
            Description = "Bethesda ObScript (scn format)",
            MinSize = 20,
            MaxSize = 100 * 1024,
            Folder = "scripts"
        },

        // Game Data Files
        ["esp"] = new()
        {
            Magic = s_tes4Magic,
            Extension = ".esp",
            Description = "Elder Scrolls Plugin",
            MinSize = 24,
            MaxSize = 500 * 1024 * 1024,
            Folder = "plugins"
        },

        // Images
        ["png"] = new()
        {
            Magic = s_pngMagic,
            Extension = ".png",
            Description = "PNG image",
            MinSize = 67,
            MaxSize = 50 * 1024 * 1024,
            Folder = "images"
        },

        // Xbox 360 System Formats
        ["xex"] = new()
        {
            Magic = s_xex2Magic,
            Extension = ".xex",
            Description = "Xbox 360 Executable",
            MinSize = 24,
            MaxSize = 100 * 1024 * 1024,
            Folder = "executables"
        },
        ["xdbf"] = new()
        {
            Magic = s_xdbfMagic,
            Extension = ".xdbf",
            Description = "Xbox Dashboard File",
            MinSize = 24,
            MaxSize = 10 * 1024 * 1024,
            Folder = "xbox"
        },
        ["xui_scene"] = new()
        {
            Magic = s_xuisMagic,
            Extension = ".xui",
            Description = "XUI Scene",
            MinSize = 24,
            MaxSize = 5 * 1024 * 1024,
            Folder = "xbox"
        },
        ["xui_binary"] = new()
        {
            Magic = s_xuibMagic,
            Extension = ".xui",
            Description = "XUI Binary",
            MinSize = 24,
            MaxSize = 5 * 1024 * 1024,
            Folder = "xbox"
        }
    }.ToFrozenDictionary();

    /// <summary>
    ///     Xbox 360 GPU texture formats (from DDXConv/Xenia).
    /// </summary>
    public static FrozenDictionary<int, string> Xbox360GpuTextureFormats { get; } = new Dictionary<int, string>
    {
        [0x12] = "DXT1",
        [0x13] = "DXT3",
        [0x14] = "DXT5",
        [0x52] = "DXT1",
        [0x53] = "DXT3",
        [0x54] = "DXT5",
        [0x71] = "ATI2",
        [0x7B] = "ATI1",
        [0x82] = "DXT1",
        [0x86] = "DXT1",
        [0x88] = "DXT5"
    }.ToFrozenDictionary();

    /// <summary>
    ///     Bytes per compression block for each format.
    /// </summary>
    public static FrozenDictionary<string, int> BytesPerBlock { get; } = new Dictionary<string, int>
    {
        ["DXT1"] = 8,
        ["DXT3"] = 16,
        ["DXT5"] = 16,
        ["ATI1"] = 8,
        ["ATI2"] = 16,
        ["BC4U"] = 8,
        ["BC4S"] = 8,
        ["BC5U"] = 16,
        ["BC5S"] = 16
    }.ToFrozenDictionary();

    public static int GetBytesPerBlock(string fourcc)
    {
        return BytesPerBlock.TryGetValue(fourcc, out var bytes) ? bytes : 16;
    }
}
