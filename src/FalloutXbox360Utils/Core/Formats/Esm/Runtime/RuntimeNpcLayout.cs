using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Dump-specific TESNPC layout selection.
///     CoreShift drives gameplay/stat fields. AppearanceShift drives the main head-appearance
///     fields (hair, eyes, combat style, head parts). LateAppearanceShift covers the
///     original-race/face-NPC/height tail, which can drift independently in debug builds.
/// </summary>
internal readonly record struct RuntimeNpcLayout(
    int CoreShift,
    int AppearanceShift,
    int LateAppearanceShift,
    int ReadSize,
    RuntimeNpcFaceGenArrayMode FaceGenMode,
    RuntimeNpcFaceGenFieldLayout Fggs,
    RuntimeNpcFaceGenFieldLayout Fgga,
    RuntimeNpcFaceGenFieldLayout Fgts)
{
    public static RuntimeNpcLayout CreateDefault(MinidumpInfo minidumpInfo)
    {
        var shift = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(minidumpInfo));
        return CreateDirect(shift, shift, 640);
    }

    public static RuntimeNpcLayout CreateDirect(
        int coreShift,
        int appearanceShift,
        int readSize,
        int? lateAppearanceShift = null)
    {
        // Byte-verified against the captured Nov 2009 - Apr 2010 dumps (2026-06-12,
        // ~4000 struct instances per dump): the runtime FR2MatrixVTC<float> in these
        // builds is a 32-byte record { proxyPtr, 0, 0, _Myfirst, _Mylast, _Myend,
        // nrows, ncols } (iterator-debug STL config), NOT the Aug 22 2010 PDB's
        // 24-byte layout padded by alignment as an earlier comment claimed. The
        // TESNPC FaceGen block (RaceFaceOffsetCoord) sits at +324/+356/+388 (+420 =
        // unused texture-asym stream) with the default appearanceShift of 16 already
        // folded in here via the 320/352/384 bases: pointer = record+12 (_Myfirst),
        // count = record+24 (nrows). The Aug 22 MemDebug XEX itself uses the 24-byte
        // layout (block at +0x144, override ptr +0x1A4) — no captured dump does.
        return new RuntimeNpcLayout(
            coreShift,
            appearanceShift,
            lateAppearanceShift ?? appearanceShift,
            readSize,
            RuntimeNpcFaceGenArrayMode.DirectPointerCount,
            new RuntimeNpcFaceGenFieldLayout(320 + appearanceShift, 332 + appearanceShift),
            new RuntimeNpcFaceGenFieldLayout(352 + appearanceShift, 364 + appearanceShift),
            new RuntimeNpcFaceGenFieldLayout(384 + appearanceShift, 396 + appearanceShift));
    }

    public static RuntimeNpcLayout CreatePrimitiveArrayDebug(
        int coreShift,
        int appearanceShift,
        int readSize,
        int? lateAppearanceShift = null)
    {
        return new RuntimeNpcLayout(
            coreShift,
            appearanceShift,
            lateAppearanceShift ?? appearanceShift,
            readSize,
            RuntimeNpcFaceGenArrayMode.PrimitiveArray,
            new RuntimeNpcFaceGenFieldLayout(332, 344, 336),
            new RuntimeNpcFaceGenFieldLayout(360, 372, 364),
            new RuntimeNpcFaceGenFieldLayout(388, 400, 392));
    }
}
