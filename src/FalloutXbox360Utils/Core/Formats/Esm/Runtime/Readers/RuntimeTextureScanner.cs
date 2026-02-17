using System.Collections.Concurrent;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Scans memory dumps for NiPixelData runtime objects containing texture data.
///     Uses heuristic pattern matching based on PDB-derived struct layouts.
///     NiPixelData is a standalone Gamebryo object (NOT TESForm-derived).
///
///     NiPixelData (116 bytes, extends NiObject):
///       +0   vtable (ptr, 4 bytes)
///       +4   m_uiRefCount (uint32 BE) — reference count
///       +8   m_kPixelFormat (NiPixelFormat, 68 bytes inline):
///              +8+0  m_uFlags (byte)
///              +8+1  m_ucBitsPerPixel (byte)
///              +8+4  m_eFormat (enum, uint32 BE) — NiPixelFormat::Format
///              +8+8  m_eTiling (enum, uint32 BE)
///              +8+12 m_uiRendererHint (uint32 BE)
///              +8+16 m_uiExtraData (uint32 BE)
///              +8+20 m_akComponents[4] (NiComponentSpec, 12 bytes each)
///       +76  m_spPalette (NiPointer)
///       +80  m_pucPixels (byte*) — raw pixel data pointer
///       +84  m_puiWidth (uint32*) — per-mipmap width array
///       +88  m_puiHeight (uint32*) — per-mipmap height array
///       +92  m_puiOffsetInBytes (uint32*) — per-mipmap byte offset array
///       +96  m_uiMipmapLevels (uint32 BE)
///       +100 m_uiPixelStride (uint32 BE) — bytes per pixel (0 for compressed)
///       +104 m_uiRevID (uint32 BE)
///       +108 m_uiFaces (uint32 BE) — 1 for 2D, 6 for cubemap
///       +112 bNoConvert (bool)
///
///     NiSourceTexture (72 bytes, extends NiTexture extends NiObjectNET):
///       +4   m_uiRefCount (uint32 BE)
///       +48  m_kFilename (NiFixedString — char*)
///       +60  m_spSrcPixelData (NiPointer&lt;NiPixelData&gt;)
///
///     Both scans are combined into a single pass for performance.
/// </summary>
internal sealed class RuntimeTextureScanner(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimeObjectScanner _scanner = new(context);

    #region NiPixelData Field Offsets (PDB-verified)

    private const int RefCountOffset = 4;
    private const int BitsPerPixelOffset = 9;     // NiPixelFormat.m_ucBitsPerPixel (byte)
    private const int FormatOffset = 12;           // NiPixelFormat.m_eFormat (uint32 BE)
    private const int PixelsPtrOffset = 80;        // m_pucPixels (byte*)
    private const int WidthPtrOffset = 84;         // m_puiWidth (uint32*)
    private const int HeightPtrOffset = 88;        // m_puiHeight (uint32*)
    private const int OffsetsPtrOffset = 92;       // m_puiOffsetInBytes (uint32*)
    private const int MipmapLevelsOffset = 96;     // m_uiMipmapLevels (uint32 BE)
    private const int PixelStrideOffset = 100;     // m_uiPixelStride (uint32 BE)
    private const int FacesOffset = 108;           // m_uiFaces (uint32 BE)
    private const int NiPixelDataSize = 116;

    #endregion

    #region NiSourceTexture Field Offsets

    private const int SrcTexFilenameOffset = 48;       // m_kFilename (NiFixedString)
    private const int SrcTexPixelDataPtrOffset = 60;   // m_spSrcPixelData (NiPointer)
    private const int NiSourceTextureSize = 72;

    #endregion

    #region Validation Thresholds

    private const int MaxRefCount = 10_000;
    private const int MaxMipmapLevels = 16;
    private const int MinDimension = 4;
    private const int MaxDimension = 4096;
    private const int MaxPixelDataSize = 64 * 1024 * 1024; // 64 MB max per texture

    #endregion

    /// <summary>Shared counter for progress reporting.</summary>
    public int TexturesFound => _texturesFound;

    private int _texturesFound;

    /// <summary>
    ///     Scan the entire dump for NiPixelData objects with valid pixel data,
    ///     and simultaneously collect NiSourceTexture filename associations.
    ///     Returns a deduplicated list of extracted textures enriched with filenames.
    /// </summary>
    public List<ExtractedTexture> ScanForTextures(IProgress<(long Scanned, long Total)>? progress = null)
    {
        var textures = new ConcurrentBag<ExtractedTexture>();
        var dataHashes = new ConcurrentDictionary<long, byte>();
        var sourceTextureCandidates = new ConcurrentBag<(uint PixelDataPtr, uint FilenamePtr)>();
        _texturesFound = 0;
        var log = Logger.Instance;

        log.Info("Texture scanner: starting combined NiPixelData + NiSourceTexture scan ({0:N0} bytes)",
            _context.FileSize);

        _scanner.ScanAligned(
            candidateTest: CombinedFastFilter,
            processCandidate: (chunk, offset, fileOffset) =>
            {
                // Try NiPixelData extraction
                if (offset + NiPixelDataSize <= chunk.Length && IsNiPixelDataCandidate(chunk, offset))
                {
                    var texture = ValidateAndExtract(chunk, offset, fileOffset);
                    if (texture != null && dataHashes.TryAdd(texture.DataHash, 0))
                    {
                        textures.Add(texture);
                        Interlocked.Increment(ref _texturesFound);
                        var potTag = texture.IsNonPowerOfTwo ? " [non-POT]" : "";
                        log.Debug(
                            "  Found {0} texture at 0x{1:X}: {2}x{3}, {4} mips, {5:N0} bytes{6}",
                            texture.Format, fileOffset, texture.Width, texture.Height,
                            texture.MipmapLevels, texture.DataSize, potTag);
                    }
                }

                // Collect NiSourceTexture candidates (lightweight — just store pointer pairs)
                if (offset + NiSourceTextureSize <= chunk.Length)
                {
                    var filenamePtr = BinaryUtils.ReadUInt32BE(chunk, offset + SrcTexFilenameOffset);
                    var pixelDataPtr = BinaryUtils.ReadUInt32BE(chunk, offset + SrcTexPixelDataPtrOffset);
                    if (filenamePtr != 0 && pixelDataPtr != 0 &&
                        _context.IsValidPointer(filenamePtr) && _context.IsValidPointer(pixelDataPtr))
                    {
                        sourceTextureCandidates.Add((pixelDataPtr, filenamePtr));
                    }
                }
            },
            minStructSize: NiSourceTextureSize, // Smaller struct: 72 < 116
            progress: progress);

        var result = textures.OrderBy(t => t.SourceOffset).ToList();

        log.Info("Texture scanner: found {0} unique textures, {1} NiSourceTexture candidates",
            result.Count, sourceTextureCandidates.Count);

        // Resolve filenames from collected NiSourceTexture candidates
        if (result.Count > 0 && !sourceTextureCandidates.IsEmpty)
        {
            result = ResolveFilenames(result, sourceTextureCandidates);
        }

        return result;
    }

    /// <summary>
    ///     Combined fast filter: passes if the offset could be EITHER NiPixelData or NiSourceTexture.
    ///     Both are Gamebryo NiObject subclasses with refcount at +4.
    ///     Sharing the refcount check avoids redundant work — most offsets fail here.
    /// </summary>
    private bool CombinedFastFilter(byte[] chunk, int offset)
    {
        // Minimum size check for the smaller struct (NiSourceTexture = 72 bytes)
        if (offset + NiSourceTextureSize > chunk.Length)
        {
            return false;
        }

        // Shared check: all Gamebryo NiObjects have refcount at +4 in [1, 10000].
        // This rejects >99% of offsets immediately.
        var refCount = BinaryUtils.ReadUInt32BE(chunk, offset + RefCountOffset);
        if (refCount == 0 || refCount > MaxRefCount)
        {
            return false;
        }

        // Check NiSourceTexture pattern: valid pointer at +60 (cheap — just one IsValidPointer call)
        var srcTexPtr = BinaryUtils.ReadUInt32BE(chunk, offset + SrcTexPixelDataPtrOffset);
        if (_context.IsValidPointer(srcTexPtr))
        {
            return true;
        }

        // Check NiPixelData pattern: needs 116 bytes + format enum + mipmap levels
        if (offset + NiPixelDataSize > chunk.Length)
        {
            return false;
        }

        var format = BinaryUtils.ReadUInt32BE(chunk, offset + FormatOffset);
        if (format > 13)
        {
            return false;
        }

        var mipLevels = BinaryUtils.ReadUInt32BE(chunk, offset + MipmapLevelsOffset);
        if (mipLevels == 0 || mipLevels > MaxMipmapLevels)
        {
            return false;
        }

        var pixelsPtr = BinaryUtils.ReadUInt32BE(chunk, offset + PixelsPtrOffset);
        if (!_context.IsValidPointer(pixelsPtr))
        {
            return false;
        }

        var widthPtr = BinaryUtils.ReadUInt32BE(chunk, offset + WidthPtrOffset);
        var heightPtr = BinaryUtils.ReadUInt32BE(chunk, offset + HeightPtrOffset);
        if (!_context.IsValidPointer(widthPtr) || !_context.IsValidPointer(heightPtr))
        {
            return false;
        }

        var faces = BinaryUtils.ReadUInt32BE(chunk, offset + FacesOffset);
        return faces <= 6;
    }

    /// <summary>
    ///     Additional NiPixelData-specific checks within processCandidate.
    ///     The CombinedFastFilter may have let this through based on NiSourceTexture pattern only.
    /// </summary>
    private bool IsNiPixelDataCandidate(byte[] chunk, int offset)
    {
        var format = BinaryUtils.ReadUInt32BE(chunk, offset + FormatOffset);
        if (format > 13)
        {
            return false;
        }

        var mipLevels = BinaryUtils.ReadUInt32BE(chunk, offset + MipmapLevelsOffset);
        if (mipLevels == 0 || mipLevels > MaxMipmapLevels)
        {
            return false;
        }

        var pixelsPtr = BinaryUtils.ReadUInt32BE(chunk, offset + PixelsPtrOffset);
        if (!_context.IsValidPointer(pixelsPtr))
        {
            return false;
        }

        var widthPtr = BinaryUtils.ReadUInt32BE(chunk, offset + WidthPtrOffset);
        var heightPtr = BinaryUtils.ReadUInt32BE(chunk, offset + HeightPtrOffset);
        if (!_context.IsValidPointer(widthPtr) || !_context.IsValidPointer(heightPtr))
        {
            return false;
        }

        var faces = BinaryUtils.ReadUInt32BE(chunk, offset + FacesOffset);
        return faces <= 6;
    }

    /// <summary>
    ///     Resolve filenames from collected NiSourceTexture candidates by matching
    ///     their m_spSrcPixelData pointers to found NiPixelData virtual addresses.
    /// </summary>
    private List<ExtractedTexture> ResolveFilenames(
        List<ExtractedTexture> textures,
        ConcurrentBag<(uint PixelDataPtr, uint FilenamePtr)> candidates)
    {
        var log = Logger.Instance;
        var vaToIndex = BuildPixelDataVaIndex(textures);
        if (vaToIndex.Count == 0)
        {
            return textures;
        }

        var filenames = new string?[textures.Count];
        var matchCount = 0;

        foreach (var (pixelDataPtr, filenamePtr) in candidates)
        {
            if (!vaToIndex.TryGetValue(pixelDataPtr, out var index))
            {
                continue;
            }

            if (filenames[index] != null)
            {
                continue; // Already resolved
            }

            var filename = ReadNullTerminatedString(filenamePtr);
            if (filename != null)
            {
                filenames[index] = filename;
                matchCount++;
            }
        }

        var result = new List<ExtractedTexture>(textures.Count);
        for (var i = 0; i < textures.Count; i++)
        {
            result.Add(filenames[i] != null ? textures[i] with { Filename = filenames[i] } : textures[i]);
        }

        log.Info("Texture scanner: enriched {0}/{1} textures with NiSourceTexture filenames",
            matchCount, textures.Count);

        return result;
    }

    private Dictionary<uint, int> BuildPixelDataVaIndex(List<ExtractedTexture> textures)
    {
        var minidump = _context.MinidumpInfo;
        var vaToIndex = new Dictionary<uint, int>();
        for (var i = 0; i < textures.Count; i++)
        {
            var va = minidump.FileOffsetToVirtualAddress(textures[i].SourceOffset);
            if (va is > 0 and <= uint.MaxValue)
            {
                vaToIndex.TryAdd((uint)va.Value, i);
            }
        }

        return vaToIndex;
    }

    /// <summary>
    ///     Full validation and extraction for a NiPixelData candidate.
    ///     Follows pointers to read dimensions, offsets, and pixel data.
    /// </summary>
    private ExtractedTexture? ValidateAndExtract(byte[] chunk, int offset, long fileOffset)
    {
        var format = (NiTextureFormat)BinaryUtils.ReadUInt32BE(chunk, offset + FormatOffset);
        var bpp = chunk[offset + BitsPerPixelOffset];
        var mipLevels = (int)BinaryUtils.ReadUInt32BE(chunk, offset + MipmapLevelsOffset);
        var faces = Math.Max(1, (int)BinaryUtils.ReadUInt32BE(chunk, offset + FacesOffset));

        // Validate dimensions from pointer-indirected arrays
        var dims = ReadAndValidateDimensions(chunk, offset, format);
        if (dims == null)
        {
            return null;
        }

        var (width, height) = dims.Value;

        // Calculate expected data size from format and dimensions
        var expectedSize = CalculateTotalDataSize(format, width, height, mipLevels, faces);
        if (expectedSize <= 0 || expectedSize > MaxPixelDataSize)
        {
            return null;
        }

        // Cross-check with offset array if available (more accurate than calculated size).
        // Only use if it's reasonably close to our calculated size (within 2x, and at least mip0 size).
        var offsetsPtr = BinaryUtils.ReadUInt32BE(chunk, offset + OffsetsPtrOffset);
        var actualSize = ReadDataSizeFromOffsets(offsetsPtr, mipLevels, faces);
        var mip0Size = CalculateMipSize(format, width, height);
        var isPot = IsPowerOfTwo((uint)width) && IsPowerOfTwo((uint)height);

        if (!isPot)
        {
            // Non-POT textures: require exact data size match from offset array
            if (actualSize == null || actualSize.Value != expectedSize)
            {
                return null;
            }
        }
        else if (actualSize >= mip0Size && actualSize <= expectedSize * 2)
        {
            expectedSize = actualSize.Value;
        }

        // Read pixel data
        var pixelsPtr = BinaryUtils.ReadUInt32BE(chunk, offset + PixelsPtrOffset);
        var pixelData = ReadPixelData(pixelsPtr, expectedSize);
        if (pixelData == null)
        {
            return null;
        }

        return new ExtractedTexture
        {
            SourceOffset = fileOffset,
            Width = width,
            Height = height,
            MipmapLevels = mipLevels,
            Format = format,
            BitsPerPixel = bpp,
            Faces = faces,
            PixelData = pixelData,
            DataHash = ComputeDataHash(pixelData)
        };
    }

    /// <summary>
    ///     Read and validate width/height from pointer-indirected mipmap arrays.
    ///     POT dimensions pass with standard checks. Non-POT dimensions are allowed
    ///     for uncompressed formats with additional structural validation to catch
    ///     screenshots, render targets, and UI textures.
    /// </summary>
    private (int Width, int Height)? ReadAndValidateDimensions(
        byte[] chunk, int offset, NiTextureFormat format)
    {
        var widthPtr = BinaryUtils.ReadUInt32BE(chunk, offset + WidthPtrOffset);
        var width = ReadUInt32AtPointer(widthPtr);
        if (width is null or < MinDimension or > MaxDimension)
        {
            return null;
        }

        var heightPtr = BinaryUtils.ReadUInt32BE(chunk, offset + HeightPtrOffset);
        var height = ReadUInt32AtPointer(heightPtr);
        if (height is null or < MinDimension or > MaxDimension)
        {
            return null;
        }

        var isCompressed = format is NiTextureFormat.DXT1 or NiTextureFormat.DXT3 or NiTextureFormat.DXT5;
        var isPot = IsPowerOfTwo(width.Value) && IsPowerOfTwo(height.Value);

        if (isPot)
        {
            // POT textures: keep existing block-alignment check for compressed formats
            if (isCompressed && (width.Value % 4 != 0 || height.Value % 4 != 0))
            {
                return null;
            }

            return ((int)width.Value, (int)height.Value);
        }

        // Non-POT path: apply stricter compensating validation
        if (!ValidateNonPotTexture(chunk, offset, format))
        {
            return null;
        }

        return ((int)width.Value, (int)height.Value);
    }

    /// <summary>
    ///     Additional validation for non-power-of-two textures to compensate for
    ///     the relaxed dimension filter. Non-POT textures in Gamebryo are rare
    ///     (screenshots, render targets, UI) and have specific structural constraints.
    /// </summary>
    private static bool ValidateNonPotTexture(byte[] chunk, int offset, NiTextureFormat format)
    {
        // Non-POT textures cannot be block-compressed (DXT requires POT or at minimum
        // block-aligned dims, and the engine never creates non-POT DXT textures)
        if (format is NiTextureFormat.DXT1 or NiTextureFormat.DXT3 or NiTextureFormat.DXT5)
        {
            return false;
        }

        // Non-POT textures should have exactly 1 mipmap level (engine doesn't
        // generate mipchains for render targets / screenshots)
        var mipLevels = BinaryUtils.ReadUInt32BE(chunk, offset + MipmapLevelsOffset);
        if (mipLevels != 1)
        {
            return false;
        }

        // Non-POT textures should have exactly 1 face (no cubemap render targets
        // with non-POT dims in this engine)
        var faces = BinaryUtils.ReadUInt32BE(chunk, offset + FacesOffset);
        if (faces != 1)
        {
            return false;
        }

        // Validate pixel stride matches format expectation
        var pixelStride = BinaryUtils.ReadUInt32BE(chunk, offset + PixelStrideOffset);
        var expectedStride = GetExpectedPixelStride(format);
        if (expectedStride > 0 && pixelStride != expectedStride)
        {
            return false;
        }

        // Cross-check BPP with format
        var bpp = chunk[offset + BitsPerPixelOffset];
        var expectedBpp = GetExpectedBitsPerPixel(format);
        if (expectedBpp > 0 && bpp != expectedBpp)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Expected m_uiPixelStride (bytes per pixel) for each uncompressed format.
    ///     Returns 0 for unknown/compressed (no validation possible).
    /// </summary>
    private static uint GetExpectedPixelStride(NiTextureFormat format) => format switch
    {
        NiTextureFormat.RGB => 3,
        NiTextureFormat.RGBA => 4,
        NiTextureFormat.PAL or NiTextureFormat.PALA => 1,
        NiTextureFormat.Bump => 2,
        NiTextureFormat.OneCh => 1,
        NiTextureFormat.TwoCh => 2,
        NiTextureFormat.ThreeCh => 3,
        _ => 0
    };

    /// <summary>
    ///     Expected m_ucBitsPerPixel for each uncompressed format.
    ///     Returns 0 for unknown/compressed (no validation possible).
    /// </summary>
    private static byte GetExpectedBitsPerPixel(NiTextureFormat format) => format switch
    {
        NiTextureFormat.RGB or NiTextureFormat.ThreeCh => 24,
        NiTextureFormat.RGBA => 32,
        NiTextureFormat.PAL or NiTextureFormat.PALA or NiTextureFormat.OneCh => 8,
        NiTextureFormat.Bump or NiTextureFormat.TwoCh => 16,
        _ => 0
    };

    /// <summary>Read a null-terminated ASCII string from a pointer.</summary>
    private string? ReadNullTerminatedString(uint ptr)
    {
        if (ptr == 0 || !_context.IsValidPointer(ptr))
        {
            return null;
        }

        var fo = _context.VaToFileOffset(ptr);
        if (fo == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fo.Value, 256);
        if (buf == null)
        {
            return null;
        }

        for (var i = 0; i < buf.Length; i++)
        {
            if (buf[i] == 0)
            {
                return i == 0 ? null : System.Text.Encoding.ASCII.GetString(buf, 0, i);
            }

            if (buf[i] < 32 || buf[i] > 126)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Read a uint32 BE value at the given virtual address pointer.</summary>
    private uint? ReadUInt32AtPointer(uint ptr)
    {
        var fo = _context.VaToFileOffset(ptr);
        if (fo == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fo.Value, 4);
        if (buf == null)
        {
            return null;
        }

        return BinaryUtils.ReadUInt32BE(buf, 0);
    }

    /// <summary>
    ///     Read the total data size from the m_puiOffsetInBytes array.
    ///     The array has (mipLevels * faces + 1) entries, and the last entry
    ///     gives the total size.
    /// </summary>
    private int? ReadDataSizeFromOffsets(uint ptr, int mipLevels, int faces)
    {
        if (!_context.IsValidPointer(ptr))
        {
            return null;
        }

        var totalEntries = mipLevels * faces + 1;
        var fo = _context.VaToFileOffset(ptr);
        if (fo == null)
        {
            return null;
        }

        // Read the last entry (total size)
        var lastEntryFo = fo.Value + (totalEntries - 1) * 4L;
        var buf = _context.ReadBytes(lastEntryFo, 4);
        if (buf == null)
        {
            return null;
        }

        return (int)BinaryUtils.ReadUInt32BE(buf, 0);
    }

    /// <summary>Read raw pixel data from the given pointer.</summary>
    private byte[]? ReadPixelData(uint ptr, int size)
    {
        var fo = _context.VaToFileOffset(ptr);
        if (fo == null)
        {
            return null;
        }

        var data = _context.ReadBytes(fo.Value, size);
        if (data == null || data.Length < size)
        {
            return null;
        }

        // Quick sanity: reject all-zero data (unmapped memory)
        var nonZero = false;
        for (var i = 0; i < Math.Min(data.Length, 256); i++)
        {
            if (data[i] != 0)
            {
                nonZero = true;
                break;
            }
        }

        return nonZero ? data : null;
    }

    /// <summary>
    ///     Calculate total pixel data size for all mipmap levels and faces.
    /// </summary>
    private static int CalculateTotalDataSize(NiTextureFormat format, int width, int height, int mipLevels, int faces)
    {
        var total = 0;
        for (var mip = 0; mip < mipLevels; mip++)
        {
            var mipW = Math.Max(1, width >> mip);
            var mipH = Math.Max(1, height >> mip);
            total += CalculateMipSize(format, mipW, mipH);
        }

        return total * faces;
    }

    /// <summary>Calculate data size for a single mipmap level.</summary>
    private static int CalculateMipSize(NiTextureFormat format, int width, int height) => format switch
    {
        NiTextureFormat.DXT1 => Math.Max(1, width / 4) * Math.Max(1, height / 4) * 8,
        NiTextureFormat.DXT3 or NiTextureFormat.DXT5 => Math.Max(1, width / 4) * Math.Max(1, height / 4) * 16,
        NiTextureFormat.RGB => width * height * 3,
        NiTextureFormat.RGBA => width * height * 4,
        NiTextureFormat.PAL or NiTextureFormat.PALA => width * height,
        NiTextureFormat.Bump => width * height * 2,
        _ => width * height * 4 // Conservative default
    };

    private static bool IsPowerOfTwo(uint value) => value > 0 && (value & (value - 1)) == 0;

    private static long ComputeDataHash(byte[] data)
    {
        long hash = 0;
        var len = Math.Min(data.Length, 64);
        for (var i = 0; i < len; i++)
        {
            hash = hash * 31 + data[i];
        }

        // Mix in total length to differentiate same-prefix data
        return hash ^ (data.Length * 0x517CC1B727220A95L);
    }
}
