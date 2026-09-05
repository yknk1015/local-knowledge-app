using System.Text;

namespace KnowledgeApp.Data;

/// <summary>A persistent, isolated rehearsal store. This is never the Tauri production root.</summary>
public static class RehearsalDataRoot
{
    public const string DirectoryName = "jp.local.webknowledgesystem.csharp-rehearsal";
    internal const string InitializedMarker = ".knowledgeapp-rehearsal-initialized";
    internal const string InitializingMarker = ".knowledgeapp-rehearsal-initializing";
    internal static ReadOnlySpan<byte> MarkerContent => "KnowledgeApp.CSharp.Rehearsal.Root.v1\n"u8;
    private static readonly HashSet<string> ManagedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "data", "attachments", "temp", "safety-backups", "settings", "manuals", "codex-bridge", "codex-inbox", "restore-staging" };

    // No command-line, environment-variable, current-directory, or caller path override.
    public static string FixedPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) throw Problem();
            return FileSystemBoundary.ValidatePathSyntax(Path.Combine(local, DirectoryName), allowUnc: false);
        }
    }

    internal static string ValidateFixedPath(string path)
    {
        var root = FileSystemBoundary.ValidatePath(path, allowUnc: false);
        if (!string.Equals(root, FixedPath, StringComparison.OrdinalIgnoreCase)) throw Problem();
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.DriveType != DriveType.Fixed) throw Problem();
        RejectGitAncestors(root);
        return root;
    }

    internal static void ValidateInitialized(string root)
    {
        FileSystemBoundary.ValidatePath(root, allowUnc: false);
        if (!Directory.Exists(root) || File.Exists(Path.Combine(root, InitializingMarker)) ||
            Directory.Exists(Path.Combine(root, InitializingMarker))) throw Problem();
        var marker = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, InitializedMarker));
        if (!File.Exists(marker) || !FileSystemBoundary.ReadBoundedFile(marker, 128).AsSpan().SequenceEqual(MarkerContent))
            throw Problem();
        ValidateKnownEntries(root);
        var database = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "data", "knowledge.db"));
        if (!File.Exists(database) || new FileInfo(database).Length == 0) throw Problem();
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            FileSystemBoundary.ValidateManagedPath(root, database + suffix);
    }

    internal static bool Prepare(string root)
    {
        FileSystemBoundary.ValidatePath(root, allowUnc: false);
        RejectGitAncestors(root);
        if (File.Exists(root)) throw Problem();
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            ValidateInitialized(root);
            return false;
        }
        FileSystemBoundary.CreateManagedDirectory(root, root);
        // The marker is a durable intent record. Any failure leaves it intact so a
        // later startup cannot silently reinterpret partial initialization as empty.
        var pending = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, InitializingMarker));
        using var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(MarkerContent);
        file.Flush(flushToDisk: true);
        return true;
    }

    internal static void CompleteInitialization(string root)
    {
        var pending = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, InitializingMarker));
        var complete = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, InitializedMarker));
        if (!FileSystemBoundary.ReadBoundedFile(pending, 128).AsSpan().SequenceEqual(MarkerContent)) throw Problem();
        File.Move(pending, complete, overwrite: false);
        ValidateInitialized(root);
    }

    private static void ValidateKnownEntries(string root)
    {
        // A completed ownership marker is not a license to explore unrelated
        // contents. Backups exported by the user or unknown personal folders are
        // ignored; only the application's fixed managed directory paths are checked.
        foreach (var name in ManagedDirectories)
        {
            var path = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, name));
            if (File.Exists(path)) throw Problem();
        }
    }

    private static void RejectGitAncestors(string root)
    {
        for (string? current = root; current is not null; current = Path.GetDirectoryName(current))
        {
            var marker = Path.Combine(current, ".git");
            if (File.Exists(marker) || Directory.Exists(marker)) throw Problem();
        }
    }

    internal static AppProblemException Problem() => new(AppProblem.Database(
        "C#永続リハーサル用の保存先または初期化状態を確認できません。データは初期化・削除していません。"));
}
