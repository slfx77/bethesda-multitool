using Xbox360MemoryCarver.Utils;

namespace Xbox360MemoryCarver.Minidump;

/// <summary>
/// Module information from minidump.
/// </summary>
public class ModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    public uint NameRva { get; set; }
    public uint Timestamp { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Memory range information from minidump.
/// </summary>
public class MemoryRange
{
    public ulong VirtualAddress { get; set; }
    public ulong Size { get; set; }
    public long FileOffset { get; set; }
}

/// <summary>
/// Stream information from minidump header.
/// </summary>
public class StreamInfo
{
    public uint Rva { get; set; }
    public uint Size { get; set; }
}

/// <summary>
/// Extracts PE files (EXE/DLL) from Windows/Xbox 360 minidump files.
/// Handles Xbox 360 memory dumps with fragmented memory regions.
/// </summary>
public class MinidumpExtractor
{
    private readonly string _outputDir;

    public MinidumpExtractor(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(_outputDir);
    }

    /// <summary>
    /// Extract all loaded modules from a minidump file.
    /// </summary>
    public async Task<List<ModuleInfo>> ExtractModulesAsync(string dumpPath)
    {
        var extractedModules = new List<ModuleInfo>();

        try
        {
            await using var fs = new FileStream(dumpPath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // Parse header
            var (numStreams, streamDirRva) = ParseHeader(reader);
            var (moduleStream, memory64Stream) = FindStreams(reader, streamDirRva, numStreams);

            if (moduleStream == null)
            {
                Console.WriteLine("No ModuleListStream found");
                return extractedModules;
            }

            var modules = ParseModules(reader, moduleStream);
            var memoryRanges = memory64Stream != null 
                ? ParseMemoryRanges(reader, memory64Stream) 
                : [];

            // Extract each module
            foreach (var (mod, idx) in modules.Select((m, i) => (m, i)))
            {
                try
                {
                    var moduleInfo = await ExtractModuleFromRangesAsync(reader, mod, memoryRanges);
                    if (moduleInfo != null)
                    {
                        extractedModules.Add(moduleInfo);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error extracting module {mod.Name}: {ex.Message}");
                }
            }

            // Save module list
            await SaveModuleListAsync(dumpPath, modules);

            Console.WriteLine($"Successfully extracted {extractedModules.Count} modules");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing minidump {dumpPath}: {ex.Message}");
        }

        return extractedModules;
    }

    private (uint NumStreams, uint StreamDirRva) ParseHeader(BinaryReader reader)
    {
        var magic = reader.ReadBytes(4);
        if (!magic.SequenceEqual("MDMP"u8.ToArray()))
        {
            throw new InvalidDataException($"Not a valid minidump file: {System.Text.Encoding.ASCII.GetString(magic)}");
        }

        reader.ReadBytes(4); // Skip version
        uint numStreams = reader.ReadUInt32();
        uint streamDirRva = reader.ReadUInt32();

        return (numStreams, streamDirRva);
    }

    private (StreamInfo? ModuleStream, StreamInfo? Memory64Stream) FindStreams(
        BinaryReader reader, uint streamDirRva, uint numStreams)
    {
        reader.BaseStream.Seek(streamDirRva, SeekOrigin.Begin);

        StreamInfo? moduleStream = null;
        StreamInfo? memory64Stream = null;

        for (int i = 0; i < numStreams; i++)
        {
            uint streamType = reader.ReadUInt32();
            uint dataSize = reader.ReadUInt32();
            uint rva = reader.ReadUInt32();

            switch (streamType)
            {
                case 4: // ModuleListStream
                    moduleStream = new StreamInfo { Rva = rva, Size = dataSize };
                    break;
                case 9: // Memory64ListStream
                    memory64Stream = new StreamInfo { Rva = rva, Size = dataSize };
                    break;
            }
        }

        return (moduleStream, memory64Stream);
    }

    private static List<ModuleInfo> ParseModules(BinaryReader reader, StreamInfo moduleStream)
    {
        reader.BaseStream.Seek(moduleStream.Rva, SeekOrigin.Begin);
        uint numModules = reader.ReadUInt32();

        var modules = new List<ModuleInfo>();

        for (int i = 0; i < numModules; i++)
        {
            var module = new ModuleInfo
            {
                BaseAddress = reader.ReadUInt64(),
                Size = reader.ReadUInt32()
            };
            reader.ReadUInt32(); // checksum
            module.Timestamp = reader.ReadUInt32();
            module.NameRva = reader.ReadUInt32();
            reader.ReadBytes(108 - 24); // Skip rest of MINIDUMP_MODULE

            modules.Add(module);
        }

        // Read module names
        foreach (var mod in modules)
        {
            reader.BaseStream.Seek(mod.NameRva, SeekOrigin.Begin);
            uint nameLen = reader.ReadUInt32();
            var nameBytes = reader.ReadBytes((int)nameLen);
            mod.Name = System.Text.Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
        }

        Console.WriteLine($"Found {modules.Count} modules in dump");
        return modules;
    }

    private static List<MemoryRange> ParseMemoryRanges(BinaryReader reader, StreamInfo memory64Stream)
    {
        reader.BaseStream.Seek(memory64Stream.Rva, SeekOrigin.Begin);
        ulong numRanges = reader.ReadUInt64();
        ulong baseRva = reader.ReadUInt64();

        var memoryRanges = new List<MemoryRange>();

        for (ulong i = 0; i < numRanges; i++)
        {
            var range = new MemoryRange
            {
                VirtualAddress = reader.ReadUInt64(),
                Size = reader.ReadUInt64()
            };
            memoryRanges.Add(range);
        }

        // Calculate file offsets
        long currentOffset = (long)baseRva;
        foreach (var r in memoryRanges)
        {
            r.FileOffset = currentOffset;
            currentOffset += (long)r.Size;
        }

        return memoryRanges;
    }

    private async Task<ModuleInfo?> ExtractModuleFromRangesAsync(
        BinaryReader reader,
        ModuleInfo mod,
        List<MemoryRange> memoryRanges)
    {
        ulong modStart = mod.BaseAddress;
        ulong modEnd = mod.BaseAddress + mod.Size;

        // Find all memory ranges that fall within this module's address space
        var moduleRanges = memoryRanges
            .Where(r => (r.VirtualAddress >= modStart && r.VirtualAddress < modEnd) ||
                        (r.VirtualAddress + r.Size > modStart && r.VirtualAddress + r.Size <= modEnd))
            .OrderBy(r => r.VirtualAddress)
            .ToList();

        if (moduleRanges.Count == 0)
        {
            Console.WriteLine($"No memory regions found for {mod.Name}");
            return null;
        }

        // Create buffer for the full module
        var moduleData = new byte[mod.Size];

        // Fill in data from each range
        long bytesFilled = 0;
        foreach (var r in moduleRanges)
        {
            long offsetInModule = (long)r.VirtualAddress - (long)mod.BaseAddress;
            if (offsetInModule < 0 || offsetInModule >= mod.Size)
                continue;

            reader.BaseStream.Seek(r.FileOffset, SeekOrigin.Begin);
            var data = reader.ReadBytes((int)r.Size);

            int copySize = (int)Math.Min(data.Length, mod.Size - offsetInModule);
            Array.Copy(data, 0, moduleData, offsetInModule, copySize);
            bytesFilled += copySize;
        }

        // Check MZ header
        if (moduleData.Length < 2 || moduleData[0] != 'M' || moduleData[1] != 'Z')
        {
            Console.WriteLine($"Module {mod.Name} doesn't have MZ header");
            return null;
        }

        // Save the module
        var safeName = BinaryUtils.SanitizeFilename(Path.GetFileName(mod.Name));
        var outputPath = Path.Combine(_outputDir, safeName);

        // Ensure unique filename
        int counter = 1;
        while (File.Exists(outputPath))
        {
            var name = Path.GetFileNameWithoutExtension(safeName);
            var ext = Path.GetExtension(safeName);
            outputPath = Path.Combine(_outputDir, $"{name}_{counter++}{ext}");
        }

        await File.WriteAllBytesAsync(outputPath, moduleData);
        Console.WriteLine($"Extracted: {safeName} ({BinaryUtils.FormatSize(moduleData.Length)})");

        return mod;
    }

    private async Task SaveModuleListAsync(string dumpPath, List<ModuleInfo> modules)
    {
        var dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        var listPath = Path.Combine(_outputDir, $"{dumpName}_modules.txt");

        var lines = modules.Select(m => 
            $"0x{m.BaseAddress:X16} - 0x{m.BaseAddress + m.Size:X16} ({BinaryUtils.FormatSize(m.Size)}) {m.Name}");

        await File.WriteAllLinesAsync(listPath, lines);
    }
}
