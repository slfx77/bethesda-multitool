using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     An independent writer of Shadowkey (N-Gage, little-endian) test inputs for the cut-2 model readers: mesh records
///     (static, animated, multi-skin, counted and uncounted texture headers), <c>models.idx</c>/<c>models.huge</c>/
///     <c>models.txt</c> packs, and whole zone file sets. It restates the layouts from the cut-2 plan and shares no code
///     with the decoders under test (<c>Core/Formats/Travels/Shadowkey</c>).
/// </summary>
/// <remarks>
///     <see cref="GoldenAnimated" /> and <see cref="GoldenStatic" /> write byte for byte what the Python oracle's
///     <c>golden_animated</c> and <c>golden_static</c> write (<c>tools/scripts/gate2/shadowkey_cover.py</c>), so the default
///     suite can pin the oracle's digests of the same bytes.
/// </remarks>
internal static class ShadowkeyTestBuilder
{
    /// <summary>The golden records' frame-0 positions: four vertices, (x, y, z) each.</summary>
    public static readonly short[] GoldenFrame0 = [0, 0, 0, 256, 0, 0, 256, 512, 0, 0, 512, -128];

    /// <summary>The golden frame 1: frame 0 plus (3, -7, 11) per vertex.</summary>
    public static readonly short[] GoldenFrame1 = [3, -7, 11, 259, -7, 11, 259, 505, 11, 3, 505, -117];

    /// <summary>The golden UVs: five pairs, vertex 0 owning UVs 0 and 4.</summary>
    public static readonly ushort[] GoldenUvs = [0, 0, 512, 0, 512, 512, 0, 512, 1024, 256];

    /// <summary>The golden faces: (v0, v1, v2, t0, t1, t2) twice.</summary>
    public static readonly ushort[] GoldenFaces = [0, 1, 2, 0, 1, 2, 0, 2, 3, 4, 2, 3];

    /// <summary>The golden first skin (2x2, the last texel magenta).</summary>
    public static readonly ushort[] GoldenSkin0 = [0x0F00, 0x00F0, 0x000F, 0x0F0F];

    /// <summary>The golden second skin (2x2).</summary>
    public static readonly ushort[] GoldenSkin1 = [0x0123, 0x0456, 0x0789, 0x0ABC];

    /// <summary>
    ///     The Python oracle's <c>golden_animated</c>: two frames, four vertices, five UVs, two faces, two 2x2 skins,
    ///     sequences (0, 1, 10) and (1, 2, 3).
    /// </summary>
    public static byte[] GoldenAnimated()
    {
        return new Record
        {
            Frames = 2,
            Positions = [.. GoldenFrame0, .. GoldenFrame1],
            Uvs = GoldenUvs,
            Faces = GoldenFaces,
            Skins = 2,
            Width = 2,
            Height = 2,
            Texels = [.. GoldenSkin0, .. GoldenSkin1],
            Sequences = [(0, 1, 10), (1, 2, 3)]
        }.Build();
    }

    /// <summary>The Python oracle's <c>golden_static</c>: one frame of the same geometry, the second skin, (0, 1, 1).</summary>
    public static byte[] GoldenStatic()
    {
        return new Record
        {
            Frames = 1,
            Positions = GoldenFrame0,
            Uvs = GoldenUvs,
            Faces = GoldenFaces,
            Skins = 1,
            Width = 2,
            Height = 2,
            Texels = GoldenSkin1,
            Sequences = [(0, 1, 1)]
        }.Build();
    }

