namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>How a FaceGen TRI record's payload bytes are interpreted (opaque blob, float3 vectors, or uint32 values).</summary>
internal enum TriRecordPayloadKind
{
    Opaque = 0,
    Float3 = 1,
    UInt32 = 2
}
