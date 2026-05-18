using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Rewrites encoded subrecords that carry FormIDs through a DMP-source to emitted/master
///     alias map before bytes are merged or written to the plugin.
/// </summary>
internal static class EncodedSubrecordFormIdRemapper
{
    public static IReadOnlyList<EncodedSubrecord> Remap(
        string recordType,
        IReadOnlyList<EncodedSubrecord> subrecords,
        IReadOnlyDictionary<uint, uint> aliases)
    {
        if (aliases.Count == 0 || subrecords.Count == 0)
        {
            return subrecords;
        }

        List<EncodedSubrecord>? rewritten = null;
        for (var i = 0; i < subrecords.Count; i++)
        {
            var subrecord = subrecords[i];
            var replacement = RemapSubrecord(recordType, subrecord, aliases);
            if (!ReferenceEquals(replacement, subrecord) && rewritten is null)
            {
                rewritten = new List<EncodedSubrecord>(subrecords.Count);
                for (var j = 0; j < i; j++)
                {
                    rewritten.Add(subrecords[j]);
                }
            }

            rewritten?.Add(replacement);
        }

        return rewritten ?? subrecords;
    }

    private static EncodedSubrecord RemapSubrecord(
        string recordType,
        EncodedSubrecord subrecord,
        IReadOnlyDictionary<uint, uint> aliases)
    {
        var offsets = GetFormIdOffsets(recordType, subrecord);
        if (offsets.Count == 0)
        {
            return subrecord;
        }

        byte[]? bytes = null;
        foreach (var offset in offsets)
        {
            if (offset < 0 || offset + 4 > subrecord.Bytes.Length)
            {
                continue;
            }

            var raw = BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Bytes.AsSpan(offset, 4));
            if (!aliases.TryGetValue(raw, out var replacement) || replacement == raw)
            {
                continue;
            }

            bytes ??= (byte[])subrecord.Bytes.Clone();
            SubrecordEncoder.WriteFormId(bytes, offset, replacement);
        }

        return bytes is null ? subrecord : new EncodedSubrecord(subrecord.Signature, bytes);
    }

    private static IReadOnlyList<int> GetFormIdOffsets(string recordType, EncodedSubrecord subrecord)
    {
        var signature = subrecord.Signature;

        if (recordType is "REFR" or "ACHR" or "ACRE")
        {
            return signature switch
            {
                "NAME" or "XEZN" or "XOWN" or "XESP" or "XTEL" => Offset0WhenAtLeast4(subrecord),
                "XLOC" => subrecord.Bytes.Length >= 8 ? [4] : [],
                "XLKR" when subrecord.Bytes.Length >= 8 => [0, 4],
                "XLKR" => Offset0WhenAtLeast4(subrecord),
                _ => []
            };
        }

        if (recordType == "NPC_")
        {
            return signature switch
            {
                "SNAM" or "CNTO" or "COED" => Offset0WhenAtLeast4(subrecord),
                "INAM" or "VTCK" or "TPLT" or "RNAM" or "SPLO" or "SCRI" or "PKID" or "CNAM"
                    or "PNAM" or "HNAM" or "ENAM" or "ZNAM" => Offset0WhenAtLeast4(subrecord),
                _ => []
            };
        }

        if (recordType == "CREA")
        {
            return signature switch
            {
                "SNAM" or "CNTO" or "COED" => Offset0WhenAtLeast4(subrecord),
                "INAM" or "VTCK" or "TPLT" or "RNAM" or "SPLO" or "SCRI" or "PKID" or "ZNAM"
                    => Offset0WhenAtLeast4(subrecord),
                _ => []
            };
        }

        if (recordType == "CELL")
        {
            return signature switch
            {
                "LTMP" or "LNAM" or "XEZN" or "XCAS" or "XCMO" or "XCIM" => Offset0WhenAtLeast4(subrecord),
                "XCLR" => FourByteArrayOffsets(subrecord),
                _ => []
            };
        }

        if (recordType == "LTEX")
        {
            return signature is "TNAM" or "GNAM" ? Offset0WhenAtLeast4(subrecord) : [];
        }

        return recordType switch
        {
            "SCPT" => signature == "SCRO" ? Offset0WhenAtLeast4(subrecord) : [],
            "CONT" => signature is "SCRI" or "CNTO" or "COED" ? Offset0WhenAtLeast4(subrecord) : [],
            "FACT" => signature == "XNAM" ? Offset0WhenAtLeast4(subrecord) : [],
            "FLST" => signature == "LNAM" ? Offset0WhenAtLeast4(subrecord) : [],
            "LVLC" or "LVLI" or "LVLN" => signature == "LVLO" && subrecord.Bytes.Length >= 8 ? [4] : [],
            _ => signature == "SCRI" ? Offset0WhenAtLeast4(subrecord) : []
        };
    }

    private static IReadOnlyList<int> Offset0WhenAtLeast4(EncodedSubrecord subrecord)
        => subrecord.Bytes.Length >= 4 ? [0] : [];

    private static IReadOnlyList<int> FourByteArrayOffsets(EncodedSubrecord subrecord)
    {
        if (subrecord.Bytes.Length < 4 || subrecord.Bytes.Length % 4 != 0)
        {
            return [];
        }

        var offsets = new int[subrecord.Bytes.Length / 4];
        for (var i = 0; i < offsets.Length; i++)
        {
            offsets[i] = i * 4;
        }

        return offsets;
    }
}
