using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     An Arena <c>SAVEGAME.NN</c> slot file (166,631 bytes). Six contiguous blocks, all
///     plaintext, tiling the file exactly — measured 2026-09-06 on the retail
///     SAVEGAME.00/.01/.02 (.64 is a byte-identical copy of .02):
///     <code>
///         0x00000   64,000  screenshot: 320x200 8-bit mode-13h framebuffer (the save-slot menu)
///         0x0FA00      768  palette: PAL.COL's 8-bit body right-shifted by 2 (6-bit VGA DAC), 768/768
///         0x0FD00    3,559  state block (raw; only the offsets listed on <see cref="State" /> are known)
///         0x10AE7   32,768  MAP1 plane: 128x128 u16 LE, 256-byte row pitch, fill 0xE000
///         0x18AE7   32,768  FLOR plane: 128x128 u16 LE, 256-byte row pitch, fill 0x0000
///         0x20AE7   32,768  raw DOS conventional memory, linear 0x0000-0x7FFF
///     </code>
///     Each boundary is anchored independently: the screenshot by P1.IMG from GLOBAL.BSA sitting at
///     row 147 (94.3% byte identity) and LOADSAVE.IMG at row 0; the palette by PAL.COL &gt;&gt; 2
///     matching 768/768 at exactly one offset in the file; the two planes by their live cells
///     equalling the decompressed MIF layers (SAVEGAME.02 vs MAGE6.MIF 625/625 on both planes;
///     SAVEGAME.00 vs START.MIF FLOR 2,499/2,500, MAP1 2,419/2,500 with every one of the 81
///     deltas on a 0x8000-flag cell: 41 gained bit 0x0200 with the low byte kept, 31 were
///     cleared to 0x0000, 9 appeared where the MIF has 0x0000 — runtime state, whose meaning
///     is not established); the memory image by the ACD.EXE relocation table (99.92% after applying
///     the 325 in-window relocations at load segment 0x01A3) and by the BIOS tick at linear 0x46C
///     reading 1.7 s before each file's own mtime. The 3,559-byte state block is the residual
///     between its two measured neighbours; its internal structure is NOT established beyond the
///     five fields documented on <see cref="State" />.
///     <para>
///         The planes are odd-aligned: cell (0,0) of MAP1 sits at file offset 0x10AE7, proven by
///         cells 50..127 of every row reading 0xE000 on 9,984/9,984 at that base against
///         9,984/9,984 exceptions one byte later (the even base straddles the fill and reads
///         0x00E0). Only the top-left <see cref="LiveWidth" /> x
///         <see cref="LiveDepth" /> rectangle is live; the rest is fill. 0xE000 appears in 0 of 651
///         retail MAP1 layers, so the MAP1 fill discriminates live from dead cells; 0x0000 is a
///         legitimate floor value (present in 543/651 retail FLOR layers) so the FLOR extent is
///         taken from the header, never measured.
///     </para>
///     <para>
///         Nothing here is XOR-obfuscated: every block was matched against an external plaintext
///         oracle (retail art, PAL.COL, the decompressed MIF layers, the relocated ACD.EXE image).
///         The XOR cipher Arena does use belongs to the .INF files, see <see cref="ArenaInfFile" />.
///     </para>
/// </summary>
internal sealed class ArenaSaveGame
{
    /// <summary>Exact length of every retail SAVEGAME.NN.</summary>
    public const int FileLength = 166_631;

    /// <summary>File offset of the 320x200 framebuffer (the file starts with it).</summary>
    public const int ScreenshotOffset = 0;

    /// <summary>Framebuffer width in pixels (mode 13h).</summary>
    public const int ScreenshotWidth = 320;

    /// <summary>Framebuffer height in pixels (mode 13h).</summary>
    public const int ScreenshotHeight = 200;

    /// <summary>Framebuffer length: 64,000 bytes, one palette index per pixel.</summary>
    public const int ScreenshotLength = ScreenshotWidth * ScreenshotHeight;

    /// <summary>File offset of the 768-byte 6-bit palette (0x0FA00).</summary>
    public const int PaletteOffset = 64_000;

    /// <summary>Palette length: 256 entries x 3 components.</summary>
    public const int PaletteLength = 768;

    /// <summary>File offset of the state block (0x0FD00).</summary>
    public const int StateOffset = 64_768;

    /// <summary>State block length: the residual between the palette and the MAP1 plane.</summary>
    public const int StateLength = 3_559;

    /// <summary>Cells per side of each plane; the array is square regardless of the live extent.</summary>
    public const int PlaneDimension = 128;

    /// <summary>Bytes from the start of one plane row to the next (128 cells x 2 bytes).</summary>
    public const int PlaneRowPitch = PlaneDimension * 2;

