using System.Reflection;
using System.Xml.Linq;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Parses nif.xml schema to understand NIF block structures for endian conversion.
///     Based on NifSkope's nif.xml format specification.
/// </summary>
public sealed class NifXmlSchema
{
    private static NifXmlSchema? _instance;
    private static readonly object Lock = new();

    /// <summary>Basic types with their sizes in bytes.</summary>
    public IReadOnlyDictionary<string, int> BasicTypes { get; }

    /// <summary>Enum types with their storage type.</summary>
    public IReadOnlyDictionary<string, string> EnumTypes { get; }

    /// <summary>Bitflag types with their storage type.</summary>
    public IReadOnlyDictionary<string, string> BitflagTypes { get; }

    /// <summary>Compound struct definitions.</summary>
    public IReadOnlyDictionary<string, NifStruct> Structs { get; }

    /// <summary>NiObject block definitions.</summary>
    public IReadOnlyDictionary<string, NifBlock> Blocks { get; }

    /// <summary>Gets the singleton instance, loading from embedded resource.</summary>
    public static NifXmlSchema Instance
    {
        get
        {
            if (_instance is null)
            {
                lock (Lock)
                {
                    _instance ??= LoadFromEmbeddedResource();
                }
            }

            return _instance;
        }
    }

    private NifXmlSchema(
        Dictionary<string, int> basicTypes,
        Dictionary<string, string> enumTypes,
        Dictionary<string, string> bitflagTypes,
        Dictionary<string, NifStruct> structs,
        Dictionary<string, NifBlock> blocks)
    {
        BasicTypes = basicTypes;
        EnumTypes = enumTypes;
        BitflagTypes = bitflagTypes;
        Structs = structs;
        Blocks = blocks;
    }

    private static NifXmlSchema LoadFromEmbeddedResource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "Xbox360MemoryCarver.nif.xml";

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found");

