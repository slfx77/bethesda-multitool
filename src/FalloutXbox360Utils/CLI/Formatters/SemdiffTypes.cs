namespace FalloutXbox360Utils.CLI.Formatters;

/// <summary>
///     Data types used by the semantic diff command.
/// </summary>
internal static class SemdiffTypes
{
    internal sealed record ParsedRecord(
        string Type,
        uint FormId,
        uint Flags,
        int Offset,
        List<ParsedSubrecord> Subrecords);

    internal sealed record ParsedSubrecord(string Signature, byte[] Data, int Offset);

    internal sealed record RecordDiff(
        uint FormId,
        string RecordType,
        DiffType DiffType,
        ParsedRecord? RecordA,
        ParsedRecord? RecordB,
        List<FieldDiff>? FieldDiffs = null);

    internal sealed record FieldDiff(
        string Signature,
        byte[]? DataA,
        byte[]? DataB,
        string? Message,
        bool BigEndianA,
        bool BigEndianB,
        string RecordType);

    internal enum DiffType
    {
        OnlyInA,
        OnlyInB,
        Different
    }
}
