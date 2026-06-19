using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class ByteArrayMemoryAccessorTests
{
    [Fact]
    public void ReadArray_ReadsExactRequestedRange()
    {
        var data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var accessor = new ByteArrayMemoryAccessor(data);
        var target = new byte[8];

        var bytesRead = accessor.ReadArray(10, target, 2, 4);

        Assert.Equal(4, bytesRead);
        Assert.Equal([0, 0, 10, 11, 12, 13, 0, 0], target);
    }

    [Fact]
    public void ReadArray_MatchesMemoryMappedAccessorOnSyntheticData()
    {
        var data = Enumerable.Range(0, 256).Select(i => (byte)(255 - i)).ToArray();
        using var mmf = MemoryMappedFile.CreateNew(null, data.Length);
        using var view = mmf.CreateViewAccessor(0, data.Length);
        view.WriteArray(0, data, 0, data.Length);

        var byteAccessor = new ByteArrayMemoryAccessor(data);
        var mmfAccessor = new MmfMemoryAccessor(view);
        var fromBytes = new byte[37];
        var fromMmf = new byte[37];

        var byteCount = byteAccessor.ReadArray(91, fromBytes, 0, fromBytes.Length);
        var mmfCount = mmfAccessor.ReadArray(91, fromMmf, 0, fromMmf.Length);

        Assert.Equal(mmfCount, byteCount);
        Assert.Equal(fromMmf, fromBytes);
    }
}