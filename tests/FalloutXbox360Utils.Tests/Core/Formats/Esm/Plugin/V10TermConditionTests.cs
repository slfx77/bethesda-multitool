using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v10 tests for TERM menu-item CTDA conditions (with optional CIS1/CIS2 string parameters).
///     Conditions emit between ITXT and the result-script block per fopdoc.
/// </summary>
public class V10TermConditionTests
{
    [Fact]
    public void TermEncoder_EncodeNew_ConditionEmittedBetweenItxtAndRnam()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Login",
                    ResultScript = 0x111,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x20, // ==
                            ComparisonValue = 1.0f,
                            FunctionIndex = 76, // GetIsID
                            Parameter1 = 0x1234,
                            RunOn = 0
                        }
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "ITXT" or "CTDA" or "RNAM")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["ITXT", "CTDA", "RNAM"], sigs);
    }

    [Fact]
    public void TermEncoder_EncodeNew_MultipleConditionsEmittedInOrder()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Maybe",
                    ResultScript = 0x1,
                    Conditions =
                    [
                        new DialogueCondition { Type = 0x20, FunctionIndex = 1 },
                        new DialogueCondition { Type = 0x21, FunctionIndex = 2 }, // OR-bit set
                        new DialogueCondition { Type = 0x40, FunctionIndex = 3 }  // !=
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var ctdas = encoded.Subrecords.Where(s => s.Signature == "CTDA").ToList();
        Assert.Equal(3, ctdas.Count);
        foreach (var ctda in ctdas)
        {
            Assert.Equal(28, ctda.Bytes.Length);
        }

        // FunctionIndex (offset 8, uint16 LE) preserves order.
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[0].Bytes.AsSpan(8, 2)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[1].Bytes.AsSpan(8, 2)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[2].Bytes.AsSpan(8, 2)));
    }

    [Fact]
    public void TermEncoder_EncodeNew_CtdaLayoutMatchesPdb()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "X",
                    ResultScript = 0x1,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x35,
                            ComparisonValue = 3.5f,
                            FunctionIndex = 250,
                            Parameter1 = 0xABCDEFu,
                            Parameter2 = 0x12345678u,
                            RunOn = 4, // Linked Reference
                            Reference = 0xDEADBEEFu
                        }
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);
        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");

        Assert.Equal(0x35, ctda.Bytes[0]);
        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(ctda.Bytes.AsSpan(4, 4)));
        Assert.Equal(250, BinaryPrimitives.ReadUInt16LittleEndian(ctda.Bytes.AsSpan(8, 2)));
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(16, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(20, 4)));
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void TermEncoder_EncodeNew_Cis1FollowsCtdaWhenStringParam1Set()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "X",
                    ResultScript = 0x1,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x20,
                            FunctionIndex = 449, // GetVariable
                            Parameter1String = "MyScriptVar"
                        }
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "CTDA" or "CIS1" or "CIS2" or "RNAM")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["CTDA", "CIS1", "RNAM"], sigs);

        var cis1 = Assert.Single(encoded.Subrecords, s => s.Signature == "CIS1");
        // Null-terminated Latin-1 string.
        Assert.Equal("MyScriptVar".Length + 1, cis1.Bytes.Length);
        Assert.Equal(0, cis1.Bytes[^1]);
    }

    [Fact]
    public void TermEncoder_EncodeNew_Cis1AndCis2BothEmittedWhenBothSet()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "X",
                    ResultScript = 0x1,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x20,
                            FunctionIndex = 449,
                            Parameter1String = "P1",
                            Parameter2String = "P2"
                        }
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "CTDA" or "CIS1" or "CIS2")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["CTDA", "CIS1", "CIS2"], sigs);
    }

    [Fact]
    public void TermEncoder_EncodeNew_ConditionsPrecedeEmbeddedScriptBlock()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "X",
                    CompiledData = [0x10],
                    Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 1 }]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "ITXT" or "CTDA" or "SCHR" or "SCDA")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["ITXT", "CTDA", "SCHR", "SCDA"], sigs);
    }

    [Fact]
    public void TermEncoder_EncodeNew_NoConditionsLeavesItxtRnamPairIntact()
    {
        // Regression: menu items without conditions should still emit just ITXT + RNAM.
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems = [new TerminalMenuItem { Text = "X", ResultScript = 0x1 }]
        };

        var encoded = TermEncoder.EncodeNew(term);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CIS1");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CIS2");
    }
}
