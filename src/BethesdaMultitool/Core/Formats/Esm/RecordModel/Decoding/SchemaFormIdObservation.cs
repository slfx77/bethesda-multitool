namespace BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;

/// <summary>A non-zero reference at an exact byte offset in one physical subrecord payload.</summary>
public sealed record SchemaFormIdObservation(string SubrecordSignature, int SubrecordOrdinal,
    string Field, int FieldOffset, uint FormId, SchemaFormIdOrigin Origin);

public enum SchemaFormIdOrigin
{
    TypedField,
    UnionFallback
}
