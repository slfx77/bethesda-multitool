using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeParityMatrixTests
{
    private static readonly HashSet<string> AllowedStatuses =
    [
        "typed-runtime",
        "partial-runtime",
        "generic-only",
        "esm-only"
    ];

    private static readonly HashSet<string> AllowedConfidence =
    [
        "high",
        "medium",
        "low"
    ];

    [Fact]
    public void RuntimeParityMatrix_HasValidSchema()
    {
        using var doc = LoadMatrix();
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var semanticModels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, row.ValueKind);

            Assert.True(row.TryGetProperty("semanticModel", out var semanticModelElement));
            Assert.True(row.TryGetProperty("recordCodes", out var recordCodesElement));
            Assert.True(row.TryGetProperty("typedRuntimeReader", out var typedRuntimeReaderElement));
            Assert.True(row.TryGetProperty("genericLayoutAvailable", out var genericLayoutElement));
            Assert.True(row.TryGetProperty("currentMergeMode", out var mergeModeElement));
            Assert.True(row.TryGetProperty("status", out var statusElement));
            Assert.True(row.TryGetProperty("highValueFields", out var highValueFieldsElement));
            Assert.True(row.TryGetProperty("evidence", out var evidenceElement));
            Assert.True(row.TryGetProperty("confidence", out var confidenceElement));
            Assert.True(row.TryGetProperty("nextAction", out var nextActionElement));

            var semanticModel = Assert.IsType<string>(semanticModelElement.GetString());
            Assert.NotEmpty(semanticModel);
            Assert.True(semanticModels.Add(semanticModel), $"Duplicate semanticModel row: {semanticModel}");

            Assert.Equal(JsonValueKind.Array, recordCodesElement.ValueKind);
            Assert.NotEmpty(recordCodesElement.EnumerateArray().ToList());
            Assert.All(recordCodesElement.EnumerateArray(),
                element => Assert.False(string.IsNullOrWhiteSpace(element.GetString())));

            Assert.True(
                typedRuntimeReaderElement.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                $"typedRuntimeReader must be string or null, got {typedRuntimeReaderElement.ValueKind}.");
            if (typedRuntimeReaderElement.ValueKind == JsonValueKind.String)
            {
                Assert.False(string.IsNullOrWhiteSpace(typedRuntimeReaderElement.GetString()));
            }

            Assert.True(
                genericLayoutElement.ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"genericLayoutAvailable must be boolean, got {genericLayoutElement.ValueKind}.");
            Assert.False(string.IsNullOrWhiteSpace(mergeModeElement.GetString()));

            var status = Assert.IsType<string>(statusElement.GetString());
            Assert.True(AllowedStatuses.Contains(status), $"Invalid status '{status}'.");

            Assert.Equal(JsonValueKind.Object, highValueFieldsElement.ValueKind);
            Assert.True(highValueFieldsElement.TryGetProperty("exact", out var exactFields));
            Assert.True(highValueFieldsElement.TryGetProperty("derived", out var derivedFields));
            Assert.True(highValueFieldsElement.TryGetProperty("esmOnly", out var esmOnlyFields));
            Assert.All([exactFields, derivedFields, esmOnlyFields], fieldBucket =>
            {
                Assert.Equal(JsonValueKind.Array, fieldBucket.ValueKind);
                Assert.All(fieldBucket.EnumerateArray(), element =>
                    Assert.False(string.IsNullOrWhiteSpace(element.GetString())));
            });

            Assert.Equal(JsonValueKind.Array, evidenceElement.ValueKind);
            Assert.NotEmpty(evidenceElement.EnumerateArray().ToList());
            Assert.All(evidenceElement.EnumerateArray(), element =>
                Assert.False(string.IsNullOrWhiteSpace(element.GetString())));

            var confidence = Assert.IsType<string>(confidenceElement.GetString());
            Assert.True(AllowedConfidence.Contains(confidence), $"Invalid confidence '{confidence}'.");

            Assert.False(string.IsNullOrWhiteSpace(nextActionElement.GetString()));
        }
    }

    [Fact]
    public void RuntimeParityMatrix_CoversEveryTypedRecordCollectionList()
    {
        using var doc = LoadMatrix();

        var matrixModels = doc.RootElement
            .EnumerateArray()
            .Select(row => row.GetProperty("semanticModel").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var recordCollectionModels = typeof(RecordCollection)
            .GetProperties()
            .Where(property =>
                property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
            .Select(property => property.PropertyType.GetGenericArguments()[0].Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = recordCollectionModels.Except(matrixModels).OrderBy(name => name).ToList();
        var extras = matrixModels.Except(recordCollectionModels).OrderBy(name => name).ToList();

        Assert.True(missing.Count == 0, $"Missing runtime parity rows: {string.Join(", ", missing)}");
        Assert.True(extras.Count == 0, $"Unexpected runtime parity rows: {string.Join(", ", extras)}");
    }

    private static JsonDocument LoadMatrix()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "runtime-parity-matrix.json");
        Assert.True(File.Exists(path), $"Runtime parity matrix not found: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "Xbox360MemoryCarver.slnx")) ||
                File.Exists(Path.Combine(dir, "docs", "runtime-parity-matrix.json")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        throw new DirectoryNotFoundException("Could not locate repo root from test base directory.");
    }
}