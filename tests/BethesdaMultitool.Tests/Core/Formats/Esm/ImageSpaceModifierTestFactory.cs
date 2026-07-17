using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace BethesdaMultitool.Tests.Core.Formats.Esm;

internal static class ImageSpaceModifierTestFactory
{
    internal static ImageSpaceModifierRecord Complete(
        uint formId = 0x000CDA79,
        bool isBigEndian = false,
        uint? introSound = null,
        uint? outroSound = null,
        bool includeUnknown = false,
        IReadOnlySet<string>? omittedFrameTables = null)
    {
        var payload = new uint[59];
        foreach (var layout in ImageSpaceModifierCaptureValidator.FrameTableLayouts)
        {
            if (!(omittedFrameTables?.Contains(layout.Signature) ?? false))
            {
                payload[layout.CountIndex] = 2;
            }
        }

        var data = new ImageSpaceModifierData
        {
            AnimatableFlag = 1,
            Duration = 2.5f,
            RawPayload = payload,
        };
        var dnam = ImadEncoder.EncodeDnam(data);
        if (isBigEndian)
        {
            // Numeric DWORDs use native big endian, but the packed bAnimatable,
            // radial-target, and DoF target/mode byte quartets remain byte-identical.
            for (var offset = 4; offset < dnam.Length; offset += 4)
            {
                if (offset is 200 or 224)
                {
                    continue;
                }

                Array.Reverse(dnam, offset, 4);
            }
        }

        var subrecords = new List<ImageSpaceModifierRawSubrecord>
        {
            new("EDID", Encoding.UTF8.GetBytes("HVSimISFX\0")),
            new("DNAM", dnam),
        };
        foreach (var layout in ImageSpaceModifierCaptureValidator.FrameTableLayouts)
        {
            if (omittedFrameTables?.Contains(layout.Signature) ?? false)
            {
                continue;
            }

            subrecords.Add(new ImageSpaceModifierRawSubrecord(
                layout.Signature,
                FrameTablePayload(layout.ElementSize, isBigEndian)));
        }

        if (includeUnknown)
        {
            subrecords.Add(new ImageSpaceModifierRawSubrecord(
                "ZZZZ",
                isBigEndian ? [0x12, 0x34, 0x56, 0x78] : [0x78, 0x56, 0x34, 0x12]));
        }

        if (introSound is { } intro)
        {
            subrecords.Add(new ImageSpaceModifierRawSubrecord("RDSD", FormIdBytes(intro, isBigEndian)));
        }

        if (outroSound is { } outro)
        {
            subrecords.Add(new ImageSpaceModifierRawSubrecord("RDSI", FormIdBytes(outro, isBigEndian)));
        }

        return new ImageSpaceModifierRecord
        {
            FormId = formId,
            EditorId = "HVSimISFX",
            Data = data,
            IntroSoundFormId = introSound,
            OutroSoundFormId = outroSound,
            OrderedSubrecords = subrecords,
            IsBigEndian = isBigEndian,
        };
    }

    private static byte[] FrameTablePayload(int elementSize, bool isBigEndian)
    {
        const int rowCount = 2;
        var wordsPerRow = elementSize / 4;
        var bytes = new byte[rowCount * elementSize];
        for (var row = 0; row < rowCount; row++)
        {
            for (var word = 0; word < wordsPerRow; word++)
            {
                var value = word == 0 ? row : row * 100f + word + 0.5f;
                var destination = bytes.AsSpan(row * elementSize + word * 4, 4);
                if (isBigEndian)
                {
                    BinaryPrimitives.WriteSingleBigEndian(destination, value);
                }
                else
                {
                    BinaryPrimitives.WriteSingleLittleEndian(destination, value);
                }
            }
        }

        return bytes;
    }

    private static byte[] FormIdBytes(uint formId, bool isBigEndian)
    {
        var bytes = new byte[4];
        if (isBigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, formId);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, formId);
        }

        return bytes;
    }
}
