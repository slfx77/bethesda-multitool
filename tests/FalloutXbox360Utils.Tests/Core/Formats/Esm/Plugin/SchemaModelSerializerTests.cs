using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Contract tests for <see cref="SchemaModelSerializer" /> — the typed-model bridge that
///     plugs into the existing schema-driven byte walker. Verifies the four core invariants
///     of the abstraction: schema-driven layout, zero-fill on missing extractors, mixed-type
///     extractors (uint→byte[]), and schema-not-found throws.
/// </summary>
public class SchemaModelSerializerTests
{
    [Fact]
    public void Serialize_WalksSchemaFieldsAndWritesLittleEndianBytes()
    {
        // ALCH/ENIT — 20 bytes: UInt32 Value + Bytes Flags(4) + FormId Addiction + Float
        // AddictionChance + FormId UseSoundOrWithdrawalEffect.
        var alch = new ConsumableRecord
        {
            FormId = 1,
            Value = 250u,
            Flags = 0x00000002u,
            AddictionFormId = 0x000FAB42u,
            AddictionChance = 0.25f,
            WithdrawalEffectFormId = 0x000FAB99u,
        };

        var extractors = new Dictionary<string, Func<ConsumableRecord, object?>>(StringComparer.Ordinal)
        {
            ["Value"] = m => m.Value,
            ["Flags"] = m => BitConverter.GetBytes(m.Flags),
            ["Addiction"] = m => m.AddictionFormId ?? 0u,
            ["AddictionChance"] = m => m.AddictionChance,
            ["UseSoundOrWithdrawalEffect"] = m => m.WithdrawalEffectFormId ?? 0u,
        };

        var bytes = SchemaModelSerializer.Serialize("ENIT", "ALCH", 20, alch, extractors);

        Assert.Equal(20, bytes.Length);
        Assert.Equal(250u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Equal(0x00000002u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        Assert.Equal(0x000FAB42u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(12, 4)));
        Assert.Equal(0x000FAB99u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)));
    }

    [Fact]
    public void Serialize_MissingExtractorsZeroFill()
    {
        // ALCH/ENIT, but the extractor map omits AddictionFormId / WithdrawalEffectFormId.
        // Missing fields should zero-fill — matches SchemaDictionarySerializer's dict-payload behaviour.
        var alch = new ConsumableRecord { FormId = 1, Value = 99u, AddictionChance = 1.5f };

        var partial = new Dictionary<string, Func<ConsumableRecord, object?>>(StringComparer.Ordinal)
        {
            ["Value"] = m => m.Value,
            ["AddictionChance"] = m => m.AddictionChance,
        };

        var bytes = SchemaModelSerializer.Serialize("ENIT", "ALCH", 20, alch, partial);

        Assert.Equal(20, bytes.Length);
        Assert.Equal(99u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
        // Flags (bytes 4-7) — omitted → zero-filled.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        // Addiction (8-11) — omitted → zero-filled.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(12, 4)));
        // UseSoundOrWithdrawalEffect (16-19) — omitted → zero-filled.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)));
    }

    [Fact]
    public void Serialize_ThrowsWhenSchemaNotRegistered()
    {
        // Sanity-check that the registry actually has no schema for our chosen sentinel —
        // otherwise the throw test isn't testing what we think it is.
        var lookup = SubrecordSchemaRegistry.GetSchema("ZZZZ", "NONE", 99);
        Assert.Null(lookup);

        var model = new ConsumableRecord { FormId = 1 };
        var extractors = new Dictionary<string, Func<ConsumableRecord, object?>>(StringComparer.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(
            () => SchemaModelSerializer.Serialize("ZZZZ", "NONE", 99, model, extractors));
        Assert.Contains("ZZZZ", ex.Message);
    }

    [Fact]
    public void SerializeSubrecord_WrapsBytesInEncodedSubrecord()
    {
        // ALCH/DATA (4 bytes, single Float "Weight").
        var alch = new ConsumableRecord { FormId = 1, Weight = 0.5f };
        var extractors = new Dictionary<string, Func<ConsumableRecord, object?>>(StringComparer.Ordinal)
        {
            ["Weight"] = m => m.Weight,
        };

        var sub = SchemaModelSerializer.SerializeSubrecord("DATA", "ALCH", 4, alch, extractors);

        Assert.Equal("DATA", sub.Signature);
        Assert.Equal(4, sub.Bytes.Length);
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(sub.Bytes));
    }
}
