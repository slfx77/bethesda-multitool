using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Controller for the pinned usvfs ABI. It does not hook the calling process.</summary>
internal sealed class NativeUsvfsSession : IDisposable
{
    private readonly nint _library;
    private readonly LinkDirectory _link;
    private readonly Spawn _spawn;
    private readonly GetProcesses _getProcesses;
    private readonly Disconnect _disconnect;
    private readonly Dump _dump;
    private bool _connected;

    internal NativeUsvfsSession(string binaryDirectory, string instance)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The isolation controller requires 64-bit Windows; the injected game may be 32-bit.");
        _library = NativeLibrary.Load(Path.Combine(binaryDirectory, "usvfs_x64.dll"));
        try
        {
            _link = Export<LinkDirectory>("usvfsVirtualLinkDirectoryStatic");
            _spawn = Export<Spawn>("usvfsCreateProcessHooked");
            _getProcesses = Export<GetProcesses>("usvfsGetVFSProcessList");
            _disconnect = Export<Disconnect>("usvfsDisconnectVFS");
            _dump = Export<Dump>("usvfsCreateVFSDump");
            var createParameters = Export<CreateParameters>("usvfsCreateParameters");
            var freeParameters = Export<FreeParameters>("usvfsFreeParameters");
            var setName = Export<SetName>("usvfsSetInstanceName");
            var create = Export<Create>("usvfsCreateVFS");
            var parameters = createParameters();
            if (parameters == 0) throw new IOException("usvfs could not allocate parameters.");
            try
            {
                setName(parameters, instance);
                if (!create(parameters))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw NativeFailure(error, "usvfsCreateVFS", $"instance='{instance}', binaryDirectory='{binaryDirectory}'");
                }
                _connected = true;
            }
            finally { freeParameters(parameters); }
        }
        catch { NativeLibrary.Free(_library); throw; }
    }

    internal void Map(string copy, string original)
    {
        // Overlay copied entries and redirect new creations. Existing original
        // entries absent from the copy remain visible; this is not a sandbox.
        if (!_link(copy, original, 4u | 8u)) // CREATETARGET | RECURSIVE
        {
            var error = Marshal.GetLastWin32Error();
            throw NativeFailure(error, "usvfsVirtualLinkDirectoryStatic", $"source='{copy}', destination='{original}'");
        }
    }

    internal Process Start(string executable, IEnumerable<string> arguments, string workingDirectory, bool hidden)
    {
        var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!_spawn(executable, command, 0, 0, false, hidden ? 0x08000000u : 0u, 0, workingDirectory, ref startup, out var process))
        {
            var error = Marshal.GetLastWin32Error();
            throw NativeFailure(error, "usvfsCreateProcessHooked", $"executable='{executable}', workingDirectory='{workingDirectory}', hidden={hidden}");
        }
        try { return Process.GetProcessById(checked((int)process.ProcessId)); }
        finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
    }

    internal int[] ProcessIds()
    {
        nuint count = 1024;
        var values = new uint[1024];
        if (!_getProcesses(ref count, values) || count > (nuint)values.Length)
            throw new IOException("Could not enumerate the VFS process membership.");
        return values.Take(checked((int)count)).Select(v => checked((int)v)).ToArray();
    }

    internal string MappingDump()
    {
        nuint size = 0;
        _dump(0, ref size);
        if (size > 4 * 1024 * 1024) throw new IOException("Unexpected VFS mapping dump size.");
        var buffer = Marshal.AllocHGlobal(checked((int)size + 1));
        try
        {
            if (!_dump(buffer, ref size)) throw new IOException("Could not read the VFS mapping dump.");
            return Marshal.PtrToStringUTF8(buffer, checked((int)size))?.TrimEnd('\0') ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_connected) { _disconnect(); _connected = false; NativeLibrary.Free(_library); }
    }

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    private static Win32Exception NativeFailure(int error, string operation, string context) =>
        new(error, $"{operation} failed ({context}); Win32 error {error} (0x{unchecked((uint)error):X8}): {new Win32Exception(error).Message}");

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { ++slashes; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            result.Append(character);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateParameters();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeParameters(nint parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate void SetName(nint parameters, string instance);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Create(nint parameters);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool LinkDirectory(string source, string destination, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool Spawn(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool GetProcesses(ref nuint count, [Out] uint[] processes);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Disconnect();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Dump(nint buffer, ref nuint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