    /// <summary>Bytes in one plane: 128 rows x 256 bytes = 32,768.</summary>
    public const int PlaneLength = PlaneDimension * PlaneRowPitch;

    /// <summary>File offset of cell (0,0) of the MAP1 plane. Odd — see the class remarks.</summary>
    public const int Map1Offset = 0x10AE7;

    /// <summary>File offset of cell (0,0) of the FLOR plane, abutting the MAP1 plane.</summary>
    public const int FloorOffset = 0x18AE7;

    /// <summary>Out-of-level sentinel in the MAP1 plane. Occurs in no retail MAP1 layer.</summary>
    public const ushort Map1Fill = 0xE000;

    /// <summary>Out-of-level fill in the FLOR plane. Also a legitimate floor value, so it cannot discriminate.</summary>
    public const ushort FloorFill = 0x0000;

    /// <summary>File offset of the raw DOS memory image (linear address 0 lands here).</summary>
    public const int MemoryImageOffset = 0x20AE7;

    /// <summary>Memory image length: linear 0x0000-0x7FFF, running to end of file.</summary>
    public const int MemoryImageLength = 32_768;

    /// <summary>33-byte level-name field, NUL-terminated with residue after the terminator.</summary>
    public const int LevelNameOffset = 0x1048C;

    /// <summary>Width of the level-name field (it ends where the .INF name begins).</summary>
    public const int LevelNameFieldLength = 33;

    /// <summary>13-byte .INF filename field (e.g. "start.inf"), NUL-terminated.</summary>
    public const int InfNameOffset = 0x104AD;

    /// <summary>Width of the .INF name field (it ends where the 0xFF run begins).</summary>
    public const int InfNameFieldLength = 13;

    /// <summary>A 64-byte run of 0xFF that follows the .INF name in every retail save.</summary>
    public const int FfRunOffset = 0x104BA;

    /// <summary>Length of the 0xFF run.</summary>
    public const int FfRunLength = 64;

    /// <summary>Verbatim 61-byte copy of the source .MIF's MHDR payload (tag and size word excluded).</summary>
    public const int MifHeaderCopyOffset = 0x1053A;

    /// <summary>
    ///     .MIF filename, NUL-terminated ASCII ("start.mif", "mage6.mif"), abutting the MHDR copy.
    ///     The field's width is NOT established: retail saves show 9 characters, a NUL and five more
    ///     zero bytes before other data resumes.
    /// </summary>
    public const int MifNameOffset = 0x10577;

    /// <summary>
    ///     Longest .MIF name the probe accepts: a DOS 8.3 name plus its terminator. This is a
    ///     bound on the PROBE, not a measured field width.
    /// </summary>
    public const int MifNameProbeLength = 13;

    private readonly byte[] _file;
    private readonly ushort[] _floor;
    private readonly ushort[] _map1;

    private ArenaSaveGame(string name, byte[] file, ArenaMifFile mapHeader, ushort[] map1, ushort[] floor)
    {
        Name = name;
        _file = file;
        MapHeader = mapHeader;
        _map1 = map1;
        _floor = floor;
        Palette6Bit = file.AsSpan(PaletteOffset, PaletteLength).ToArray();
        Screenshot = new IndexedBitmap(
            ScreenshotWidth, ScreenshotHeight, file.AsSpan(ScreenshotOffset, ScreenshotLength).ToArray());
        State = file.AsSpan(StateOffset, StateLength).ToArray();
        RawMemoryImage = file.AsSpan(MemoryImageOffset, MemoryImageLength).ToArray();
        LevelName = ReadNulTerminated(file.AsSpan(LevelNameOffset, LevelNameFieldLength));
        InfName = ReadNulTerminated(file.AsSpan(InfNameOffset, InfNameFieldLength));
        MifName = ReadNulTerminated(file.AsSpan(MifNameOffset, StateOffset + StateLength - MifNameOffset));
    }

    /// <summary>Logical file name this save was parsed from.</summary>
    public string Name { get; }

    /// <summary>The 320x200 framebuffer as palette indices (row-major, no header).</summary>
    public IndexedBitmap Screenshot { get; }

    /// <summary>The 768 palette bytes exactly as stored: 6-bit VGA DAC components (0..0x3F).</summary>
    public byte[] Palette6Bit { get; }

    /// <summary>
    ///     The stored palette promoted to 8 bits by <c>v &lt;&lt; 2</c> — the exact inverse of the
    ///     <c>&gt;&gt; 2</c> that produced it from PAL.COL, so 0x3F becomes 0xFC (not the 0xFF a
    ///     bit-replicating promotion would give). <see cref="Palette6Bit" /> keeps the raw bytes for
    ///     callers that want a different promotion.
    /// </summary>
    public Palette PromotedPalette => Palette.FromRgb8(PromoteBy2(Palette6Bit));

