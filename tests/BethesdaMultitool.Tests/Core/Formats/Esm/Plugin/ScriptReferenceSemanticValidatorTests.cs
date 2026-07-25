using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Validation;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class ScriptReferenceSemanticValidatorTests
{
    [Fact]
    public void Validate_RejectsNullScroAndReferenceCountMismatch()
    {
        var bytes = BuildPlugin(BuildScpt(
            0x01002000, 2,
            ("SCRO", UInt32Bytes(0))));

        var result = PluginSemanticValidator.Validate(bytes, new HashSet<uint>());

        Assert.Equal(2, result.ErrorCount);
        Assert.Contains("SCHR declares 2 reference(s), but 1", result.Report, StringComparison.Ordinal);
        Assert.Contains("SCRO[0] FormID 0x00000000", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsRemappedScroEngineRefAndMatchingScrv()
    {
        const uint emittedTarget = 0x01003000;
        var bytes = BuildPlugin(
            BuildRecord("STAT", emittedTarget),
            BuildScpt(
                0x01002000, 3,
                ("SLSD", SlsdBytes(5)),
                ("SCRO", UInt32Bytes(emittedTarget)),
                ("SCRO", UInt32Bytes(0x00000014)),
                ("SCRV", UInt32Bytes(5))));

        var result = PluginSemanticValidator.Validate(bytes, new HashSet<uint>());

        Assert.Equal(0, result.ErrorCount);
        Assert.Contains("3 SCPT object/variable refs", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsMasterChildScroFromAdditionalSet()
    {
        const uint childRef = 0x0010ABCD;
        var bytes = BuildPlugin(BuildScpt(
            0x01002000, 1,
            ("SCRO", UInt32Bytes(childRef))));

        var result = PluginSemanticValidator.Validate(
            bytes, new HashSet<uint>(), null, new HashSet<uint> { childRef });

        Assert.Equal(0, result.ErrorCount);
    }

    private static byte[] BuildPlugin(params byte[][] records)
    {
        return [.. BuildRecord("TES4", 0), .. records.SelectMany(static record => record)];
    }

    private static byte[] BuildScpt(
        uint formId,
        uint declaredReferences,
        params (string Signature, byte[] Data)[] subrecords)
    {
        var schr = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(4), declaredReferences);
        return BuildRecord("SCPT", formId, [("SCHR", schr), .. subrecords]);
    }

    private static byte[] BuildRecord(
        string signature,
        uint formId,
        params (string Signature, byte[] Data)[] subrecords)
    {
        var encoded = subrecords
            .Select(static subrecord => new EncodedSubrecord(subrecord.Signature, subrecord.Data))
            .ToList();
        return PluginRecordByteBuilder.BuildNewRecordBytes(signature, formId, 0, encoded);
    }

    private static byte[] UInt32Bytes(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] SlsdBytes(uint variableId)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, variableId);
        return bytes;
    }
}