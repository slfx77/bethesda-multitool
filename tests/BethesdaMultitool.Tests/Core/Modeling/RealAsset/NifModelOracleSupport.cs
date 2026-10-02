using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Shared plumbing for the manifest-driven oracle hops (plan section 6, slices 11 and 12): reading a cover file
///     through the reader contract (under the console platform of a big-endian file), the native-state rows by block,
///     the skip rules the hops apply, the header block table comparison of hop A2, and the bit-for-bit geometry
///     comparisons hop A3 inherited from the first geometry oracle test.
/// </summary>
/// <remarks>
///     <para>
///         Skip rules (never early returns): a declined control and a <c>.kf</c> animation stream are hop A2's (the
///         reader must decline them) and skip every other hop (<see cref="SkipUnlessComparableModel" />). Big-endian
///         files are compared since slice 10 typed their packed geometry and skins: hop A1 compares them field by
///         field like any other file (the geometry family through the probe's packed stream table), and hop A3
///         compares them against the PC file of the same path (<see cref="NifConsoleGeometryOracleTests" />), so only
///         the bind-pose extractor oracle, which reads little-endian files alone, still skips them
///         (<see cref="SkipIfBigEndian" />).
///     </para>
///     <para>
///         Platform: a big-endian FNV file carries no byte that says which console wrote it, and the two consoles
///         differ only in the packed vertex-color byte order, so a big-endian file is read under the
///         <c>bmt.platform</c> option the manifest source names (<see cref="Cut1aCoverFile.ConsolePlatform" />), which
///         the probe derived the same way and recorded as the expectation's <c>platform</c>
///         (<see cref="ConsoleReadOptions" /> asserts the two agree).
///     </para>
///     <para>
///         JSON readers: the reader's native-state payloads and the expectations are both parsed with
///         <see cref="JsonNode" />; floats are compared by their IEEE bits after a double-to-single cast, which is exact
///         for both sides (the probe writes the exact double value of each stored single; the reader writes the single
///         through <see cref="NifModelNativeValues.Float" />). A non-finite value is written as a hex string by the
///         reader and as <c>NaN</c>/<c>Infinity</c> by the probe; <see cref="TryFloat" /> reports both as unreadable.
///     </para>
/// </remarks>
internal static class NifModelOracleSupport
{
    /// <summary>The tag the probe sets on a file with BSPackedAdditionalGeometryData.</summary>
    public const string PackedGeometryTag = "packedgeom=1";

    /// <summary>The probe's orthonormality tolerance (rot=nonorthonormal), transcribed from the probe (ROT_TOL).</summary>
    public const double ProbeRotationTolerance = 1e-3;

    /// <summary>
    ///     Reads a cover file through the reader contract, optionally with a texture companion resolver and the app
    ///     options of the read (the console platform of a big-endian file, <see cref="ConsoleReadOptions" />).
    /// </summary>
    public static ModelReadResult Read(byte[] bytes, Cut1aCoverFile file, ModelCompanionResolver? resolver = null,
        IReadOnlyDictionary<string, string>? options = null)
    {
        return NifModelTestSupport.ReadWith(bytes, resolver, path: file.DataRelativePath, options: options);
    }

    /// <summary>Skips a theory row that no comparing hop can read: a declined control or a <c>.kf</c> stream (hop A2's rows).</summary>
    public static void SkipUnlessComparableModel(Cut1aCoverFile file, JsonObject expectation)
    {
        var declined = expectation["declined"];
        Assert.SkipWhen(file.IsDeclinedControl || declined is not null,
            $"{file}: the probe declines it ({declined}); hop A2 asserts the reader declines it too.");
        Assert.SkipWhen(file.IsAnimationStream,
            $"{file}: a .kf animation stream, which has no geometry of its own and needs a skeleton; hop A2 asserts " +
            "its admission and the cut-1b hops compare its clips.");
    }

    /// <summary>
    ///     Skips a big-endian row of a hop whose oracle reads little-endian files only (the bind-pose extractor), naming
    ///     the hop that compares the file instead.
    /// </summary>
    public static void SkipIfBigEndian(Cut1aCoverFile file, JsonObject expectation, string comparedBy)
    {
        Assert.SkipWhen(file.IsBigEndian, HasTag(expectation, PackedGeometryTag)
            ? $"{file}: big-endian with BSPackedAdditionalGeometryData; this hop's oracle reads little-endian " +
              $"streams only, and the file is compared against the PC file of the same path by {comparedBy}."
            : $"{file}: big-endian with inline streams; this hop's oracle reads little-endian streams only, and the " +
              $"file is compared against the PC file of the same path by {comparedBy}.");
    }

