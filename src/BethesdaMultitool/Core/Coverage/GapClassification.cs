namespace BethesdaMultitool.Core.Coverage;

/// <summary>The inferred content type of an unrecognized coverage gap, from sampling its bytes.</summary>
public enum GapClassification
{
    ZeroFill,
    AsciiText,
    StringPool,
    PointerDense,
    AssetManagement,
    RecordSignature,
    BinaryData
}
