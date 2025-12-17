using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xbox360MemoryCarver.Utils;

namespace Xbox360MemoryCarver.Minidump;

/// <summary>
/// Comprehensive dump metadata from all available streams.
/// </summary>
public class DumpInfo
{
    // Header info
    public uint Version { get; set; }
    public uint NumberOfStreams { get; set; }
    public DateTime? TimestampUtc { get; set; }
    public uint Flags { get; set; }

    // System info (stream type 7)
    public SystemInfo? System { get; set; }

    // Exception info (stream type 6)
    public ExceptionInfo? Exception { get; set; }

    // Process info (stream type 15)
    public ProcessInfo? Process { get; set; }

    // Thread info (stream type 3)
    public List<ThreadInfo> Threads { get; set; } = [];

    // Memory ranges (stream type 9)
    public List<MemoryRange> MemoryRanges { get; set; } = [];

    // Modules (stream type 4)
    public List<ModuleInfo> Modules { get; set; } = [];

    // Comments (stream types 10, 11)
    public string? CommentA { get; set; }
    public string? CommentW { get; set; }

    // Summary stats
    public long TotalMemorySize { get; set; }
    public int MemoryRegionCount { get; set; }
}

/// <summary>
/// System information from MINIDUMP_SYSTEM_INFO (stream type 7).
/// </summary>
public class SystemInfo
{
    public ushort ProcessorArchitecture { get; set; }
    public string ProcessorArchitectureName => ProcessorArchitecture switch
    {
        0 => "x86 (Intel)",
        3 => "PowerPC (Xbox 360)",  // Xbox 360 uses PowerPC
        5 => "ARM",
        6 => "Intel Itanium",
        9 => "x64 (AMD64)",
        12 => "ARM64",
        0xFFFF => "Unknown",
        _ => $"Other (0x{ProcessorArchitecture:X})"
    };
    public ushort ProcessorLevel { get; set; }
    public ushort ProcessorRevision { get; set; }
    public byte NumberOfProcessors { get; set; }
    public byte ProductType { get; set; }
    public uint MajorVersion { get; set; }
    public uint MinorVersion { get; set; }
    public uint BuildNumber { get; set; }
    public uint PlatformId { get; set; }
    public string? ServicePack { get; set; }
    public string OsVersionString => $"{MajorVersion}.{MinorVersion}.{BuildNumber}";
}

/// <summary>
/// Exception information from MINIDUMP_EXCEPTION_STREAM (stream type 6).
/// </summary>
public class ExceptionInfo
{
    public uint ThreadId { get; set; }
    public uint ExceptionCode { get; set; }
    public string ExceptionCodeName => ExceptionCode switch
    {
        0xC0000005 => "EXCEPTION_ACCESS_VIOLATION",
        0xC000001D => "EXCEPTION_ILLEGAL_INSTRUCTION",
        0xC0000094 => "EXCEPTION_INT_DIVIDE_BY_ZERO",
        0xC0000095 => "EXCEPTION_INT_OVERFLOW",
        0xC00000FD => "EXCEPTION_STACK_OVERFLOW",
        0x80000003 => "EXCEPTION_BREAKPOINT",
        0x80000004 => "EXCEPTION_SINGLE_STEP",
        0xC0000006 => "EXCEPTION_IN_PAGE_ERROR",
        0xC000008C => "EXCEPTION_ARRAY_BOUNDS_EXCEEDED",
        0xC000008D => "EXCEPTION_FLT_DENORMAL_OPERAND",
        0xC000008E => "EXCEPTION_FLT_DIVIDE_BY_ZERO",
        0xC0000090 => "EXCEPTION_FLT_INVALID_OPERATION",
        0xC0000091 => "EXCEPTION_FLT_OVERFLOW",
        0xC0000092 => "EXCEPTION_FLT_STACK_CHECK",
        0xC0000093 => "EXCEPTION_FLT_UNDERFLOW",
        0xC0000026 => "EXCEPTION_INVALID_DISPOSITION",
        0xC0000025 => "EXCEPTION_NONCONTINUABLE_EXCEPTION",
        0xC0000096 => "EXCEPTION_PRIV_INSTRUCTION",
        _ => $"0x{ExceptionCode:X8}"
    };
    public uint ExceptionFlags { get; set; }
    public bool IsNonContinuable => ExceptionFlags != 0;
    public ulong ExceptionAddress { get; set; }
    public uint NumberParameters { get; set; }
    public ulong[]? ExceptionInformation { get; set; }
}

