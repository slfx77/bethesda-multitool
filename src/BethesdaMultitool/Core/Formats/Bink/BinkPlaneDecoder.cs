// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' shipped decoder binkw32.dll
// (Fallout Tactics, md5 ecbd8213e89f8afde368f8eb05ff5a9c), via our Ghidra decompilation at
// tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt and our own capstone disassembly of the
// same file, as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification"), sections
//   4.5 and 6 (FUN_30008AB0 at 0x30008AB0, the plane decoder).
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box, comparing output pixels.

namespace BethesdaMultitool.Core.Formats.Bink;

/// <summary>
///     Decodes one plane of one frame: reads the plane's 23 Huffman tree headers, then walks the
///     8x8 block grid a row at a time, refilling all nine bundles at the head of each row.
///     <para>
///         One instance is bound to one plane GEOMETRY and is reused for every frame, because the
///         bundles are sized from the plane width and would otherwise be reallocated per plane.
///     </para>
/// </summary>
internal sealed class BinkPlaneDecoder
{
    private readonly BinkBundle[] _bundles;
    private readonly BinkHuffmanTree[] _colourContextTrees = new BinkHuffmanTree[16];
    private readonly int[] _idct = new int[64];
    private readonly int[] _natural = new int[64];
    private readonly byte[] _prediction = new byte[64];
    private readonly sbyte[] _residual = new sbyte[64];
    private readonly byte[] _scratch = new byte[64];
    private readonly BinkBlockTransform _transform = new();

    private int _colourContext;

    internal BinkPlaneDecoder(int planeWidth)
    {
        _bundles = BinkBundle.CreateForPlane(planeWidth);
    }

    /// <summary>Per-block-type occurrence counts since the last <see cref="ResetStatistics" />.</summary>
    internal long[] BlockTypeCounts { get; } = new long[12];

    /// <summary>Per-scaled-sub-type occurrence counts since the last <see cref="ResetStatistics" />.</summary>
    internal long[] ScaledSubTypeCounts { get; } = new long[16];

    /// <summary>Clears the coverage counters.</summary>
    internal void ResetStatistics()
    {
        Array.Clear(BlockTypeCounts);
        Array.Clear(ScaledSubTypeCounts);
    }

    /// <summary>
    ///     Decodes one plane into <paramref name="current" />, reading <paramref name="previous" />
    ///     for the motion-compensated block types. The reader is repositioned to a word boundary
    ///     first — every plane restarts the bit cache.
    /// </summary>
    internal void Decode(BinkBitReader reader, BinkPlane current, BinkPlane previous)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        reader.ResetToWordBoundary();
        foreach (var bundle in _bundles)
        {
            bundle.Reset();
        }

        ReadTreeHeaders(reader);

