namespace KnowledgeApp.Data;

/// <summary>Fixed C# production root, isolated from the legacy runtime and rehearsal data.</summary>
public static class ProductionDataRoot
{
    public const string DirectoryName = "jp.local.webknowledgesystem.csharp";
    internal const string InitializingMarker = ".knowledgeapp-csharp-initializing";
    internal const string InitializedMarker = ".knowledgeapp-csharp-initialized";
    private static readonly string[] ManagedDirectories =
        ["data", "attachments", "temp", "safety-backups", "settings", "manuals", "codex-bridge", "codex-inbox", "restore-staging"];
    private static ReadOnlySpan<byte> MarkerBytes => "KnowledgeApp.CSharp.Production.Root.v1\n"u8;

    public static string FixedPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DirectoryName);

    internal static string ValidateFixedPath(string path)
    {
        var root = ValidateLocation(path);
        if (!string.Equals(root, FixedPath, StringComparison.OrdinalIgnoreCase) ||
            new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed) throw Problem();
        return root;
    }

    internal static string ValidateLocation(string path)
    {
        var root = FileSystemBoundary.ValidatePath(path, allowUnc: false);
        for (string? parent = root; parent is not null; parent = Path.GetDirectoryName(parent))
        {
            var git = Path.Combine(parent, ".git");
            if (File.Exists(git) || Directory.Exists(git)) throw Problem();
        }
        return root;
    }

    internal static bool Prepare(string root)
    {
        ValidateLocation(root);
        if (File.Exists(root)) throw Problem();
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            ValidateExistingLayout(root);
            return false;
        }
        FileSystemBoundary.CreateManagedDirectory(root, root);
        using var marker = new FileStream(Path.Combine(root, InitializingMarker), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        marker.Write(MarkerBytes);
        marker.Flush(flushToDisk: true);
        return true;
    }

    internal static void ValidateExistingLayout(string root, bool recovering = false)
    {
        ValidateLocation(root);
        if (!Directory.Exists(root)) throw Problem();
        if (File.Exists(Path.Combine(root, InitializingMarker)) || Directory.Exists(Path.Combine(root, InitializingMarker)))
            throw InitializationProblem();
        // A restored legacy-compatible store can have no C# ownership marker.
        // Only the C# root is opened: the old runtime's separate root is never
        // discovered, adopted or synchronized here. A valid current DB is checked
        // on an isolated copy before opening, never replaced by a new DB.
        var marker = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, InitializedMarker));
        if (Directory.Exists(marker) || File.Exists(marker) &&
            !FileSystemBoundary.ReadBoundedFile(marker, 128).AsSpan().SequenceEqual(MarkerBytes)) throw Problem();
        foreach (var name in ManagedDirectories)
            if (File.Exists(FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, name)))) throw Problem();
        var database = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "data", "knowledge.db"));
        if (!recovering && (!File.Exists(database) || new FileInfo(database).Length == 0)) throw Problem();
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            FileSystemBoundary.ValidateManagedPath(root, database + suffix);
    }

    internal static void CompleteInitialization(string root)
    {
        var pending = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, InitializingMarker));
        if (!FileSystemBoundary.ReadBoundedFile(pending, 128).AsSpan().SequenceEqual(MarkerBytes)) throw Problem();
        File.Move(pending, Path.Combine(root, InitializedMarker), overwrite: false);
        ValidateExistingLayout(root);
    }

    internal static AppProblemException Problem() => new(AppProblem.Database(
        "本番用の保存先またはデータの状態を確認できません。既存データは初期化・削除していません。"));

    private static AppProblemException InitializationProblem() => new(new AppProblem(
        "DB-001", "本番用データの初期化が途中で中断されたため、安全確認が必要です。",
        "アプリを閉じ、保存先の空き容量・権限を確認してください。データや初期化マーカーを削除・上書きせず、管理者へ相談してください。"));
}
