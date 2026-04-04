namespace FalloutXbox360Utils.Core.RuntimeBuffer;

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
