using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Reads the mapped section's backing file, independently of a virtualized loader path.</summary>
internal static class RuntimeModuleIdentity
{
    internal static string MappedFile(Process process, ProcessModule module)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Mapped module identity requires Windows.");
        var path = new StringBuilder(32768);
        var length = K32GetMappedFileNameW(process.Handle, module.BaseAddress, path, (uint)path.Capacity);
        if (length == 0 || length >= path.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the mapped module backing file.");
        return path.ToString();
    }

    internal static bool IsBackingFile(string mappedFile, string expectedFile)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Mapped module identity requires Windows.");
        using var file = File.OpenHandle(expectedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 2); // VOLUME_NAME_NT
        if (length == 0 || length >= path.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve the expected backing file.");
        return string.Equals(mappedFile, path.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    internal static string ReadableMappedPath(string mappedFile)
    {
        if (!mappedFile.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mapped module did not return an NT device path.");
        return @"\\?\GLOBALROOT" + mappedFile;
    }

    internal static async Task<JsonObject> ExecutableIdentityAsync(string displayPath, string mappedFile, CancellationToken token)
    {
        var backingPath = ReadableMappedPath(mappedFile);
        await using var file = File.OpenRead(backingPath);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        return new JsonObject
        {
            ["executablePath"] = displayPath,
            ["executableMappedFile"] = mappedFile,
            ["executableBackingPath"] = backingPath,
            ["executableFileSha256"] = sha256,
            ["hashScope"] = "mapped-executable-backing-file-on-disk"
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint K32GetMappedFileNameW(nint process, nint address, StringBuilder path, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
