using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Materials;

public sealed class StarfieldMaterialUvAnimationTests
{
    [Fact]
    public void Parse_ResolvesUserBackedLinearUvOffsetLoopWithoutShiftingFollowingComponents()
    {
        var database = StarfieldMaterialDatabase.Parse(BuildControllerDatabase());

        Assert.NotNull(database);
        var animation = database!.ResolveLayerUvAnimation(@"materials\test\animated.mat", 0);
        Assert.True(animation.IsResolved);
        Assert.Equal(new Vector2(0f, 1f), animation.InitialOffset);
        Assert.Equal(new Vector2(0f, -0.2f), animation.Velocity);
        Assert.Equal(5f, animation.PeriodSeconds);

        // Controller-owned LIST/USER chunks are part of the first component's value. Consuming them
        // must not advance the positional ComponentInfo pairing used by the following OBJT.
        Assert.Equal("after_controller.dds", database.ResolveDiffuse(@"materials\test\animated.mat"));
        Assert.Equal(2, database.ComponentTableCount);
        Assert.Equal(2, database.ComponentChunkCount);
    }

    [Fact]
    public void ResolveLayerUvAnimation_FailsClosedForUnmappedLayer()
    {
        var database = StarfieldMaterialDatabase.Parse(BuildControllerDatabase());

        Assert.NotNull(database);
        Assert.False(database!.ResolveLayerUvAnimation(@"materials\test\animated.mat", 1).IsResolved);
        Assert.False(database.ResolveLayerUvAnimation(@"materials\test\animated.mat", -1).IsResolved);
    }

    [Fact]
    public void Parse_FailsClosedWhenLinearCurveDoesNotWrapToEquivalentTextureCoordinate()
    {
        var database = StarfieldMaterialDatabase.Parse(BuildControllerDatabase(yEnd: 0.5f));

        Assert.NotNull(database);
        Assert.False(database!.ResolveLayerUvAnimation(@"materials\test\animated.mat", 0).IsResolved);
    }

