using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Analysis;

public sealed class EsmScriptSourceCoverageAnalyzerTests
{
    [Fact]
    public void Windows1252Ellipsis_IsExecutablePunctuation_NotLatin1Whitespace()
    {
        var script = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "SCPT",
                FormId = 0x01000800
            },
            // Windows-1252 0x85 is U+2026 HORIZONTAL ELLIPSIS. Latin-1 would decode it
            // as U+0085 NEXT LINE, which .NET classifies as whitespace and would hide the
            // source-only executable contradiction this audit exists to report.
            Subrecords =
            [
                new ParsedSubrecord
                {
                    Signature = "SCTX",
                    Data = [0x85, 0x00]
                }
            ]
        };

        var row = Assert.Single(EsmScriptSourceCoverageAnalyzer.Analyze([script]));

        Assert.Equal(1, row.SctxDecodedLength);
        Assert.Equal("executable", row.SourceClassification);
        Assert.True(row.HardContradiction);
        Assert.Contains("executable SCTX has no SCDA payload", row.HardContradictionReason,
            StringComparison.Ordinal);
    }
}