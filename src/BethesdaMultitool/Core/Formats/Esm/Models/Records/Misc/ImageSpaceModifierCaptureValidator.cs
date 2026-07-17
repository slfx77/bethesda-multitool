using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Fail-closed structural validation for a newly-emitted IMAD. Runtime-only capture
///     stubs frequently retain an EditorID while losing the fixed DNAM/count table or one
///     or more authored frame tables. Such a stub is useful for display, but it is not a
///     loadable replacement for the source record and must not enter the planner's emit set.
/// </summary>
internal static class ImageSpaceModifierCaptureValidator
{
    internal sealed record FrameTableLayout(string Signature, int CountIndex, int ElementSize);

    /// <summary>
    ///     Canonical on-disk table order. CountIndex addresses DNAM's 59 DWORD payload
    ///     after Animatable/Duration. Zero counts legitimately omit their subrecord.
    /// </summary>
    internal static readonly IReadOnlyList<FrameTableLayout> FrameTableLayouts =
    [
        new("BNAM", 43, 8), // Blur radius
        new("VNAM", 44, 8), // Double vision strength
        new("TNAM", 42, 20), // Tint color (time + RGBA)
        new("NAM3", 57, 20), // Fade color (time + RGBA)
        new("RNAM", 45, 8), // Radial blur strength
        new("SNAM", 46, 8), // Radial blur ramp up
        new("UNAM", 47, 8), // Radial blur start
        new("NAM1", 55, 8), // Radial blur ramp down
        new("NAM2", 56, 8), // Radial blur down start
        new("WNAM", 51, 8), // DoF strength
        new("XNAM", 52, 8), // DoF distance
        new("YNAM", 53, 8), // DoF range
        new("NAM4", 58, 8), // Motion blur strength
        .. Enumerable.Range(0, 21)
            .SelectMany(static ordinal => new[]
            {
                new FrameTableLayout($"{(char)ordinal}IAD", ordinal * 2, 8),
                new FrameTableLayout($"{(char)(0x40 + ordinal)}IAD", ordinal * 2 + 1, 8),
            }),
    ];

    internal static bool IsCompleteNewCapture(
        ImageSpaceModifierRecord record,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.OrderedSubrecords.Count == 0)
        {
            reason = "ordered source subrecord stream is absent";
            return false;
        }

        var edids = record.OrderedSubrecords
            .Select(static (sub, index) => (Subrecord: sub, Index: index))
            .Where(static item => item.Subrecord.Signature == "EDID")
            .ToArray();
        if (edids.Length != 1
            || edids[0].Subrecord.Data.Length < 2
            || edids[0].Subrecord.Data[^1] != 0
            || edids[0].Subrecord.Data[0] == 0)
        {
            reason = "requires exactly one non-empty, null-terminated EDID";
            return false;
        }

        var dnams = record.OrderedSubrecords
            .Select(static (sub, index) => (Subrecord: sub, Index: index))
            .Where(static item => item.Subrecord.Signature == "DNAM")
            .ToArray();
        if (dnams.Length != 1 || dnams[0].Subrecord.Data.Length != 244)
        {
            reason = "requires exactly one complete 244-byte DNAM";
            return false;
        }

        if (edids[0].Index > dnams[0].Index)
        {
            reason = "EDID must precede DNAM";
            return false;
        }

        var rankBySignature = FrameTableLayouts
            .Select(static (layout, rank) => (layout.Signature, Rank: rank))
            .ToDictionary(static item => item.Signature, static item => item.Rank, StringComparer.Ordinal);
        var lastRank = -1;
        for (var i = 0; i < record.OrderedSubrecords.Count; i++)
        {
            var signature = record.OrderedSubrecords[i].Signature;
            if (!rankBySignature.TryGetValue(signature, out var rank))
            {
                continue;
            }

            if (i < dnams[0].Index || rank < lastRank)
            {
                reason = $"frame table {DisplaySignature(signature)} is out of canonical order";
                return false;
            }

            lastRank = rank;
        }

        foreach (var layout in FrameTableLayouts)
        {
            var tables = record.OrderedSubrecords
                .Where(sub => string.Equals(sub.Signature, layout.Signature, StringComparison.Ordinal))
                .ToArray();
            if (tables.Any(table => table.Data.Length % layout.ElementSize != 0))
            {
                reason = $"frame table {DisplaySignature(layout.Signature)} has a truncated row";
                return false;
            }

            var actualCount = tables.Aggregate(
                0UL,
                (total, table) => total + (ulong)(table.Data.Length / layout.ElementSize));
            var expectedCount = ReadDnamPayloadSlot(
                dnams[0].Subrecord.Data, layout.CountIndex, record.IsBigEndian);
            if (actualCount != expectedCount)
            {
                reason = $"frame table {DisplaySignature(layout.Signature)} has {actualCount} row(s), "
                         + $"but DNAM declares {expectedCount}";
                return false;
            }

            if (actualCount == 0)
            {
                continue;
            }

            var tableBytes = tables.Length == 1
                ? tables[0].Data
                : tables.SelectMany(static table => table.Data).ToArray();
            if (!AreFrameTableKeysValid(
                    tableBytes,
                    layout.ElementSize,
                    record.IsBigEndian,
                    dnams[0].Subrecord.Data[0] != 0,
                    out var keyFailure))
            {
                reason = $"frame table {DisplaySignature(layout.Signature)} {keyFailure}";
                return false;
            }
        }

