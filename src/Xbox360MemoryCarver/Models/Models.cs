namespace Xbox360MemoryCarver.Models;

/// <summary>
/// File signature information for carving.
/// </summary>
public class SignatureInfo
{
    public required byte[] Magic { get; init; }
    public required string Extension { get; init; }
    public required string Description { get; init; }
    public int MinSize { get; init; } = 64;
    public int MaxSize { get; init; } = 50 * 1024 * 1024;
    public string Folder { get; init; } = "";
}

/// <summary>
/// Record of a carved file with original dump location info.
/// </summary>
public class CarveEntry
{
    public required string FileType { get; init; }
    public long Offset { get; init; }
    public long SizeInDump { get; init; }
    public long SizeOutput { get; init; }
    public required string Filename { get; init; }
    public bool IsCompressed { get; init; }
    public string ContentType { get; init; } = "";

    /// <summary>
    /// Indicates the file was partially recovered (e.g., missing chunks).
    /// </summary>
    public bool IsPartial { get; init; }

    /// <summary>
    /// Additional notes about the carving result (e.g., "atlas-only", "truncated").
    /// </summary>
    public string? Notes { get; init; }
}

/// <summary>
/// DDX file header information.
/// </summary>
public class DdxHeader
{
    public required byte[] Magic { get; init; }
    public required string FormatType { get; init; }
    public int Version { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; } = 1;
    public int MipCount { get; init; }
    public int GpuFormat { get; init; }
    public required string FormatName { get; init; }
    public bool IsTiled { get; init; }
    public int DataOffset { get; init; }
    public int CompressedSize { get; init; }
    public int UncompressedSize { get; init; }

    public bool Is3Xdr => FormatType == "3XDR";
}

/// <summary>
/// DDS file header structure.
/// </summary>
public class DdsHeader
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; }
    public required byte[] FourCc { get; init; }
    public int PitchOrLinearSize { get; init; }

    // DDS constants
    private const int DdsHeaderSize = 124;
    private const int DdsPixelFormatSize = 32;
    private const int DdsdCaps = 0x1;
    private const int DdsdHeight = 0x2;
    private const int DdsdWidth = 0x4;
    private const int DdsdPixelFormat = 0x1000;
    private const int DdsdMipmapCount = 0x20000;
    private const int DdsdLinearSize = 0x80000;
    private const int DdpfFourCc = 0x4;
    private const int DdscapsTexture = 0x1000;
    private const int DdscapsMipmap = 0x400000;
    private const int DdscapsComplex = 0x8;

    public byte[] ToBytes()
    {
        var header = new byte[DdsHeaderSize];
        var writer = new BinaryWriter(new MemoryStream(header));

        int flags = DdsdCaps | DdsdHeight | DdsdWidth | DdsdPixelFormat | DdsdLinearSize;
        if (MipCount > 1) flags |= DdsdMipmapCount;

        int caps = DdscapsTexture;
        if (MipCount > 1) caps |= DdscapsMipmap | DdscapsComplex;

        // Header size
        writer.Write(DdsHeaderSize);
        // Flags
        writer.Write(flags);
        // Height
        writer.Write(Height);
        // Width
        writer.Write(Width);
        // Pitch/Linear size
        writer.Write(PitchOrLinearSize);
        // Depth
        writer.Write(0);
        // Mipmap count
        writer.Write(MipCount);

        // Reserved1[11] - 44 bytes
        writer.BaseStream.Position = 72;

        // Pixel format
        writer.Write(DdsPixelFormatSize);
        writer.Write(DdpfFourCc);
        writer.Write(FourCc);
        writer.Write(0); // RGBBitCount
        writer.Write(0); // RBitMask
        writer.Write(0); // GBitMask
        writer.Write(0); // BBitMask
        writer.Write(0); // ABitMask

        // Caps at offset 104
        writer.BaseStream.Position = 104;
        writer.Write(caps);

        // Magic + header
        var result = new byte[4 + DdsHeaderSize];
        "DDS "u8.CopyTo(result);
        header.CopyTo(result, 4);

        return result;
    }
}

/// <summary>
/// Statistics for file carving by type.
/// </summary>
public class FileTypeStats
{
    public int Count { get; set; }
    public long TotalBytes { get; set; }
    public List<string> Files { get; } = [];
}

/// <summary>
/// Result of a DDX to DDS conversion.
/// </summary>
public class DdxConversionResult
{
    /// <summary>
    /// Whether the conversion succeeded (at least partial output).
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The converted DDS data, if successful.
    /// </summary>
    public byte[]? DdsData { get; init; }

    /// <summary>
    /// Whether the output is partial (e.g., atlas-only, missing mips).
    /// </summary>
    public bool IsPartial { get; init; }

    /// <summary>
    /// Notes about the conversion (e.g., "atlas-only: 8 mip levels recovered").
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Raw console output from DDXConv for diagnostics.
    /// </summary>
    public string? ConsoleOutput { get; init; }
}

/// <summary>
/// Complete extraction report.
/// </summary>
public class ExtractionReport
{
    public string DumpFile { get; set; } = "";
    public long DumpSize { get; set; }
    public DateTime ExtractionTime { get; set; } = DateTime.Now;
    public string Version { get; set; } = Program.Version;

    public int TotalFilesCarved { get; set; }
    public long TotalBytesCarved { get; set; }
    public Dictionary<string, FileTypeStats> FilesByType { get; } = [];

    /// <summary>
    /// Detailed manifest of all carved files with offsets and metadata.
    /// </summary>
    public List<CarveEntry> Manifest { get; set; } = [];

    /// <summary>
    /// Count of partial/incomplete files recovered.
    /// </summary>
    public int PartialFilesCount { get; set; }

    public double CoveragePercent { get; set; }
    public long IdentifiedBytes { get; set; }
    public long UnknownBytes { get; set; }
}
