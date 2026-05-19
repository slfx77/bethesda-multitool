using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="DebrisRecord" /> (DEBR) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, [DATA + MODT?]* per variant. DATA is a structured byte
///     stream: 1B percentage + null-terminated path + 1B flags. MODT (if present) carries
///     the texture hash for the same model.
///     Our model only captures the model paths; per-variant flags + texture hashes are not
///     modeled. Emit minimal DATA with percentage=100 + path + flags=0; warn that detailed
///     properties are deferred.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class DebrEncoder : IRecordEncoder
{
    public string RecordType => "DEBR";
    public Type ModelType => typeof(DebrisRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(DebrisRecord debr)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(debr.EditorId))
        {
            warnings.Add($"New DEBR 0x{debr.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", debr.EditorId ?? string.Empty));

        if (debr.ModelPaths.Count == 0 && debr.VariantCount > 0)
        {
            warnings.Add(
                $"New DEBR 0x{debr.FormId:X8} reports {debr.VariantCount} variants but no model paths captured.");
        }

        foreach (var modelPath in debr.ModelPaths)
        {
            subs.Add(new EncodedSubrecord("DATA", BuildVariantDataSubrecord(modelPath ?? string.Empty)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     DEBR DATA layout per variant: 1B percentage + null-terminated path + 1B flags.
    ///     Percentage defaults to 100, flags default to 0 — exact values aren't modeled.
    /// </summary>
    private static byte[] BuildVariantDataSubrecord(string modelPath)
    {
        var pathByteCount = Encoding.Latin1.GetByteCount(modelPath);
        var data = new byte[1 + pathByteCount + 1 + 1];
        data[0] = 100; // percentage
        Encoding.Latin1.GetBytes(modelPath, 0, modelPath.Length, data, 1);
        data[1 + pathByteCount] = 0; // null terminator
        data[1 + pathByteCount + 1] = 0; // flags
        return data;
    }
}