        if (!ValidateSound(record, "RDSD", record.IntroSoundFormId, out reason)
            || !ValidateSound(record, "RDSI", record.OutroSoundFormId, out reason))
        {
            return false;
        }

        // SubrecordSchemaProcessor treats non-string IMAD variants as arrays of four-byte
        // values. Reject a big-endian tail it cannot convert rather than preserve a
        // half-converted payload and advertise the record as a live SCRO target.
        if (record.IsBigEndian)
        {
            var malformed = record.OrderedSubrecords.FirstOrDefault(static sub =>
                sub.Signature != "EDID" && sub.Data.Length % 4 != 0);
            if (malformed is not null)
            {
                reason = $"big-endian {DisplaySignature(malformed.Signature)} payload has a non-DWORD tail";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    ///     Shared implementation of the finite-value guard and the decompiled
    ///     TESImageSpaceModifier::AreKeysValid contract. Authored animatable curves
    ///     must contain at least two keys, span normalized time 0..1 exactly, and have
    ///     strictly increasing key times. Non-animatable curves retain their captured
    ///     time values, but every stored float must still be finite.
    /// </summary>
    internal static bool AreFrameTableKeysValid(
        ReadOnlySpan<byte> tableBytes,
        int elementSize,
        bool isBigEndian,
        bool animatable,
        out string reason)
    {
        reason = string.Empty;
        if (elementSize <= 0 || elementSize % 4 != 0 || tableBytes.Length % elementSize != 0)
        {
            reason = "has a truncated row";
            return false;
        }

        for (var offset = 0; offset < tableBytes.Length; offset += 4)
        {
            var value = isBigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(tableBytes.Slice(offset, 4))
                : BinaryPrimitives.ReadSingleLittleEndian(tableBytes.Slice(offset, 4));
            if (!float.IsFinite(value))
            {
                reason = $"contains a non-finite float at byte {offset}";
                return false;
            }
        }

        if (!animatable || tableBytes.IsEmpty)
        {
            return true;
        }

        var count = tableBytes.Length / elementSize;
        if (count < 2)
        {
            reason = "has fewer than two keys for an animatable curve";
            return false;
        }

        var previousTime = ReadTime(tableBytes, 0, isBigEndian);
        if (previousTime != 0f)
        {
            reason = "does not begin at time 0 for an animatable curve";
            return false;
        }

        for (var row = 1; row < count; row++)
        {
            var time = ReadTime(tableBytes, row * elementSize, isBigEndian);
            if (time <= previousTime)
            {
                reason = "has non-increasing times for an animatable curve";
                return false;
            }

            previousTime = time;
        }

        if (previousTime != 1f)
        {
            reason = "does not end at time 1 for an animatable curve";
            return false;
        }

        return true;
    }

    private static float ReadTime(ReadOnlySpan<byte> tableBytes, int offset, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(tableBytes.Slice(offset, 4))
            : BinaryPrimitives.ReadSingleLittleEndian(tableBytes.Slice(offset, 4));
    }

    private static uint ReadDnamPayloadSlot(byte[] dnam, int index, bool isBigEndian)
    {
        var bytes = dnam.AsSpan(8 + index * 4, 4);
        return isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static bool ValidateSound(
        ImageSpaceModifierRecord record,
        string signature,
        uint? semanticFormId,
        out string reason)
    {
        var sounds = record.OrderedSubrecords
            .Where(sub => string.Equals(sub.Signature, signature, StringComparison.Ordinal))
            .ToArray();
        if (sounds.Length > 1 || sounds.Any(static sub => sub.Data.Length != 4))
        {
            reason = $"{signature} must be absent or one four-byte FormID";
            return false;
        }

        var rawFormId = sounds.Length == 0
            ? 0u
            : record.IsBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(sounds[0].Data)
                : BinaryPrimitives.ReadUInt32LittleEndian(sounds[0].Data);
        if (rawFormId != (semanticFormId ?? 0u))
        {
            reason = $"{signature} raw FormID and semantic projection disagree";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string DisplaySignature(string signature)
    {
        return signature.Length == 4 && char.IsControl(signature[0])
            ? $"0x{(int)signature[0]:X2}IAD"
            : signature;
    }
}
