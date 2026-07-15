using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Validation;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class PackageSemanticValidatorTests
{
    private const uint PackFormId = 0x01001000;
    private const uint TargetFormId = 0x01002000;
    private const uint ActorFormId = 0x01003000;

    [Fact]
    public void Validate_rejects_zero_FormID_bearing_package_union()
    {
        var bytes = BuildPlugin(BuildRecord(
            "PACK", PackFormId, ("PTDT", PackageUnion(0, 0, 16))));

        var result = Validate(bytes);

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("PTDT Type 0 FormID 0x00000000", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_package_union_target_of_wrong_type()
    {
        var bytes = BuildPlugin(
            BuildRecord("PACK", PackFormId, ("PLDT", PackageUnion(0, TargetFormId, 12))));

        var result = Validate(bytes, "CELL");

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("resolves to CELL, invalid for that union arm", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_accepts_type_compatible_package_target()
    {
        var bytes = BuildPlugin(
            BuildRecord("PACK", PackFormId, ("PTDT", PackageUnion(0, TargetFormId, 16))));

        var result = Validate(bytes, "REFR");

        Assert.Equal(0, result.ErrorCount);
        Assert.Contains("1 package target/location refs", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_asserts_actor_PKID_closure_and_type()
    {
        const uint dangling = 0x01004000;
        var bytes = BuildPlugin(
            BuildRecord("PACK", PackFormId),
            BuildRecord("NPC_", ActorFormId,
                ("PKID", FormIdBytes(PackFormId)),
                ("PKID", FormIdBytes(TargetFormId)),
                ("PKID", FormIdBytes(dangling))));

        var result = Validate(bytes, "REFR");

        Assert.Equal(2, result.ErrorCount);
        Assert.Contains("resolves to REFR, expected PACK", result.Report, StringComparison.Ordinal);
        Assert.Contains("does not resolve to an emitted/master PACK", result.Report, StringComparison.Ordinal);
    }

    private static SemanticValidationResult Validate(byte[] bytes, string? masterTargetType = null) =>
        PluginSemanticValidator.Validate(
            bytes,
            masterTargetType is null ? new HashSet<uint>() : [TargetFormId],
            masterTargetType is null
                ? new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal)
                : new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal)
                {
                    [masterTargetType] = [TargetFormId],
                });

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

    private static byte[] PackageUnion(byte type, uint formId, int length)
    {
        var bytes = new byte[length];
        bytes[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), formId);
        return bytes;
    }

    private static byte[] FormIdBytes(uint formId)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, formId);
        return bytes;
    }
}