        var doc = XDocument.Load(stream);
        return Parse(doc);
    }

    /// <summary>Load schema from a file path (for testing).</summary>
    public static NifXmlSchema LoadFromFile(string path)
    {
        var doc = XDocument.Load(path);
        return Parse(doc);
    }

    private static NifXmlSchema Parse(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidOperationException("Invalid nif.xml: no root element");

        var basicTypes = ParseBasicTypes(root);
        var enumTypes = ParseEnumTypes(root);
        var bitflagTypes = ParseBitflagTypes(root);
        var structs = ParseStructs(root);
        var blocks = ParseBlocks(root);

        return new NifXmlSchema(basicTypes, enumTypes, bitflagTypes, structs, blocks);
    }

    private static Dictionary<string, int> ParseBasicTypes(XElement root)
    {
        var types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Built-in types with known sizes
            ["byte"] = 1,
            ["char"] = 1,
            ["bool"] = 1,
            ["ubyte"] = 1,
            ["short"] = 2,
            ["ushort"] = 2,
            ["int"] = 4,
            ["uint"] = 4,
            ["int64"] = 8,
            ["uint64"] = 8,
            ["float"] = 4,
            ["hfloat"] = 2,
            ["Ptr"] = 4,
            ["Ref"] = 4,
            ["StringOffset"] = 4,
            ["NiFixedString"] = 4,
            ["BlockTypeIndex"] = 2,
            ["FileVersion"] = 4,
            ["Flags"] = 2,
            ["ulittle32"] = 4
        };

        // Parse explicit basic type definitions
        foreach (var element in root.Elements("basic"))
        {
            var name = element.Attribute("name")?.Value;
            var sizeStr = element.Attribute("size")?.Value;

            if (name is not null && int.TryParse(sizeStr, out var size))
            {
                types[name] = size;
            }
        }

        return types;
    }

    private static Dictionary<string, string> ParseEnumTypes(XElement root)
    {
        var enums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in root.Elements("enum"))
        {
            var name = element.Attribute("name")?.Value;
            var storage = element.Attribute("storage")?.Value ?? "uint";

            if (name is not null)
            {
                enums[name] = storage;
            }
        }

        return enums;
    }

    private static Dictionary<string, string> ParseBitflagTypes(XElement root)
    {
        var bitflags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in root.Elements("bitflags"))
        {
            var name = element.Attribute("name")?.Value;
            var storage = element.Attribute("storage")?.Value ?? "uint";

            if (name is not null)
            {
                bitflags[name] = storage;
            }
        }

        return bitflags;
    }

    private static Dictionary<string, NifStruct> ParseStructs(XElement root)
    {
        var structs = new Dictionary<string, NifStruct>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in root.Elements("struct"))
        {
            var name = element.Attribute("name")?.Value;
            if (name is null)
            {
                continue;
            }

            var fields = ParseFields(element);
            structs[name] = new NifStruct(name, fields);
        }

        return structs;
    }

    private static Dictionary<string, NifBlock> ParseBlocks(XElement root)
    {
        var blocks = new Dictionary<string, NifBlock>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in root.Elements("niobject"))
        {
            var name = element.Attribute("name")?.Value;
            if (name is null)
            {
                continue;
            }

            var inherit = element.Attribute("inherit")?.Value;
            var isAbstract = element.Attribute("abstract")?.Value == "true";
            var fields = ParseFields(element);

            blocks[name] = new NifBlock(name, inherit, isAbstract, fields);
        }

        return blocks;
    }

    private static List<NifField> ParseFields(XElement parent)
    {
        var fields = new List<NifField>();

        foreach (var element in parent.Elements("field"))
        {
            var name = element.Attribute("name")?.Value ?? "";
            var type = element.Attribute("type")?.Value ?? "uint";
            var template = element.Attribute("template")?.Value;
            var length = element.Attribute("length")?.Value;
            var width = element.Attribute("width")?.Value;
            var cond = element.Attribute("cond")?.Value;
            var vercond = element.Attribute("vercond")?.Value;
            var arg = element.Attribute("arg")?.Value;

            fields.Add(new NifField(name, type, template, length, width, cond, vercond, arg));
        }

        return fields;
    }

    /// <summary>
    ///     Gets the size of a type in bytes, or 0 if variable/unknown.
    /// </summary>
    public int GetTypeSize(string typeName)
    {
        // Check basic types first
        if (BasicTypes.TryGetValue(typeName, out var size))
        {
            return size;
        }

        // Check enums (use storage type)
        if (EnumTypes.TryGetValue(typeName, out var enumStorage))
        {
            return GetTypeSize(enumStorage);
        }

        // Check bitflags (use storage type)
        if (BitflagTypes.TryGetValue(typeName, out var bitflagStorage))
        {
            return GetTypeSize(bitflagStorage);
        }

        // Known compound types with fixed sizes
        return typeName switch
        {
            "Vector3" => 12,       // 3 floats
            "Vector4" => 16,       // 4 floats
            "Quaternion" => 16,    // 4 floats
            "Matrix33" => 36,      // 9 floats
            "Matrix44" => 64,      // 16 floats
            "Color3" => 12,        // 3 floats
            "Color4" => 16,        // 4 floats
            "ByteColor3" => 3,     // 3 bytes
            "ByteColor4" => 4,     // 4 bytes
            "NiBound" => 16,       // Vector3 + float (center + radius)
            "TexCoord" => 8,       // 2 floats
            "Triangle" => 6,       // 3 ushorts
            "MatchGroup" => 0,     // Variable
            "SizedString" => 0,    // Variable (4 byte length + chars)
            "string" => 0,         // Variable
            "ShortString" => 0,    // Variable (1 byte length + chars)
            "ByteArray" => 0,      // Variable
            "ByteMatrix" => 0,     // Variable
            _ => 0                 // Unknown or variable
        };
    }

    /// <summary>
    ///     Determines if a type needs byte swapping (multi-byte numeric).
    /// </summary>
    public bool NeedsSwap(string typeName)
    {
        var size = GetTypeSize(typeName);
        return size is 2 or 4 or 8;
    }

    /// <summary>
    ///     Gets all fields for a block type, including inherited fields.
    /// </summary>
    public List<NifField> GetAllFields(string blockTypeName)
    {
        var allFields = new List<NifField>();
        CollectFields(blockTypeName, allFields, []);
        return allFields;
    }

    private void CollectFields(string typeName, List<NifField> fields, HashSet<string> visited)
    {
        if (!visited.Add(typeName))
        {
            return; // Prevent cycles
        }

        if (Blocks.TryGetValue(typeName, out var block))
        {
            // First collect inherited fields
            if (block.Inherit is not null)
            {
                CollectFields(block.Inherit, fields, visited);
            }

            // Then add this block's fields
            fields.AddRange(block.Fields);
        }
        else if (Structs.TryGetValue(typeName, out var structDef))
        {
            fields.AddRange(structDef.Fields);
        }
    }
}

/// <summary>Represents a NIF struct (compound type) definition.</summary>
public sealed record NifStruct(string Name, List<NifField> Fields);

/// <summary>Represents a NIF block (niobject) definition.</summary>
public sealed record NifBlock(string Name, string? Inherit, bool IsAbstract, List<NifField> Fields);

/// <summary>Represents a field within a struct or block.</summary>
public sealed record NifField(
    string Name,
    string Type,
    string? Template,
    string? Length,
    string? Width,
    string? Condition,
    string? VersionCondition,
    string? Arg);
