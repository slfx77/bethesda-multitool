using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class EncodedSubrecordFormIdRemapperTests
{
    [Fact]
    public void Remap_NpcSubrecords_RewritesKnownFormIdFieldsOnly()
    {
        var aliases = new Dictionary<uint, uint>
        {
            [0x000F83A0] = 0x00133FDD,
            [0x00001234] = 0x00005678
        };
        var cnto = new byte[8];
        SubrecordEncoder.WriteFormId(cnto, 0, 0x000F83A0);
        SubrecordEncoder.WriteInt32(cnto, 4, 3);
        var fggs = new byte[4];
        SubrecordEncoder.WriteFormId(fggs, 0, 0x000F83A0);

        var remapped = EncodedSubrecordFormIdRemapper.Remap("NPC_",
        [
            new EncodedSubrecord("CNTO", cnto),
            new EncodedSubrecord("PNAM", BitConverter.GetBytes(0x00001234u)),
            new EncodedSubrecord("FGGS", fggs)
        ], aliases);

        Assert.Equal(0x00133FDDu, ReadFormId(remapped[0].Bytes, 0));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(remapped[0].Bytes.AsSpan(4, 4)));
        Assert.Equal(0x00005678u, ReadFormId(remapped[1].Bytes, 0));
        Assert.Equal(0x000F83A0u, ReadFormId(remapped[2].Bytes, 0));
    }

    [Fact]
    public void Remap_PlacedReferenceSubrecords_RewritesBaseAndLinkedFormIds()
    {
        var aliases = new Dictionary<uint, uint>
        {
            [0x000F83A0] = 0x00133FDD,
            [0x00001111] = 0x00002222
        };
        var xloc = new byte[20];
        xloc[0] = 50;
        SubrecordEncoder.WriteFormId(xloc, 4, 0x00001111);
        var xlkr = new byte[8];
        SubrecordEncoder.WriteFormId(xlkr, 0, 0x00001111);
        SubrecordEncoder.WriteFormId(xlkr, 4, 0x000F83A0);

        var remapped = EncodedSubrecordFormIdRemapper.Remap("REFR",
        [
            new EncodedSubrecord("NAME", BitConverter.GetBytes(0x000F83A0u)),
            new EncodedSubrecord("XLOC", xloc),
            new EncodedSubrecord("XLKR", xlkr)
        ], aliases);

        Assert.Equal(0x00133FDDu, ReadFormId(remapped[0].Bytes, 0));
        Assert.Equal(0x00002222u, ReadFormId(remapped[1].Bytes, 4));
        Assert.Equal(0x00002222u, ReadFormId(remapped[2].Bytes, 0));
        Assert.Equal(0x00133FDDu, ReadFormId(remapped[2].Bytes, 4));
    }

    private static uint ReadFormId(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