/// <summary>
/// Process information from MINIDUMP_MISC_INFO (stream type 15).
/// </summary>
public class ProcessInfo
{
    public uint ProcessId { get; set; }
    public DateTime? CreateTime { get; set; }
    public uint UserTime { get; set; }  // seconds
    public uint KernelTime { get; set; }  // seconds
    public TimeSpan TotalCpuTime => TimeSpan.FromSeconds(UserTime + KernelTime);
}

/// <summary>
/// Thread information from MINIDUMP_THREAD (stream type 3).
/// </summary>
public class ThreadInfo
{
    public uint ThreadId { get; set; }
    public uint SuspendCount { get; set; }
    public uint PriorityClass { get; set; }
    public uint Priority { get; set; }
    public ulong Teb { get; set; }  // Thread Environment Block
    public ulong StackStart { get; set; }
    public uint StackSize { get; set; }
}

/// <summary>
/// Module information from minidump.
/// </summary>
public class ModuleInfo
{
    public ulong BaseAddress { get; set; }
    public uint Size { get; set; }
    [JsonIgnore]
    public uint NameRva { get; set; }
    public uint Timestamp { get; set; }
    public DateTime? TimestampUtc => Timestamp > 0
        ? DateTimeOffset.FromUnixTimeSeconds(Timestamp).UtcDateTime
        : null;
    public string Name { get; set; } = "";
}

/// <summary>
/// Memory range information from minidump.
/// </summary>
public class MemoryRange
{
    public ulong VirtualAddress { get; set; }
    public ulong Size { get; set; }
    [JsonIgnore]
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
/// Note: Microsoft.Diagnostics.Runtime (ClrMD) doesn't support Xbox 360's PowerPC architecture,
/// so we use manual parsing following the documented MINIDUMP_* structures from DbgHelp.
/// </summary>
public class MinidumpExtractor
{
    private readonly string _outputDir;

    // Stream type constants from MINIDUMP_STREAM_TYPE
    private const uint ThreadListStream = 3;
    private const uint ModuleListStream = 4;
    private const uint ExceptionStream = 6;
    private const uint SystemInfoStream = 7;
    private const uint Memory64ListStream = 9;
    private const uint CommentStreamA = 10;
    private const uint CommentStreamW = 11;
    private const uint MiscInfoStream = 15;

    public MinidumpExtractor(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(_outputDir);
    }

