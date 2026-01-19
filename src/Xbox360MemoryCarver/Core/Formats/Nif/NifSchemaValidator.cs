// Schema validator for NIF block converters
// Compares manual converter implementations against nif.xml definitions

using System.Globalization;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Validates manual NIF block converters against schema definitions.
///     Useful for catching bugs like missing inherited fields.
/// </summary>
public static class NifSchemaValidator
{
    /// <summary>
    ///     Calculates the minimum size of a block type from its schema definition.
    ///     Returns null if the block has variable-size fields.
    /// </summary>
    public static int? CalculateMinSize(NifSchema schema, NifObjectDef objDef)
    {
        var totalSize = 0;

        foreach (var field in objDef.AllFields)
        {
            // Skip conditional fields for minimum size
            if (field.VersionCond != null || field.Condition != null)
            {
                continue;
            }

            // Arrays have dynamic size
            if (field.Length != null && !int.TryParse(field.Length, out _))
            {
                return null;
            }

            var fieldSize = GetFieldSize(schema, field);
            if (fieldSize == null)
            {
                return null;
            }

            if (field.Length != null && int.TryParse(field.Length, out var count))
            {
                totalSize += fieldSize.Value * count;
            }
            else
            {
                totalSize += fieldSize.Value;
            }
        }

        return totalSize;
    }

    private static int? GetFieldSize(NifSchema schema, NifFieldDef field)
    {
        var size = schema.GetTypeSize(field.Type);
        if (size.HasValue)
        {
            return size.Value;
        }

        // Check if it's a struct
        var structDef = schema.GetStruct(field.Type);
        if (structDef?.FixedSize.HasValue == true)
        {
            return structDef.FixedSize.Value;
        }

        return null;
    }

    /// <summary>
    ///     Lists all fields for a block type with their sizes.
    ///     Useful for debugging converter implementations.
    /// </summary>
    public static string GetBlockFieldLayout(NifSchema schema, string blockType)
    {
        var objDef = schema.GetObject(blockType);
        if (objDef == null)
        {
            return $"Unknown block type: {blockType}";
        }

        var lines = new List<string>
        {
            $"Block: {blockType}",
            $"Inherits: {objDef.Inherit ?? "(none)"}",
            $"Fields ({objDef.AllFields.Count} total):",
            ""
        };

        var offset = 0;
        foreach (var field in objDef.AllFields)
        {
            var size = GetFieldSize(schema, field);
            lines.Add(FormatFieldLine(field, size, offset));
            offset = UpdateOffset(offset, size, field);
        }

        lines.Add("");
        lines.Add(
            $"Total fixed size: {(offset >= 0 ? offset.ToString(CultureInfo.InvariantCulture) : "variable")} bytes");

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatFieldLine(NifFieldDef field, int? size, int offset)
    {
        var sizeStr = size.HasValue ? $"{size.Value}" : "?";
        var conditional = GetConditionalString(field);
        var arrayInfo = field.Length != null ? $"[{field.Length}]" : "";
        return $"  +{offset,4:X4}: {field.Name,-30} {field.Type}{arrayInfo,-20} ({sizeStr} bytes){conditional}";
    }

    private static string GetConditionalString(NifFieldDef field)
    {
        if (field.VersionCond != null)
        {
            return $" [vercond: {field.VersionCond}]";
        }

        if (field.Condition != null)
        {
            return $" [cond: {field.Condition}]";
        }

        return "";
    }

    private static int UpdateOffset(int offset, int? size, NifFieldDef field)
    {
        if (!size.HasValue)
        {
            return -1;
        }

        if (field.Length == null)
        {
            return offset + size.Value;
        }

        if (int.TryParse(field.Length, out var count))
        {
            return offset + size.Value * count;
        }

        return -1;
    }
}
