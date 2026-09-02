using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Parses idle animation pose overrides from NiControllerSequence blocks in skeleton/KF NIFs.
/// </summary>
internal static class NifAnimationParser
{
    private const int ModernControlledBlockStride = 29;
    private const int ModernNodeNameOffset = 9;

    // Oblivion 20.0.0.4/.5 BS11 stores names in a NiStringPalette. The priority byte makes every
    // following field unaligned: Interpolator @0, Controller @4, Priority @8, Palette ref @9, then
    // five StringOffsets @13/@17/@21/@25/@29. That extra palette ref makes the entry 33 bytes, not
    // the 29-byte 20.2.0.7 form used by FO3 and later.
    private const int OblivionControlledBlockStride = 33;
    private const int OblivionPaletteRefOffset = 9;
    private const int OblivionNodeNameOffset = 13;
    private const int MaxInlineStringBytes = 512;
    private const uint OblivionBsVersion = 11;

    /// <summary>
    ///     Parse idle pose overrides from NiControllerSequence blocks in a skeleton/KF NIF.
    /// </summary>
    internal static Dictionary<string, AnimPoseOverride>? ParseIdlePoseOverrides(
        byte[] data,
        NifInfo nif,
        bool sampleLastKeyframe = false)
    {
        var be = nif.IsBigEndian;
        var sequenceBlock = NifControllerSequenceSelector.SelectIdleSequence(data, nif, be);
        if (sequenceBlock == null)
        {
            return null;
        }

        var isOblivionPaletteLayout = IsOblivionPaletteSequence(nif);
        var pos = sequenceBlock.DataOffset;
        var sequenceEndLong = (long)sequenceBlock.DataOffset + sequenceBlock.Size;
        if (sequenceBlock.DataOffset < 0 || sequenceBlock.Size < 0 ||
            sequenceEndLong > data.LongLength)
        {
            return null;
        }

        var sequenceEnd = (int)sequenceEndLong;
        if (isOblivionPaletteLayout)
        {
            if (!TrySkipSizedString(data, ref pos, sequenceEnd, out _))
            {
                return null;
            }
        }
        else
        {
            if (pos + 4 > sequenceEnd)
            {
                return null;
            }

            pos += 4;
        }

        if (pos + 8 > sequenceEnd)
        {
            return null;
        }
        var numBlocks = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;
        if (numBlocks <= 0 || numBlocks > 500)
        {
            return null;
        }

        pos += 4;

        var controlledBlockStride = isOblivionPaletteLayout
            ? OblivionControlledBlockStride
            : ModernControlledBlockStride;
        var afterBlocksLong = (long)pos + (long)numBlocks * controlledBlockStride;
        if (afterBlocksLong > sequenceEnd)
        {
            return null;
        }

        var result = new Dictionary<string, AnimPoseOverride>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < numBlocks; i++)
        {
            var blockStart = pos + i * controlledBlockStride;
            var interpolatorRef = BinaryUtils.ReadInt32(data, blockStart, be);
            if (interpolatorRef < 0 || interpolatorRef >= nif.Blocks.Count)
            {
                continue;
            }

            string nodeName;
            if (isOblivionPaletteLayout)
            {
                var paletteRef = BinaryUtils.ReadInt32(
                    data,
                    blockStart + OblivionPaletteRefOffset,
                    false);
                var nodeNameOffset = BinaryUtils.ReadUInt32(
                    data,
                    blockStart + OblivionNodeNameOffset,
                    false);
                if (!TryResolvePaletteString(
                        data,
                        nif,
                        paletteRef,
                        nodeNameOffset,
                        out nodeName))
                {
                    continue;
                }
            }
            else
            {
                var nodeNameIndex = BinaryUtils.ReadInt32(
                    data,
                    blockStart + ModernNodeNameOffset,
                    be);
                if (nodeNameIndex < 0 || nodeNameIndex >= nif.Strings.Count ||
                    string.IsNullOrWhiteSpace(nif.Strings[nodeNameIndex]))
                {
                    continue;
                }

                nodeName = nif.Strings[nodeNameIndex];
            }

            var pose = NifInterpolatorPoseReader.Parse(
                data,
                nif,
                nif.Blocks[interpolatorRef],
                be,
                sampleLastKeyframe);
            if (pose == null)
            {
                continue;
            }

            result[nodeName] = pose.Value;
        }

