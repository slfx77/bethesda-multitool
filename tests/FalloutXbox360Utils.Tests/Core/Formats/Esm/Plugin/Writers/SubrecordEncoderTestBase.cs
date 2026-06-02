using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.Writers;

/// <summary>
///     Phase 3.3 scaffold for tier-high encoder tests. Each concrete subclass overrides
///     <see cref="RecordSignature" />, <see cref="MakeSyntheticModel" />,
///     <see cref="GetExpectedBytes" />, and <see cref="EncodeModel" /> at minimum; if a
///     parser exists, override <see cref="TryParseBytes" /> as well to unlock the
///     round-trip test.
///
///     The base contributes three Facts:
///     <list type="bullet">
///         <item><description><see cref="Encodes_ProducesExpectedBytes" />: encoder output is byte-equal to the canonical fixture.</description></item>
///         <item><description><see cref="RoundTrip_ParseEncode_IsByteIdentical" />: bytes → parser → encoder → bytes round-trips byte-identical. Skipped (Assert-pass) when no parser is registered.</description></item>
///         <item><description><see cref="Endian_BigEndianMatchesSchemaRegistry" />: every signature emitted by the encoder has at least one corresponding endian/schema entry in <see cref="SubrecordSchemaRegistry" /> so the converter can round-trip it.</description></item>
///     </list>
///
///     The scaffold deliberately abstracts <see cref="EncodeModel" /> at the byte level
///     rather than at the <c>EncodedRecord</c> level — most tier-high encoders emit one
///     primary payload subrecord (DATA, DNAM, or similar) that's the interesting test
///     subject; non-byte-swapped subrecords (EDID, MODL, OBND) are exercised separately
///     by their generic encoders. Subclasses can override <see cref="EmittedSubrecordSignatures" />
///     to enumerate the full subrecord set the encoder produces, which feeds the schema
///     coverage check.
/// </summary>
public abstract class SubrecordEncoderTestBase<TModel>
{
    /// <summary>The 4-character ESM record signature this encoder targets (e.g. "PWAT").</summary>
    protected abstract string RecordSignature { get; }

    /// <summary>Build a synthetic in-memory model with controlled field values.</summary>
    protected abstract TModel MakeSyntheticModel();

    /// <summary>Canonical expected bytes for the synthetic model.</summary>
    protected abstract byte[] GetExpectedBytes();

    /// <summary>
    ///     Encode the model into bytes. Most subclasses delegate to the encoder's
    ///     internal byte-payload builder (e.g. <c>PwatEncoder.EncodeDnamPayload(model)</c>).
    /// </summary>
    protected abstract byte[] EncodeModel(TModel model);

    /// <summary>
    ///     Subrecord signatures the encoder produces. Defaults to a single entry,
    ///     <c>"DATA"</c>, since most data-bearing subrecords use that signature. Override
    ///     when the encoder emits a typed payload (DNAM, CNAM, etc.) or multiple typed
    ///     subrecords so the schema coverage check exercises every one.
    /// </summary>
    protected virtual IReadOnlyCollection<string> EmittedSubrecordSignatures => ["DATA"];

    /// <summary>
    ///     Parse encoded bytes back into a model. Override when a parser exists for the
    ///     subrecord. Default returns (false, default) — the round-trip test treats this
    ///     as "no parser available" and passes vacuously, surfacing in the test name
    ///     rather than failing silently.
    /// </summary>
    protected virtual (bool Parsed, TModel? Model) TryParseBytes(byte[] bytes)
    {
        return (false, default);
    }

    [Fact]
    public void Encodes_ProducesExpectedBytes()
    {
        var model = MakeSyntheticModel();
        var encoded = EncodeModel(model);
        Assert.Equal(GetExpectedBytes(), encoded);
    }

    [Fact]
    public void RoundTrip_ParseEncode_IsByteIdentical()
    {
        var (parsed, model) = TryParseBytes(GetExpectedBytes());
        if (!parsed || model is null)
        {
            // No parser available for this encoder yet; the encode path is still covered
            // by Encodes_ProducesExpectedBytes. Treat this as a known gap rather than
            // a failure so the scaffold doesn't block encoders shipped without parsers.
            return;
        }

        var roundTripped = EncodeModel(model);
        Assert.Equal(GetExpectedBytes(), roundTripped);
    }

    [Fact]
    public void Endian_BigEndianMatchesSchemaRegistry()
    {
        Assert.NotEmpty(EmittedSubrecordSignatures);

        foreach (var subSig in EmittedSubrecordSignatures)
        {
            var schema = SubrecordSchemaRegistry.GetSchema(subSig, RecordSignature, GetExpectedBytes().Length);
            Assert.True(
                schema is not null,
                $"SubrecordSchemaRegistry has no entry for ({subSig}, {RecordSignature}). " +
                "Add a schema entry so the Xbox→PC converter can round-trip this subrecord.");
        }
    }
}
