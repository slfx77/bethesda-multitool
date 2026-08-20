using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.Visual;

/// <summary>
///     Reconstructs a complete IMAD ordered stream from a live TESImageSpaceModifier.
///     The Release Beta objects captured by xex21/xex22 predate the two sound-pointer
///     fields in the August MemDebug PDB, so their own-class fields are eight bytes
///     earlier. Both layouts are probed without mutating global string diagnostics.
///     Exactly one candidate must validate; a dual-valid object is ambiguous and is
///     rejected rather than selected from the dump-family hint.
/// </summary>
internal sealed class RuntimeImageSpaceModifierReader(
    RuntimeMemoryContext context,
    bool preferEarlyLayout)
{
    private const byte ImadFormType = 0x54;
    private const byte SoundFormType = 0x0D;
    private const string Owner = "TESImageSpaceModifier";
    private const int DataSize = 244;
    private const int ParameterTableCount = 42;
    private const int MaxKeysPerTable = 4096;

    // xex21/xex22 Release Beta: sizeof(TESImageSpaceModifier)=0x740. The intro/outro
    // fields do not exist, so Data and every later own-class field are PDB-8.
    private static readonly RuntimeLayout EarlyLayout = new(
        "ReleaseBeta-0x740",
        0x740,
        -8,
        0x28,
        0x11C,
        0x65C,
        0x704,
        0x734,
        0x738,
        null,
        null);

    // Fallout MemDebug August PDB: sizeof(TESImageSpaceModifier)=0x748.
    private static readonly RuntimeLayout FinalPdbLayout = new(
        "MemDebugPdb-0x748",
        0x748,
        0,
        0x30,
        0x124,
        0x664,
        0x70C,
        0x73C,
        0x740,
        0x28,
        0x2C);

    private static readonly Dictionary<string, string> NamedPointerFields =
        new(StringComparer.Ordinal)
        {
            ["BNAM"] = "pBlurFloatKey",
            ["VNAM"] = "pDoubleFloatKey",
            ["TNAM"] = "pTintColorKey",
            ["NAM3"] = "pFadeColorKey",
            ["RNAM"] = "pRadialBlurStrengthFloatKey",
            ["SNAM"] = "pRadialBlurRampupFloatKey",
            ["UNAM"] = "pRadialBlurStartFloatKey",
            ["NAM1"] = "pRadialBlurRampDownFloatKey",
            ["NAM2"] = "pRadialBlurDownStartFloatKey",
            ["WNAM"] = "pDepthOfFieldStrengthFloatKey",
            ["XNAM"] = "pDepthOfFieldDistanceFloatKey",
            ["YNAM"] = "pDepthOfFieldRangeFloatKey",
            ["NAM4"] = "pMotionBlurStrengthFloatKey"
        };

    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimePdbFieldAccessor _fields = new(context);

    internal ImageSpaceModifierRecord? ReadRuntimeImageSpaceModifier(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != ImadFormType || string.IsNullOrWhiteSpace(entry.EditorId))
        {
            return null;
        }

        var preferred = preferEarlyLayout ? EarlyLayout : FinalPdbLayout;
        var alternate = preferEarlyLayout ? FinalPdbLayout : EarlyLayout;
        var preferredRecord = TryReadCandidate(
            entry, preferred, out var preferredFailure, out var preferredNameObservation);
        var alternateRecord = TryReadCandidate(
            entry, alternate, out var alternateFailure, out var alternateNameObservation);

        if (preferredRecord != null && alternateRecord != null)
        {
            Logger.Instance.Debug(
                $"  [Runtime IMAD] 0x{entry.FormId:X8} is ambiguous: it satisfies both " +
                $"{preferred.Name} and {alternate.Name}; refusing runtime reconstruction.");
            return null;
        }

        if (preferredRecord != null)
        {
            RecordNameObservation(preferredNameObservation);
            return preferredRecord;
        }

        if (alternateRecord != null)
        {
            RecordNameObservation(alternateNameObservation);
            Logger.Instance.Debug(
                $"  [Runtime IMAD] 0x{entry.FormId:X8} rejected hinted {preferred.Name} " +
                $"({preferredFailure}); recovered with {alternate.Name}.");
            return alternateRecord;
        }

        // Neither layout was selected. Retain at most one BSString diagnostic from the
        // final failure path: prefer an exact-EDID discriminator, then the build-family
        // candidate. The speculative alternate probe must never add a second count.
        var finalNameObservation = SelectFinalNameObservation(
            entry.EditorId, preferredNameObservation, alternateNameObservation);
        RecordNameObservation(finalNameObservation);

        Logger.Instance.Debug(
            $"  [Runtime IMAD] 0x{entry.FormId:X8} rejected: " +
            $"{preferred.Name}: {preferredFailure}; {alternate.Name}: {alternateFailure}.");
        return null;
    }

    private ImageSpaceModifierRecord? TryReadCandidate(
        RuntimeEditorIdEntry entry,
        RuntimeLayout runtimeLayout,
        out string failure,
        out RuntimeNameObservation? nameObservation)
    {
        failure = string.Empty;
        nameObservation = null;
        var view = OpenCandidateView(entry, runtimeLayout, out failure);
        if (view == null || !ValidatePdbGeometry(view, runtimeLayout, out failure))
        {
            return null;
        }

        var runtimeName = ReadRuntimeName(view, entry, out nameObservation);
        if (!string.Equals(runtimeName, entry.EditorId, StringComparison.Ordinal))
        {
            failure = runtimeName == null
                ? "strName is absent or invalid"
                : $"strName '{runtimeName}' does not match indexed EDID '{entry.EditorId}'";
            return null;
        }

        if (!TryReadData(view.Buffer, runtimeLayout.DataOffset, out var data, out failure))
        {
            return null;
        }

        var ordered = new List<ImageSpaceModifierRawSubrecord>(
            2 + ImageSpaceModifierCaptureValidator.FrameTableLayouts.Count + 2)
        {
            new("EDID", Encoding.ASCII.GetBytes(entry.EditorId + '\0')),
            new("DNAM", EncodeCanonicalDnam(data))
        };
        var parameterKeys = new IReadOnlyList<ImageSpaceModifierFloatKey>?[21, 2];
        var scalarKeys = new Dictionary<string, IReadOnlyList<ImageSpaceModifierFloatKey>>(
            StringComparer.Ordinal);
        IReadOnlyList<ImageSpaceModifierColorKey> tintKeys = [];
        IReadOnlyList<ImageSpaceModifierColorKey> fadeKeys = [];

        foreach (var tableLayout in ImageSpaceModifierCaptureValidator.FrameTableLayouts)
        {
            var count = data.RawPayload[tableLayout.CountIndex];
            if (count == 0)
            {
                continue;
            }

            var pointerOffset = ResolveTablePointerOffset(view, tableLayout, out failure);
            if (!pointerOffset.HasValue
                || !TryReadKeyTable(view.Buffer, pointerOffset.Value, count,
                    tableLayout.ElementSize, data.IsAnimatable, out var tableBytes, out failure))
            {
                failure = $"{DisplaySignature(tableLayout.Signature)}: {failure}";
                return null;
            }

            ordered.Add(new ImageSpaceModifierRawSubrecord(tableLayout.Signature, tableBytes));
            if (MiscEnvironmentHandlerSignature.TryParameter(
                    tableLayout.Signature, out var parameter, out var operation))
            {
                parameterKeys[(int)parameter, (int)operation] = ReadFloatKeys(tableBytes);
            }
            else if (tableLayout.Signature == "TNAM")
            {
                tintKeys = ReadColorKeys(tableBytes);
            }
            else if (tableLayout.Signature == "NAM3")
            {
                fadeKeys = ReadColorKeys(tableBytes);
            }
            else
            {
                scalarKeys[tableLayout.Signature] = ReadFloatKeys(tableBytes);
            }
        }

        uint? introSound = null;
        uint? outroSound = null;
        if (runtimeLayout.IntroSoundOffset.HasValue)
        {
            if (!TryReadOptionalSound(view.Buffer, runtimeLayout.IntroSoundOffset.Value,
                    out introSound, out failure)
                || !TryReadOptionalSound(view.Buffer, runtimeLayout.OutroSoundOffset!.Value,
                    out outroSound, out failure))
            {
                return null;
            }

            if (introSound.HasValue)
            {
                ordered.Add(new ImageSpaceModifierRawSubrecord("RDSD", FormIdBytes(introSound.Value)));
            }

            if (outroSound.HasValue)
            {
                ordered.Add(new ImageSpaceModifierRawSubrecord("RDSI", FormIdBytes(outroSound.Value)));
            }
        }

        var parameters = new ImageSpaceModifierParameterTimeline[21];
        for (var i = 0; i < parameters.Length; i++)
        {
            parameters[i] = new ImageSpaceModifierParameterTimeline(
                (ImageSpaceModifierParameter)i,
                parameterKeys[i, (int)ImageSpaceModifierOperation.Multiply] ?? [],
                parameterKeys[i, (int)ImageSpaceModifierOperation.Add] ?? []);
        }

        var result = new ImageSpaceModifierRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Data = data,
            Parameters = parameters,
            ScalarTimelines = scalarKeys,
            TintColorTimeline = tintKeys,
            FadeColorTimeline = fadeKeys,
            IntroSoundFormId = introSound,
            OutroSoundFormId = outroSound,
            OrderedSubrecords = ordered,
            Offset = view.FileOffset,
            IsBigEndian = false,
            FromRuntime = true
        };

        if (!ImageSpaceModifierCaptureValidator.IsCompleteNewCapture(result, out failure))
        {
            failure = $"reconstructed stream failed validation: {failure}";
            return null;
        }

        return result;
    }

    private string? ReadRuntimeName(
        PdbStructView view,
        RuntimeEditorIdEntry entry,
        out RuntimeNameObservation? observation)
    {
        observation = null;
        var nameOffset = view.Offset("strName", Owner);
        if (!nameOffset.HasValue)
        {
            return null;
        }

        var value = _context.ReadBSStringTDiag(
            view.Buffer,
            nameOffset.Value,
            out var failure,
            out var pointer,
            out var length,
            out var rawHex,
            out var partialData);
        observation = new RuntimeNameObservation(
            value,
            failure,
            new BSStringDiagnostics.DiagSample(
                entry.FormId,
                entry.EditorId,
                entry.FormType,
                view.FileOffset,
                nameOffset.Value,
                pointer,
                length,
                rawHex,
                partialData));
        return value;
    }

    private static RuntimeNameObservation? SelectFinalNameObservation(
        string expectedEditorId,
        RuntimeNameObservation? preferred,
        RuntimeNameObservation? alternate)
    {
        var preferredMatches = string.Equals(preferred?.Value, expectedEditorId, StringComparison.Ordinal);
        var alternateMatches = string.Equals(alternate?.Value, expectedEditorId, StringComparison.Ordinal);
        if (preferredMatches != alternateMatches)
        {
            return preferredMatches ? preferred : alternate;
        }

        return preferred ?? alternate;
    }

    private static void RecordNameObservation(RuntimeNameObservation? observation)
    {
        if (observation == null)
        {
            return;
        }

        BSStringDiagnostics.RecordWithSample("strName", observation.Failure, observation.Sample);
    }

    private PdbStructView? OpenCandidateView(
        RuntimeEditorIdEntry entry,
        RuntimeLayout runtimeLayout,
        out string failure)
    {
        failure = string.Empty;
        if (!entry.TesFormOffset.HasValue)
        {
            failure = "TESForm file offset is absent";
            return null;
        }

        var structVa = _context.MinidumpInfo.FileOffsetToVirtualAddress(entry.TesFormOffset.Value);
        if (!structVa.HasValue)
        {
            failure = "TESForm file offset has no captured VA";
            return null;
        }

        var buffer = _context.ReadBytesAtVa(structVa.Value, runtimeLayout.StructSize);
        if (buffer == null)
        {
            failure = $"0x{runtimeLayout.StructSize:X} object is not fully captured";
            return null;
        }

        if (buffer.Length < 16
            || (buffer[4] != entry.FormType && buffer[4] != (entry.OriginalFormType ?? entry.FormType))
            || BinaryUtils.ReadUInt32BE(buffer, 12) != entry.FormId
            || entry.FormId == 0)
        {
            failure = "TESForm identity guard failed";
            return null;
        }

        var pdbLayout = PdbStructLayouts.Get(ImadFormType);
        if (pdbLayout is not { StructSize: 0x748 })
        {
            failure = "embedded MemDebug IMAD PDB layout is absent or changed";
            return null;
        }

        return new PdbStructView(_fields, pdbLayout, buffer, entry.TesFormOffset.Value, entry)
            .WithShift(Owner, runtimeLayout.OwnerShift);
    }

    private static bool ValidatePdbGeometry(
        PdbStructView view,
        RuntimeLayout runtimeLayout,
        out string failure)
    {
        failure = string.Empty;
        var dataOffset = view.Offset("Data", Owner);
        var interpolatorOffset = view.Offset("ppInterpolator", Owner);
        var parameterPointerOffset = view.Offset("pppFloatKey", Owner);
        var firstNamedPointerOffset = view.Offset("pBlurFloatKey", Owner);
        var lastNamedPointerOffset = view.Offset("pMotionBlurStrengthFloatKey", Owner);
        var nameOffset = view.Offset("strName", Owner);
        var firstNamedInterpolatorOffset = view.Offset("BlurInterpolator", Owner);

        if (dataOffset != runtimeLayout.DataOffset
            || interpolatorOffset != runtimeLayout.InterpolatorOffset
            || parameterPointerOffset != runtimeLayout.ParameterKeyPointerOffset
            || firstNamedPointerOffset != runtimeLayout.FirstNamedKeyPointerOffset
            || lastNamedPointerOffset != runtimeLayout.LastNamedKeyPointerOffset
            || nameOffset != runtimeLayout.NameOffset
            || interpolatorOffset + ParameterTableCount * 24 != firstNamedInterpolatorOffset
            || parameterPointerOffset + ParameterTableCount * 4 != firstNamedPointerOffset
            || lastNamedPointerOffset + 4 != nameOffset
            || dataOffset + DataSize != interpolatorOffset)
        {
            failure = "PDB-derived array geometry does not match the evidenced layout";
            return false;
        }

        return true;
    }

    private static bool TryReadData(
        byte[] buffer,
        int dataOffset,
        out ImageSpaceModifierData data,
        out string failure)
    {
        data = new ImageSpaceModifierData();
        failure = string.Empty;
        if (dataOffset < 0 || dataOffset + DataSize > buffer.Length)
        {
            failure = "Data block is truncated";
            return false;
        }

        var source = buffer.AsSpan(dataOffset, DataSize);
        var animatable = source[0];
        var duration = BinaryPrimitives.ReadSingleBigEndian(source.Slice(4, 4));
        var useRadialTarget = source[200];
        var radialCenterX = BinaryPrimitives.ReadSingleBigEndian(source.Slice(204, 4));
        var radialCenterY = BinaryPrimitives.ReadSingleBigEndian(source.Slice(208, 4));
        var useDofTarget = source[224];
        var dofFlags = source[225];
        if (animatable > 1 || useRadialTarget > 1 || useDofTarget > 1
            || !float.IsFinite(duration) || duration < 0f
            || !float.IsFinite(radialCenterX) || !float.IsFinite(radialCenterY)
            || ContainsNonZero(source.Slice(1, 3))
            || ContainsNonZero(source.Slice(201, 3))
            || ContainsNonZero(source.Slice(226, 2)))
        {
            failure = "Data flags, padding, or floats are implausible";
            return false;
        }

        var payload = new uint[59];
        foreach (var tableLayout in ImageSpaceModifierCaptureValidator.FrameTableLayouts)
        {
            var countOffset = 8 + tableLayout.CountIndex * 4;
            var count = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(countOffset, 4));
            if (count > MaxKeysPerTable)
            {
                failure = $"{DisplaySignature(tableLayout.Signature)} count {count} exceeds " +
                          $"the {MaxKeysPerTable}-key safety cap";
                return false;
            }

            payload[tableLayout.CountIndex] = count;
        }

        payload[48] = useRadialTarget;
        payload[49] = BitConverter.SingleToUInt32Bits(radialCenterX);
        payload[50] = BitConverter.SingleToUInt32Bits(radialCenterY);
        payload[54] = (uint)(useDofTarget | (dofFlags << 8));
        data = new ImageSpaceModifierData
        {
            AnimatableFlag = animatable,
            Duration = duration,
            RawPayload = payload
        };
        return true;
    }

    private static bool ContainsNonZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int? ResolveTablePointerOffset(
        PdbStructView view,
        ImageSpaceModifierCaptureValidator.FrameTableLayout tableLayout,
        out string failure)
    {
        failure = string.Empty;
        if (tableLayout.CountIndex < ParameterTableCount)
        {
            var baseOffset = view.Offset("pppFloatKey", Owner);
            if (baseOffset.HasValue)
            {
                return baseOffset.Value + tableLayout.CountIndex * 4;
            }
        }
        else if (NamedPointerFields.TryGetValue(tableLayout.Signature, out var field))
        {
            var pointerOffset = view.Offset(field, Owner);
            if (pointerOffset.HasValue)
            {
                return pointerOffset;
            }
        }

        failure = "key-pointer field is absent from the PDB layout";
        return null;
    }

    private bool TryReadKeyTable(
        byte[] objectBuffer,
        int pointerOffset,
        uint count,
        int elementSize,
        bool animatable,
        out byte[] canonicalBytes,
        out string failure)
    {
        canonicalBytes = [];
        failure = string.Empty;
        if (pointerOffset < 0 || pointerOffset + 4 > objectBuffer.Length)
        {
            failure = "key-pointer slot is outside the object";
            return false;
        }

        var pointer = BinaryUtils.ReadUInt32BE(objectBuffer, pointerOffset);
        if (pointer == 0 || !_context.IsValidPointer(pointer))
        {
            failure = count == 0 ? string.Empty : "positive count has a null or unmapped pointer";
            return count == 0;
        }

        var byteCount = checked((int)count * elementSize);
        var source = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(pointer), byteCount);
        if (source == null || source.Length != byteCount)
        {
            failure = $"key array is not fully captured ({byteCount} bytes required)";
            return false;
        }

        canonicalBytes = new byte[byteCount];
        for (var offset = 0; offset < byteCount; offset += 4)
        {
            var bits = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(canonicalBytes.AsSpan(offset, 4), bits);
        }

        if (!ImageSpaceModifierCaptureValidator.AreFrameTableKeysValid(
                canonicalBytes, elementSize, false, animatable, out failure))
        {
            canonicalBytes = [];
            return false;
        }

        return true;
    }

    private bool TryReadOptionalSound(
        byte[] objectBuffer,
        int pointerOffset,
        out uint? formId,
        out string failure)
    {
        formId = null;
        failure = string.Empty;
        if (pointerOffset < 0 || pointerOffset + 4 > objectBuffer.Length)
        {
            failure = "sound-pointer slot is outside the object";
            return false;
        }

        var pointer = BinaryUtils.ReadUInt32BE(objectBuffer, pointerOffset);
        if (pointer == 0)
        {
            return true;
        }

        formId = _context.FollowPointerToFormId(objectBuffer, pointerOffset, SoundFormType);
        if (!formId.HasValue)
        {
            failure = $"non-null sound pointer at +0x{pointerOffset:X} is invalid or not SOUN";
            return false;
        }

        return true;
    }

    private static byte[] EncodeCanonicalDnam(ImageSpaceModifierData data)
    {
        var bytes = new byte[DataSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), data.AnimatableFlag);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), data.Duration);
        for (var i = 0; i < data.RawPayload.Count && i < 59; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + i * 4, 4), data.RawPayload[i]);
        }

        return bytes;
    }

    private static byte[] FormIdBytes(uint formId)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, formId);
        return bytes;
    }

    private static ImageSpaceModifierFloatKey[] ReadFloatKeys(byte[] bytes)
    {
        var result = new ImageSpaceModifierFloatKey[bytes.Length / 8];
        for (var i = 0; i < result.Length; i++)
        {
            var offset = i * 8;
            result[i] = new ImageSpaceModifierFloatKey(
                BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4, 4)));
        }

        return result;
    }

    private static ImageSpaceModifierColorKey[] ReadColorKeys(byte[] bytes)
    {
        var result = new ImageSpaceModifierColorKey[bytes.Length / 20];
        for (var i = 0; i < result.Length; i++)
        {
            var offset = i * 20;
            result[i] = new ImageSpaceModifierColorKey(
                BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 8, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 12, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 16, 4)));
        }

        return result;
    }

    private static string DisplaySignature(string signature)
    {
        return signature.Length == 4 && char.IsControl(signature[0])
            ? $"0x{(int)signature[0]:X2}IAD"
            : signature;
    }

    private sealed record RuntimeLayout(
        string Name,
        int StructSize,
        int OwnerShift,
        int DataOffset,
        int InterpolatorOffset,
        int ParameterKeyPointerOffset,
        int FirstNamedKeyPointerOffset,
        int LastNamedKeyPointerOffset,
        int NameOffset,
        int? OutroSoundOffset,
        int? IntroSoundOffset);

    private sealed record RuntimeNameObservation(
        string? Value,
        RuntimeMemoryContext.BSStringFailure Failure,
        BSStringDiagnostics.DiagSample Sample);

    /// <summary>
    ///     Keeps the control-byte signature mapping beside the runtime reconstruction without
    ///     exposing parsing-handler internals across namespaces.
    /// </summary>
    private static class MiscEnvironmentHandlerSignature
    {
        internal static bool TryParameter(
            string signature,
            out ImageSpaceModifierParameter parameter,
            out ImageSpaceModifierOperation operation)
        {
            if (signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A'
                && signature[3] == 'D')
            {
                var prefix = signature[0];
                if (prefix <= '\u0014')
                {
                    parameter = (ImageSpaceModifierParameter)prefix;
                    operation = ImageSpaceModifierOperation.Multiply;
                    return true;
                }

                if (prefix is >= '@' and <= 'T')
                {
                    parameter = (ImageSpaceModifierParameter)(prefix - '@');
                    operation = ImageSpaceModifierOperation.Add;
                    return true;
                }
            }

            parameter = default;
            operation = default;
            return false;
        }
    }
}
