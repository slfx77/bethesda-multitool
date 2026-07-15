using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Validation;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public class PluginSemanticValidatorTests
{
    private const uint OwnerFormId = 0x01001000;
    private const uint ScriptFormId = 0x01002000;

    [Fact]
    public void Validate_AcceptsScriThatTargetsEmittedScpt()
    {
        var bytes = BuildPlugin(
            BuildRecord("ACTI", OwnerFormId, ("SCRI", FormIdBytes(ScriptFormId))),
            BuildRecord("SCPT", ScriptFormId));

        var result = PluginSemanticValidator.Validate(
            bytes,
            new HashSet<uint>(),
            new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal));

        Assert.Equal(0, result.ErrorCount);
        Assert.Contains("1 script refs", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsScriThatTargetsMasterScpt()
    {
        var bytes = BuildPlugin(BuildRecord("ACTI", OwnerFormId, ("SCRI", FormIdBytes(ScriptFormId))));
        var masterIds = new HashSet<uint> { ScriptFormId };
        var masterIdsByType = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal)
        {
            ["SCPT"] = [ScriptFormId]
        };

        var result = PluginSemanticValidator.Validate(bytes, masterIds, masterIdsByType);

        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public void Validate_RejectsDanglingScri()
    {
        var bytes = BuildPlugin(BuildRecord("DOOR", OwnerFormId, ("SCRI", FormIdBytes(ScriptFormId))));

        var result = PluginSemanticValidator.Validate(
            bytes,
            new HashSet<uint>(),
            new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal));

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("DOOR 0x01001000 SCRI FormID 0x01002000", result.Report, StringComparison.Ordinal);
        Assert.Contains("dangling SCRI", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsScriThatTargetsWrongRecordType()
    {
        var bytes = BuildPlugin(
            BuildRecord("ACTI", OwnerFormId, ("SCRI", FormIdBytes(ScriptFormId))),
            BuildRecord("QUST", ScriptFormId));

        var result = PluginSemanticValidator.Validate(
            bytes,
            new HashSet<uint>(),
            new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal));

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("resolves to QUST, expected SCPT", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsMasterScriTargetOfWrongRecordType()
    {
        var bytes = BuildPlugin(BuildRecord("ACTI", OwnerFormId, ("SCRI", FormIdBytes(ScriptFormId))));
        var masterIds = new HashSet<uint> { ScriptFormId };
        var masterIdsByType = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal)
        {
            ["QUST"] = [ScriptFormId]
        };

        var result = PluginSemanticValidator.Validate(bytes, masterIds, masterIdsByType);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("resolves to QUST, expected SCPT", result.Report, StringComparison.Ordinal);
    }

    private static byte[] BuildPlugin(params byte[][] records)
    {
        var tes4 = BuildRecord("TES4", 0);
        return [.. tes4, .. records.SelectMany(static record => record)];
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

    private static byte[] FormIdBytes(uint formId)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, formId);
        return bytes;
    }
}
