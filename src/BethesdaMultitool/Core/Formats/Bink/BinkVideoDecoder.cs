// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' shipped decoder binkw32.dll
// (Fallout Tactics, md5 ecbd8213e89f8afde368f8eb05ff5a9c), via our Ghidra decompilation at
// tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt and our own capstone disassembly of the
// same file, as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification"), sections
//   2 and 6 (FUN_30008930 at 0x30008930, _BinkDoFrame@4 at 0x30005740).
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box: run as an executable,
// with its output pixels compared to ours.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Bink;

/// <summary>
///     Decodes a <c>BIKi</c> movie's frames. Two frame buffers are kept and swapped each frame; the
///     motion-compensated block types read the PREVIOUS frame's matching plane, so decoding is
///     inherently sequential and a seek replays from the preceding keyframe.
///     <para>
///         ⚠⚠ The plane order inside a frame is Y, then V (Cr), then U (Cb) — YV12, not I420.
///         Getting it backwards is invisible on the many corpus frames whose chroma is flat 128 and
///         wrong on every frame that has colour.
///     </para>
///     <para>
///         ⚠ Only the alpha and Y planes carry a length prefix, and that prefix counts from the
///         length FIELD, not from the data after it. The two chroma planes have none: the first
///         starts at the byte the Y length points to, the second wherever the first stopped.
///     </para>
///     <para>
///         WHERE WE ARE KNOWINGLY NOT BIT-IDENTICAL TO RAD. All eight are on paths NO retail file
///         in the 59-file / 109,985-frame corpus takes, so none is measurable against the DLL and
///         each is a deliberate choice, not a finding. Two are different PIXELS on malformed or
///         unusual input; six are "refuse instead of corrupt", where the DLL would read or write
///         outside what it declared and we throw:
///         <list type="number">
///             <item>
///                 Out-of-plane motion vectors. <c>BinkPlaneDecoder.GatherReference</c> CLAMPS the
///                 source coordinate; the DLL clamps nothing and reads into its own padding.
///                 0 of 312,425 corpus vectors leave the plane.
///             </item>
///             <item>
///                 Scaled-block overspill past the decoded plane height. We pad every plane with 16
///                 slack rows and columns; the DLL spills into the next plane of one frame
///                 allocation. Indistinguishable on real data.
///             </item>
///             <item>
///                 This decoder throws when the Y plane does not land on its declared length; the
///                 DLL carries on.
///             </item>
///             <item>
///                 <c>BinkBundle.Next</c> throws when a bundle is asked for an element past its
///                 refill.
///             </item>
///             <item>
///                 <c>BinkBundle.Refill</c> throws when a declared count exceeds the bundle's
///                 capacity.
///             </item>
///             <item>
///                 <c>BinkHuffmanTree.ReadExplicitSymbolList</c> throws on a repeated symbol,
///                 where the DLL would run off the end of its 16-byte map.
///             </item>
///             <item><c>BinkBlockTransform.Append</c> throws on work-list overflow.</item>
///             <item><c>BinkBlockTransform.PushFront</c> throws on work-list underflow.</item>
///         </list>
///         Block types 10/11 and scaled sub-types 4/7 are NOT in this list: those are no-ops in the
///         DLL too (its jump tables send them to a default that draws nothing), so leaving the
///         destination alone matches it. Nor are the alpha (0x100000) and grayscale (0x20000)
///         flags and the <c>BIKf</c>/<c>BIKg</c>/<c>BIKh</c> codings: those are refused by the
///         constructor before a single plane is decoded, not decoded differently, and no corpus
///         file has any of them. There is no alpha-plane buffer in this class at all — an earlier
///         one was never filled, and <see cref="BinkColorConverter.ToRgba" /> takes no alpha.
///     </para>
///     <para>
///         CONTAINER-LEVEL refusals, for completeness — these sit in front of the list above and
///         are the whole set of remaining <c>throw</c> sites that a FILE (as opposed to a caller)
///         can reach. Two reproduce the DLL's own rejections, with its own strings from
///         <c>_BinkOpen@8</c> (<c>"Not a Bink file."</c> at 0x30033450, <c>"The file doesn't
///         contain any compressed frames yet."</c> at 0x3003341C). The other six are ours: a file
///         shorter than the 0x2C header, a frame-offset table running past the end of the file
///         (both in <see cref="BinkFile.Parse" />); an audio packet header or body running past its
///         frame (<see cref="BinkFile.VideoStart" />); and a frame with no room for the Y length
///         word, or a Y length that leaves the frame (<see cref="DecodeOne" />). What the DLL does
///         on those six was NOT measured — it reads from a buffer it sized from the header, so the
///         honest statement is "we refuse, it does something else", and every one of the 109,985
///         retail frames passes all eight.
///     </para>
/// </summary>
internal sealed class BinkVideoDecoder
{
    private readonly BinkPlaneDecoder _chromaDecoder;
    private readonly BinkFile _file;
    private readonly BinkPlane[] _luma = new BinkPlane[2];
    private readonly BinkPlaneDecoder _lumaDecoder;
    private readonly BinkPlane[] _u = new BinkPlane[2];
    private readonly BinkPlane[] _v = new BinkPlane[2];