        for (var y = 0; y < current.Height; y += 8)
        {
            foreach (var bundle in _bundles)
            {
                bundle.Refill(reader, _colourContextTrees, ref _colourContext);
            }

            var x = 0;
            while (x < current.Width)
            {
                var blockType = _bundles[(int)BinkBundleKind.BlockTypes].Next();
                if (blockType < BlockTypeCounts.Length)
                {
                    BlockTypeCounts[blockType]++;
                }

                if (blockType == 1)
                {
                    DecodeScaledBlock(reader, current, x, y);

                    // A SCALED block covers 16x16 and consumes a second x slot.
                    x += 16;
                    continue;
                }

                DecodeBlock(reader, current, previous, blockType, x, y);
                x += 8;
            }
        }
    }

    /// <summary>
    ///     Exactly 23 trees, in this order: bundle 0, bundle 1, the SIXTEEN colour context trees,
    ///     bundle 2's own tree, then bundles 3, 4, 5 and 8. Bundles 6 and 7 (the DC pairs) have no
    ///     tree at all. The colour context is reset with the context trees.
    /// </summary>
    private void ReadTreeHeaders(BinkBitReader reader)
    {
        _bundles[(int)BinkBundleKind.BlockTypes].Tree = BinkHuffmanTree.Read(reader);
        _bundles[(int)BinkBundleKind.SubBlockTypes].Tree = BinkHuffmanTree.Read(reader);

        for (var i = 0; i < 16; i++)
        {
            _colourContextTrees[i] = BinkHuffmanTree.Read(reader);
        }

        _colourContext = 0;
        _bundles[(int)BinkBundleKind.Colours].Tree = BinkHuffmanTree.Read(reader);
        _bundles[(int)BinkBundleKind.Pattern].Tree = BinkHuffmanTree.Read(reader);
        _bundles[(int)BinkBundleKind.XOffset].Tree = BinkHuffmanTree.Read(reader);
        _bundles[(int)BinkBundleKind.YOffset].Tree = BinkHuffmanTree.Read(reader);
        _bundles[(int)BinkBundleKind.Run].Tree = BinkHuffmanTree.Read(reader);
    }

    private void DecodeBlock(
        BinkBitReader reader, BinkPlane current, BinkPlane previous, int blockType, int x, int y)
    {
        switch (blockType)
        {
            case 0:
                CopyReference(current, previous, x, y, 0, 0);
                break;
            case 2:
            {
                var dx = _bundles[(int)BinkBundleKind.XOffset].Next();
                var dy = _bundles[(int)BinkBundleKind.YOffset].Next();
                CopyReference(current, previous, x, y, dx, dy);
                break;
            }

            case 3:
                DecodeRun(reader);
                StoreScratch(current, x, y);
                break;
            case 4:
                DecodeResidue(reader, current, previous, x, y);
                break;
            case 5:
                DecodeIntra(reader, current, x, y);
                break;
            case 6:
            {
                var colour = (byte)_bundles[(int)BinkBundleKind.Colours].Next();
                Array.Fill(_scratch, colour);
                StoreScratch(current, x, y);
                break;
            }

            case 7:
                DecodeInter(reader, current, previous, x, y);
                break;
            case 8:
                DecodePattern();
                StoreScratch(current, x, y);
                break;
            case 9:
            {
                var colours = _bundles[(int)BinkBundleKind.Colours];
                for (var i = 0; i < 64; i++)
                {
                    _scratch[i] = (byte)colours.Next();
                }

                StoreScratch(current, x, y);
                break;
            }
        }
    }

    /// <summary>
    ///     A SCALED block: one 16x16 area coded as an 8x8 that is doubled in both axes. Its sub-type
    ///     is read only on EVEN block rows; on the odd row the 16x16 written by the row above
    ///     already covered these pixels, so the block consumes nothing but its own type element.
    /// </summary>
    private void DecodeScaledBlock(BinkBitReader reader, BinkPlane current, int x, int y)
    {
        if ((y & 8) != 0)
        {
            return;
        }

        var subType = _bundles[(int)BinkBundleKind.SubBlockTypes].Next();
        if (subType < ScaledSubTypeCounts.Length)
        {
            ScaledSubTypeCounts[subType]++;
        }

        switch (subType)
        {
            case 3:
                DecodeRun(reader);
                StoreScratchDoubled(current, x, y);
                break;
            case 5:
            {
                var dc = _bundles[(int)BinkBundleKind.IntraDc].Next();
                _transform.ReadCoefficients(reader, dc, _natural);
                var quantiser = (int)reader.Read(4);
                _transform.InverseDct(_natural, BinkTables.IntraDequantisers[quantiser], _idct);
                for (var i = 0; i < 64; i++)
                {
                    _scratch[i] = (byte)_idct[i];
                }

                StoreScratchDoubled(current, x, y);
                break;
            }

            case 6:
            {
                var colour = (byte)_bundles[(int)BinkBundleKind.Colours].Next();
                Array.Fill(_scratch, colour);
                StoreScratchDoubled(current, x, y);
                break;
            }

            case 8:
                DecodePattern();
                StoreScratchDoubled(current, x, y);
                break;
            case 9:
            {
                var colours = _bundles[(int)BinkBundleKind.Colours];
                for (var i = 0; i < 64; i++)
                {
                    _scratch[i] = (byte)colours.Next();
                }

                StoreScratchDoubled(current, x, y);
                break;
            }
        }
    }

    /// <summary>
    ///     Block type 3: a run fill in one of the 16 scan orders. The loop condition is checked only
    ///     BETWEEN runs, so a run may carry the cursor past 63; a well-formed stream lands on
    ///     exactly 63 or exactly 64, and the single trailing pixel is read only in the first case.
    /// </summary>
    private void DecodeRun(BinkBitReader reader)
    {
        var scan = BinkTables.ScanOrders[(int)reader.Read(4)];
        var colours = _bundles[(int)BinkBundleKind.Colours];
        var runs = _bundles[(int)BinkBundleKind.Run];

        var i = 0;
        while (i < 63)
        {
            if (!reader.ReadFlag())
            {
                var length = runs.Next() + 1;
                for (var n = 0; n < length; n++)
                {
                    // The element is consumed even when the run overshoots the block, because the
                    // DLL consumes it; clipping the WRITE keeps us in bounds without desyncing.
                    var colour = (byte)colours.Next();
                    if (i < 64)
                    {
                        _scratch[scan[i]] = colour;
                    }

                    i++;
                }
            }
            else
            {
                var colour = (byte)colours.Next();
                var length = runs.Next() + 1;
                for (var n = 0; n < length; n++)
                {
                    if (i < 64)
                    {
                        _scratch[scan[i]] = colour;
                    }

                    i++;
                }
            }
        }

        if (i == 63)
        {
            _scratch[scan[63]] = (byte)colours.Next();
        }
    }

    /// <summary>Block type 8: two colours and eight pattern bytes, bit <c>c</c> selecting colour 2.</summary>
    private void DecodePattern()
    {
        var colours = _bundles[(int)BinkBundleKind.Colours];
        var patterns = _bundles[(int)BinkBundleKind.Pattern];
        var colour1 = (byte)colours.Next();
        var colour2 = (byte)colours.Next();
        for (var r = 0; r < 8; r++)
        {
            var pattern = patterns.Next();
            for (var c = 0; c < 8; c++)
            {
                _scratch[r * 8 + c] = ((pattern >> c) & 1) != 0 ? colour2 : colour1;
            }
        }
    }

    /// <summary>
    ///     Block type 4: a motion-compensated copy plus an additive 8-bit residual. The add is
    ///     modular 8-bit arithmetic with NO clamp — that is what the DLL does.
    /// </summary>
    private void DecodeResidue(
        BinkBitReader reader, BinkPlane current, BinkPlane previous, int x, int y)
    {
        var dx = _bundles[(int)BinkBundleKind.XOffset].Next();
        var dy = _bundles[(int)BinkBundleKind.YOffset].Next();
        GatherReference(previous, x, y, dx, dy, _prediction);

        var budget = (int)reader.Read(7);
        _transform.ReadResidual(reader, budget, _residual);

        for (var i = 0; i < 64; i++)
        {
            _scratch[i] = (byte)(_prediction[i] + _residual[i]);
        }

        StoreScratch(current, x, y);
    }

    /// <summary>
    ///     Block type 5: DC from a bundle, then the AC coefficients, and ONLY THEN the 4 quantiser
    ///     bits. Reading the quantiser first desyncs the stream.
    /// </summary>
    private void DecodeIntra(BinkBitReader reader, BinkPlane current, int x, int y)
    {
        var dc = _bundles[(int)BinkBundleKind.IntraDc].Next();
        _transform.ReadCoefficients(reader, dc, _natural);
        var quantiser = (int)reader.Read(4);
        _transform.InverseDct(_natural, BinkTables.IntraDequantisers[quantiser], _idct);

        var pixels = current.Pixels;
        for (var r = 0; r < 8; r++)
        {
            var dst = current.OffsetOf(x, y + r);
            for (var c = 0; c < 8; c++)
            {
                // Truncated to 8 bits with NO saturation (mov [ecx],bl).
                pixels[dst + c] = (byte)_idct[r * 8 + c];
            }
        }
    }

    /// <summary>Block type 7: a motion-compensated prediction with an inverse-DCT residual added.</summary>
    private void DecodeInter(
        BinkBitReader reader, BinkPlane current, BinkPlane previous, int x, int y)
    {
        var dx = _bundles[(int)BinkBundleKind.XOffset].Next();
        var dy = _bundles[(int)BinkBundleKind.YOffset].Next();
        GatherReference(previous, x, y, dx, dy, _prediction);

        var dc = _bundles[(int)BinkBundleKind.InterDc].Next();
        _transform.ReadCoefficients(reader, dc, _natural);
        var quantiser = (int)reader.Read(4);
        _transform.InverseDct(_natural, BinkTables.InterDequantisers[quantiser], _idct);

        var pixels = current.Pixels;
        for (var r = 0; r < 8; r++)
        {
            var dst = current.OffsetOf(x, y + r);
            for (var c = 0; c < 8; c++)
            {
                pixels[dst + c] = (byte)(_prediction[r * 8 + c] + _idct[r * 8 + c]);
            }
        }
    }

    private void CopyReference(BinkPlane current, BinkPlane previous, int x, int y, int dx, int dy)
    {
        GatherReference(previous, x, y, dx, dy, _prediction);
        _prediction.CopyTo(_scratch, 0);
        StoreScratch(current, x, y);
    }

    /// <summary>
    ///     Gathers the 8x8 at <c>(x + dx, y + dy)</c> from the reference plane.
    ///     <para>
    ///         ⚠ The DLL clamps nothing — it reads inside its own padded allocation. Across 312,425
    ///         motion blocks in our corpus NO vector left the plane, so the correct behaviour for a
    ///         stream that has one is unconstrained by any measurement. We clamp the source
    ///         coordinate rather than read out of bounds, and say so: it is a safety choice on a
    ///         path no retail movie takes.
    ///     </para>
    /// </summary>
    private static void GatherReference(
        BinkPlane previous, int x, int y, int dx, int dy, byte[] destination)
    {
        var sourceX = x + dx;
        var sourceY = y + dy;
        var pixels = previous.Pixels;

        if (sourceX >= 0 && sourceY >= 0
                         && sourceX + 8 <= previous.Width && sourceY + 8 <= previous.Height)
        {
            for (var r = 0; r < 8; r++)
            {
                Array.Copy(pixels, previous.OffsetOf(sourceX, sourceY + r), destination, r * 8, 8);
            }

            return;
        }

        for (var r = 0; r < 8; r++)
        {
            var row = Math.Clamp(sourceY + r, 0, previous.Height - 1);
            for (var c = 0; c < 8; c++)
            {
                var column = Math.Clamp(sourceX + c, 0, previous.Width - 1);
                destination[r * 8 + c] = pixels[previous.OffsetOf(column, row)];
            }
        }
    }

    private void StoreScratch(BinkPlane current, int x, int y)
    {
        for (var r = 0; r < 8; r++)
        {
            Array.Copy(_scratch, r * 8, current.Pixels, current.OffsetOf(x, y + r), 8);
        }
    }

    /// <summary>Writes the 8x8 scratch into a 16x16 area, every pixel and every row duplicated.</summary>
    private void StoreScratchDoubled(BinkPlane current, int x, int y)
    {
        var pixels = current.Pixels;
        for (var r = 0; r < 8; r++)
        {
            var top = current.OffsetOf(x, y + r * 2);
            var bottom = top + current.Stride;
            for (var c = 0; c < 8; c++)
            {
                var value = _scratch[r * 8 + c];
                pixels[top + c * 2] = value;
                pixels[top + c * 2 + 1] = value;
                pixels[bottom + c * 2] = value;
                pixels[bottom + c * 2 + 1] = value;
            }
        }
    }
}
