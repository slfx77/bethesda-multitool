using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>
///     Reads the identity fields from a captured <c>TESForm</c> subobject.
///     Bethesda's form maps store <c>TESForm*</c>, including for complete objects whose
///     multiple-inheritance layout places that subobject after another base. Therefore the
///     map value itself is already the canonical base for <c>cFormType</c> at +4 and
///     <c>iFormID</c> at +12; complete-object offsets must not be probed here.
///     The header validates when its <c>formType</c> byte is recognized by
///     <see cref="RuntimeBuildOffsets.GetRecordTypeCode" /> AND its <c>formId</c>
///     looks valid (non-zero, not 0xFFFFFFFF). When an expected FormID is supplied,
///     the header must additionally match it — used by the pAllForms walker that
///     already knows the key FormID for each entry.
/// </summary>
internal static class TesFormHeaderProbe
{
    internal const int FormTypeOffset = 4;
    internal const int FormIdOffset = 12;

    /// <summary>Minimum TESForm-subobject prefix containing both identity fields.</summary>
    internal const int RequiredBufferSize = 16;

    /// <summary>
    ///     Returns true and populates <paramref name="formType" /> + <paramref name="formId" />
    ///     when the canonical TESForm-subobject header validates against the supplied buffer. When
    ///     <paramref name="expectedFormId" /> is non-null, the probe additionally requires
    ///     the header's FormID to match it (e.g., the pAllForms walker's key-vs-struct
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

        if (buffer.Length < RequiredBufferSize)
        {
            return false;
        }

        var candidateType = buffer[FormTypeOffset];
        if (RuntimeBuildOffsets.GetRecordTypeCode(candidateType) is null)
        {
            return false;
        }

        var candidateId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (candidateId is 0 or 0xFFFFFFFF)
        {
            return false;
        }

        if (expectedFormId.HasValue && candidateId != expectedFormId.Value)
        {
            return false;
        }

        formType = candidateType;
        formId = candidateId;
        return true;
    }
}
