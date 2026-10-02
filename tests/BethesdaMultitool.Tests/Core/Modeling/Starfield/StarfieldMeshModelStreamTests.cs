using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Starfield.StarfieldMeshModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     The typed source streams (cut-2 plan decisions D3, D4 and D6, section 3.2): weights as per-slot streams with
///     repeated bones kept, colors as a non-primary RGBA stream, non-finite halves kept bit for bit, and the Dec4 zero
///     sentinel kept as decoded. Expectations come from the builder's own values.
/// </summary>
public class StarfieldMeshModelStreamTests
{
    [Fact]
    public void Weights_ArePerSlotStreams_ValuesAsStored_RepeatedBonesKept()
    {
        var builder = StarfieldMeshFileLayoutTests.FullBuilder();
        var document = Read(builder.Build()).Document;
        var primitive = Primary(document);

        var names = primitive.Attributes.Select(static a => a.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "starfield.uv1.raw", "starfield.color", "starfield.bone.0", "starfield.weight.0", "starfield.bone.1",
                "starfield.weight.1"
            }, names);
        for (var slot = 0; slot < 2; slot++)
        {
            var bone = Stream(primitive, $"starfield.bone.{slot}");
            var weight = Stream(primitive, $"starfield.weight.{slot}");
            Assert.Equal((SceneAttributeComponentType.UInt16, 1, false), (bone.ComponentType, bone.Components, bone.Normalized));
            Assert.Equal((SceneAttributeComponentType.UInt16, 1, true),
                (weight.ComponentType, weight.Components, weight.Normalized));
            Assert.Equal(SceneAttributeDomain.Vertex, bone.Domain);
            Assert.Equal(builder.Weights!.Where((_, i) => i % 2 == slot).Select(static p => p.Bone), U16(bone));
            Assert.Equal(builder.Weights!.Where((_, i) => i % 2 == slot).Select(static p => p.Weight), U16(weight));
        }

        Assert.Null(primitive.SkinInfluences);
        Assert.Empty(document.Skins);
        Assert.Contains(document.Diagnostics, d => d.Code == StarfieldMeshModelDiagnostics.SkinPaletteInNif);

