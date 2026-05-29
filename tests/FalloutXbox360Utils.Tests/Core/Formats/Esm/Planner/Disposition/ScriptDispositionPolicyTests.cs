using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Disposition;

public sealed class ScriptDispositionPolicyTests
{
    [Fact]
    public void Truncated_Proto_Scda_Returns_KeepMaster()
    {
        var policy = new ScriptDispositionPolicy();
        var script = new ScriptRecord
        {
            FormId = 0x0014DA58,
            CompiledData = new byte[100], // Proto SCDA shorter than master's.
        };
        var master = MakeMasterScript(0x0014DA58, scdaSize: 2151);

        var decision = policy.Decide(MakeOverride(script, master));

        Assert.NotNull(decision);
        Assert.Equal(RecordDisposition.KeepMaster, decision.Disposition);
        Assert.Contains("downgrade", decision.Provenance.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Equal_Scda_Size_Returns_Null_Letting_Default_Choose_Override()
    {
        var policy = new ScriptDispositionPolicy();
        var script = new ScriptRecord { FormId = 0x0014DA58, CompiledData = new byte[2151] };
        var master = MakeMasterScript(0x0014DA58, scdaSize: 2151);

        Assert.Null(policy.Decide(MakeOverride(script, master)));
    }

    [Fact]
    public void Larger_Proto_Scda_Returns_Null()
    {
        var policy = new ScriptDispositionPolicy();
        var script = new ScriptRecord { FormId = 0x0014DA58, CompiledData = new byte[3000] };
        var master = MakeMasterScript(0x0014DA58, scdaSize: 2151);

        Assert.Null(policy.Decide(MakeOverride(script, master)));
    }

    [Fact]
    public void Master_Only_Entry_Returns_Null()
    {
        var policy = new ScriptDispositionPolicy();
        var entry = new CatalogEntry
        {
            Type = "SCPT",
            Source = SourceKind.MasterOnly,
            MasterFormId = 0x0014DA58,
            Master = MakeMasterScript(0x0014DA58, scdaSize: 2151),
        };

        Assert.Null(policy.Decide(entry));
    }

    [Fact]
    public void Dmp_New_Script_Returns_Null()
    {
        var policy = new ScriptDispositionPolicy();
        var entry = new CatalogEntry
        {
            Type = "SCPT",
            Source = SourceKind.DmpNew,
            DmpFormId = 0xAA000001,
            Model = new ScriptRecord { FormId = 0xAA000001 },
        };

        Assert.Null(policy.Decide(entry));
    }

    private static CatalogEntry MakeOverride(ScriptRecord script, ParsedMainRecord master) => new()
    {
        Type = "SCPT",
        Source = SourceKind.DmpOverride,
        MasterFormId = script.FormId,
        DmpFormId = script.FormId,
        Model = script,
        Master = master,
    };

    private static ParsedMainRecord MakeMasterScript(uint formId, int scdaSize) => new()
    {
        Header = new MainRecordHeader
        {
            Signature = "SCPT",
            DataSize = 0,
            Flags = 0,
            FormId = formId,
            Timestamp = 0,
            VcsInfo = 0,
            Version = 15,
        },
        Offset = 0,
        Subrecords =
        [
            new ParsedSubrecord
            {
                Signature = "SCDA",
                Data = new byte[scdaSize],
            },
        ],
    };
}