    private int _current;
    private int _decodedFrame = -1;

    internal BinkVideoDecoder(BinkFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!file.IsDecodableVideo)
        {
            throw new NotSupportedException(
                $"{file.Name}: only BIKi video without the alpha (0x100000) or grayscale (0x20000) " +
                $"flags is implemented; this file is {file.Magic} with flags 0x{file.Flags:X8}.");
        }

        _file = file;

        // Plane geometry, from _BinkOpen@8 and FUN_30008930. The luma plane's DECODED region is
        // align8 of the picture, which can be taller than the picture itself: 720x486 decodes
        // 720x488 luma and the two extra rows are simply not displayed.
        var chromaWidth = BinkPlane.Align8((file.Width + 1) >> 1);
        var chromaHeight = BinkPlane.Align8((file.Height + 1) >> 1);
        var lumaWidth = BinkPlane.Align8(file.Width);
        var lumaHeight = BinkPlane.Align8(file.Height);

        for (var i = 0; i < 2; i++)
        {
            _luma[i] = new BinkPlane(lumaWidth, lumaHeight);
            _v[i] = new BinkPlane(chromaWidth, chromaHeight);
            _u[i] = new BinkPlane(chromaWidth, chromaHeight);
        }

        _lumaDecoder = new BinkPlaneDecoder(lumaWidth);
        _chromaDecoder = new BinkPlaneDecoder(chromaWidth);
    }

    internal int Width => _file.Width;

    internal int Height => _file.Height;

    internal int FrameCount => _file.FrameCount;

    /// <summary>
    ///     Y planes whose declared length matched the bit reader's landing point exactly.
    ///     <para>
    ///         ⚠ This counter is a TALLY, not a check. <see cref="DecodeOne" /> throws on a
    ///         mismatch, so it only ever counts successes and asserting on it proves no more than
    ///         "DecodeFrame did not throw". The enforcement is the throw; the number is here so a
    ///         caller can report how many planes were verified. For an invariant that is genuinely
    ///         observable rather than fatal, see <see cref="LastChromaEnd" />.
    ///     </para>
    /// </summary>
    internal int YPlanesLandedExactly { get; private set; }

    /// <summary>
    ///     Byte the last decoded frame's Y plane ended on — the bit reader's landing point, which
    ///     equals the plane's declared length added to the plane's own start.
    /// </summary>
    internal int LastYPlaneEnd { get; private set; }

    /// <summary>
    ///     Byte the last decoded frame's SECOND chroma (U) plane ended on. Unlike the Y plane this
    ///     is NOT declared anywhere and NOT enforced: the chroma planes carry no length prefix, so
    ///     nothing in the bitstream says where they should stop and a caller can compare this with
    ///     <see cref="LastFrameEnd" /> to see how the decode landed against the frame boundary.
    /// </summary>
    internal int LastChromaEnd { get; private set; }

    /// <summary>End of the last decoded frame, from the container's frame-offset table.</summary>
    internal int LastFrameEnd { get; private set; }

    /// <summary>Block-type coverage over every plane decoded so far.</summary>
    internal long[] BlockTypeCounts { get; } = new long[12];

    /// <summary>Scaled sub-type coverage over every plane decoded so far.</summary>
    internal long[] ScaledSubTypeCounts { get; } = new long[16];

    /// <summary>The current frame's luma plane.</summary>
    internal BinkPlane LumaPlane => _luma[_current];

    /// <summary>The current frame's V (Cr) plane.</summary>
    internal BinkPlane ChromaVPlane => _v[_current];

    /// <summary>The current frame's U (Cb) plane.</summary>
    internal BinkPlane ChromaUPlane => _u[_current];

    /// <summary>Decodes frame <paramref name="index" /> and returns it as RGBA.</summary>
    internal byte[] DecodeFrameRgba(int index)
    {
        DecodeFrame(index);
        return BinkColorConverter.ToRgba(LumaPlane, ChromaVPlane, ChromaUPlane, _file.Width, _file.Height);
    }

    /// <summary>
    ///     Decodes frame <paramref name="index" />, replaying from the preceding keyframe when the
    ///     decoder is not already positioned. Sequential playback costs one frame per call.
    /// </summary>
    internal void DecodeFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _file.FrameCount);

        var start = _decodedFrame + 1;
        if (index < start || _decodedFrame < 0)
        {
            start = PrecedingKeyFrame(index);
            Reset();
        }

        for (var frame = start; frame <= index; frame++)
        {
            DecodeOne(frame);
        }
    }

    /// <summary>Clears both frame buffers and forgets the decode position.</summary>
    internal void Reset()
    {
        for (var i = 0; i < 2; i++)
        {
            Array.Clear(_luma[i].Pixels);
            Array.Clear(_v[i].Pixels);
            Array.Clear(_u[i].Pixels);
        }

        _current = 0;
        _decodedFrame = -1;
    }

    /// <summary>Index of the keyframe at or before <paramref name="index" />.</summary>
    internal int PrecedingKeyFrame(int index)
    {
        for (var frame = index; frame > 0; frame--)
        {
            if (_file.IsKeyFrame(frame))
            {
                return frame;
            }
        }

        return 0;
    }

    private void DecodeOne(int index)
    {
        // Swap first, so the buffer we decode into is the one from TWO frames ago — exactly what
        // the DLL's ping-pong leaves for a block type that draws nothing.
        _current ^= 1;
        var previous = _current ^ 1;

        var bytes = _file.Bytes;
        var position = _file.VideoStart(index);
        var frameEnd = _file.FrameEnd(index);
        var reader = new BinkBitReader(bytes, position);

        if (position + 4 > frameEnd)
        {
            throw new InvalidDataException($"{_file.Name}: frame {index} has no video payload.");
        }

        var lumaLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position));
        var lumaEnd = position + lumaLength;
        if (lumaLength < 4 || lumaEnd > frameEnd)
        {
            throw new InvalidDataException(
                $"{_file.Name}: frame {index}'s Y plane length ({lumaLength}) leaves the frame.");
        }

        reader.SeekToByte(position + 4);
        _lumaDecoder.Decode(reader, _luma[_current], _luma[previous]);

        if (reader.WordPointer != lumaEnd)
        {
            throw new InvalidDataException(
                $"{_file.Name}: frame {index}'s Y plane ended at byte {reader.WordPointer} but its " +
                $"length field declares {lumaEnd} — the bitstream desynced inside the plane.");
        }

        YPlanesLandedExactly++;
        LastYPlaneEnd = lumaEnd;

        reader.SeekToByte(lumaEnd);
        _chromaDecoder.Decode(reader, _v[_current], _v[previous]);
        _chromaDecoder.Decode(reader, _u[_current], _u[previous]);
        LastChromaEnd = reader.WordPointer;
        LastFrameEnd = frameEnd;

        Accumulate(_lumaDecoder);
        Accumulate(_chromaDecoder);
        _decodedFrame = index;
    }

    private void Accumulate(BinkPlaneDecoder decoder)
    {
        for (var i = 0; i < BlockTypeCounts.Length; i++)
        {
            BlockTypeCounts[i] += decoder.BlockTypeCounts[i];
        }

        for (var i = 0; i < ScaledSubTypeCounts.Length; i++)
        {
            ScaledSubTypeCounts[i] += decoder.ScaledSubTypeCounts[i];
        }

        decoder.ResetStatistics();
    }
}