    /// <summary>
    ///     Writes <c>models.idx</c>, <c>models.huge</c> and <c>models.txt</c> for the given records (an empty array is an
    ///     empty slot); <paramref name="names" /> defaults to <c>slotN.bin</c>.
    /// </summary>
    public static (byte[] Index, byte[] Pack, string Names) Pack(IReadOnlyList<byte[]> records,
        IReadOnlyList<string>? names = null)
    {
        var index = new byte[4 + 8 * records.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(index, (uint)records.Count);
        using var pack = new MemoryStream();
        var text = new StringBuilder();
        for (var slot = 0; slot < records.Count; slot++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(4 + 8 * slot), (uint)pack.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(8 + 8 * slot), (uint)records[slot].Length);
            pack.Write(records[slot]);
            var name = names?[slot] ?? string.Create(CultureInfo.InvariantCulture, $"slot{slot}.bin");
            text.Append(CultureInfo.InvariantCulture, $"{slot} 0 64 64 {name}\r\n");
        }

        return (index, pack.ToArray(), text.ToString());
    }

    /// <summary>A compressed zone file: the u32 inflated length, then one zlib stream.</summary>
    public static byte[] Envelope(byte[] payload)
    {
        using var output = new MemoryStream();
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
        output.Write(length);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(payload);
        }

