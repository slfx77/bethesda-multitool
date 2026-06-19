namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>How a runtime string was attributed to an owning record/struct during ownership analysis.</summary>
public enum ClaimSource
{
    RawRecordSubrecord,
    RuntimeStructField,
    TextContentMatch,
    SecondPassVtable,
    SecondPassReverse,
    SecondPassReverseRelaxed,
    ManagerGlobal,
    RuntimeEditorId
}
