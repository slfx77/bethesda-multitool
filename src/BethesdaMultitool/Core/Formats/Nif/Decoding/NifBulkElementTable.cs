using BethesdaMultitool.Core.Formats.Nif.Schema;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     The element types whose arrays decode in bulk. Each candidate is admitted only when the loaded schema confirms
///     its shape (a basic type of the component's size, or a fixed-size struct of exactly that many unconditional
///     fields of the component type), so a schema that defines one differently silently falls back to a list of
///     values instead of being mis-read.
/// </summary>
internal static class NifBulkElementTable
{
    private static readonly NifBulkElement[] Candidates =
    [
        new("float", NifBulkComponent.Float32, 1),
        new("Vector2", NifBulkComponent.Float32, 2),
        new("Vector3", NifBulkComponent.Float32, 3),
        new("Vector4", NifBulkComponent.Float32, 4),
        new("Color3", NifBulkComponent.Float32, 3),
        new("Color4", NifBulkComponent.Float32, 4),
        new("TexCoord", NifBulkComponent.Float32, 2),
        new("ushort", NifBulkComponent.UInt16, 1),
        new("Triangle", NifBulkComponent.UInt16, 3),
        new("byte", NifBulkComponent.UInt8, 1),
        new("char", NifBulkComponent.UInt8, 1)
    ];

    /// <summary>Builds the table of bulk element types the schema confirms.</summary>
    public static IReadOnlyDictionary<string, NifBulkElement> Build(NifSchema schema)
    {
        var table = new Dictionary<string, NifBulkElement>(StringComparer.Ordinal);
        foreach (var candidate in Candidates)
        {
            if (IsConfirmed(schema, candidate))
            {
                table[candidate.TypeName] = candidate;
            }
        }

        return table;
    }

    private static bool IsConfirmed(NifSchema schema, NifBulkElement candidate)
    {
        var componentType = candidate.Component switch
        {
            NifBulkComponent.Float32 => "float",
            NifBulkComponent.UInt16 => "ushort",
            _ => "byte"
        };

        if (candidate.ComponentsPerElement == 1)
        {
            return schema.BasicTypes.TryGetValue(candidate.TypeName, out var basic) &&
                   string.Equals(basic.Name, candidate.TypeName, StringComparison.Ordinal) &&
                   basic.Size == candidate.ComponentSize;
        }

        if (!schema.Structs.TryGetValue(candidate.TypeName, out var structDef) ||
            structDef.FixedSize != candidate.ElementSize ||
            structDef.Fields.Count != candidate.ComponentsPerElement)
        {
            return false;
        }

        foreach (var field in structDef.Fields)
        {
            if (!string.Equals(field.Type, componentType, StringComparison.Ordinal) ||
                field.Length is not null || field.Width is not null || field.Condition is not null ||
                field.VersionCond is not null || field.Since is not null || field.Until is not null ||
                field.OnlyT is not null || field.ExcludeT is not null || field.Arg is not null ||
                field.Template is not null)
            {
                return false;
            }
        }

        return true;
    }
}