        // Control: vertex 0 stores bone 3 twice, (3, 65534) then (3, 1). Merging the duplicate into one lane would give
        // slot 0 the weight 65535 and slot 1 zero; the streams keep both slots as stored.
        Assert.Equal(65534, U16(Stream(primitive, "starfield.weight.0"))[0]);
        Assert.Equal(1, U16(Stream(primitive, "starfield.weight.1"))[0]);
        Assert.Equal(3, U16(Stream(primitive, "starfield.bone.1"))[0]);
    }

    [Fact]
    public void Colors_AreANonPrimaryRgbaStream_AndThePortableColorStaysWhite()
    {
        var builder = StarfieldMeshFileLayoutTests.FullBuilder();
        var primitive = Primary(Read(builder.Build()).Document);

        var color = Stream(primitive, StarfieldMeshModelGeometry.ColorAttribute);

        Assert.Equal((SceneAttributeComponentType.UInt8, 4, true), (color.ComponentType, color.Components, color.Normalized));
        Assert.Equal(SceneColorSpace.Unknown, color.ColorSpace);
        Assert.Equal(SceneValueProvenance.Unknown, color.ColorSpaceProvenance);
        Assert.Equal(StarfieldMeshModelGeometry.ColorEvidence, color.ColorSpaceEvidence);
        var rgba = builder.Colors!.SelectMany(static c => new[] { c.R, c.G, c.B, c.A }).ToArray();
        Assert.Equal(rgba, color.CopyContent());
        Assert.Null(primitive.PrimaryColorAttributeIndex);
        Assert.All(primitive.Vertices, static v => Assert.Equal(Vector4.One, v.Color));

        // Control: the stored order is B, G, R, A, which these colors make differ from the stream's R, G, B, A.
        var stored = builder.Colors!.SelectMany(static c => new[] { c.B, c.G, c.R, c.A }).ToArray();
        Assert.NotEqual(stored, color.CopyContent());
    }

    [Fact]
    public void ANonFiniteHalfReadsZero_AndItsSetsBitsAreKept()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        builder.Uv0![2] = (StarfieldMeshTestBuilder.HalfInfinity, StarfieldMeshTestBuilder.HalfOne);

        var document = Read(builder.Build()).Document;
        var primitive = Primary(document);

        Assert.Equal(new Vector2(0f, 1f), primitive.Vertices[2].TexCoord);
        Assert.Equal(new Vector2(1f, 0f), primitive.Vertices[1].TexCoord);
        var raw = Stream(primitive, StarfieldMeshModelGeometry.RawUvAttribute(0));
        Assert.Equal((SceneAttributeComponentType.UInt16, 2), (raw.ComponentType, raw.Components));
        Assert.Equal(builder.Uv0.SelectMany(static p => new[] { p.U, p.V }), U16(raw));
        Assert.Contains(document.Diagnostics, d => d.Code == StarfieldMeshModelDiagnostics.UvNonFinite);
        SceneValidation.ValidateStructure(document);

        // Control: an all-finite stream carries no raw stream and no diagnostic.
        var finite = Read(StarfieldMeshTestBuilder.Quad().Build()).Document;
        Assert.Empty(Primary(finite).Attributes);
        Assert.DoesNotContain(finite.Diagnostics, d => d.Code == StarfieldMeshModelDiagnostics.UvNonFinite);
    }

    [Fact]
    public void TheSecondUvSet_IsTheFirstAdditionalSet()
    {
        var builder = StarfieldMeshFileLayoutTests.FullBuilder();
        var primitive = Primary(Read(builder.Build()).Document);

        var uv1 = Assert.Single(primitive.AdditionalTextureCoordinates);
        var expected = builder.Uv1!.Select(static p => new Vector2(Half(p.U), Half(p.V))).ToArray();
        Assert.Equal(expected, uv1.Values);
        Assert.Equal(new Vector2(0f, 1f), uv1.Values[2]); // (inf, 1) reads (0, 1)

        static float Half(ushort bits)
        {
            var value = (float)BitConverter.UInt16BitsToHalf(bits);
            return float.IsFinite(value) ? value : 0f;
        }
    }

    [Fact]
    public void TheDec4ZeroSentinelIsKeptAsDecoded_AndCounted()
    {
        var document = Read(StarfieldMeshFileLayoutTests.FullBuilder().Build()).Document;
        var primitive = Primary(document);
        var channel = ExactFloat32.Round(-1, 1023);

        // Normal 3 and tangent 2 are (511, 511, 511): -1/1023 per channel, not an exact zero (tangent 2's W code 0 is
        // glTF's +1).
        Assert.Equal(new Vector3(channel, channel, channel), primitive.Vertices[3].Normal);
        Assert.Equal(new Vector4(channel, channel, channel, 1f), primitive.Tangents!.Values[2]);
        Assert.NotEqual(Vector3.Zero, primitive.Vertices[3].Normal);
        var row = Assert.Single(document.NativeStates, s => s.Kind == StarfieldMeshModelNativeState.SentinelsKind);
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal(new[] { 0, 1, 0, 0 }, payload["normalsByW"]!.AsArray().Select(static n => n!.GetValue<int>()));
        Assert.Equal(new[] { 1, 0, 0, 0 }, payload["tangentsByW"]!.AsArray().Select(static n => n!.GetValue<int>()));
        Assert.Contains(document.Diagnostics, d => d.Code == StarfieldMeshModelDiagnostics.Dec4Sentinel);
        SceneValidation.ValidateStructure(document);

        // Control: the quad has no sentinel, so no row and no diagnostic.
        var quad = Read(StarfieldMeshTestBuilder.Quad().Build()).Document;
        Assert.DoesNotContain(quad.NativeStates, s => s.Kind == StarfieldMeshModelNativeState.SentinelsKind);
        Assert.Equal(StarfieldMeshFile.Dec4Channel(511), channel);
    }
}
