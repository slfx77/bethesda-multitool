using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for <c>MediaSet</c> (MSET, FormType 0x6F).
///     <para>
///         MSET has no record model of its own — it reaches the writer as a
///         <see cref="GenericEsmRecord" /> — so this reader returns one too, exactly like
///         <see cref="RuntimeAcousticSpaceReader" />. It keys <see cref="GenericEsmRecord.Fields" />
///         by <b>subrecord signature</b>, which is what <c>MsetEncoder</c> reads: no new model, no
///         writer change.
///     </para>
///     <para>
///         Why a typed reader rather than the generic PDB sweep: the six most valuable members are
///         16-byte <c>MediaSet::MediaLayer</c> structs whose first member is itself a
///         <c>BSStringT&lt;char&gt;</c> <i>pointer</i>. The generic reader hands back the raw bytes
///         of any struct larger than 8 bytes, and raw bytes cannot resolve a pointer to heap text —
///         so the six layer names (NAM2..NAM7), the very thing a media set is made of, are
///         unrecoverable without walking the struct.
///     </para>
///     <para>
///         VERIFIED <c>MediaSet::MediaLayer</c> (16 bytes): <c>Name</c> @0 (8-byte BSStringT),
///         <c>Attenuation</c> @8 (float), <c>Percent</c> @12 (float). PDB <c>MediaSet</c> is 212
///         bytes with the six layers at @88, @104, @120, @136, @152 and @168.
///     </para>
///     <para>
///         Slots are <b>positional</b>: layer <i>n</i>'s attenuation belongs in the <i>n</i>th dB
///         signature and nowhere else. Because each slot has its own xEdit signature the mapping is
///         carried by <see cref="LayerSignatures" /> rather than by list position, so a layer the
///         capture could not read leaves its three signatures absent instead of letting the next
///         layer slide into them.
///     </para>
///     <para>
///         Following the ASPC rule, every sound pointer is type-validated against SOUN: a pointer
///         that does not resolve to a sound yields nothing rather than a wrong FormID.
///     </para>
/// </summary>
internal sealed class RuntimeMediaSetReader(RuntimeMemoryContext context)
{
    private const byte MsetFormType = 0x6F;
    private const byte SounFormType = 0x0D;

    /// <summary>Size of one <c>MediaSet::MediaLayer</c>.</summary>
    private const int LayerSize = 16;

    /// <summary>Offset of <c>Name</c> inside a <c>MediaLayer</c>.</summary>
    private const int LayerNameOffset = 0;

    /// <summary>Offset of <c>Attenuation</c> inside a <c>MediaLayer</c>.</summary>
    private const int LayerAttenuationOffset = 8;

    /// <summary>Offset of <c>Percent</c> inside a <c>MediaLayer</c>.</summary>
    private const int LayerPercentOffset = 12;

    /// <summary><c>MediaSet.Type</c> @84 — the MEDIASETTYPE enum behind NAM1.</summary>
    private const int TypeOffset = 84;

    /// <summary><c>MediaSet.cEnableFlags</c> @184 — the u8 flag byte behind PNAM.</summary>
    private const int EnableFlagsOffset = 184;

    /// <summary><c>MediaSet.fOne</c> @188; fTwo/fThree/fFour follow at 4-byte strides.</summary>
    private const int FirstTimingOffset = 188;

    /// <summary><c>MediaSet.pSoundOne</c> @204 — HNAM.</summary>
    private const int SoundOneOffset = 204;

    /// <summary><c>MediaSet.pSoundTwo</c> @208 — INAM.</summary>
    private const int SoundTwoOffset = 208;

    /// <summary>Byte offsets of the six <c>MediaSet::MediaLayer</c> members (pMLOne..pMLSix).</summary>
    private static readonly int[] LayerOffsets = [88, 104, 120, 136, 152, 168];

    /// <summary>
    ///     Per-layer subrecord signatures in xEdit's declared order: the name string, the
    ///     attenuation in dB, and the boundary percentage. Index <i>n</i> is layer <i>n</i>.
    /// </summary>
    private static readonly (string Name, string Attenuation, string Percent)[] LayerSignatures =
    [
        ("NAM2", "NAM8", "JNAM"),
        ("NAM3", "NAM9", "KNAM"),
        ("NAM4", "NAM0", "LNAM"),
        ("NAM5", "ANAM", "MNAM"),
        ("NAM6", "BNAM", "NNAM"),
        ("NAM7", "CNAM", "ONAM")
    ];

