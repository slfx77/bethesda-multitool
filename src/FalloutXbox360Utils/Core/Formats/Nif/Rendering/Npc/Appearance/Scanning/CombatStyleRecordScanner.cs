using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class CombatStyleRecordScanner
{
    // CSSD layout: 64 bytes total. Weapon Restrictions is uint32 at offset 40.
    // 0=None, 1=MeleeOnly, 2=RangedOnly. See:
    // https://tes5edit.github.io/fopdoc/Fallout3/Records/CSTY.html
    private const int WeaponRestrictionsOffset = 40;

    internal static CstyEntry? Process(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record)
    {
        var recordData = NpcRecordDataReader.ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
        {
            return null;
        }

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
        var restriction = WeaponRestriction.None;

        foreach (var subrecord in subrecords)
        {
            if (subrecord.Signature == "CSSD" &&
                subrecord.Data.Length >= WeaponRestrictionsOffset + 4)
            {
                var raw = BinaryUtils.ReadUInt32(subrecord.Data, WeaponRestrictionsOffset, bigEndian);
                restriction = raw switch
                {
                    1 => WeaponRestriction.MeleeOnly,
                    2 => WeaponRestriction.RangedOnly,
                    _ => WeaponRestriction.None
                };
                break;
            }
        }

        return new CstyEntry(restriction);
    }
}
