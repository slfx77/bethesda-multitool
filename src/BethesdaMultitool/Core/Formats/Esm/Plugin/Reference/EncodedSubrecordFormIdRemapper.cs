using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Rewrites encoded subrecords that carry FormIDs through a DMP-source to emitted/master
///     alias map before bytes are merged or written to the plugin.
/// </summary>
internal static class EncodedSubrecordFormIdRemapper
{
    /// <summary>
    ///     Rewrites every FormID-bearing field in the encoded subrecords through the alias map,
    ///     returning the originals unchanged when no aliases apply.
    /// </summary>
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
                // XRDO: Range(0) Type(4) StaticPercentage(8) PositionRef(12).
                "XRDO" => subrecord.Bytes.Length >= 16 ? [12] : [],
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

        if (recordType == "INFO")
        {
            return signature switch
            {
                "QSTI" or "TPIC" or "PNAM" or "ANAM" or "NAME" or "TCLT" or "TCLF" or "TCFU"
                    => Offset0WhenAtLeast4(subrecord),
                "TRDT" when subrecord.Bytes.Length >= 20 => [16],
                _ => []
            };
        }

        if (recordType == "LTEX")
        {
            return signature is "TNAM" or "GNAM" ? Offset0WhenAtLeast4(subrecord) : [];
        }

        if (recordType == "FLOR")
        {
            // PFIG (ingredient), SCRI (script), SNAM (sound) — all single FormIDs at offset 0.
            return signature is "PFIG" or "SCRI" or "SNAM" ? Offset0WhenAtLeast4(subrecord) : [];
        }

        return recordType switch
        {
            "SCPT" => signature == "SCRO" ? Offset0WhenAtLeast4(subrecord) : [],
            "CONT" => signature is "SCRI" or "CNTO" or "COED" ? Offset0WhenAtLeast4(subrecord) : [],
            "FACT" => signature == "XNAM" ? Offset0WhenAtLeast4(subrecord) : [],
            "FLST" => signature == "LNAM" ? Offset0WhenAtLeast4(subrecord) : [],
            "LVLC" or "LVLI" or "LVLN" => signature == "LVLO" && subrecord.Bytes.Length >= 8 ? [4] : [],
            // Generic-only types added 2026-08-03. Each ref is a single FormID at offset 0;
            // without these arms a ref to a proto-new target keeps its stale source FormID.
            "MSTT" or "ADDN" => signature == "SNAM" ? Offset0WhenAtLeast4(subrecord) : [],
            "TACT" => signature is "SCRI" or "SNAM" or "VNAM" or "INAM"
                ? Offset0WhenAtLeast4(subrecord)
                : [],
            "ASPC" => signature is "SNAM" or "RDAT" ? Offset0WhenAtLeast4(subrecord) : [],
            // PWAT DNAM is { uint32 Flags @0, WATR FormID @4 } — flags FIRST. See the layout
            // remark on PwatEncoder.EncodePwatDnam. This arm carried the transposed layout until
            // 2026-08-07: it was rewriting the FLAG word as if it were a reference and leaving
            // the real WATR FormID unremapped, so a PWAT pointing at a proto-new water kept its
            // stale source FormID while its flags were corrupted.
            "PWAT" => signature == "DNAM" && subrecord.Bytes.Length >= 8 ? [4] : [],
            // ANIO DATA is the IDLE animation FormID (not a data blob, despite the signature).
            "ANIO" => signature == "DATA" ? Offset0WhenAtLeast4(subrecord) : [],
            // CLMT WLST is an array of 12-byte entries: WTHR FormID @0, chance @4, GLOB FormID @8.
            "CLMT" => signature == "WLST" ? WlstFormIdOffsets(subrecord) : [],
            _ => signature == "SCRI" ? Offset0WhenAtLeast4(subrecord) : []
        };
    }

    private static IReadOnlyList<int> Offset0WhenAtLeast4(EncodedSubrecord subrecord)
        => subrecord.Bytes.Length >= 4 ? [0] : [];

    /// <summary>CLMT WLST: per 12-byte entry, FormIDs sit at +0 (WTHR) and +8 (GLOB).</summary>
    private static List<int> WlstFormIdOffsets(EncodedSubrecord subrecord)
    {
        if (subrecord.Bytes.Length < 12 || subrecord.Bytes.Length % 12 != 0)
        {
            return [];
        }

        var offsets = new List<int>(subrecord.Bytes.Length / 12 * 2);
        for (var entry = 0; entry < subrecord.Bytes.Length; entry += 12)
        {
            offsets.Add(entry);
            offsets.Add(entry + 8);
        }

        return offsets;
    }

    private static int[] FourByteArrayOffsets(EncodedSubrecord subrecord)
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
