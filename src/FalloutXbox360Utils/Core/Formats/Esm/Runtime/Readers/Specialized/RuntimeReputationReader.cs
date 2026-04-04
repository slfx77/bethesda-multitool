using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESReputation (REPU, 96 bytes, FormType 0x68).
///     Reads full name, positive/negative threshold values.
/// </summary>
internal sealed class RuntimeReputationReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeReputationReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public ReputationRecord? ReadRuntimeReputation(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != RepuFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, StructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, FullNameOffset);
        var positiveValue = BinaryUtils.ReadFloatBE(buffer, PositiveValueOffset);
        var negativeValue = BinaryUtils.ReadFloatBE(buffer, NegativeValueOffset);

        if (!RuntimeMemoryContext.IsNormalFloat(positiveValue))
        {
            positiveValue = 0f;
        }

        if (!RuntimeMemoryContext.IsNormalFloat(negativeValue))
        {
            negativeValue = 0f;
        }

        return new ReputationRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            PositiveValue = positiveValue,
            NegativeValue = negativeValue,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte RepuFormType = 0x68;
    private const int StructSize = 96;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44; // TESFullName.cFullName BSStringT
    private const int PositiveValueOffset = 84; // fPositiveValue float32
    private const int NegativeValueOffset = 88; // fNegativeValue float32

    #endregion
}
