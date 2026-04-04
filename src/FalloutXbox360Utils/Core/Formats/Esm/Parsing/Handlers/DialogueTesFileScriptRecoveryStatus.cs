namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal enum DialogueTesFileScriptRecoveryStatus
{
    NoTesFileOffset,
    UncalibratedBase,
    MappedPageMissing,
    HeaderReadFailed,
    SignatureMismatch,
    FormIdMismatch,
    CompressedRecord,
    NoScriptSubrecords,
    Recovered
}
