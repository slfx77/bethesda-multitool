using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Records;

/// <summary>
///     Probes candidate offsets for TESForm header fields (cFormType + iFormID)
///     to handle Bethesda classes whose multiple-inheritance order places TESForm
///     after other bases. Three layouts cover every known FormType:
///     <list type="bullet">
///         <item><c>(+4, +12)</c> — standard TESForm-first (~99% of types).</item>
///         <item><c>(+24, +32)</c> — TESFullName-first (MSTT / BGSMovableStatic).</item>
///         <item><c>(+16, +24)</c> — TESProduceForm-first (FLOR / TESFlora).</item>
///     </list>
///     A candidate validates when its <c>formType</c> byte is recognized by
///     <see cref="RuntimeBuildOffsets.GetRecordTypeCode" /> AND its <c>formId</c>
///     looks valid (non-zero, not 0xFFFFFFFF). When an expected FormID is supplied,
///     the candidate must additionally match it — used by the pAllForms walker that
///     already knows the key FormID for each entry.
///     Heap pointer high-bytes on Xbox 360 sit in the 0x82..0x9F range — all above
///     the max known FormType (~0x7F) — so the standard probe naturally fails on
///     multi-inheritance objects and falls through to the alternates without
///     misclassification.
/// </summary>
internal static class TesFormHeaderProbe
{
    /// <summary>Minimum buffer length required to probe every candidate (iFormID @ +32, 4 bytes wide).</summary>
    internal const int RequiredBufferSize = 36;

    private static readonly (int FormTypeOffset, int FormIdOffset)[] Candidates =
    [
        (4, 12),   // Standard TESForm-first
        (24, 32),  // MSTT / TESFullName-first
        (16, 24)   // FLOR / TESProduceForm-first
    ];

    /// <summary>
    ///     Returns true and populates <paramref name="formType" /> + <paramref name="formId" />
    ///     when one of the candidate layouts validates against the supplied buffer. When
    ///     <paramref name="expectedFormId" /> is non-null, the probe additionally requires
    ///     the candidate's FormID to match it (e.g., the pAllForms walker's key-vs-struct
    ///     consistency check).
    /// </summary>
    internal static bool TryProbe(
        ReadOnlySpan<byte> buffer,
        out byte formType,
        out uint formId,
        uint? expectedFormId = null)
    {
        formType = 0;
        formId = 0;

        foreach (var (typeOffset, idOffset) in Candidates)
        {
            if (idOffset + 4 > buffer.Length)
            {
                continue;
            }

            var candidateType = buffer[typeOffset];
            if (RuntimeBuildOffsets.GetRecordTypeCode(candidateType) is null)
            {
                continue;
            }

            var candidateId = BinaryUtils.ReadUInt32BE(buffer, idOffset);
            if (candidateId is 0 or 0xFFFFFFFF)
            {
                continue;
            }

            if (expectedFormId.HasValue && candidateId != expectedFormId.Value)
            {
                continue;
            }

            formType = candidateType;
            formId = candidateId;
            return true;
        }

        return false;
    }
}
