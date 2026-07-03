using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

// Serial: these toggle the shared static UnmodeledSubrecordLog.Enabled flag, so they must not run
// in parallel with other tests that parse records.
[CollectionDefinition("UnmodeledSubrecordLog", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit [CollectionDefinition] marker type; the 'Collection' suffix is idiomatic for these.")]
public sealed class UnmodeledSubrecordLogCollection;

/// <summary>
///     Verifies the flagging foundation: a subrecord a typed handler iterates but does not model now
///     surfaces via <see cref="UnmodeledSubrecordLog" /> (its <c>default:</c> case) instead of being
///     silently dropped.
/// </summary>
[Collection("UnmodeledSubrecordLog")]
public class UnmodeledSubrecordLogTests
{
    [Fact]
    public void Note_IsNoOp_WhenDisabled()
    {
        UnmodeledSubrecordLog.Clear();
        UnmodeledSubrecordLog.Enabled = false;

        UnmodeledSubrecordLog.Note("AMMO", "ZZZZ", 4);

        Assert.False(UnmodeledSubrecordLog.HasEntries);
    }

    [Fact]
    public void TypedHandler_UnknownSubrecord_IsFlagged_NotSilentlyDropped()
    {
        var recordBytes = BuildRecordBytes(0x00070001, "AMMO", false,
            ("EDID", NullTermString("TestAmmo")),
            ("ZZZZ", [1, 2, 3, 4]));

        var mainRecord = new DetectedMainRecord("AMMO", (uint)(recordBytes.Length - 24), 0, 0x00070001, 0, false);
        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);
        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);

        UnmodeledSubrecordLog.Clear();
        UnmodeledSubrecordLog.Enabled = true;
        try
        {
            parser.ParseAmmo();

            var snapshot = UnmodeledSubrecordLog.Snapshot();
            Assert.Contains(snapshot, e => e.RecordType == "AMMO" && e.Signature == "ZZZZ" && e.DataLength == 4);
            // A modeled subrecord (EDID) must NOT be flagged.
            Assert.DoesNotContain(snapshot, e => e.Signature == "EDID");
        }
        finally
        {
            UnmodeledSubrecordLog.Enabled = false;
            UnmodeledSubrecordLog.Clear();
        }
    }
}