    private static byte[] BuildControllerDatabase(float yEnd = 0f)
    {
        const string objectInfo = "BSComponentDB2::DBFileIndex::ObjectInfo";
        const string componentInfo = "BSComponentDB2::DBFileIndex::ComponentInfo";
        const string controllerComponent = "BSBind::ControllerComponent";
        const string controllers = "BSBind::Controllers";
        const string mapping = "BSBind::Controllers::Mapping";
        const string address = "BSBind::Address";
        const string controller = "BSBind::Float2DCurveController";
        const string float2Curve = "BSFloat2DCurve";
        const string floatCurve = "BSFloatCurve";
        const string control = "BSFloatCurve::Control";
        const string textureFile = "BSMaterial::MRTextureFile";

        var names = new[]
        {
            objectInfo, componentInfo, controllerComponent, controllers, mapping, address,
            controller, float2Curve, floatCurve, control, textureFile,
            "upControllers", "MappingsA", "UseRandomOffset", "Address", "Controller", "Path",
            "Curve", "Loop", "Mask", "XCurve", "YCurve", "Controls", "Type", "DefaultValue",
            "IsSampleInterpolating", "MaxValue", "MinValue", "MaxInput", "InputDistance", "Edge",
            "Input", "Value"
        };
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var stringTable = new List<byte>();
        foreach (var name in names)
        {
            offsets[name] = (uint)stringTable.Count;
            stringTable.AddRange(Encoding.ASCII.GetBytes(name));
            stringTable.Add(0);
        }

        uint Custom(string name) => offsets[name];
        static uint BuiltIn(uint index) => 0xFFFFFF01u + index;

        var chunks = new List<byte[]>
        {
            // The ObjectInfo field count selects the launch-era 21-byte stride. Its field descriptors
            // are irrelevant to this bounded fixture and intentionally omitted, matching older tests.
            Chunk("CLAS", Concat(U32(Custom(objectInfo)), U32(1), U16(0), U16(4))),
            ClassChunk(Custom(controllerComponent), 0,
                (Custom("upControllers"), BuiltIn(4))), // <ref>
            ClassChunk(Custom(controllers), 0,
                (Custom("MappingsA"), BuiltIn(2)), // List
                (Custom("UseRandomOffset"), BuiltIn(15))), // bool
            ClassChunk(Custom(mapping), 0,
                (Custom("Address"), Custom(address)),
                (Custom("Controller"), BuiltIn(4))),
            ClassChunk(Custom(address), 0,
                (Custom("Path"), BuiltIn(2))),
            // Retail writes dynamically referenced controller implementations as USER chunks.
            ClassChunk(Custom(controller), 4,
                (Custom("Curve"), Custom(float2Curve)),
                (Custom("Loop"), BuiltIn(15)),
                (Custom("Mask"), BuiltIn(1))),
            ClassChunk(Custom(float2Curve), 0,
                (Custom("XCurve"), Custom(floatCurve)),
                (Custom("YCurve"), Custom(floatCurve))),
            ClassChunk(Custom(floatCurve), 0,
                (Custom("Controls"), BuiltIn(2)),
                (Custom("Type"), BuiltIn(1)),
                (Custom("DefaultValue"), BuiltIn(16)),
                (Custom("IsSampleInterpolating"), BuiltIn(15)),
                (Custom("MaxValue"), BuiltIn(16)),
                (Custom("MinValue"), BuiltIn(16)),
                (Custom("MaxInput"), BuiltIn(16)),
                (Custom("InputDistance"), BuiltIn(16)),
                (Custom("Edge"), BuiltIn(1))),
            ClassChunk(Custom(control), 0,
                (Custom("Input"), BuiltIn(16)),
                (Custom("Value"), BuiltIn(16)))
        };

        const uint materialId = 1;
        var resource = StarfieldMaterialDatabase.ComputeResourceId(@"materials\test\animated.mat");
        chunks.Add(Chunk("LIST", Concat(
            U32(Custom(objectInfo)),
            U32(1),
            ObjectRecord(resource.File, resource.Ext, resource.Dir, materialId))));
        chunks.Add(Chunk("LIST", Concat(
            U32(Custom(componentInfo)),
            U32(2),
            U32(materialId), U32(0), // ControllerComponent
            U32(materialId), U32(0)))); // trailing texture component

        // ControllerComponent.upControllers -> Controllers. MappingsA and curve Controls live in
        // sibling LIST chunks; the dynamic controller implementation lives in a USER sibling.
        chunks.Add(Chunk("OBJT", Concat(
            U32(Custom(controllerComponent)),
            U32(Custom(controllers)),
            [0]))); // Controllers.UseRandomOffset
        chunks.Add(Chunk("LIST", Concat(
            U32(Custom(mapping)),
            U32(1),
            U32(Custom(controller))))); // Mapping.Address has no inline bytes; Controller is a ref
        chunks.Add(Chunk("LIST", Concat(
            U32(BuiltIn(1)),
            U32(1),
            Str("UVOffset1"))));

        var xCurve = CurveBody(defaultValue: 0.5f, period: 5f);
        var yCurve = CurveBody(defaultValue: 0.1f, period: 5f);
        chunks.Add(Chunk("USER", Concat(
            U32(Custom(controller)),
            U32(Custom(controller)),
            xCurve,
            yCurve,
            [1],
            Str("X;Y"))));
        chunks.Add(Chunk("LIST", Concat(
            U32(Custom(control)),
            U32(1),
            F32(1.971f), F32(0f))));
        chunks.Add(Chunk("LIST", Concat(
            U32(Custom(control)),
            U32(2),
            F32(0f), F32(1f),
            F32(5f), F32(yEnd))));

        // A real second component after the nested controller chunks pins positional accounting.
        chunks.Add(Chunk("OBJT", Concat(
            U32(Custom(textureFile)),
            Str("after_controller.dds"))));

        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("BETH"));
        file.AddRange(U32(8));
        file.AddRange(U32(4));
        file.AddRange(U32((uint)chunks.Count + 2));
        file.AddRange(Encoding.ASCII.GetBytes("STRT"));
        file.AddRange(U32((uint)stringTable.Count));
        file.AddRange(stringTable);
        foreach (var chunk in chunks)
        {
            file.AddRange(chunk);
        }

        return [.. file];
    }

    private static byte[] CurveBody(float defaultValue, float period)
    {
        // Controls is a sibling LIST and contributes no inline bytes here.
        return Concat(
            Str("Linear"),
            F32(defaultValue),
            [1],
            F32(1f),
            F32(0f),
            F32(period),
            F32(period),
            Str("Clamp"));
    }

    private static byte[] ClassChunk(
        uint className,
        ushort flags,
        params (uint Name, uint Type)[] fields)
    {
        var body = new List<byte>();
        body.AddRange(U32(className));
        body.AddRange(U32(1));
        body.AddRange(U16(flags));
        body.AddRange(U16((ushort)fields.Length));
        foreach (var field in fields)
        {
            body.AddRange(U32(field.Name));
            body.AddRange(U32(field.Type));
            body.AddRange(U16(0));
            body.AddRange(U16(0));
        }

        return Chunk("CLAS", [.. body]);
    }

    private static byte[] ObjectRecord(uint file, uint extension, uint directory, uint databaseId)
    {
        return Concat(U32(file), U32(extension), U32(directory), U32(databaseId), U32(0), [1]);
    }

    private static byte[] Chunk(string tag, byte[] body)
    {
        return Concat(Encoding.ASCII.GetBytes(tag), U32((uint)body.Length), body);
    }

    private static byte[] Str(string value)
    {
        return Concat(U16((ushort)value.Length), Encoding.ASCII.GetBytes(value));
    }

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);
    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);
    private static byte[] F32(float value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var part in parts)
        {
            result.AddRange(part);
        }

        return [.. result];
    }
}