        return output.ToArray();
    }

    /// <summary>A fixed-width text field: the Latin-1 text, a NUL, then <paramref name="fill" /> bytes to the width.</summary>
    public static byte[] Field(string text, int width, byte fill = 0)
    {
        var field = new byte[width];
        Array.Fill(field, fill);
        var bytes = Encoding.Latin1.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, width)).CopyTo(field);
        if (bytes.Length < width)
        {
            field[bytes.Length] = 0;
        }

        return field;
    }

    /// <summary>One mesh record, written field by field (every property may be set to a broken value).</summary>
    internal sealed class Record
    {
        /// <summary>Header word 0 (7 on retail).</summary>
        public ushort Tag { get; init; } = 7;

        /// <summary>The frame count.</summary>
        public int Frames { get; init; } = 1;

        /// <summary>Frame-major positions, (x, y, z) per vertex per frame.</summary>
        public short[] Positions { get; init; } = [];

        /// <summary>UV pairs (8.8 texels).</summary>
        public ushort[] Uvs { get; init; } = [];

        /// <summary>Faces, (v0, v1, v2, t0, t1, t2) each.</summary>
        public ushort[] Faces { get; init; } = [];

        /// <summary>The skin count (written only by the counted header).</summary>
        public int Skins { get; init; } = 1;

        /// <summary>The texture width.</summary>
        public int Width { get; init; } = 2;

        /// <summary>The texture height.</summary>
        public int Height { get; init; } = 2;

        /// <summary>Every skin's texels, skin-major; defaults to a ramp.</summary>
        public ushort[]? Texels { get; init; }

        /// <summary>The sequences.</summary>
        public (int Start, int End, int Rate)[] Sequences { get; init; } = [(0, 1, 1)];

        /// <summary>True for the counted texture header (every pack record), false for the interior sky's uncounted one.</summary>
        public bool Counted { get; init; } = true;

        /// <summary>Header word 5; null writes 3 x vertices.</summary>
        public int? CoordinateCount { get; init; }

        /// <summary>Header word 6 (1 on retail).</summary>
        public ushort Trailer { get; init; } = 1;

        /// <summary>Bytes appended after the sequence table.</summary>
        public int TrailingBytes { get; init; }

        /// <summary>The vertex count the positions imply.</summary>
        public int Vertices => Frames == 0 ? 0 : Positions.Length / (3 * Frames);

        /// <summary>Writes the record.</summary>
        public byte[] Build()
        {
            using var output = new MemoryStream();
            void U16(int value)
            {
                Span<byte> word = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)value);
                output.Write(word);
            }

            U16(Tag);
            U16(Frames);
            U16(Vertices);
            U16(Uvs.Length / 2);
            U16(Faces.Length / 6);
            U16(CoordinateCount ?? 3 * Vertices);
            U16(Trailer);
            foreach (var value in Positions)
            {
                U16(value);
            }

            foreach (var value in Uvs)
            {
                U16(value);
            }

            foreach (var value in Faces)
            {
                U16(value);
            }

            if (Counted)
            {
                U16(Skins);
            }

            U16(Width);
            U16(Height);
            var texels = Texels ?? Enumerable.Range(0, (Counted ? Skins : 1) * Width * Height)
                .Select(static k => (ushort)(0x0F00 + (k & 0xFF))).ToArray();
            foreach (var texel in texels)
            {
                U16(texel);
            }

            U16(Sequences.Length);
            foreach (var (start, end, rate) in Sequences)
            {
                U16(start);
                U16(end);
                U16(rate);
            }

            output.Write(new byte[TrailingBytes]);
            return output.ToArray();
        }
    }

    /// <summary>One zone <c>.zcp</c> prototype.</summary>
    /// <param name="Floor">The four floor corner heights (8.8), slot order.</param>
    /// <param name="Ceiling">The four ceiling corner heights (8.8), slot order.</param>
    internal sealed record Prototype(short[] Floor, short[] Ceiling)
    {
        /// <summary>A flat room: floor 0, ceiling 0x0400 (4.0 tiles).</summary>
        public static Prototype Flat { get; } = new([0, 0, 0, 0], [0x400, 0x400, 0x400, 0x400]);

        /// <summary>The 36-byte record: shade, 0xCD, reference floor and ceiling, heights, 8 slots 0xFF, 4 edges, extra.</summary>
        public byte[] Build()
        {
            var record = new byte[36];
            record[0] = 3;
            record[1] = 0xCD;
            BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(2), Floor[0]);
            BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(4), Ceiling[0]);
            for (var i = 0; i < 4; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(6 + i * 2), Floor[i]);
                BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(14 + i * 2), Ceiling[i]);
            }

            record.AsSpan(22, 8).Fill(0xFF);
            record[30] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(34), 7);
            return record;
        }
    }

    /// <summary>One <c>.ent</c> placement.</summary>
    /// <param name="X">Raw 24.8 x.</param>
    /// <param name="Y">Raw 24.8 y.</param>
    /// <param name="Z">Raw 24.8 height.</param>
    /// <param name="Angle2">The yaw slot (65,536 per turn).</param>
    /// <param name="Scale">The raw 8.8 scale.</param>
    /// <param name="EntityId">The <c>entities.txt</c> id.</param>
    /// <param name="Angle0">Angle slot 0.</param>
    /// <param name="Angle1">Angle slot 1.</param>
    internal sealed record Placement(int X, int Y, int Z, int Angle2, ushort Scale, uint EntityId, int Angle0 = 0,
        int Angle1 = 0)
    {
        /// <summary>The 72-byte record: position, angles, scale, the 0xCCCC hole, the id, the name and the script.</summary>
        public byte[] Build(string name)
        {
            var record = new byte[72];
            BinaryPrimitives.WriteInt32LittleEndian(record, X);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), Y);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), Z);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), Angle0);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), Angle1);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), Angle2);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(24), Scale);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(26), 0xCCCC);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), EntityId);
            Field(name, 8, 0xCC).CopyTo(record.AsSpan(32));
            Field("script", 32, 0xCC).CopyTo(record.AsSpan(40));
            return record;
        }
    }

    /// <summary>
    ///     A whole zone file set (see <see cref="Build" />): the grid, its prototypes, the sky, one 128x128 texture per
    ///     surface texture index, the light and fog tables, the placements and the small files, plus the zone's model
    ///     list, <c>entities.txt</c> and the pack.
    /// </summary>
    internal sealed class Zone
    {
        /// <summary>The zone stem (every file is named after it).</summary>
        public string Stem { get; init; } = "testzone";

        /// <summary>The zone name the <c>.zmp</c> header carries; null writes the stem.</summary>
        public string? ZoneName { get; init; }

        /// <summary>The grid width.</summary>
        public int Width { get; init; } = 2;

        /// <summary>The grid height.</summary>
        public int Height { get; init; } = 2;

        /// <summary>Per cell (row-major): (flags, prototype index).</summary>
        public (byte Flags, ushort Prototype)[]? Cells { get; init; }

        /// <summary>The prototypes.</summary>
        public Prototype[] Prototypes { get; init; } = [Prototype.Flat];

        /// <summary>The sky payload (a mesh record); null writes a one-frame counted sky of a 2x2 skin.</summary>
        public byte[]? Sky { get; init; }

        /// <summary>The number of <c>.ztx</c> textures.</summary>
        public int Textures { get; init; } = 2;

        /// <summary>The <c>.sur</c> rows' texture indices.</summary>
        public byte[] SurfaceTextures { get; init; } = [0, 1];

        /// <summary>The 768 palette bytes; null writes a gray ramp with magenta at entry 6.</summary>
        public byte[]? Palette { get; init; }

        /// <summary>Whether the light table pins the palette's magenta entry to 0x0F0F at every bank and level.</summary>
        public bool LightTablePinsKey { get; init; } = true;

        /// <summary>The placements.</summary>
        public Placement[] Placements { get; init; } = [];

        /// <summary>The pack's records (slot order; an empty array is an empty slot).</summary>
        public byte[][] Records { get; init; } = [GoldenStatic()];

        /// <summary>The <c>entities.txt</c> rows: (id, model index).</summary>
        public (uint Id, int Model)[] Entities { get; init; } = [(100, 0)];

        /// <summary>Slots the zone's model list blanks to <c>NULL.bin</c>.</summary>
        public int[] NotResident { get; init; } = [];

        /// <summary>Whether to write a <c>.sta</c> (the <c>.ent</c> records' 32-byte prefixes).</summary>
        public bool WithSta { get; init; }

        /// <summary>File families to leave out (for the missing-companion control).</summary>
        public string[] Omit { get; init; } = [];

        /// <summary>The default palette: a gray ramp with magenta at entry 6.</summary>
        public static byte[] DefaultPalette()
        {
            var palette = new byte[768];
            for (var i = 0; i < 256; i++)
            {
                palette[i * 3] = (byte)i;
                palette[i * 3 + 1] = (byte)(255 - i);
                palette[i * 3 + 2] = (byte)(i / 2);
            }

            palette[18] = 0xFF;
            palette[19] = 0x00;
            palette[20] = 0xFF;
            return palette;
        }

        /// <summary>The default sky: one frame, four vertices, a counted 2x2 skin, sequence (0, 1, 1).</summary>
        public static byte[] DefaultSky(bool counted = true)
        {
            return new Record
            {
                Positions = GoldenFrame0, Uvs = GoldenUvs, Faces = GoldenFaces, Texels = GoldenSkin1, Counted = counted
            }.Build();
        }

        /// <summary>Every file of the set by name.</summary>
        public Dictionary<string, byte[]> Build()
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var cells = Cells ?? Enumerable.Repeat(((byte)0, (ushort)0), Width * Height).ToArray();
            var zmp = new byte[132 + 6 * cells.Length];
            Field(ZoneName ?? Stem, 32, 0xCD).CopyTo(zmp, 0);
            Field("No Auth", 32, 0xCD).CopyTo(zmp, 32);
            Field("Nondescript", 64, 0xCD).CopyTo(zmp, 64);
            BinaryPrimitives.WriteUInt16LittleEndian(zmp.AsSpan(128), (ushort)Width);
            BinaryPrimitives.WriteUInt16LittleEndian(zmp.AsSpan(130), (ushort)Height);
            for (var i = 0; i < cells.Length; i++)
            {
                zmp[132 + i * 6] = cells[i].Flags;
                BinaryPrimitives.WriteUInt16LittleEndian(zmp.AsSpan(132 + i * 6 + 4), cells[i].Prototype);
            }

            files[Stem + ".zmp"] = Envelope(zmp);
            var zcp = new List<byte>();
            zcp.AddRange(BitConverter.GetBytes((uint)Prototypes.Length));
            foreach (var prototype in Prototypes)
            {
                zcp.AddRange(prototype.Build());
            }

            files[Stem + ".zcp"] = Envelope([.. zcp]);
            files[Stem + ".zsk"] = Envelope(Sky ?? DefaultSky());
            var ztx = new byte[1 + Textures * 16384];
            ztx[0] = (byte)Textures;
            for (var i = 1; i < ztx.Length; i++)
            {
                ztx[i] = (byte)((i - 1) / 128 % 256);
            }

            files[Stem + ".ztx"] = Envelope(ztx);
            var palette = Palette ?? DefaultPalette();
            files[Stem + ".pal"] = palette;
            var zlu = new byte[131072];
            for (var i = 0; i < 65536; i++)
            {
                var entry = i % 256;
                var value = entry == 6 && LightTablePinsKey && IsMagenta(palette, 6) ? 0x0F0F : (entry & 0x0FF);
                BinaryPrimitives.WriteUInt16LittleEndian(zlu.AsSpan(i * 2), (ushort)value);
            }

            files[Stem + ".zlu"] = Envelope(zlu);
            var zfg = new byte[131072];
            for (var colour = 0; colour < 4096; colour++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(zfg.AsSpan(colour * 2), (ushort)colour);
            }

            files[Stem + ".zfg"] = Envelope(zfg);
            var ent = new List<byte>();
            ent.AddRange(BitConverter.GetBytes((uint)Placements.Length));
            for (var i = 0; i < Placements.Length; i++)
            {
                ent.AddRange(Placements[i].Build(string.Create(CultureInfo.InvariantCulture, $"p{i}")));
            }

            files[Stem + ".ent"] = [.. ent];
            if (WithSta)
            {
                var sta = new List<byte>();
                sta.AddRange(BitConverter.GetBytes((uint)Placements.Length));
                foreach (var placement in Placements)
                {
                    sta.AddRange(placement.Build("sta").AsSpan(0, 32).ToArray());
                }

                files[Stem + ".sta"] = [.. sta];
            }

            var sur = new List<byte> { (byte)SurfaceTextures.Length };
            foreach (var texture in SurfaceTextures)
            {
                sur.AddRange([7, 7, 0, 0, 0, 0, 0, texture]);
            }

            files[Stem + ".sur"] = [.. sur];
            var zon = new List<byte> { 1, 0, 0, 0, 0, 0, 1, 0, 1, 0 };
            zon.AddRange(Field("trigger", 64, 0xCC));
            files[Stem + ".zon"] = [.. zon];
            files[Stem + ".pth"] = [0, 0];
            files[Stem + ".stn"] = [0, 0];

            var models = new StringBuilder();
            var names = new List<string>();
            for (var slot = 0; slot < Records.Length; slot++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"slot{slot}.bin");
                names.Add(name);
                var line = NotResident.Contains(slot) || Records[slot].Length == 0 ? "0 0 0 NULL.bin" : "0 64 64 " + name;
                models.Append(CultureInfo.InvariantCulture, $"{slot} {line}\r\n");
            }

            files[Stem + "_models.txt"] = Encoding.Latin1.GetBytes(models.ToString());
            var entities = new StringBuilder();
            foreach (var (id, model) in Entities)
            {
                entities.Append(CultureInfo.InvariantCulture, $"{id}\t{model}  1 !thing\r\n");
            }

            files["entities.txt"] = Encoding.Latin1.GetBytes(entities.ToString());
            var (index, pack, text) = Pack(Records, names);
            files["models.idx"] = index;
            files["models.huge"] = pack;
            files["models.txt"] = Encoding.Latin1.GetBytes(text);
            foreach (var omit in Omit)
            {
                files.Remove(omit.StartsWith('.') ? Stem + omit : omit);
            }

            return files;
        }

        private static bool IsMagenta(byte[] palette, int entry)
        {
            return palette[entry * 3] == 0xFF && palette[entry * 3 + 1] == 0 && palette[entry * 3 + 2] == 0xFF;
        }
    }
}
