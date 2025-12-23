using System.Text;

namespace Xbox360MemoryCarver.Core.Models;

/// <summary>
/// File signature information for carving.
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
/// File signature definitions for carving various file types from memory dumps.
/// </summary>
public static class FileSignatures
{
    // Common magic bytes
    public static readonly byte[] GamebryoMagic = Encoding.ASCII.GetBytes("Gamebryo File Format");
    public static readonly byte[] RiffMagic = Encoding.ASCII.GetBytes("RIFF");
    public static readonly byte[] Tes4Magic = Encoding.ASCII.GetBytes("TES4");
    public static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Xbox 360 DDX texture magic bytes
    public static readonly byte[] Ddx3XdoMagic = Encoding.ASCII.GetBytes("3XDO");
    public static readonly byte[] Ddx3XdrMagic = Encoding.ASCII.GetBytes("3XDR");

    // Xbox 360 specific formats
    public static readonly byte[] Xex2Magic = Encoding.ASCII.GetBytes("XEX2");
    public static readonly byte[] XdbfMagic = Encoding.ASCII.GetBytes("XDBF");
    public static readonly byte[] XuisMagic = Encoding.ASCII.GetBytes("XUIS");
    public static readonly byte[] XuibMagic = Encoding.ASCII.GetBytes("XUIB");

    /// <summary>
    /// All supported file signatures for carving.
    /// </summary>
    public static readonly Dictionary<string, SignatureInfo> Signatures = new()
    {
        // Textures
        ["dds"] = new SignatureInfo
        {
            Magic = Encoding.ASCII.GetBytes("DDS "),
            Extension = ".dds",
            Description = "DirectDraw Surface texture",
            MinSize = 128,
            MaxSize = 50 * 1024 * 1024,
            Folder = "textures"
        },

        // Xbox 360 DDX textures
        ["ddx_3xdo"] = new SignatureInfo
        {
            Magic = Ddx3XdoMagic,
            Extension = ".ddx",
            Description = "Xbox 360 DDX texture (3XDO format)",
            MinSize = 68,
            MaxSize = 50 * 1024 * 1024,
            Folder = "ddx"
        },
        ["ddx_3xdr"] = new SignatureInfo
        {
            Magic = Ddx3XdrMagic,
            Extension = ".ddx",
            Description = "Xbox 360 DDX texture (3XDR engine-tiled format)",
            MinSize = 68,
            MaxSize = 50 * 1024 * 1024,
            Folder = "ddx"
        },

        // 3D Models
        ["nif"] = new SignatureInfo
        {
            Magic = GamebryoMagic,
            Extension = ".nif",
            Description = "NetImmerse/Gamebryo 3D model",
            MinSize = 100,
            MaxSize = 20 * 1024 * 1024,
            Folder = "models"
        },

        // Audio
        ["xma"] = new SignatureInfo
        {
            Magic = RiffMagic,
            Extension = ".xma",
            Description = "Xbox Media Audio (RIFF/XMA)",
            MinSize = 44,
            MaxSize = 100 * 1024 * 1024,
            Folder = "audio"
        },
        ["lip"] = new SignatureInfo
        {
            Magic = Encoding.ASCII.GetBytes("LIPS"),
            Extension = ".lip",
            Description = "Lip-sync animation",
            MinSize = 20,
            MaxSize = 5 * 1024 * 1024,
            Folder = "lipsync"
        },

        // Scripts
        ["script_scn"] = new SignatureInfo
        {
            Magic = Encoding.ASCII.GetBytes("scn "),
            Extension = ".txt",
            Description = "Bethesda ObScript (scn format)",
            MinSize = 20,
            MaxSize = 100 * 1024,
            Folder = "scripts"
        },

        // Game Data Files
        ["esp"] = new SignatureInfo
        {
            Magic = Tes4Magic,
            Extension = ".esp",
            Description = "Elder Scrolls Plugin",
            MinSize = 24,
            MaxSize = 500 * 1024 * 1024,
            Folder = "plugins"
        },

        // Images
        ["png"] = new SignatureInfo
        {
            Magic = PngMagic,
            Extension = ".png",
            Description = "PNG image",
            MinSize = 67,
            MaxSize = 50 * 1024 * 1024,
            Folder = "images"
        },

        // Xbox 360 System Formats
        ["xex"] = new SignatureInfo
        {
            Magic = Xex2Magic,
            Extension = ".xex",
            Description = "Xbox 360 Executable",
            MinSize = 24,
            MaxSize = 100 * 1024 * 1024,
            Folder = "executables"
        },
        ["xdbf"] = new SignatureInfo
        {
            Magic = XdbfMagic,
            Extension = ".xdbf",
            Description = "Xbox Dashboard File",
            MinSize = 24,
            MaxSize = 10 * 1024 * 1024,
            Folder = "xbox"
        },
        ["xui_scene"] = new SignatureInfo
        {
            Magic = XuisMagic,
            Extension = ".xui",
            Description = "XUI Scene",
            MinSize = 24,
            MaxSize = 5 * 1024 * 1024,
            Folder = "xbox"
        },
        ["xui_binary"] = new SignatureInfo
        {
            Magic = XuibMagic,
            Extension = ".xui",
            Description = "XUI Binary",
            MinSize = 24,
            MaxSize = 5 * 1024 * 1024,
            Folder = "xbox"
        }
    };

    /// <summary>
    /// Xbox 360 GPU texture formats (from DDXConv/Xenia).
    /// </summary>
    public static readonly Dictionary<int, string> Xbox360GpuTextureFormats = new()
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
    };

    /// <summary>
    /// Bytes per compression block for each format.
    /// </summary>
    public static readonly Dictionary<string, int> BytesPerBlock = new()
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
    };

    public static int GetBytesPerBlock(string fourcc)
    {
        return BytesPerBlock.TryGetValue(fourcc, out var bytes) ? bytes : 16;
    }
}
