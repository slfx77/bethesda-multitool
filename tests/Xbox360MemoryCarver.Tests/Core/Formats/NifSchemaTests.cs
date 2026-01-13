using Xbox360MemoryCarver.Core.Formats.Nif;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Formats;

public class NifSchemaTests
{
    private static NifSchema LoadSchema()
    {
        // Find nif.xml in the source tree
        var baseDir = AppContext.BaseDirectory;
        var xmlPath = Path.Combine(baseDir, "..", "..", "..", "..", "..", "src",
            "Xbox360MemoryCarver", "Core", "Formats", "Nif", "nif.xml");

        if (!File.Exists(xmlPath))
        {
            // Try alternate path
            xmlPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "src",
                "Xbox360MemoryCarver", "Core", "Formats", "Nif", "nif.xml"));
        }

        Assert.True(File.Exists(xmlPath), $"nif.xml not found at {xmlPath}");
        return NifSchema.LoadFromFile(xmlPath);
    }

    [Fact]
    public void Schema_LoadsBasicTypes()
    {
        var schema = LoadSchema();

        Assert.True(schema.BasicTypes.Count >= 20);
        Assert.Contains("uint", schema.BasicTypes.Keys);
        Assert.Contains("ushort", schema.BasicTypes.Keys);
        Assert.Contains("float", schema.BasicTypes.Keys);
        Assert.Contains("Ref", schema.BasicTypes.Keys);
        Assert.Contains("Ptr", schema.BasicTypes.Keys);

        Assert.Equal(4, schema.BasicTypes["uint"].Size);
        Assert.Equal(2, schema.BasicTypes["ushort"].Size);
        Assert.Equal(4, schema.BasicTypes["float"].Size);
        Assert.Equal(4, schema.BasicTypes["Ref"].Size);
        Assert.Equal(4, schema.BasicTypes["Ptr"].Size);
    }

    [Fact]
    public void Schema_LoadsStructs()
    {
        var schema = LoadSchema();

        Assert.True(schema.Structs.Count >= 100);

        var vector3 = schema.GetStruct("Vector3");
        Assert.NotNull(vector3);
        Assert.Equal(12, vector3.FixedSize);
        Assert.Equal(3, vector3.Fields.Count);

        var vector4 = schema.GetStruct("Vector4");
        Assert.NotNull(vector4);
        Assert.Equal(16, vector4.FixedSize);
        Assert.Equal(4, vector4.Fields.Count);
    }

    [Fact]
    public void Schema_LoadsNiObjects()
    {
        var schema = LoadSchema();

        Assert.True(schema.Objects.Count >= 400);
        Assert.Contains("NiNode", schema.Objects.Keys);
        Assert.Contains("bhkCollisionObject", schema.Objects.Keys);
    }

    [Fact]
    public void Schema_ResolvesInheritance_bhkCollisionObject()
    {
        var schema = LoadSchema();

        var bhkColl = schema.GetObject("bhkCollisionObject");
        Assert.NotNull(bhkColl);

        // Should inherit from bhkNiCollisionObject
        Assert.Equal("bhkNiCollisionObject", bhkColl.Inherit);

        // Own fields should be 0 (it's just a concrete version of bhkNiCollisionObject)
        Assert.Empty(bhkColl.Fields);

        // All fields should include inherited fields from the chain:
        // NiObject (0) -> NiCollisionObject (1: Target) -> bhkNiCollisionObject (2: Flags, Body) -> bhkCollisionObject (0)
        Assert.Equal(3, bhkColl.AllFields.Count);

        // Verify field order
        Assert.Equal("Target", bhkColl.AllFields[0].Name);
        Assert.Equal("Ptr", bhkColl.AllFields[0].Type);

        Assert.Equal("Flags", bhkColl.AllFields[1].Name);
        // Type is bhkCOFlags which is a bitflags with ushort storage

        Assert.Equal("Body", bhkColl.AllFields[2].Name);
        Assert.Equal("Ref", bhkColl.AllFields[2].Type);
    }

    [Fact]
    public void Schema_ResolvesInheritance_bhkBlendCollisionObject()
    {
        var schema = LoadSchema();

        var bhkBlend = schema.GetObject("bhkBlendCollisionObject");
        Assert.NotNull(bhkBlend);

        // Should inherit from bhkCollisionObject
        Assert.Equal("bhkCollisionObject", bhkBlend.Inherit);

        // Own fields: Heir Gain, Vel Gain (and possibly Unknown Float 1/2 for older versions)
        Assert.True(bhkBlend.Fields.Count >= 2);

        // All fields should include:
        // NiObject (0) -> NiCollisionObject (1) -> bhkNiCollisionObject (2) -> bhkCollisionObject (0) -> bhkBlendCollisionObject (2+)
        Assert.True(bhkBlend.AllFields.Count >= 5);

        // First 3 should be inherited
        Assert.Equal("Target", bhkBlend.AllFields[0].Name);
        Assert.Equal("Flags", bhkBlend.AllFields[1].Name);
        Assert.Equal("Body", bhkBlend.AllFields[2].Name);

        // Last ones are own
        Assert.Equal("Heir Gain", bhkBlend.AllFields[3].Name);
        Assert.Equal("Vel Gain", bhkBlend.AllFields[4].Name);
    }

    [Fact]
    public void Schema_GetTypeSize_ReturnsCorrectSizes()
    {
        var schema = LoadSchema();

        Assert.Equal(1, schema.GetTypeSize("byte"));
        Assert.Equal(2, schema.GetTypeSize("ushort"));
        Assert.Equal(2, schema.GetTypeSize("short"));
        Assert.Equal(4, schema.GetTypeSize("uint"));
        Assert.Equal(4, schema.GetTypeSize("int"));
        Assert.Equal(4, schema.GetTypeSize("float"));
        Assert.Equal(8, schema.GetTypeSize("uint64"));
        Assert.Equal(4, schema.GetTypeSize("Ref"));
        Assert.Equal(4, schema.GetTypeSize("Ptr"));

        // Structs with fixed size
        Assert.Equal(12, schema.GetTypeSize("Vector3"));
        Assert.Equal(16, schema.GetTypeSize("Vector4"));
        Assert.Equal(16, schema.GetTypeSize("Quaternion"));
    }

    [Fact]
    public void Schema_IsBlockReference_IdentifiesRefAndPtr()
    {
        Assert.True(NifSchema.IsBlockReference("Ref"));
        Assert.True(NifSchema.IsBlockReference("Ptr"));
        Assert.False(NifSchema.IsBlockReference("uint"));
        Assert.False(NifSchema.IsBlockReference("float"));
    }

    [Fact]
    public void Validator_bhkCollisionObject_SizeIs10Bytes()
    {
        var schema = LoadSchema();
        var objDef = schema.GetObject("bhkCollisionObject")!;

        // bhkCollisionObject: Target(4) + Flags(2) + Body(4) = 10 bytes
        var size = NifSchemaValidator.CalculateMinSize(schema, objDef);

        Assert.NotNull(size);
        Assert.Equal(10, size.Value);
    }

    [Fact]
    public void Validator_bhkBlendCollisionObject_SizeIs18Bytes()
    {
        var schema = LoadSchema();
        var objDef = schema.GetObject("bhkBlendCollisionObject")!;

        // bhkBlendCollisionObject: Target(4) + Flags(2) + Body(4) + HeirGain(4) + VelGain(4) = 18 bytes
        // Note: May have additional conditional fields for older versions
        var size = NifSchemaValidator.CalculateMinSize(schema, objDef);

        Assert.NotNull(size);
        Assert.True(size.Value >= 18, $"Expected at least 18 bytes, got {size.Value}");
    }

    [Fact]
    public void Validator_GetBlockFieldLayout_ShowsInheritedFields()
    {
        var schema = LoadSchema();
        var layout = NifSchemaValidator.GetBlockFieldLayout(schema, "bhkCollisionObject");

        Assert.Contains("Target", layout);
        Assert.Contains("Flags", layout);
        Assert.Contains("Body", layout);
        Assert.Contains("Ptr", layout);
        Assert.Contains("Ref", layout);
    }
}