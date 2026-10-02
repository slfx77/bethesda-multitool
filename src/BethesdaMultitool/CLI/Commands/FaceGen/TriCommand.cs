using System.CommandLine;
using System.Numerics;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.FaceGen.Tri;

namespace BethesdaMultitool.CLI.Commands.FaceGen;

/// <summary>Exports source TRI inspection data without changing the source geometry or guessing actor bindings.</summary>
internal static class TriCommand
{
    /// <summary>Creates a bounded standalone TRI inspection command with optional complete JSON data.</summary>
    internal static Command Create()
    {
        var root = new Command("tri", "Inspect documented FaceGen FRTRI003 geometry and both morph families");
        var inspect = new Command("inspect", "Read a complete little-endian TRI file");
        var input = new Argument<string>("input") { Description = "Complete .tri file path" };
        var json = new Option<bool>("--json") { Description = "Write machine-readable metadata to stdout" };
        var data = new Option<bool>("--data") { Description = "Include all source arrays and packed deltas (implies --json)" };
        inspect.Arguments.Add(input);
        inspect.Options.Add(json);
        inspect.Options.Add(data);
        inspect.SetAction(async (result, token) =>
        {
            try
            {
                var document = await TriReader.ReadFileAsync(result.GetValue(input)!, token).ConfigureAwait(false);
                if (result.GetValue(json) || result.GetValue(data))
                {
                    using var output = Console.OpenStandardOutput();
                    WriteJson(output, document, result.GetValue(data), token);
                }
                else
                {
                    Console.WriteLine($"FRTRI003: {document.Header.VertexCount} base vertices, {document.Header.StatisticalVertexCount} statistical targets, {document.Header.TriangleCount} triangles, {document.Header.QuadCount} quads");
                    Console.WriteLine($"SHA-256: {document.SourceHash}; encoded bytes: {document.EncodedSize}");
                    for (var index = 0; index < document.DifferentialMorphs.Count; index++)
                    {
                        Console.WriteLine($"Differential {index}: {document.DifferentialMorphs[index].Label.Text}");
                    }
                    for (var index = 0; index < document.StatisticalMorphs.Count; index++)
                    {
                        Console.WriteLine($"Statistical {index}: {document.StatisticalMorphs[index].Label.Text}");
                    }
                    Console.WriteLine("Source coordinates and exact labels; actor shape statistics, LIP aliases and audio alignment are not applied.");
                }
                return 0;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"TRI inspection failed: {exception.Message}");
                return 1;
            }
        });
        root.Subcommands.Add(inspect);
        return root;
    }

    /// <summary>Streams reflection-free source JSON, preserving original packed values and both morph families.</summary>
    internal static void WriteJson(Stream output, TriDocument document, bool includeData, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        var header = document.Header;
        writer.WriteStartObject();
        writer.WriteString("format", "FRTRI003");
        writer.WriteString("sha256", document.SourceHash);
        writer.WriteNumber("encodedBytes", document.EncodedSize);
        writer.WriteString("valueSpace", "source coordinates; actor shaping, LIP aliases and audio alignment not applied");
        writer.WriteStartObject("header");
        writer.WriteNumber("vertices", header.VertexCount);
        writer.WriteNumber("triangles", header.TriangleCount);
        writer.WriteNumber("quads", header.QuadCount);
        writer.WriteNumber("vertexLabels", header.VertexLabelCount);
        writer.WriteNumber("surfaceLabels", header.SurfaceLabelCount);
        writer.WriteNumber("textureCoordinates", header.TextureCoordinateCount);
        writer.WriteNumber("extensionFlags", header.ExtensionFlags);
        writer.WriteNumber("differentialMorphs", header.DifferentialMorphCount);
        writer.WriteNumber("statisticalMorphs", header.StatisticalMorphCount);
        writer.WriteNumber("statisticalVertices", header.StatisticalVertexCount);
        writer.WriteBase64String("reservedBytes", header.Reserved.Span);
        writer.WriteEndObject();
        writer.WriteStartArray("differentialMorphs");
        for (var index = 0; index < document.DifferentialMorphs.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var morph = document.DifferentialMorphs[index];
            writer.WriteStartObject();
            writer.WriteNumber("index", index);
            WriteLabel(writer, morph.Label);
            writer.WriteNumber("scale", morph.Scale);
            if (includeData)
            {
                writer.WriteStartArray("packedDeltasXYZ");
                var values = morph.PackedDeltas.Span;
                for (var offset = 0; offset < values.Length; offset++)
                {
                    writer.WriteNumberValue(values[offset]);
                    FlushPeriodically(writer, offset, token);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.Flush();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("statisticalMorphs");
        for (var index = 0; index < document.StatisticalMorphs.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var morph = document.StatisticalMorphs[index];
            writer.WriteStartObject();
            writer.WriteNumber("index", index);
            WriteLabel(writer, morph.Label);
            writer.WriteNumber("affectedVertices", morph.VertexIndices.Length);
            writer.WriteNumber("firstTargetVertex", morph.FirstTargetVertex);
            if (includeData)
            {
                WriteIndices(writer, "vertexIndices", morph.VertexIndices.Span, token);
            }
            writer.WriteEndObject();
            writer.Flush();
        }
        writer.WriteEndArray();
        if (includeData)
        {
            WriteVectors(writer, "verticesXYZ", document.Vertices.Span, token);
            WriteVectors(writer, "statisticalTargetsXYZ", document.StatisticalVertices.Span, token);
            WriteIndices(writer, "triangleIndices", document.Triangles.Span, token);
            WriteIndices(writer, "quadIndices", document.Quads.Span, token);
            WriteIndices(writer, "triangleTextureIndices", document.TriangleTextureIndices.Span, token);
            WriteIndices(writer, "quadTextureIndices", document.QuadTextureIndices.Span, token);
            writer.WriteStartArray("textureCoordinatesUV");
            var uvs = document.TextureCoordinates.Span;
            for (var index = 0; index < uvs.Length; index++)
            {
                writer.WriteNumberValue(uvs[index].X);
                writer.WriteNumberValue(uvs[index].Y);
                FlushPeriodically(writer, index, token);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("vertexLabels");
            foreach (var label in document.VertexLabels)
            {
                token.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteNumber("vertexIndex", label.VertexIndex);
                WriteLabel(writer, label.Label);
                writer.WriteEndObject();
                writer.Flush();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("surfaceLabels");
            foreach (var label in document.SurfaceLabels)
            {
                token.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteNumber("sourceInteger", label.SurfaceIndex);
                WriteVectors(writer, "sourceCoordinates", [label.Coordinates], token);
                WriteLabel(writer, label.Label);
                writer.WriteEndObject();
                writer.Flush();
            }
            writer.WriteEndArray();
        }
        token.ThrowIfCancellationRequested();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Writes display text beside exact encoded bytes and the explicit character width.</summary>
    private static void WriteLabel(Utf8JsonWriter writer, TriLabel label)
    {
        writer.WriteString("name", label.Text);
        writer.WriteBase64String("labelBytes", label.Bytes.Span);
        writer.WriteNumber("labelCodeUnitWidth", label.CodeUnitWidth);
    }

    /// <summary>Writes an unchanged flat integer array with bounded writer buffering.</summary>
    private static void WriteIndices(Utf8JsonWriter writer, string name, ReadOnlySpan<int> values, CancellationToken token)
    {
        writer.WriteStartArray(name);
        for (var index = 0; index < values.Length; index++)
        {
            writer.WriteNumberValue(values[index]);
            FlushPeriodically(writer, index, token);
        }
        writer.WriteEndArray();
    }

    /// <summary>Writes an unchanged flat XYZ array with bounded writer buffering.</summary>
    private static void WriteVectors(Utf8JsonWriter writer, string name, ReadOnlySpan<Vector3> values, CancellationToken token)
    {
        writer.WriteStartArray(name);
        for (var index = 0; index < values.Length; index++)
        {
            writer.WriteNumberValue(values[index].X);
            writer.WriteNumberValue(values[index].Y);
            writer.WriteNumberValue(values[index].Z);
            FlushPeriodically(writer, index, token);
        }
        writer.WriteEndArray();
    }

    /// <summary>Checks cancellation and releases accumulated JSON output at a bounded interval.</summary>
    private static void FlushPeriodically(Utf8JsonWriter writer, int index, CancellationToken token)
    {
        if ((index & 1023) == 0)
        {
            token.ThrowIfCancellationRequested();
            writer.Flush();
        }
    }
}