    /// <summary>
    ///     Level display name from the 33-byte field at <see cref="LevelNameOffset" />, read to
    ///     its first NUL. The field is written as a terminated string without clearing the
    ///     remainder — SAVEGAME.02 holds "level1\0evel 1\0", where the residue is the tail of the
    ///     previous occupant "start level 1" — so it is never trimmed, only terminated.
    /// </summary>
    public string LevelName { get; }

    /// <summary>The .INF filename from the 13-byte field at <see cref="InfNameOffset" />.</summary>
    public string InfName { get; }

    /// <summary>The .MIF filename at <see cref="MifNameOffset" />, read to its first NUL.</summary>
    public string MifName { get; }

    /// <summary>
    ///     The source .MIF's MHDR payload, parsed through <see cref="ArenaMifFile" />'s own header
    ///     parser (it carries no levels). <see cref="ArenaMifFile.Width" /> and
    ///     <see cref="ArenaMifFile.Depth" /> are MHDR bytes 21 and 23 (file 66,895 and 66,897);
    ///     <see cref="ArenaMifFile.DeclaredLevelCount" /> is byte 19 (file 66,893). Which of the
    ///     two words is width and which depth is NOT settled by the retail saves, all of which are
    ///     square; the assignment follows the MIF parser's convention.
    /// </summary>
    public ArenaMifFile MapHeader { get; }

    /// <summary>Live columns of each plane, from the MHDR copy.</summary>
    public int LiveWidth => MapHeader.Width;

    /// <summary>Live rows of each plane, from the MHDR copy.</summary>
    public int LiveDepth => MapHeader.Depth;

    /// <summary>
    ///     The MAP1 plane: 128 x 128 little-endian u16 cells, row-major (
    ///     <c>
    ///         index = column + row *
    ///         128
    ///     </c>
    ///     ), out-of-level cells = <see cref="Map1Fill" />.
    /// </summary>
    public ReadOnlySpan<ushort> Map1 => _map1;

    /// <summary>
    ///     The FLOR plane, shaped like <see cref="Map1" />, out-of-level cells = <see cref="FloorFill" />.
    /// </summary>
    public ReadOnlySpan<ushort> Floor => _floor;

    /// <summary>
    ///     The 3,559-byte state block, raw. Known offsets, given as file offsets (subtract
    ///     <see cref="StateOffset" /> for an index into this array):
    ///     <list type="bullet">
    ///         <item>0x1048C level name, 33 bytes, NUL-terminated with residue</item>
    ///         <item>0x104AD .INF filename, 13 bytes</item>
    ///         <item>0x104BA 64 bytes of 0xFF</item>
    ///         <item>0x1053A MHDR payload copy, 61 bytes (u16 level count at +19, dims at +21/+23)</item>
    ///         <item>0x10577 .MIF filename, NUL-terminated, width not established</item>
    ///     </list>
    ///     Everything else in the block is unidentified. Two saves in the same level differ in
    ///     only 10 of these bytes, so most of it is level-constant.
    /// </summary>
    public byte[] State { get; }

    /// <summary>
    ///     The final 32,768 bytes: a raw, live snapshot of DOS conventional memory from linear
    ///     0x00000 (the interrupt vector table) to 0x07FFF, captured at save time with ACD.EXE
    ///     loaded at segment 0x01A3. Exposed verbatim; not parsed.
    /// </summary>
    public byte[] RawMemoryImage { get; }

    /// <summary>The whole file, for callers that need a region this reader does not name.</summary>
    public ReadOnlySpan<byte> RawBytes => _file;

