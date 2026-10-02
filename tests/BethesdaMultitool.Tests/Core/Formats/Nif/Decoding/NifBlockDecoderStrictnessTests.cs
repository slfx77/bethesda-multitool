using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Nif.Decoding.NifDecodingTestSupport;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>
///     The decoder's strictness rules, pinned with small synthetic nif.xml definitions so each rule is exercised in
///     isolation: unevaluable expressions, undeclared names, the declared-but-excluded-is-zero rule, forward
///     references, a missing <c>#ARG#</c>, unknown vercond macros, bounded allocation and unknown or abstract types.
/// </summary>
public class NifBlockDecoderStrictnessTests
{
    private const string SchemaXml = """
        <niftoolsxml version="0.10.0.0">
          <basic name="byte" integral="true" size="1" />
          <basic name="uint" integral="true" size="4" />
          <basic name="float" size="4" />
          <niobject name="NiObject" abstract="true" />
          <niobject name="TestUnsupportedOperator" inherit="NiObject">
            <field name="Count" type="uint" />
            <field name="Values" type="uint" length="Count #MUL# 2" />
          </niobject>
          <niobject name="TestUndeclaredName" inherit="NiObject">
            <field name="Count" type="uint" />
            <field name="Values" type="uint" length="Missing Count" />
          </niobject>
          <niobject name="TestExcludedNameIsZero" inherit="NiObject">
            <field name="Old Count" type="uint" until="10.0.1.0" />
            <field name="Count" type="uint" />
            <field name="Values" type="uint" length="(Old Count #BITOR# Count)" />
          </niobject>
          <niobject name="TestForwardReference" inherit="NiObject">
            <field name="Values" type="uint" length="Count" />
            <field name="Count" type="uint" />
          </niobject>
          <niobject name="TestArgumentWithoutArg" inherit="NiObject">
            <field name="Values" type="uint" length="#ARG#" />
          </niobject>
          <niobject name="TestUnknownMacro" inherit="NiObject">
            <field name="Flag" type="uint" vercond="#NOT_A_MACRO#" />
          </niobject>
          <niobject name="TestHugeArray" inherit="NiObject">
            <field name="Count" type="uint" />
            <field name="Values" type="float" length="Count" />
          </niobject>
          <niobject name="TestAbstract" inherit="NiObject" abstract="true" />
        </niftoolsxml>
        """;

    private static readonly NifSchema Schema = SchemaFromXml(SchemaXml);

    private static NifBlockDecoder OneBlock(string type, Action<NifTestBlockWriter> write, bool bigEndian = false)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock(type, write);
        return NifDecodingTestSupport.Open(builder.Build(), Schema);
    }

    [Fact]
    public void UnevaluableExpression_FailsStrict_AndIsReportedTolerant()
    {
        var decoder = OneBlock("TestUnsupportedOperator", w => w.U32(2).U32(10).U32(11).U32(12).U32(13));

        var strict = Assert.Throws<NifDecodeException>(() => decoder.Decode(0, NifDecodeMode.Strict));
        Assert.Equal(NifDecodeFailureKind.Schema, strict.Failure.Kind);
        Assert.Equal("Values", strict.Failure.FieldPath);
        Assert.Contains("#MUL#", strict.Failure.Reason);

        var tolerant = decoder.Decode(0, NifDecodeMode.Tolerant);
        Assert.NotNull(tolerant.Failure);
        Assert.Equal(NifDecodeFailureKind.Schema, tolerant.Failure.Kind);
        Assert.Equal("Values", tolerant.Failure.FieldPath);
        AssertInteger(tolerant.Root, "Count", 2);
        Assert.False(tolerant.Root.Contains("Values"));
        Assert.Equal(4, tolerant.ConsumedBytes);
    }

    [Fact]
    public void UndeclaredName_Fails()
    {
        var decoder = OneBlock("TestUndeclaredName", w => w.U32(0));

        var strict = Assert.Throws<NifDecodeException>(() => decoder.Decode(0, NifDecodeMode.Strict));
        Assert.Equal(NifDecodeFailureKind.Schema, strict.Failure.Kind);
        Assert.Contains("Missing Count", strict.Failure.Reason);
        Assert.Equal(NifDecodeFailureKind.Schema, decoder.Decode(0, NifDecodeMode.Tolerant).Failure?.Kind);
    }

    [Fact]
    public void DeclaredButExcludedName_EvaluatesToZero()
    {
        var block = OneBlock("TestExcludedNameIsZero", w => w.U32(2).U32(0xAAAA).U32(0xBBBB))
            .Decode(0, NifDecodeMode.Strict);

        Assert.True(block.IsComplete);
        Assert.False(block.Root.Contains("Old Count"));
        Assert.Equal(2, block.Root.Get<NifArrayValue>("Values").Count);
    }

    [Fact]
    public void NameReferencedBeforeItIsRead_Fails()
    {
        var strict = Assert.Throws<NifDecodeException>(() =>
            OneBlock("TestForwardReference", w => w.U32(0)).Decode(0, NifDecodeMode.Strict));

        Assert.Equal(NifDecodeFailureKind.Schema, strict.Failure.Kind);
        Assert.Contains("before", strict.Failure.Reason);
    }

    [Fact]
    public void ArgumentTokenWithoutAnArgument_Fails()
    {
        var strict = Assert.Throws<NifDecodeException>(() =>
            OneBlock("TestArgumentWithoutArg", w => w.U32(0)).Decode(0, NifDecodeMode.Strict));

        Assert.Equal(NifDecodeFailureKind.Schema, strict.Failure.Kind);
        Assert.Contains("#ARG#", strict.Failure.Reason);
    }

    [Fact]
    public void UnknownVercondMacro_Fails()
    {
        var strict = Assert.Throws<NifDecodeException>(() =>
            OneBlock("TestUnknownMacro", w => w.U32(0)).Decode(0, NifDecodeMode.Strict));

        Assert.Equal(NifDecodeFailureKind.Schema, strict.Failure.Kind);
        Assert.Equal("Flag", strict.Failure.FieldPath);
        Assert.Contains("#NOT_A_MACRO#", strict.Failure.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrayLongerThanTheBytesLeft_FailsBeforeAllocating(bool bigEndian)
    {
        var decoder = OneBlock("TestHugeArray", w => w.U32(0x7FFFFFFF).F32(1f), bigEndian);

        var strict = Assert.Throws<NifDecodeException>(() => decoder.Decode(0, NifDecodeMode.Strict));
        Assert.Equal(NifDecodeFailureKind.Data, strict.Failure.Kind);
        Assert.Equal("Values", strict.Failure.FieldPath);

        // Control: a count that fits decodes.
        var fits = OneBlock("TestHugeArray", w => w.U32(1).F32(1f), bigEndian).Decode(0, NifDecodeMode.Strict);
        Assert.Equal(1, fits.Root.Get<NifFloatArrayValue>("Values").Count);
    }

    [Theory]
    [InlineData("NotInTheSchema")]
    [InlineData("TestAbstract")]
    public void UnknownOrAbstractBlockType_FailsAsASchemaError(string type)
    {
        var decoder = OneBlock(type, w => w.U32(0));

        var strict = Assert.Throws<NifDecodeException>(() => decoder.Decode(0, NifDecodeMode.Strict));
        Assert.Equal(NifDecodeFailureKind.Schema, strict.Failure.Kind);
        Assert.Contains(type, strict.Failure.Reason);
        Assert.Equal(NifDecodeFailureKind.Schema, decoder.Decode(0, NifDecodeMode.Tolerant).Failure?.Kind);
    }
}
