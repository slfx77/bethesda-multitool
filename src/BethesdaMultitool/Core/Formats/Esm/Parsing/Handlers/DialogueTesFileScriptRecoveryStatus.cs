namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal enum DialogueTesFileScriptRecoveryStatus
{
    NoTesFileOffset,
    UncalibratedBase,
    MappedPageMissing,
    HeaderReadFailed,
    PayloadReadFailed,
    RecordTooLarge,
    SignatureMismatch,
    FormIdMismatch,
    CompressedRecord,
    NoScriptSubrecords,
    Recovered
}
