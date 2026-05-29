using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Reference walker for NAVM. The cross-references live inside the NVEX subrecord:
///     a packed array of 10-byte entries, each carrying one target NAVM FormID at
///     offset 0..3. Walks the raw bytes since the planner doesn't have a typed NVEX
///     model.
/// </summary>
public sealed class NavmReferenceWalker : IRecordReferenceWalker
{
    private const int NvexEntryStride = 10;

    public string RecordType => "NAVM";
    public Type ModelType => typeof(NavMeshRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not NavMeshRecord navm)
        {
            yield break;
        }

        foreach (var subrecord in navm.RawSubrecords)
        {
            if (subrecord.Signature != "NVEX")
            {
                continue;
            }

            var bytes = subrecord.Bytes;
            var entryCount = bytes.Length / NvexEntryStride;
            for (var i = 0; i < entryCount; i++)
            {
                var offset = i * NvexEntryStride;
                if (offset + 4 > bytes.Length)
                {
                    break;
                }

                var targetFormId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
                yield return new RawReference
                {
                    FieldPath = FieldPath.Indexed("NVEX", i),
                    FormId = targetFormId,
                };
            }
        }
    }
}
