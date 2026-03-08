using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Legacy façade over record-specific NPC appearance scanners.
/// </summary>
internal static class NpcEsmRecordParsers
{
    internal static NpcScanEntry? ProcessNpcRecord(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record) =>
        NpcRecordScanner.Process(esmData, bigEndian, record);

    internal static RaceScanEntry? ProcessRaceRecord(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record) =>
        RaceRecordScanner.Process(esmData, bigEndian, record);

    internal static HairScanEntry? ProcessHairRecord(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record) =>
        HairRecordScanner.Process(esmData, bigEndian, record);

    internal static EyesScanEntry? ProcessEyesRecord(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record) =>
        EyesRecordScanner.Process(esmData, bigEndian, record);

    internal static HdptScanEntry? ProcessHdptRecord(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record) =>
        HeadPartRecordScanner.Process(esmData, bigEndian, record);

    internal static ArmoScanEntry? ProcessArmoRecord(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record) =>
        ArmorRecordScanner.Process(esmData, bigEndian, record);

    internal static WeapScanEntry? ProcessWeapRecord(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record) =>
        WeaponRecordScanner.Process(esmData, bigEndian, record);

    internal static List<uint>? ProcessLvliRecord(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record) =>
        LeveledListRecordScanner.Process(esmData, bigEndian, record);
}
