namespace BethesdaMultitool.Core.RuntimeBuffer;

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
