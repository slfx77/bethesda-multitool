using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm;

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