    /// <summary>
    /// Parse all available metadata from a minidump file.
    /// </summary>
    public async Task<DumpInfo?> ParseDumpInfoAsync(string dumpPath)
    {
        try
        {
            await using var fs = new FileStream(dumpPath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            var dumpInfo = new DumpInfo();

            // Parse header
            var (version, numStreams, streamDirRva, timestamp, flags) = ParseFullHeader(reader);
            dumpInfo.Version = version;
            dumpInfo.NumberOfStreams = numStreams;
            dumpInfo.TimestampUtc = timestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime
                : null;
            dumpInfo.Flags = flags;

            // Find all streams
            var streams = FindAllStreams(reader, streamDirRva, numStreams);

            // Parse each stream type
            if (streams.TryGetValue(SystemInfoStream, out var sysInfo))
                dumpInfo.System = ParseSystemInfo(reader, sysInfo);

            if (streams.TryGetValue(ExceptionStream, out var excInfo))
                dumpInfo.Exception = ParseExceptionInfo(reader, excInfo);

            if (streams.TryGetValue(MiscInfoStream, out var miscInfo))
                dumpInfo.Process = ParseProcessInfo(reader, miscInfo);

            if (streams.TryGetValue(ThreadListStream, out var threadInfo))
                dumpInfo.Threads = ParseThreadList(reader, threadInfo);

            if (streams.TryGetValue(ModuleListStream, out var modInfo))
                dumpInfo.Modules = ParseModules(reader, modInfo);

            if (streams.TryGetValue(Memory64ListStream, out var memInfo))
            {
                dumpInfo.MemoryRanges = ParseMemoryRanges(reader, memInfo);
                dumpInfo.MemoryRegionCount = dumpInfo.MemoryRanges.Count;
                dumpInfo.TotalMemorySize = dumpInfo.MemoryRanges.Sum(r => (long)r.Size);
            }

            if (streams.TryGetValue(CommentStreamA, out var commentA))
                dumpInfo.CommentA = ParseCommentA(reader, commentA);

            if (streams.TryGetValue(CommentStreamW, out var commentW))
                dumpInfo.CommentW = ParseCommentW(reader, commentW);

            return dumpInfo;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing dump info: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract all loaded modules from a minidump file.
    /// Parses MINIDUMP_MODULE_LIST (stream type 4) and MINIDUMP_MEMORY64_LIST (stream type 9).
    /// </summary>
    public async Task<(List<ModuleInfo> Modules, DumpInfo? Info)> ExtractModulesAsync(string dumpPath)
    {
        var extractedModules = new List<ModuleInfo>();
        DumpInfo? dumpInfo = null;

        try
        {
            await using var fs = new FileStream(dumpPath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // Parse full dump info
            fs.Position = 0;
            dumpInfo = await ParseDumpInfoAsync(dumpPath);

            // Reset and parse for extraction
            fs.Position = 0;
            var (_, numStreams, streamDirRva, _, _) = ParseFullHeader(reader);
            var streams = FindAllStreams(reader, streamDirRva, numStreams);

            if (!streams.TryGetValue(ModuleListStream, out var moduleStream))
            {
                Console.WriteLine("No ModuleListStream found");
                return (extractedModules, dumpInfo);
            }

            var modules = ParseModules(reader, moduleStream);
            var memoryRanges = streams.TryGetValue(Memory64ListStream, out var mem64Stream)
                ? ParseMemoryRanges(reader, mem64Stream)
                : [];

            // Extract each module
            foreach (var mod in modules)
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

            // Save dump info and module list
            await SaveDumpInfoAsync(dumpPath, dumpInfo, modules);

            Console.WriteLine($"Successfully extracted {extractedModules.Count} modules");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing minidump {dumpPath}: {ex.Message}");
        }

        return (extractedModules, dumpInfo);
    }

    /// <summary>
    /// Parse full MINIDUMP_HEADER structure.
    /// </summary>
    private static (uint Version, uint NumStreams, uint StreamDirRva, uint Timestamp, uint Flags) ParseFullHeader(BinaryReader reader)
    {
        var magic = reader.ReadBytes(4);
        if (!magic.SequenceEqual("MDMP"u8.ToArray()))
        {
            throw new InvalidDataException($"Not a valid minidump file: {Encoding.ASCII.GetString(magic)}");
        }

        uint version = reader.ReadUInt32();
        uint numStreams = reader.ReadUInt32();
        uint streamDirRva = reader.ReadUInt32();
        reader.ReadUInt32(); // CheckSum
        uint timestamp = reader.ReadUInt32();
        uint flags = reader.ReadUInt32();

        return (version, numStreams, streamDirRva, timestamp, flags);
    }

    /// <summary>
    /// Parse MINIDUMP_HEADER structure (legacy compatibility).
    /// </summary>
    private (uint NumStreams, uint StreamDirRva) ParseHeader(BinaryReader reader)
    {
        var (_, numStreams, streamDirRva, _, _) = ParseFullHeader(reader);
        return (numStreams, streamDirRva);
    }

    /// <summary>
    /// Find all streams from MINIDUMP_DIRECTORY.
    /// </summary>
    private Dictionary<uint, StreamInfo> FindAllStreams(BinaryReader reader, uint streamDirRva, uint numStreams)
    {
        reader.BaseStream.Seek(streamDirRva, SeekOrigin.Begin);
        var streams = new Dictionary<uint, StreamInfo>();

        for (int i = 0; i < numStreams; i++)
        {
            uint streamType = reader.ReadUInt32();
            uint dataSize = reader.ReadUInt32();
            uint rva = reader.ReadUInt32();

            if (dataSize > 0 && rva > 0)
            {
                streams[streamType] = new StreamInfo { Rva = rva, Size = dataSize };
            }
        }

        return streams;
    }

    /// <summary>
    /// Parse MINIDUMP_SYSTEM_INFO (stream type 7).
    /// </summary>
    private SystemInfo ParseSystemInfo(BinaryReader reader, StreamInfo stream)
    {
        reader.BaseStream.Seek(stream.Rva, SeekOrigin.Begin);

        var info = new SystemInfo
        {
            ProcessorArchitecture = reader.ReadUInt16(),
            ProcessorLevel = reader.ReadUInt16(),
            ProcessorRevision = reader.ReadUInt16()
        };

        // Union: Reserved0 or NumberOfProcessors + ProductType
        var reserved = reader.ReadUInt16();
        info.NumberOfProcessors = (byte)(reserved & 0xFF);
        info.ProductType = (byte)((reserved >> 8) & 0xFF);

        info.MajorVersion = reader.ReadUInt32();
        info.MinorVersion = reader.ReadUInt32();
        info.BuildNumber = reader.ReadUInt32();
        info.PlatformId = reader.ReadUInt32();

        // CSDVersionRva - points to service pack string
        uint csdRva = reader.ReadUInt32();
        if (csdRva > 0)
        {
            long savedPos = reader.BaseStream.Position;
            reader.BaseStream.Seek(csdRva, SeekOrigin.Begin);
            uint strLen = reader.ReadUInt32();
            if (strLen > 0 && strLen < 1024)
            {
                var strBytes = reader.ReadBytes((int)strLen);
                info.ServicePack = Encoding.Unicode.GetString(strBytes).TrimEnd('\0');
            }
            reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);
        }

        return info;
    }

    /// <summary>
    /// Parse MINIDUMP_EXCEPTION_STREAM (stream type 6).
    /// </summary>
    private ExceptionInfo ParseExceptionInfo(BinaryReader reader, StreamInfo stream)
    {
        reader.BaseStream.Seek(stream.Rva, SeekOrigin.Begin);

        var info = new ExceptionInfo
        {
            ThreadId = reader.ReadUInt32()
        };
        reader.ReadUInt32(); // __alignment

        // MINIDUMP_EXCEPTION structure
        info.ExceptionCode = reader.ReadUInt32();
        info.ExceptionFlags = reader.ReadUInt32();
        reader.ReadUInt64(); // ExceptionRecord (pointer to nested)
        info.ExceptionAddress = reader.ReadUInt64();
        info.NumberParameters = reader.ReadUInt32();
        reader.ReadUInt32(); // __unusedAlignment

        // Exception information array (up to 15 parameters)
        if (info.NumberParameters > 0 && info.NumberParameters <= 15)
        {
            info.ExceptionInformation = new ulong[info.NumberParameters];
            for (int i = 0; i < info.NumberParameters; i++)
            {
                info.ExceptionInformation[i] = reader.ReadUInt64();
            }
        }

        return info;
    }

    /// <summary>
    /// Parse MINIDUMP_MISC_INFO (stream type 15).
    /// </summary>
    private ProcessInfo ParseProcessInfo(BinaryReader reader, StreamInfo stream)
    {
        reader.BaseStream.Seek(stream.Rva, SeekOrigin.Begin);

        uint sizeOfInfo = reader.ReadUInt32();
        uint flags = reader.ReadUInt32();

        var info = new ProcessInfo();

        if ((flags & 0x01) != 0) // MINIDUMP_MISC1_PROCESS_ID
        {
            info.ProcessId = reader.ReadUInt32();
        }
        else
        {
            reader.ReadUInt32();
        }

        if ((flags & 0x02) != 0) // MINIDUMP_MISC1_PROCESS_TIMES
        {
            uint createTime = reader.ReadUInt32();
            info.CreateTime = createTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(createTime).UtcDateTime
                : null;
            info.UserTime = reader.ReadUInt32();
            info.KernelTime = reader.ReadUInt32();
        }

        return info;
    }

    /// <summary>
    /// Parse MINIDUMP_THREAD_LIST (stream type 3).
    /// </summary>
    private List<ThreadInfo> ParseThreadList(BinaryReader reader, StreamInfo stream)
    {
        reader.BaseStream.Seek(stream.Rva, SeekOrigin.Begin);
        uint numThreads = reader.ReadUInt32();

        var threads = new List<ThreadInfo>();

        for (int i = 0; i < numThreads; i++)
        {
            var thread = new ThreadInfo
            {
                ThreadId = reader.ReadUInt32(),
                SuspendCount = reader.ReadUInt32(),
                PriorityClass = reader.ReadUInt32(),
                Priority = reader.ReadUInt32(),
                Teb = reader.ReadUInt64()
            };

            // Stack - MINIDUMP_MEMORY_DESCRIPTOR
            thread.StackStart = reader.ReadUInt64();
            uint stackMemSize = reader.ReadUInt32();
            uint stackMemRva = reader.ReadUInt32();
            thread.StackSize = stackMemSize;

            // ThreadContext - MINIDUMP_LOCATION_DESCRIPTOR
            reader.ReadUInt32(); // DataSize
            reader.ReadUInt32(); // Rva

            threads.Add(thread);
        }

        return threads;
    }

    /// <summary>
    /// Parse ANSI comment (stream type 10).
    /// </summary>
    private string? ParseCommentA(BinaryReader reader, StreamInfo stream)
    {
        reader.BaseStream.Seek(stream.Rva, SeekOrigin.Begin);
        var bytes = reader.ReadBytes((int)stream.Size);
        return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    /// <summary>
    /// Parse Unicode comment (stream type 11).
    /// </summary>
    private string? ParseCommentW(BinaryReader reader, StreamInfo stream)
    {
        reader.BaseStream.Seek(stream.Rva, SeekOrigin.Begin);
        var bytes = reader.ReadBytes((int)stream.Size);
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    /// <summary>
    /// Find ModuleListStream (type 4) and Memory64ListStream (type 9) from MINIDUMP_DIRECTORY.
    /// </summary>
    private (StreamInfo? ModuleStream, StreamInfo? Memory64Stream) FindStreams(
        BinaryReader reader, uint streamDirRva, uint numStreams)
    {
        var streams = FindAllStreams(reader, streamDirRva, numStreams);
        streams.TryGetValue(ModuleListStream, out var moduleStream);
        streams.TryGetValue(Memory64ListStream, out var memory64Stream);
        return (moduleStream, memory64Stream);
    }

    /// <summary>
    /// Parse MINIDUMP_MODULE_LIST structure to get all modules.
    /// </summary>
    private static List<ModuleInfo> ParseModules(BinaryReader reader, StreamInfo moduleStream)
    {
        reader.BaseStream.Seek(moduleStream.Rva, SeekOrigin.Begin);
        uint numModules = reader.ReadUInt32();

        var modules = new List<ModuleInfo>();

        // Each MINIDUMP_MODULE is 108 bytes
        for (int i = 0; i < numModules; i++)
        {
            var module = new ModuleInfo
            {
                BaseAddress = reader.ReadUInt64(),  // BaseOfImage (8 bytes)
                Size = reader.ReadUInt32()          // SizeOfImage (4 bytes)
            };
            reader.ReadUInt32(); // CheckSum (4 bytes)
            module.Timestamp = reader.ReadUInt32(); // TimeDateStamp (4 bytes)
            module.NameRva = reader.ReadUInt32();   // ModuleNameRva (4 bytes)
            reader.ReadBytes(108 - 24); // Skip rest: VS_FIXEDFILEINFO, CvRecord, MiscRecord, Reserved

            modules.Add(module);
        }

        // Read module names from MINIDUMP_STRING at each ModuleNameRva
        foreach (var mod in modules)
        {
            reader.BaseStream.Seek(mod.NameRva, SeekOrigin.Begin);
            uint nameLen = reader.ReadUInt32(); // Length in bytes (Unicode)
            var nameBytes = reader.ReadBytes((int)nameLen);
            mod.Name = System.Text.Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
        }

        Console.WriteLine($"Found {modules.Count} modules in dump");
        return modules;
    }

    /// <summary>
    /// Parse MINIDUMP_MEMORY64_LIST to get memory ranges and their file offsets.
    /// </summary>
    private static List<MemoryRange> ParseMemoryRanges(BinaryReader reader, StreamInfo memory64Stream)
    {
        reader.BaseStream.Seek(memory64Stream.Rva, SeekOrigin.Begin);
        ulong numRanges = reader.ReadUInt64();  // NumberOfMemoryRanges
        ulong baseRva = reader.ReadUInt64();    // BaseRva - where memory data starts

        var memoryRanges = new List<MemoryRange>();

        // Read MINIDUMP_MEMORY_DESCRIPTOR64 array
        for (ulong i = 0; i < numRanges; i++)
        {
            var range = new MemoryRange
            {
                VirtualAddress = reader.ReadUInt64(),  // StartOfMemoryRange
                Size = reader.ReadUInt64()             // DataSize
            };
            memoryRanges.Add(range);
        }

        // Calculate file offsets (memory data is stored contiguously after descriptors)
        long currentOffset = (long)baseRva;
        foreach (var r in memoryRanges)
        {
            r.FileOffset = currentOffset;
            currentOffset += (long)r.Size;
        }

        return memoryRanges;
    }

    /// <summary>
    /// Extract a module by reading from the appropriate memory ranges.
    /// Xbox 360 dumps may have fragmented memory, so we reconstruct from multiple ranges.
    /// </summary>
    private async Task<ModuleInfo?> ExtractModuleFromRangesAsync(
        BinaryReader reader,
        ModuleInfo mod,
        List<MemoryRange> memoryRanges)
    {
        ulong modStart = mod.BaseAddress;
        ulong modEnd = mod.BaseAddress + mod.Size;

        // Find all memory ranges that overlap with this module's address space
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

        // Check for PE MZ header
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

    private async Task SaveDumpInfoAsync(string dumpPath, DumpInfo? dumpInfo, List<ModuleInfo> modules)
    {
        var dumpName = Path.GetFileNameWithoutExtension(dumpPath);

        // Save text report
        var reportPath = Path.Combine(_outputDir, $"{dumpName}_dump_info.txt");
        var sb = new StringBuilder();

        sb.AppendLine($"=== Minidump Analysis Report ===");
        sb.AppendLine($"File: {Path.GetFileName(dumpPath)}");
        sb.AppendLine();

        if (dumpInfo != null)
        {
            sb.AppendLine("--- Header Information ---");
            sb.AppendLine($"Version: 0x{dumpInfo.Version:X8}");
            sb.AppendLine($"Streams: {dumpInfo.NumberOfStreams}");
            if (dumpInfo.TimestampUtc.HasValue)
                sb.AppendLine($"Timestamp: {dumpInfo.TimestampUtc.Value:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Flags: 0x{dumpInfo.Flags:X8}");
            sb.AppendLine();

            if (dumpInfo.System != null)
            {
                sb.AppendLine("--- System Information ---");
                sb.AppendLine($"Architecture: {dumpInfo.System.ProcessorArchitectureName}");
                sb.AppendLine($"Processors: {dumpInfo.System.NumberOfProcessors}");
                sb.AppendLine($"Processor Level: {dumpInfo.System.ProcessorLevel}");
                sb.AppendLine($"Processor Revision: 0x{dumpInfo.System.ProcessorRevision:X4}");
                sb.AppendLine($"OS Version: {dumpInfo.System.OsVersionString}");
                if (!string.IsNullOrEmpty(dumpInfo.System.ServicePack))
                    sb.AppendLine($"Service Pack: {dumpInfo.System.ServicePack}");
                sb.AppendLine();
            }

            if (dumpInfo.Exception != null)
            {
                sb.AppendLine("--- Exception Information ---");
                sb.AppendLine($"Exception Code: {dumpInfo.Exception.ExceptionCodeName}");
                sb.AppendLine($"Exception Address: 0x{dumpInfo.Exception.ExceptionAddress:X16}");
                sb.AppendLine($"Faulting Thread ID: {dumpInfo.Exception.ThreadId}");
                sb.AppendLine($"Non-Continuable: {dumpInfo.Exception.IsNonContinuable}");
                if (dumpInfo.Exception.ExceptionInformation?.Length > 0)
                {
                    sb.AppendLine($"Parameters: {string.Join(", ", dumpInfo.Exception.ExceptionInformation.Select(p => $"0x{p:X}"))}");
                }
                sb.AppendLine();
            }

            if (dumpInfo.Process != null)
            {
                sb.AppendLine("--- Process Information ---");
                sb.AppendLine($"Process ID: {dumpInfo.Process.ProcessId}");
                if (dumpInfo.Process.CreateTime.HasValue)
                    sb.AppendLine($"Create Time: {dumpInfo.Process.CreateTime.Value:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"User Time: {dumpInfo.Process.UserTime} seconds");
                sb.AppendLine($"Kernel Time: {dumpInfo.Process.KernelTime} seconds");
                sb.AppendLine($"Total CPU Time: {dumpInfo.Process.TotalCpuTime}");
                sb.AppendLine();
            }

            if (dumpInfo.Threads.Count > 0)
            {
                sb.AppendLine($"--- Threads ({dumpInfo.Threads.Count}) ---");
                foreach (var thread in dumpInfo.Threads)
                {
                    sb.AppendLine($"  Thread {thread.ThreadId}: Priority={thread.Priority}, Stack=0x{thread.StackStart:X} ({BinaryUtils.FormatSize(thread.StackSize)})");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"--- Memory Regions ({dumpInfo.MemoryRegionCount}) ---");
            sb.AppendLine($"Total Memory: {BinaryUtils.FormatSize(dumpInfo.TotalMemorySize)}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(dumpInfo.CommentA))
            {
                sb.AppendLine("--- Comment (ANSI) ---");
                sb.AppendLine(dumpInfo.CommentA);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(dumpInfo.CommentW))
            {
                sb.AppendLine("--- Comment (Unicode) ---");
                sb.AppendLine(dumpInfo.CommentW);
                sb.AppendLine();
            }
        }

        sb.AppendLine($"--- Modules ({modules.Count}) ---");
        foreach (var m in modules)
        {
            sb.AppendLine($"0x{m.BaseAddress:X16} - 0x{m.BaseAddress + m.Size:X16} ({BinaryUtils.FormatSize(m.Size)}) {m.Name}");
            if (m.TimestampUtc.HasValue)
                sb.AppendLine($"    Built: {m.TimestampUtc.Value:yyyy-MM-dd HH:mm:ss} UTC");
        }

        await File.WriteAllTextAsync(reportPath, sb.ToString());

        // Save JSON for programmatic access
        if (dumpInfo != null)
        {
            var jsonPath = Path.Combine(_outputDir, $"{dumpName}_dump_info.json");
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(dumpInfo, options));
        }
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
