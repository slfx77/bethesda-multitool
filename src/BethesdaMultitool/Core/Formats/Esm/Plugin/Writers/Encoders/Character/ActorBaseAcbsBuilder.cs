using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

/// <summary>
///     Builds the FO3/FNV 24-byte ACBS payload for NPC_ and CREA encoders.
///     Numeric layout is shared; flag policy is record-specific.
/// </summary>
/// <remarks>
///     ACBS layout (24 bytes): uint32 Flags(0) + uint16 FatigueBase(4) + uint16 BarterGold(6) +
///     int16 Level(8) + uint16 CalcMin(10) + uint16 CalcMax(12) + uint16 SpeedMult(14) +
///     float KarmaAlignment(16) + int16 DispositionBase(20) + uint16 TemplateFlags(22).
/// </remarks>
internal static class ActorBaseAcbsBuilder
{
    private static readonly Dictionary<string, Func<ActorBaseSubrecord, object?>> AcbsExtractors =
        new(StringComparer.Ordinal)
        {
            ["Flags"] = m => m.Flags,
            ["Fatigue"] = m => m.FatigueBase,
            ["BarterGold"] = m => m.BarterGold,
            ["Level"] = m => m.Level,
            ["CalcMin"] = m => m.CalcMin,
            ["CalcMax"] = m => m.CalcMax,
            ["SpeedMult"] = m => m.SpeedMultiplier,
            ["KarmaAlignment"] = m => m.KarmaAlignment,
            ["Disposition"] = m => m.DispositionBase,
            ["TemplateFlags"] = m => m.TemplateFlags
        };

    /// <summary>
    /// Serialize FO3/FNV ACBS. NPC AutoCalc is 0x10 and UseTemplate is 0x100.
    /// CREA 0x10/0x20/0x40 are Swims/Flies/Walks: preserve all creature flag bits,
    /// including 0x100, whose write policy is not established by the generic SDK enum.
    /// TemplateFlags remain a separate field. Preserve the existing zero-speed default.
    /// </summary>
    public static byte[] Build(
        string recordType,
        ActorBaseSubrecord s,
        bool forceAutoCalc = false,
        ushort extraTemplateFlags = 0)
    {
        var flags = s.Flags;
        if (recordType == "NPC_" && forceAutoCalc)
        {
            flags |= 0x00000010u;
        }

        if (recordType == "NPC_" && (extraTemplateFlags != 0 || s.TemplateFlags != 0))
        {
            flags |= 0x00000100u;
        }

        var mutated = s with
        {
            Flags = flags,
            SpeedMultiplier = s.SpeedMultiplier == 0 ? (ushort)100 : s.SpeedMultiplier,
            TemplateFlags = (ushort)(s.TemplateFlags | extraTemplateFlags)
        };

        return SchemaModelSerializer.Serialize("ACBS", recordType, 24, mutated, AcbsExtractors);
    }

    /// <summary>
    ///     Override identity policy: an OVERRIDE of a master NPC/creature
    ///     keeps MASTER's ACBS Flags dword and TemplateFlags word — runtime captures leak
    ///     state bits into both (the Omerta entrance guard gained TemplateFlags
    ///     0x015F→0x835F: UseScript 0x0200 re-points the engine at his TEMPLATE's script,
    ///     silencing the weapons-check forcegreet, plus a bogus 0x8000 bit; his ACBS flags
    ///     gained PCLevelMult the same way). Captured NUMERIC fields (fatigue, gold, level,
    ///     speed, karma, disposition) stay — deliberate proto stat drift. Walks the merged
    ///     subrecord stream and patches the ACBS payload in place.
    /// </summary>
    public static byte[] RestoreMasterIdentityFlags(byte[] mergedSubrecordBytes, ParsedMainRecord master)
    {
        var masterAcbs = master.Subrecords.FirstOrDefault(s =>
            s.Signature == "ACBS" && s.Data.Length >= 24);
        if (masterAcbs is null)
        {
            return mergedSubrecordBytes;
        }

        var bytes = mergedSubrecordBytes;
        var i = 0;
        while (i + 6 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 4, 2));
            var dataStart = i + 6;
            if (dataStart + length > bytes.Length)
            {
                break; // Malformed tail — leave untouched.
            }

            if (length >= 24
                && bytes[i] == (byte)'A' && bytes[i + 1] == (byte)'C'
                && bytes[i + 2] == (byte)'B' && bytes[i + 3] == (byte)'S')
            {
                var patched = (byte[])bytes.Clone();
                masterAcbs.Data.AsSpan(0, 4).CopyTo(patched.AsSpan(dataStart, 4)); // Flags
                masterAcbs.Data.AsSpan(22, 2).CopyTo(patched.AsSpan(dataStart + 22, 2)); // TemplateFlags
                return patched;
            }

            i = dataStart + length;
        }

        return bytes;
    }

    /// <summary>
    /// Build missing-stats defaults: Level=1, SpeedMult=100. Only NPC_ receives
    /// UseTemplate (0x100) when extra template fields are requested.
    /// </summary>
    public static byte[] BuildDefault(string recordType, ushort extraTemplateFlags = 0)
    {
        var defaults = new ActorBaseSubrecord(
            recordType == "NPC_" && extraTemplateFlags != 0 ? 0x00000100u : 0u,
            0,
            0,
            1,
            0,
            0,
            100,
            0f,
            0,
            extraTemplateFlags,
            0,
            false);

        return SchemaModelSerializer.Serialize("ACBS", recordType, 24, defaults, AcbsExtractors);
    }
}