    /// <summary>
    ///     The app options a cover file is read under: for a big-endian file the <c>bmt.platform</c> option naming the
    ///     console of its manifest source, which must be the platform the probe recorded; null for a little-endian file.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ConsoleReadOptions(Cut1aCoverFile file, JsonObject expectation)
    {
        if (!file.IsBigEndian)
        {
            return null;
        }

        var platform = file.ConsolePlatform;
        Assert.True(platform is not null, $"{file}: the manifest source names neither X360 nor PS3: {file.Source}");
        Assert.Equal(platform, Text(expectation["platform"]));
        return new Dictionary<string, string> { [BethesdaModelRegistration.PlatformOption] = platform! };
    }

    /// <summary>True when the probe tagged the file with <paramref name="tag" />.</summary>
    public static bool HasTag(JsonObject expectation, string tag)
    {
        return expectation["tags"] is JsonArray tags &&
               tags.Any(t => string.Equals(t?.GetValue<string>(), tag, StringComparison.Ordinal));
    }

    /// <summary>The <c>bmt.nif.block</c> row payloads by block index.</summary>
    public static Dictionary<int, JsonObject> BlockRows(ModelDocument document)
    {
        var rows = new Dictionary<int, JsonObject>();
        foreach (var row in NifModelTestSupport.Rows(document, NifModelNativeState.BlockKind))
        {
            var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
            rows.Add(payload["index"]!.GetValue<int>(), payload);
        }

        return rows;
    }

    /// <summary>The rows of one kind with their target element index (material, image, skin, mesh).</summary>
    public static List<(int Index, JsonObject Payload)> IndexedRows(ModelDocument document, string kind)
    {
        return NifModelTestSupport.Rows(document, kind)
            .Select(row => (row.Target.Index!.Value, JsonNode.Parse(row.PayloadJson)!.AsObject()))
            .ToList();
    }

    /// <summary>
    ///     The reader's primitive and its native facts per geometry block, from the bmt.nif.primitive rows. A geometry
    ///     placed under parents with different effective materials has one primitive per material (slice 5); they share
    ///     the geometry block's streams, so the first stands for all of them here.
    /// </summary>
    public static Dictionary<int, (ScenePrimitive Primitive, JsonObject Payload)> PrimitivesByGeometryBlock(
        ModelDocument document)
    {
        var result = new Dictionary<int, (ScenePrimitive, JsonObject)>();
        foreach (var row in NifModelTestSupport.Rows(document, NifModelGeometryReader.PrimitiveKind))
        {
            var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
            var mesh = row.Target.Index!.Value;
            result.TryAdd(payload["geometryBlock"]!.GetValue<int>(), (document.Meshes[mesh].Primitives[0], payload));
        }

        return result;
    }