    /// <summary>Signatures for fOne..fFour, in declaration order.</summary>
    private static readonly string[] TimingSignatures = ["DNAM", "ENAM", "FNAM", "GNAM"];

    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads one runtime media set, or null when the struct can't be read.</summary>
    public GenericEsmRecord? ReadRuntimeMediaSet(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != MsetFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, MsetFormType);
        if (view == null)
        {
            return null;
        }

        var buffer = view.Buffer;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        // NAM1 is the MEDIASETTYPE enum. xEdit's list has four entries plus a -1 'No Set'
        // sentinel, so 0xFFFFFFFF is legitimate and only the 4..0xFFFFFFFE band is a bad read.
        if (TryReadUInt32(buffer, TypeOffset) is { } setType && IsPlausibleSetType(setType))
        {
            fields["NAM1"] = setType;
        }

        for (var layer = 0; layer < LayerOffsets.Length && layer < LayerSignatures.Length; layer++)
        {
            ReadLayer(buffer, LayerOffsets[layer], LayerSignatures[layer], fields);
        }

        if (EnableFlagsOffset < buffer.Length)
        {
            fields["PNAM"] = (uint)buffer[EnableFlagsOffset];
        }

        for (var i = 0; i < TimingSignatures.Length; i++)
        {
            if (TryReadFloat(buffer, FirstTimingOffset + (i * 4)) is { } timing)
            {
                fields[TimingSignatures[i]] = timing;
            }
        }

        if (_context.FollowPointerToFormId(buffer, SoundOneOffset, SounFormType) is { } introSound)
        {
            fields["HNAM"] = introSound;
        }

        if (_context.FollowPointerToFormId(buffer, SoundTwoOffset, SounFormType) is { } outroSound)
        {
            fields["INAM"] = outroSound;
        }

        return new GenericEsmRecord
        {
            FormId = entry.FormId,
            RecordType = "MSET",
            EditorId = entry.EditorId,
            FullName = _fields.ReadBsString(buffer, view.FileOffset, view.Layout, "cFullName", "TESFullName", entry),
            Fields = fields,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read one 16-byte <c>MediaLayer</c> into its three signature slots.
    ///     <para>
    ///         The layer's <c>Name</c> gates the whole slot. A media set uses only as many layers as
    ///         its type needs — a Battle Set fills one, a Location Set all six — and an unused
    ///         layer is entirely zeroed, so its <c>Attenuation</c> and <c>Percent</c> read back as a
    ///         perfectly valid 0.0f. Writing those would put a spurious "0 dB" layer in the plugin
    ///         where the source had none, so an unreadable name means the layer is skipped whole.
    ///     </para>
    ///     <para>
    ///         Skipping leaves the three signatures absent rather than shifting a later layer up
    ///         into them: each slot has its own signature, and this method only ever writes the one
    ///         it was handed.
    ///     </para>
    /// </summary>
    private void ReadLayer(
        byte[] buffer,
        int layerOffset,
        (string Name, string Attenuation, string Percent) signatures,
        Dictionary<string, object?> fields)
    {
        if (layerOffset + LayerSize > buffer.Length)
        {
            return;
        }

        var name = _context.ReadBSStringTDiag(buffer, layerOffset + LayerNameOffset, out var failure);
        BSStringDiagnostics.Record(signatures.Name, failure);
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        fields[signatures.Name] = name;

        if (TryReadFloat(buffer, layerOffset + LayerAttenuationOffset) is { } attenuation)
        {
            fields[signatures.Attenuation] = attenuation;
        }

        if (TryReadFloat(buffer, layerOffset + LayerPercentOffset) is { } percent)
        {
            fields[signatures.Percent] = percent;
        }
    }

    /// <summary>
    ///     xEdit's MEDIASETTYPE enum runs 0..3 with -1 as an explicit 'No Set'. Anything between
    ///     is a misaligned read — most often a pointer — and must not become NAM1.
    /// </summary>
    private static bool IsPlausibleSetType(uint value)
    {
        return value <= 3 || value == uint.MaxValue;
    }

    private static uint? TryReadUInt32(byte[] buffer, int offset)
    {
        return offset + 4 <= buffer.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4))
            : null;
    }

    /// <summary>
    ///     Read a big-endian float, rejecting non-finite and subnormal values the same way the
    ///     generic reader does — those are garbage bytes rather than a captured dB or percentage.
    /// </summary>
    private static float? TryReadFloat(byte[] buffer, int offset)
    {
        if (offset + 4 > buffer.Length)
        {
            return null;
        }

        var value = BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(offset, 4));
        return RuntimeMemoryContext.IsNormalOrZeroFloat(value) ? value : null;
    }
}
