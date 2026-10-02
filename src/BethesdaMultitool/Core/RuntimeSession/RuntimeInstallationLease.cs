using System.Diagnostics;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Serializes use of an explicitly selected working installation across CLI processes.</summary>
internal sealed class RuntimeInstallationLease : IDisposable
{
    private const string OwnerName = ".bmt-installation-owner.txt";
    private static readonly string[] EngineProcessNames = ["FalloutNV", "nvse_loader"];
    private readonly FileStream handle;
    private readonly string installation;
    private readonly string ownerRoot;

    private RuntimeInstallationLease(FileStream handle, string installation, string ownerRoot)
    {
        this.handle = handle;
        this.installation = installation;
        this.ownerRoot = ownerRoot;
    }

    internal static RuntimeInstallationLease AcquireForPreparation(string installation, string ownerRoot,
        IEnumerable<string> originals)
    {
        installation = Path.GetFullPath(installation);
        ownerRoot = Path.GetFullPath(ownerRoot);
        ValidateScope(installation, ownerRoot, originals);
        var lease = Open(installation, ownerRoot);
        try
        {
            var owner = ReadOwner(installation);
            if (owner.Length != 0 && !PreviousOwnerClosed(installation, owner))
                throw new InvalidOperationException($"Installation is reserved by {owner}. Close its controller or release its unlaunched prepared profile.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static RuntimeInstallationLease? AcquireForProfile(RuntimeRunProfile profile)
    {
        if (profile.Isolation?.ReusedInstallation is not { } installation) return null;
        ValidateProfileScope(profile);
        var lease = Open(installation, profile.Root);
        try
        {
            RequireOwner(profile);
            var saved = ReadProfile(profile.Root);
            if (saved.Activation != profile.Activation || saved.ProcessId != profile.ProcessId ||
                saved.ProcessStartedUtc != profile.ProcessStartedUtc)
                throw new InvalidOperationException("The supplied profile is not the current installation owner state.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static void ValidateProfileScope(RuntimeRunProfile profile)
    {
        var layout = profile.Isolation ?? throw new InvalidDataException("Installation layout is missing.");
        var installation = layout.ReusedInstallation ?? throw new InvalidDataException("Reusable installation is not declared.");
        if (!SamePath(installation, layout.GameCopy)) throw new InvalidDataException("Reusable installation differs from GameCopy.");
        foreach (var userData in new[] { layout.DocumentsCopy, layout.LocalDataCopy })
            if (!RuntimeIsolationService.ContainsPath(profile.Root, userData) || SamePath(profile.Root, userData) ||
                RuntimeIsolationService.ContainsPath(installation, userData) || RuntimeIsolationService.ContainsPath(userData, installation))
                throw new InvalidDataException("Private user data must remain inside its session and separate from the installation.");
        if (RuntimeIsolationService.ContainsPath(layout.DocumentsCopy, layout.LocalDataCopy) ||
            RuntimeIsolationService.ContainsPath(layout.LocalDataCopy, layout.DocumentsCopy))
            throw new InvalidDataException("Private Documents and LocalAppData copies must not overlap.");
        ValidateScope(installation, profile.Root, [layout.GameSource, layout.DocumentsSource, layout.LocalDataSource]);
    }

    internal static void RequireOwner(RuntimeRunProfile profile)
    {
        ValidateProfileScope(profile);
        if (!SamePath(ReadOwner(profile.Isolation!.ReusedInstallation!), profile.Root))
            throw new InvalidOperationException("The reusable installation belongs to another session.");
    }

    internal Task CommitAsync(CancellationToken token) => RuntimeIsolationService.AtomicWrite(
        Path.Combine(installation, OwnerName), ownerRoot, token);

    internal Task ReleaseAsync(CancellationToken token) => RuntimeIsolationService.AtomicWrite(
        Path.Combine(installation, OwnerName), string.Empty, token);

    private static RuntimeInstallationLease Open(string installation, string ownerRoot)
    {
        EnsureNoGameProcess();
        var lockPath = Path.Combine(installation, ".bmt-installation.lock");
        RuntimeIsolationService.RejectReparseAncestors(lockPath);
        var handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return new RuntimeInstallationLease(handle, installation, ownerRoot);
    }

    private static void EnsureNoGameProcess()
    {
        foreach (var name in EngineProcessNames)
        {
            var running = Process.GetProcessesByName(name);
            foreach (var process in running) process.Dispose();
            if (running.Length != 0) throw new InvalidOperationException($"An engine or loader process is already running: {name}.");
        }
    }

    internal static void ValidateScope(string installation, string ownerRoot, IEnumerable<string> originals)
    {
        if (!Directory.Exists(installation)) throw new DirectoryNotFoundException("Reuse requires an existing working installation.");
        foreach (var path in originals.Append(ownerRoot))
            if (RuntimeIsolationService.ContainsPath(path, installation) || RuntimeIsolationService.ContainsPath(installation, path))
                throw new InvalidDataException("Working installation, session and original scopes must be separate.");
        RuntimeIsolationService.RejectReparseAncestors(installation);
        RuntimeIsolationService.RejectReparseAncestors(ownerRoot);
    }

    private static string ReadOwner(string installation)
    {
        var path = Path.Combine(installation, OwnerName);
        RuntimeIsolationService.RejectReparseAncestors(path);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static RuntimeRunProfile ReadProfile(string root)
    {
        RuntimeIsolationService.RejectReparseAncestors(root);
        var path = Path.Combine(root, RuntimeRunProfile.FileName);
        RuntimeIsolationService.RejectReparseAncestors(path);
        var profile = JsonSerializer.Deserialize(File.ReadAllText(path), RuntimeJsonContext.Default.RuntimeRunProfile)
            ?? throw new InvalidDataException("Reserved profile is empty.");
        if (!SamePath(root, profile.Root)) throw new InvalidDataException("Reserved profile root differs from its location.");
        return profile;
    }

    private static bool PreviousOwnerClosed(string installation, string root)
    {
        var previous = ReadProfile(root);
        if (previous.Isolation?.ReusedInstallation is not { } declared || !SamePath(installation, declared))
            throw new InvalidDataException("Previous reservation does not identify this working installation.");
        var path = Path.Combine(root, RuntimeIsolationService.StateFileName);
        if (!File.Exists(path)) return false;
        RuntimeIsolationService.RejectReparseAncestors(path);
        var state = JsonSerializer.Deserialize(File.ReadAllText(path), RuntimeJsonContext.Default.RuntimeIsolationState);
        return (previous.Activation is "engine-exited" or "activation-failed") && state is not null &&
            (state.Status is "engine-exited-originals-unchanged" or "failed-engine-exited-originals-unchanged") &&
            state.GameProcessId == previous.ProcessId && state.ProcessStartedUtc == previous.ProcessStartedUtc &&
            state.ExecutableSha256 == previous.ExecutableSha256;
    }

    private static bool SamePath(string left, string right) => left.Length != 0 && right.Length != 0 &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    public void Dispose() => handle.Dispose();
}