    /// <summary>
    ///     The first difference between the reader's block census (type per index) and the probe's header block table
    ///     (<c>blockTypeNames</c> indexed by <c>blockTypeIndices</c>), or null when they agree element for element.
    /// </summary>
    public static string? FirstBlockTableMismatch(IReadOnlyList<string> readerTypes, JsonObject expectation)
    {
        var names = expectation["blockTypeNames"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var indices = expectation["blockTypeIndices"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        if (indices.Count != readerTypes.Count)
        {
            return $"the probe's block table has {indices.Count} blocks, the reader's census {readerTypes.Count}.";
        }

        for (var i = 0; i < indices.Count; i++)
        {
            var expected = names[indices[i]];
            if (!string.Equals(expected, readerTypes[i], StringComparison.Ordinal))
            {
                return $"block {i}: the probe says {expected}, the reader {readerTypes[i]}.";
            }
        }

        return null;
    }

    /// <summary>
    ///     The reader's own census (<see cref="NifModelCoverage.Census" /> over the decoded header) without a read, for
    ///     a cut-1a key the read declines (a .kf stream).
    /// </summary>
    public static IReadOnlyList<string> ReaderCensusTypes(byte[] bytes)
    {
        var info = NifParser.Parse(bytes);
        Assert.True(info is not null, "NifParser could not parse the header the probe listed a block table for.");
        var decoder = new NifBlockDecoder(NifSchema.LoadEmbedded(), info!, bytes);
        return NifModelCoverage.Census(decoder.Header).Select(e => e.Kind).ToList();
    }

    /// <summary>
    ///     The header block table as NifParser reads it, for a key outside cut 1a (20.0.0.4 has no block sizes, which
    ///     the cut-1a decoder requires) whose table the probe still listed.
    /// </summary>
    public static IReadOnlyList<string> ParserBlockTypes(byte[] bytes)
    {
        var info = NifParser.Parse(bytes);
        Assert.True(info is not null, "NifParser could not parse the header the probe listed a block table for.");
        return info!.Blocks.Select(b => b.TypeName).ToList();
    }

    /// <summary>True when the shape's own property list holds a NiTexturingProperty (the oracle bakes its transform).</summary>
    public static bool HasTexturingProperty(NifBlockDecoder decoder, NifInfo nif, int shape)
    {
        var root = decoder.Decode(shape, NifDecodeMode.Tolerant).Root;
        if (!root.TryGet("Properties", out var value) || value is not NifArrayValue properties)
        {
            return false;
        }

        return properties.Items.OfType<NifRefValue>().Any(reference =>
            !reference.IsNone && (uint)reference.Index < (uint)nif.Blocks.Count &&
            nif.Blocks[reference.Index].TypeName == "NiTexturingProperty");
    }

    /// <summary>The oracle's triangles minus those repeating an index (the reader's list rule), and how many that removed.</summary>
    public static List<int> WithoutRepeatedIndices(ushort[] triangles, out int repeated)
    {
        var kept = new List<int>(triangles.Length);
        repeated = 0;
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if (a == b || b == c || a == c)
            {
                repeated++;
                continue;
            }

            kept.Add(a);
            kept.Add(b);
            kept.Add(c);
        }

        return kept;
    }

    /// <summary>The first component whose bits differ, or a count difference, described; null when all match.</summary>
    public static string? FirstMismatch(float[] expected, IReadOnlyList<Vector3> actual, string where)
    {
        if (expected.Length != actual.Count * 3)
        {
            return $"{where}: the oracle has {expected.Length / 3} elements, the reader {actual.Count}.";
        }

        for (var i = 0; i < actual.Count; i++)
        {
            float[] components = [actual[i].X, actual[i].Y, actual[i].Z];
            for (var c = 0; c < 3; c++)
            {
                if (BitConverter.SingleToUInt32Bits(expected[i * 3 + c]) !=
                    BitConverter.SingleToUInt32Bits(components[c]))
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"{where}: element {i} component {c}: oracle {expected[i * 3 + c]:R}, reader {components[c]:R}.");
                }
            }
        }

