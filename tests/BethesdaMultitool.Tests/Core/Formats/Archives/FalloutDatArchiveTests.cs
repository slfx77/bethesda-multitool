using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Fallout;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Synthetic vectors for the two Fallout containers, shaped after the retail archives measured
///     2026-09-05. Neither has a magic word: DAT1's probe is its big-endian directory tiling the
///     file, DAT2's is its little-endian footer accounting for the file, so the rejection cases
///     are what keeps the probe chain honest.
/// </summary>
public sealed class FalloutDatArchiveTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fallout-dat-{Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Temp cleanup only.
            }
        }
    }

    private static byte[] Pascal(string s) => [(byte)s.Length, .. Encoding.ASCII.GetBytes(s)];

    private static byte[] Be(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    /// <summary>A DAT1 LZSS stream that is one verbatim block plus the terminator — valid without a compressor.</summary>
    private static byte[] LzssVerbatim(byte[] payload)
    {
        var header = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(header, (short)-payload.Length);
        return [.. header, .. payload, 0, 0];
    }

    private static byte[] BuildDat1(params (string Directory, string Name, byte[] Data, bool Compressed)[] files)
    {
        var directories = files.Select(f => f.Directory).Distinct(StringComparer.Ordinal).ToList();
        var payloads = files.Select(f => f.Compressed ? LzssVerbatim(f.Data) : f.Data).ToList();

        // Directory size first, so offsets can be absolute.
        var directoryLength = Dat1Archive.HeaderLength + directories.Sum(d => 1 + d.Length)
                              + directories.Count * Dat1Archive.DirectoryHeaderLength
                              + files.Sum(f => 1 + f.Name.Length + Dat1Archive.EntryFieldsLength);

        var bytes = new List<byte>();
        bytes.AddRange(Be((uint)directories.Count));
        bytes.AddRange(Be(94)); bytes.AddRange(Be(0)); bytes.AddRange(Be(0));
        foreach (var directory in directories)
        {
            bytes.AddRange(Pascal(directory));
        }

        var offset = directoryLength;
        foreach (var directory in directories)
        {
            var members = files.Select((f, i) => (f, i)).Where(t => t.f.Directory == directory).ToList();
            bytes.AddRange(Be((uint)members.Count));
            bytes.AddRange(Be(0)); bytes.AddRange(Be(0)); bytes.AddRange(Be(0));
            foreach (var (file, index) in members)
            {
                bytes.AddRange(Pascal(file.Name));
                bytes.AddRange(Be(file.Compressed ? Dat1Archive.CompressedAttribute : Dat1Archive.StoredAttribute));
                bytes.AddRange(Be((uint)offset));
                bytes.AddRange(Be((uint)file.Data.Length));
                bytes.AddRange(Be(file.Compressed ? (uint)payloads[index].Length : 0u));
                offset += payloads[index].Length;
            }
        }

        Assert.Equal(directoryLength, bytes.Count);
        foreach (var payload in payloads)
        {
            bytes.AddRange(payload);
        }

        return [.. bytes];
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    private static byte[] BuildDat2(params (string Path, byte[] Data, bool Compressed)[] files)
    {
        var data = new List<byte>();
        var tree = new List<byte>();
        tree.AddRange(BitConverter.GetBytes((uint)files.Length));
        foreach (var (path, content, compressed) in files)
        {
            var payload = compressed ? Zlib(content) : content;
            tree.AddRange(BitConverter.GetBytes((uint)path.Length));
            tree.AddRange(Encoding.ASCII.GetBytes(path));
            tree.Add(compressed ? Dat2Archive.ZlibType : Dat2Archive.StoredType);
            tree.AddRange(BitConverter.GetBytes((uint)content.Length));
            tree.AddRange(BitConverter.GetBytes((uint)payload.Length));
            tree.AddRange(BitConverter.GetBytes((uint)data.Count));
            data.AddRange(payload);
        }

        var total = data.Count + tree.Count + Dat2Archive.FooterLength;
        return [.. data, .. tree, .. BitConverter.GetBytes((uint)tree.Count), .. BitConverter.GetBytes((uint)total)];
    }

    [Fact]
    public void Dat1_ListsDirectoriesAndExtractsStoredAndCompressedEntries()
    {
        var path = WriteTemp(BuildDat1(
            (".", "MASTER.LST", "hello"u8.ToArray(), false),
            (@"ART\INTRFACE", "MENU.FRM", [1, 2, 3, 4, 5, 6], true)));

        var directory = Dat1Archive.Parse(path);
        Assert.Equal([".", @"ART\INTRFACE"], directory.Directories);
        Assert.Equal(["MASTER.LST", "ART/INTRFACE/MENU.FRM"], directory.Entries.Select(e => e.FullPath));

        using var backend = new BethesdaMultitool.Core.Formats.Archives.Dat1Backend(directory);
        var entries = backend.ListFiles();
        Assert.Equal("DAT1 (Fallout)", backend.FormatName);
        Assert.Equal("hello"u8.ToArray(), backend.Extract(entries[0]));
        Assert.Equal([1, 2, 3, 4, 5, 6], backend.Extract(entries[1]));
        Assert.True(entries[1].Compressed);
        Assert.Equal("ART/INTRFACE", entries[1].FolderPath);
    }

    [Fact]
    public void Dat1_Probe_RejectsUnknownAttributeAndBadTiling()
    {
        var good = BuildDat1((".", "A.TXT", "abc"u8.ToArray(), false));
        Assert.True(Dat1Archive.TryProbe(WriteTemp(good)));

        // Attributes must be exactly 0x20 or 0x40.
        var badAttribute = (byte[])good.Clone();
        var attributeAt = Dat1Archive.HeaderLength + 2 + Dat1Archive.DirectoryHeaderLength + 6;
        BinaryPrimitives.WriteUInt32BigEndian(badAttribute.AsSpan(attributeAt), 0x10);
        Assert.False(Dat1Archive.TryProbe(WriteTemp(badAttribute)));

        // The furthest entry must end exactly on EOF: trailing junk fails the tiling.
        Assert.False(Dat1Archive.TryProbe(WriteTemp([.. good, 0])));

        // And an entry past EOF fails containment.
        var overrun = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(overrun.AsSpan(attributeAt + 8), 999);
        Assert.False(Dat1Archive.TryProbe(WriteTemp(overrun)));

        // Extension gate: the same bytes under another extension are not claimed.
        var wrongExtension = Path.ChangeExtension(WriteTemp(good), ".bin");
        File.WriteAllBytes(wrongExtension, good);
        _tempFiles.Add(wrongExtension);
        Assert.False(Dat1Archive.TryProbe(wrongExtension));
    }

    [Fact]
    public void Dat2_ListsTreeAndInflatesZlibEntries()
    {
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Fallout 2 ", 50)));
        var path = WriteTemp(BuildDat2(
            (@"text\english\game\pro_item.msg", text, true),
            (@"color.pal", [9, 8, 7], false)));

        var directory = Dat2Archive.Parse(path);
        Assert.Equal(["text/english/game/pro_item.msg", "color.pal"], directory.Entries.Select(e => e.FullPath));

        using var backend = new BethesdaMultitool.Core.Formats.Archives.Dat2Backend(directory);
        var entries = backend.ListFiles();
        Assert.Equal("DAT2 (Fallout 2)", backend.FormatName);
        Assert.Equal(text, backend.Extract(entries[0]));
        Assert.Equal([9, 8, 7], backend.Extract(entries[1]));
        Assert.Equal(("text/english/game", "pro_item.msg", ".msg"), (entries[0].FolderPath, entries[0].Name, entries[0].Extension));
    }

    [Fact]
    public void Dat2_Probe_RejectsFooterThatDoesNotAccountForTheFile()
    {
        var good = BuildDat2(("a.txt", "abc"u8.ToArray(), false));
        Assert.True(Dat2Archive.TryProbe(WriteTemp(good)));

        // dataSize must equal the file length.
        Assert.False(Dat2Archive.TryProbe(WriteTemp([.. good, 0])));

        // The declared count must walk the tree to exactly its end.
        var badCount = (byte[])good.Clone();
        var treeSize = BinaryPrimitives.ReadUInt32LittleEndian(good.AsSpan(good.Length - 8));
        var treeAt = good.Length - 8 - (int)treeSize;
        BinaryPrimitives.WriteUInt32LittleEndian(badCount.AsSpan(treeAt), 2);
        Assert.False(Dat2Archive.TryProbe(WriteTemp(badCount)));

        // Type bytes other than 0/1 do not exist.
        var badType = (byte[])good.Clone();
        badType[treeAt + 4 + 4 + 5] = 7;
        Assert.False(Dat2Archive.TryProbe(WriteTemp(badType)));
    }

    [Fact]
    public void TheTwoProbesDoNotClaimEachOthersArchives()
    {
        var dat1 = WriteTemp(BuildDat1((".", "A.TXT", "abc"u8.ToArray(), false)));
        var dat2 = WriteTemp(BuildDat2(("a.txt", "abc"u8.ToArray(), false)));

        Assert.False(Dat2Archive.TryProbe(dat1));
        Assert.False(Dat1Archive.TryProbe(dat2));
    }
}
