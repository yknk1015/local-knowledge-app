using System.IO;

namespace KnowledgeApp.CSharp;

/// <summary>Coordinates installed-app lifetime with an explicitly scoped installer.</summary>
public sealed class InstallationActivityGuard : IDisposable
{
    public const string FileName = ".knowledgeapp-installation-lock";
    private readonly FileStream? _lease;

    private InstallationActivityGuard(FileStream? lease) => _lease = lease;

    // A portable package has no installer marker. Installed packages retain this
    // zero-byte lock beside the executable; the installer opens it with share=0.
    public static InstallationActivityGuard AcquireForCurrentExecutable() =>
        AcquireAt(AppContext.BaseDirectory);

    internal static InstallationActivityGuard AcquireAt(string executableDirectory)
    {
        var directory = Path.GetFullPath(executableDirectory);
        if (!Directory.Exists(directory)) throw new IOException("The installation directory is unavailable.");
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The installation directory must not traverse a reparse point.");
        }
        var path = Path.Combine(directory, FileName);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return new InstallationActivityGuard(null); }
        // An inaccessible existing marker is not equivalent to a portable
        // installation. Propagate access failures instead of skipping the lock.
        if ((attributes & FileAttributes.Directory) != 0) throw new IOException("The installer activity marker is not a file.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The installer activity marker must not be a reparse point.");
        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (lease.Length != 0)
        {
            lease.Dispose();
            throw new IOException("The installer activity marker must be empty.");
        }
        return new InstallationActivityGuard(lease);
    }

    public void Dispose() => _lease?.Dispose();
}