        RemoveAccumRootOverrides(
            data,
            nif,
            be,
            (int)afterBlocksLong,
            sequenceEnd,
            isOblivionPaletteLayout,
            result);
        return result.Count > 0 ? result : null;
    }

    private static void RemoveAccumRootOverrides(
        byte[] data,
        NifInfo nif,
        bool be,
        int afterBlocks,
        int sequenceEnd,
        bool isOblivionPaletteLayout,
        Dictionary<string, AnimPoseOverride> result)
    {
        var accumRootPos = afterBlocks + 28;
        string accumName;
        if (isOblivionPaletteLayout)
        {
            if (!TrySkipSizedString(data, ref accumRootPos, sequenceEnd, out accumName) ||
                string.IsNullOrWhiteSpace(accumName))
            {
                return;
            }
        }
        else
        {
            if (accumRootPos + 4 > sequenceEnd)
            {
                return;
            }

            var accumRootIndex = BinaryUtils.ReadInt32(data, accumRootPos, be);
            if (accumRootIndex < 0 || accumRootIndex >= nif.Strings.Count)
            {
                return;
            }

            accumName = nif.Strings[accumRootIndex];
        }

        result.Remove(accumName);
        result.Remove(accumName + " NonAccum");

        var pelvisVariant = accumName.Replace(
            "Pelvis",
            "NonAccum",
            StringComparison.OrdinalIgnoreCase);
        if (pelvisVariant != accumName)
        {
            result.Remove(pelvisVariant);
        }
    }

    private static bool IsOblivionPaletteSequence(NifInfo nif)
    {
        return (nif.BinaryVersion is NifVersions.Gamebryo20004 or NifVersions.Gamebryo20005) &&
               nif.BsVersion == OblivionBsVersion &&
               nif.UserVersion is 10 or 11 &&
               nif.HasInlineStrings &&
               !nif.IsBigEndian;
    }

    private static bool TrySkipSizedString(
        byte[] data,
        ref int pos,
        int end,
        out string value)
    {
        value = string.Empty;
        if (pos < 0 || pos + 4 > end)
        {
            return false;
        }

        var length = BinaryUtils.ReadUInt32(data, pos, false);
        pos += 4;
        if (length > MaxInlineStringBytes || (long)pos + length > end)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(data, pos, (int)length);
        pos += (int)length;
        return true;
    }

    private static bool TryResolvePaletteString(
        byte[] data,
        NifInfo nif,
        int paletteRef,
        uint stringOffset,
        out string value)
    {
        value = string.Empty;
        if (stringOffset is uint.MaxValue or 0x0000FFFF ||
            paletteRef < 0 || paletteRef >= nif.Blocks.Count)
        {
            return false;
        }

        var palette = nif.Blocks[paletteRef];
        if (palette.TypeName != "NiStringPalette" ||
            palette.DataOffset < 0 || palette.Size < 8 ||
            (long)palette.DataOffset + palette.Size > data.LongLength)
        {
            return false;
        }

        var paletteLength = BinaryUtils.ReadUInt32(data, palette.DataOffset, false);
        var payloadStart = palette.DataOffset + 4;
        var payloadEndLong = (long)payloadStart + paletteLength;
        if (paletteLength > int.MaxValue ||
            payloadEndLong + 4L != (long)palette.DataOffset + palette.Size ||
            payloadEndLong + 4L > data.LongLength)
        {
            return false;
        }

        var payloadEnd = (int)payloadEndLong;
        if (BinaryUtils.ReadUInt32(data, payloadEnd, false) != paletteLength ||
            stringOffset >= paletteLength)
        {
            return false;
        }

        var stringStart = payloadStart + (int)stringOffset;
        if (stringOffset > 0 && data[stringStart - 1] != 0)
        {
            return false;
        }

        var terminator = Array.IndexOf(
            data,
            (byte)0,
            stringStart,
            Math.Min(payloadEnd - stringStart, MaxInlineStringBytes + 1));
        var byteCount = terminator - stringStart;
        if (terminator < 0 || byteCount is <= 0 or > MaxInlineStringBytes)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(data, stringStart, byteCount);
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    ///     Per-channel animation override. Rotation is always present; translation and scale
    ///     are optional.
    /// </summary>
    internal readonly record struct AnimPoseOverride(
        Quaternion Rotation,
        bool HasTranslation,
        float Tx,
        float Ty,
        float Tz,
        bool HasScale,
        float Scale);
}