        return null;
    }

    /// <summary>The first Vector3 whose bits differ between two lists, or a count difference, described; null when all match.</summary>
    public static string? FirstMismatch(IReadOnlyList<Vector3> expected, IReadOnlyList<Vector3> actual, string where)
    {
        if (expected.Count != actual.Count)
        {
            return $"{where}: the oracle has {expected.Count} elements, the reader {actual.Count}.";
        }

        for (var i = 0; i < actual.Count; i++)
        {
            if (!SameBits(expected[i].X, actual[i].X) || !SameBits(expected[i].Y, actual[i].Y) ||
                !SameBits(expected[i].Z, actual[i].Z))
            {
                return $"{where}: element {i}: oracle {Show(expected[i])}, reader {Show(actual[i])}.";
            }
        }

        return null;
    }

    /// <summary>The first Vector4 whose bits differ between two lists, or a count difference, described; null when all match.</summary>
    public static string? FirstMismatch(IReadOnlyList<Vector4> expected, IReadOnlyList<Vector4> actual, string where)
    {
        if (expected.Count != actual.Count)
        {
            return $"{where}: the oracle has {expected.Count} elements, the reader {actual.Count}.";
        }

        for (var i = 0; i < actual.Count; i++)
        {
            if (!SameBits(expected[i].X, actual[i].X) || !SameBits(expected[i].Y, actual[i].Y) ||
                !SameBits(expected[i].Z, actual[i].Z) || !SameBits(expected[i].W, actual[i].W))
            {
                return $"{where}: element {i}: oracle {expected[i]}, reader {actual[i]}.";
            }
        }

        return null;
    }

    /// <summary>The first UV whose bits differ, or a count difference, described; null when all match.</summary>
    public static string? FirstUvMismatch(float[] expected, ScenePrimitive primitive, string where)
    {
        if (expected.Length != primitive.Vertices.Count * 2)
        {
            return $"{where}: the oracle has {expected.Length / 2} UVs, the reader {primitive.Vertices.Count}.";
        }

        // The reader neutralizes a NaN or infinite component to 0 and keeps the set's exact bits in a raw attribute;
        // the oracle keeps the stored value. Both are checked where they differ, so the comparison stays exact.
        var raw = primitive.Attributes.FirstOrDefault(a =>
            a.Name == NifModelGeometryData.RawTexCoordAttribute + "0.raw")?.CopyContent();
        for (var i = 0; i < primitive.Vertices.Count; i++)
        {
            var uv = primitive.Vertices[i].TexCoord;
            for (var c = 0; c < 2; c++)
            {
                var stored = expected[i * 2 + c];
                var read = c == 0 ? uv.X : uv.Y;
                if (float.IsFinite(stored))
                {
                    if (BitConverter.SingleToUInt32Bits(stored) != BitConverter.SingleToUInt32Bits(read))
                    {
                        return string.Create(CultureInfo.InvariantCulture,
                            $"{where}: UV {i} component {c}: oracle {stored:R}, reader {read:R}.");
                    }

                    continue;
                }

                if (BitConverter.SingleToUInt32Bits(read) != 0)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"{where}: UV {i} component {c}: the stored value is not finite, the reader gives {read:R} instead of 0.");
                }

                if (raw is null)
                {
                    return $"{where}: UV {i} component {c}: the stored value is not finite and no raw attribute keeps its bits.";
                }

                var rawBits = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((i * 2 + c) * 4));
                if (rawBits != BitConverter.SingleToUInt32Bits(stored))
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"{where}: UV {i} component {c}: raw bits 0x{rawBits:X8} differ from the stored 0x{BitConverter.SingleToUInt32Bits(stored):X8}.");
                }
            }
        }

        return null;
    }

    /// <summary>A JSON number as a single, or false for a missing or non-finite (string-encoded) value.</summary>
    public static bool TryFloat(JsonNode? node, out float value)
    {
        value = float.NaN;
        if (node is not JsonValue json || !json.TryGetValue<double>(out var number))
        {
            return false;
        }

        value = (float)number;
        return true;
    }

    /// <summary>A JSON number as a single; throws when it is missing or not finite.</summary>
    public static float Float(JsonNode? node)
    {
        return TryFloat(node, out var value)
            ? value
            : throw new InvalidDataException($"Expected a finite JSON number, found {node?.ToJsonString() ?? "null"}.");
    }

    /// <summary>A JSON number as a double.</summary>
    public static double Double(JsonNode? node)
    {
        return node!.GetValue<double>();
    }

    /// <summary>A JSON number as an int.</summary>
    public static int Int(JsonNode? node)
    {
        return node!.GetValue<int>();
    }

    /// <summary>A JSON number as a long.</summary>
    public static long Long(JsonNode? node)
    {
        return node!.GetValue<long>();
    }

    /// <summary>A JSON boolean.</summary>
    public static bool Bool(JsonNode? node)
    {
        return node!.GetValue<bool>();
    }

    /// <summary>A JSON string, or null for a JSON null.</summary>
    public static string? Text(JsonNode? node)
    {
        return node?.GetValue<string>();
    }

    /// <summary>True when two singles have the same bits.</summary>
    public static bool SameBits(float expected, float actual)
    {
        return BitConverter.SingleToUInt32Bits(expected) == BitConverter.SingleToUInt32Bits(actual);
    }

    /// <summary>True when every component of a Vector3 has the bits of the probe's three numbers.</summary>
    public static bool SameBits(JsonNode? triple, Vector3 actual)
    {
        var array = triple!.AsArray();
        return SameBits(Float(array[0]), actual.X) && SameBits(Float(array[1]), actual.Y) &&
               SameBits(Float(array[2]), actual.Z);
    }

    /// <summary>A Vector3 as text for a message.</summary>
    public static string Show(Vector3 value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"({value.X:R}, {value.Y:R}, {value.Z:R})");
    }
}
