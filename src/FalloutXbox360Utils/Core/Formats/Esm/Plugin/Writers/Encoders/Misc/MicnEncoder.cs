using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a <see cref="MenuIconRecord" /> (MICN) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, ICON(texture path).
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class MicnEncoder : IRecordEncoder
{
    public string RecordType => "MICN";
    public Type ModelType => typeof(MenuIconRecord);

    internal static EncodedRecord EncodeNew(MenuIconRecord micn)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(micn.EditorId))
        {
            warnings.Add($"New MICN 0x{micn.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", micn.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(micn.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", micn.IconPath));
        }
        else
        {
            warnings.Add($"New MICN 0x{micn.FormId:X8} has no icon path — record will not render in-game.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