    /// <summary>
    ///     Probes for a SAVEGAME.NN. Four gates: the exact length; every palette byte within the
    ///     6-bit range; the MHDR copy parsing (non-zero width and depth); and the .MIF name being
    ///     printable ASCII, NUL-terminated within <see cref="MifNameProbeLength" /> bytes and
    ///     ending in ".mif". On an arbitrary 166,631-byte file the length gate passes by
    ///     construction and the MHDR gate fails only when either dimension word is zero, so the
    ///     discrimination rests on the palette gate (768 bytes all below 0x40) and the name gate.
    /// </summary>
    public static bool IsSaveGame(ReadOnlySpan<byte> file)
    {
        if (file.Length != FileLength)
        {
            return false;
        }

        foreach (var component in file.Slice(PaletteOffset, PaletteLength))
        {
            if (component > 0x3F)
            {
                return false;
            }
        }

        if (TryParseMapHeader(file, "probe") is null)
        {
            return false;
        }

        var nameField = file.Slice(MifNameOffset, MifNameProbeLength);
        var terminator = nameField.IndexOf((byte)0);
        if (terminator < 5)
        {
            return false;
        }

        var name = nameField[..terminator];
        foreach (var c in name)
        {
            if (c is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return Encoding.ASCII.GetString(name).EndsWith(".mif", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a SAVEGAME.NN. The length must be exactly <see cref="FileLength" />.</summary>
    public static ArenaSaveGame Parse(ReadOnlySpan<byte> file, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (file.Length != FileLength)
        {
            throw new InvalidDataException(
                $"'{name}' is {file.Length} bytes; an Arena SAVEGAME is exactly {FileLength}.");
        }

        var header = TryParseMapHeader(file, name)
                     ?? throw new InvalidDataException(
                         $"'{name}' carries an unparsable MHDR copy at 0x{MifHeaderCopyOffset:X}.");

        return new ArenaSaveGame(
            name,
            file.ToArray(),
            header,
            ReadPlane(file, Map1Offset),
            ReadPlane(file, FloorOffset));
    }

    /// <summary>
    ///     The live <see cref="LiveWidth" /> x <see cref="LiveDepth" /> sub-grid of the MAP1 plane,
    ///     row-major with <c>index = x + z * LiveWidth</c> — the same layout
    ///     <see cref="ArenaMifLevel.Map1" /> uses, so the two compare cell-for-cell.
    /// </summary>
    public ushort[] GetLiveMap1()
    {
        return ExtractLive(_map1);
    }

    /// <summary>The live sub-grid of the FLOR plane, laid out like <see cref="GetLiveMap1" />.</summary>
    public ushort[] GetLiveFloor()
    {
        return ExtractLive(_floor);
    }

    /// <summary>Cell (<paramref name="column" />, <paramref name="row" />) of the 128x128 MAP1 plane.</summary>
    public ushort Map1At(int column, int row)
    {
        return _map1[CellIndex(column, row)];
    }

    /// <summary>Cell (<paramref name="column" />, <paramref name="row" />) of the 128x128 FLOR plane.</summary>
    public ushort FloorAt(int column, int row)
    {
        return _floor[CellIndex(column, row)];
    }

    /// <summary>Encodes the screenshot as a PNG through the stored palette (<see cref="PromotedPalette" />).</summary>
    public byte[] RenderScreenshotPng()
    {
        var texture = Screenshot.ToDecodedTexture(PromotedPalette);
        return PngWriter.EncodeRgba(texture.Pixels, texture.Width, texture.Height);
    }

    /// <summary>Writes <see cref="RenderScreenshotPng" />'s output to <paramref name="path" />.</summary>
    public void SaveScreenshotPng(string path)
    {
        var texture = Screenshot.ToDecodedTexture(PromotedPalette);
        PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height, path);
    }

    private static ArenaMifFile? TryParseMapHeader(ReadOnlySpan<byte> file, string name)
    {
        // The copy is the MHDR payload alone, so re-wrap it in the tag + u16 size the .MIF parser
        // expects. With nothing after the header it yields a level-less ArenaMifFile.
        var wrapped = new byte[6 + ArenaMifFile.HeaderPayloadSize];
        "MHDR"u8.CopyTo(wrapped);
        BinaryPrimitives.WriteUInt16LittleEndian(wrapped.AsSpan(4), ArenaMifFile.HeaderPayloadSize);
        file.Slice(MifHeaderCopyOffset, ArenaMifFile.HeaderPayloadSize).CopyTo(wrapped.AsSpan(6));

        try
        {
            return ArenaMifFile.Parse(wrapped, name);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static ushort[] ReadPlane(ReadOnlySpan<byte> file, int offset)
    {
        var cells = new ushort[PlaneDimension * PlaneDimension];
        var plane = file.Slice(offset, PlaneLength);
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = BinaryPrimitives.ReadUInt16LittleEndian(plane[(i * 2)..]);
        }

        return cells;
    }

    private ushort[] ExtractLive(ushort[] plane)
    {
        var width = Math.Min(LiveWidth, PlaneDimension);
        var depth = Math.Min(LiveDepth, PlaneDimension);
        var live = new ushort[width * depth];
        for (var z = 0; z < depth; z++)
        {
            Array.Copy(plane, z * PlaneDimension, live, z * width, width);
        }

        return live;
    }

    private static int CellIndex(int column, int row)
    {
        if ((uint)column >= PlaneDimension || (uint)row >= PlaneDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(column), $"({column}, {row}) is outside the {PlaneDimension}x{PlaneDimension} plane.");
        }

        return column + row * PlaneDimension;
    }

    private static byte[] PromoteBy2(byte[] sixBit)
    {
        var rgb = new byte[sixBit.Length];
        for (var i = 0; i < sixBit.Length; i++)
        {
            rgb[i] = (byte)(sixBit[i] << 2);
        }

        return rgb;
    }

    private static string ReadNulTerminated(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }
}
