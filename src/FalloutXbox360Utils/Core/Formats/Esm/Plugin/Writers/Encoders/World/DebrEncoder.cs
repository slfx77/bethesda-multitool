using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="DebrisRecord" /> (DEBR) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, [DATA + MODT?]* per variant. DATA is a structured byte
///     stream: 1B percentage + null-terminated path + 1B flags. MODT (if present) carries
///     the texture hash for the same model.
///     DEBR DATA now models percentage + path + flags. MODT texture hashes remain opaque and
///     are retained from master on override paths.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class DebrEncoder : IRecordEncoder
{
    public string RecordType => "DEBR";
    public Type ModelType => typeof(DebrisRecord);

    internal static EncodedRecord EncodeNew(DebrisRecord debr)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(debr.EditorId))
        {
            warnings.Add($"New DEBR 0x{debr.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", debr.EditorId ?? string.Empty));

        if (debr.Variants.Count == 0 && debr.ModelPaths.Count == 0 && debr.VariantCount > 0)
        {
            warnings.Add(
                $"New DEBR 0x{debr.FormId:X8} reports {debr.VariantCount} variants but no model paths captured.");
        }

        if (debr.Variants.Count > 0)
        {
            foreach (var variant in debr.Variants)
            {
                subs.Add(new EncodedSubrecord("DATA", BuildVariantDataSubrecord(variant)));
            }
        }
        else
        {
            foreach (var modelPath in debr.ModelPaths)
            {
                subs.Add(new EncodedSubrecord("DATA",
                    BuildVariantDataSubrecord(new DebrisVariantData(100, modelPath ?? string.Empty, 0))));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     DEBR DATA layout per variant: 1B percentage + null-terminated path + 1B flags.
    ///     Legacy runtime-only records may still arrive with just a model path; callers synthesize
    ///     percentage=100 and flags=0 before reaching this method.
    /// </summary>
    private static byte[] BuildVariantDataSubrecord(DebrisVariantData variant)
    {
        var modelPath = variant.ModelPath ?? string.Empty;
        var pathByteCount = Encoding.Latin1.GetByteCount(modelPath);
        var data = new byte[1 + pathByteCount + 1 + 1];
        data[0] = variant.Percentage;
        Encoding.Latin1.GetBytes(modelPath, 0, modelPath.Length, data, 1);
        data[1 + pathByteCount] = 0; // null terminator
        data[1 + pathByteCount + 1] = variant.Flags;
        return data;
    }
}
