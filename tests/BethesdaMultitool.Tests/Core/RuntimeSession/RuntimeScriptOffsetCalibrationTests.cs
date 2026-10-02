using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeScriptOffsetCalibrationTests
{
    private static readonly byte[] Code = Convert.FromHexString("1d0000001000080002002a000000000016000f0001000b00205869100500010072010059100b00010072020000000000000019000000110000001000060000005800000015000b00730100060020581f10000016000d0003000900207301002031203d3d1c000100bc11130003007203006e010000007a9a9999999999d93f7711070001006e0a000000221002000000190000001900000011000000");
    private static readonly RuntimeSourcePlugin Source = new(0, "FalloutNV.esm", "50991d36804b7d1e70df1afd7471b72f0e29d1b456ee2516a9717c002564e7c1");

    [Theory]
    [InlineData("synthetic-bracket", "Matched")]
    [InlineData("legacy-no-proof", "Unavailable")]
    [InlineData("missing-end", "Unavailable")]
    [InlineData("wrong-process", "Unavailable")]
    [InlineData("wrong-start-time", "Unavailable")]
    [InlineData("before-filetime-epoch", "Unavailable")]
    [InlineData("old-hash-field-only", "Unavailable")]
    [InlineData("wrong-native", "Unavailable")]
    [InlineData("wrong-native-base", "Unavailable")]
    [InlineData("wrong-capture", "Unavailable")]
    [InlineData("wrong-connection", "Unavailable")]
    [InlineData("wrong-session", "Unavailable")]
    [InlineData("unavailable-end", "Unavailable")]
    [InlineData("wrong-window-byte", "Unavailable")]
    [InlineData("wrong-window-address", "Unavailable")]
    [InlineData("different-caller", "Unavailable")]
    [InlineData("interior-buffer", "Unavailable")]
    [InlineData("different-after-buffer", "Unavailable")]
    [InlineData("wrong-opcode", "Mismatch")]
    public async Task Actual_expression_row_requires_its_own_trace_proofs(string scenario, string expected)
    {
        var row = RuntimeDispatchProofFixture.ActualCommand();
        if (scenario == "different-caller") row["callerAddress"] = 0x005ACBB7;
        if (scenario == "interior-buffer") row["commandLocation"]!["before"]!["pointerRangeStatus"] = "inside-data";
        if (scenario == "different-after-buffer") row["commandLocation"]!["after"]!["scriptDataAddress"] = 1;
        if (scenario == "wrong-opcode") row["opcode"] = 0x102E;
        var trace = await RuntimeDispatchProofFixture.Trace(row, Source.Sha256, proofs: scenario != "legacy-no-proof",
            amend: (identity, _, end) =>
            {
                if (scenario == "missing-end") { end.Remove("dispatchWindow"); return; }
                if (scenario == "old-hash-field-only")
                {
                    identity.Remove("executableFileSha256"); identity["executableSha256"] = RuntimeScriptOffsetCalibration.ExecutableHash; return;
                }
                var proof = end["dispatchWindow"];
                if (proof is null) return;
                switch (scenario)
                {
                    case "wrong-process": proof["processId"] = 100; break;
                    case "wrong-start-time": proof["processCreationFileTime"] = 134353560780109827UL; break;
                    case "before-filetime-epoch": identity["processStartedUtc"] = "1601-01-01T00:00:00+01:00"; break;
                    case "wrong-native": proof["nativeSha256"] = new string('b', 64); break;
                    case "wrong-native-base": proof["nativeImageBase"] = 0x55001000; break;
                    case "wrong-capture": proof["captureGeneration"] = 5; break;
                    case "wrong-connection": proof["connectionGeneration"] = 3; break;
                    case "wrong-session": proof["session"] = "other-capture"; break;
                    case "unavailable-end": proof["status"] = "unavailable"; break;
                    case "wrong-window-byte": proof["bytesHex"] = "90" + proof["bytesHex"]!.GetValue<string>()[2..]; break;
                    case "wrong-window-address": proof["address"] = 0x005AC7A1; break;
                }
            });
        var record = new ParsedMainRecord
        {
            Offset = 4523800, Header = new() { Signature = "SCPT", FormId = 0x00105230, DataSize = (uint)(32 + Code.Length) },
            Subrecords = [new() { Signature = "SCHR", Data = new byte[20] }, new() { Signature = "SCDA", Data = Code }]
        };
        var blocks = RuntimeScriptBlockCatalog.ReadBlocks(record, Source, "retained-FalloutNV.esm", 0x00105230, id => id);
        var report = RuntimeScriptSourceMap.Build(trace, new([Source], true), new(blocks, new Dictionary<uint, string>()));
        var mapping = Assert.Single(report.Mappings);
        Assert.Equal("Matched", mapping.OwnerStatus); Assert.Equal("Matched", mapping.BytecodeStatus);
        Assert.Equal(expected, mapping.LocationStatus);
        if (scenario == "synthetic-bracket")
        {
            Assert.Equal(83u, row["commandLocation"]!["before"]!["opcodeOffset"]!.GetValue<uint>());
            Assert.Equal(79, mapping.ScdaOffset); Assert.Equal("ExpressionCall", mapping.Instruction!.Kind);
            Assert.Equal((ushort)4127, mapping.Instruction.Opcode); Assert.Equal(8, mapping.Instruction.ReconstructionLineStart);
            Assert.Contains(report.Limitations, value => value.Contains("Scoped to PC expression-X", StringComparison.Ordinal));
        }
        if (scenario == "legacy-no-proof")
            Assert.Contains(report.Limitations, value => value.StartsWith("Offset calibration: Unavailable", StringComparison.Ordinal));
    }
}
