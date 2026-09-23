using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeApp.Data;

public sealed record StorageFolder(string Purpose, string? CustomPath, string EffectivePath, string Mode);
public sealed record SaveStorageFolderInput(string Purpose, string? Path);
public sealed record StorageProbeResult(string Path, bool Writable, long? AvailableBytes);
internal sealed record StorageConfiguration(int Version, Dictionary<string, string?> Folders);

/// <summary>Device-local paths; deliberately excluded from portable backups.</summary>
public sealed class StorageSettingsService
{
    public static readonly string[] Purposes = ["backup-export", "backup-import", "json-export", "json-import", "csv-export", "csv-import"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _root;
    private readonly Action _requireAdmin;
    private readonly object _sync = new();
    public StorageSettingsService(string root, AuthenticationService authentication) : this(root, () => authentication.RequireAdmin()) { }
    public StorageSettingsService(string root, Action requireAdmin)
    {
        _root = ValidateSettingsRoot(root);
        _requireAdmin = requireAdmin;
    }
    // Device settings in shared mode do not depend on a local FAQ database existing.
    public static string ValidateSettingsRoot(string root)
    {
        var full = FileSystemBoundary.ValidatePath(root, allowUnc: false);
        if (string.Equals(full, ProductionDataRoot.FixedPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(full, RehearsalDataRoot.FixedPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(full, SharedDataRoot.FixedPath, StringComparison.OrdinalIgnoreCase))
        {
            if (new DriveInfo(System.IO.Path.GetPathRoot(full)!).DriveType != DriveType.Fixed) throw Problem("設定はローカル保存先を使用してください。");
            return ValidateGitFreeDirectory(full);
        }
        return FileSystemBoundary.ValidateSyntheticRoot(full);
    }
    private string ConfigPath => FileSystemBoundary.ValidateManagedPath(_root, System.IO.Path.Combine(_root, "device-settings", "storage.json"));

    public IReadOnlyList<StorageFolder> GetFolders()
    {
        _requireAdmin();
        lock (_sync)
        {
            var config = Read();
            return Purposes.Select(p => Describe(p, config)).ToArray();
        }
    }

    public StorageFolder Save(SaveStorageFolderInput input)
    {
        _requireAdmin();
        RequirePurpose(input.Purpose);
        lock (_sync)
        {
            var config = Read();
            var path = input.Path is null ? null : ProbeSelected(input.Path, input.Purpose).Path;
            foreach (var (purpose, other) in config.Folders)
                if (purpose != input.Purpose && path is not null && other is not null && Overlaps(path, other))
                    throw Problem("別の用途の保存先と重複しています。用途別のフォルダーを選んでください。");
            config.Folders[input.Purpose] = path; // null is an explicit reset, distinct from legacy/unset.
            Write(config);
            return Describe(input.Purpose, config);
        }
    }

    public StorageProbeResult Check(SaveStorageFolderInput input)
    {
        _requireAdmin();
        RequirePurpose(input.Purpose);
        var path = input.Path is null ? Standard(input.Purpose) : FileSystemBoundary.ValidatePathSyntax(input.Path);
        if (input.Path is null) FileSystemBoundary.CreateManagedDirectory(_root, path);
        return ProbeSelected(path, input.Purpose);
    }

    // Called only as part of an explicit file-picker action. Startup only reads local JSON.
    public string ResolveForDialog(string purpose, string? legacyDefault = null)
    {
        _requireAdmin();
        RequirePurpose(purpose);
        lock (_sync)
        {
            var config = Read();
            if (config.Folders.TryGetValue(purpose, out var path))
            {
                if (path is not null) return Existing(path, purpose);
            }
            else if (legacyDefault is not null && purpose == "backup-export")
                return Existing(legacyDefault, purpose);
            var standard = Standard(purpose);
            ValidateGitFreeDirectory(standard);
            FileSystemBoundary.CreateManagedDirectory(_root, standard);
            return standard;
        }
    }

    private string Existing(string path, string purpose)
    {
        var valid = ProbeSelected(path, purpose).Path;
        return valid;
    }
    private string Standard(string purpose) => System.IO.Path.Combine(_root, "io", purpose);
    private StorageFolder Describe(string purpose, StorageConfiguration config)
    {
        var configured = config.Folders.TryGetValue(purpose, out var path);
        return new(purpose, path, path ?? Standard(purpose), path is not null ? "custom" : configured ? "standard" : "unset");
    }
    private StorageProbeResult ProbeSelected(string path, string purpose)
    {
        var full = FileSystemBoundary.ValidatePathSyntax(path);
        // The helper's own executable is in the extraction cache. Reject overlap
        // with this host's outer executable before delegating any filesystem probe.
        if (ApplicationDirectoryBoundary.OverlapsCurrent(full))
            throw Problem("アプリの実行フォルダーを保存先には指定できません。");
        var remote = full.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(System.IO.Path.GetPathRoot(full)!).DriveType == DriveType.Network;
        var helper = System.IO.Path.Combine(AppContext.BaseDirectory, "KnowledgeApp.PathProbe.exe");
        if (File.Exists(helper)) return StorageProbeProcess.Run(helper, _root, purpose, full);
        if (remote) throw Problem("ネットワーク確認プログラムが見つかりません。配布物全体を使用してください。");
        return Probe(ValidateScopedDestination(_root, full, purpose), !purpose.EndsWith("-import", StringComparison.Ordinal));
    }
    internal string ValidateDestination(string path, string purpose) => ValidateScopedDestination(_root, path, purpose);
    public static string ValidateScopedDestination(string dataRoot, string path, string purpose)
    {
        RequirePurpose(purpose);
        try
        {
            if (ApplicationDirectoryBoundary.OverlapsCurrent(path))
                throw Problem("アプリの実行フォルダーを保存先には指定できません。");
            var full = ValidateGitFreeDirectory(path);
            if (Overlaps(full, dataRoot) && !string.Equals(full, System.IO.Path.Combine(dataRoot, "io", purpose), StringComparison.OrdinalIgnoreCase))
                throw Problem("DB・画像・設定・提案などの管理領域を入出力先には指定できません。");
            return full;
        }
        catch (AppProblemException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw Problem("保存先を安全に確認できません。既存の通常フォルダーとアクセス権を確認してください。"); }
    }

    public static string ValidateGitFreeDirectory(string path)
    {
        var full = FileSystemBoundary.ValidatePath(path);
        for (string? current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            try
            {
                File.GetAttributes(System.IO.Path.Combine(current, ".git"));
                throw Problem("Gitリポジトリ内は保存先に指定できません。リポジトリ外のフォルダーを選択してください。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    public static StorageProbeResult Probe(string path, bool write)
    {
        try
        {
            var full = ValidateGitFreeDirectory(path);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException();
            // Inspect the selected directory itself; do not enumerate user files.
            _ = File.GetAttributes(full);
            if (write)
            {
                var probe = System.IO.Path.Combine(full, $".knowledgeapp-probe-{Guid.NewGuid():N}.tmp");
                using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    4096, FileOptions.DeleteOnClose | FileOptions.WriteThrough);
                stream.WriteByte(0x4b); stream.Flush(true); stream.Position = 0;
                if (stream.ReadByte() != 0x4b) throw new IOException();
            }
            long? space = null;
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(full)!);
            if (drive.DriveType == DriveType.Fixed) space = drive.AvailableFreeSpace;
            return new(full, write, space);
        }
        catch (AppProblemException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw Problem("保存先への接続または読み書き確認に失敗しました。ネットワーク、権限、空き容量を確認してください。"); }
    }

    private StorageConfiguration Read()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new(1, new(StringComparer.Ordinal));
            var config = JsonSerializer.Deserialize<StorageConfiguration>(FileSystemBoundary.ReadBoundedFile(ConfigPath, 65536), JsonOptions)
                ?? throw new JsonException();
            if (config.Version != 1 || config.Folders is null || config.Folders.Keys.Any(p => !Purposes.Contains(p))) throw new JsonException();
            foreach (var value in config.Folders.Values)
                if (value is not null) FileSystemBoundary.ValidatePathSyntax(value); // Never probe remote paths on startup.
            return config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw Problem("端末の保存先設定を読み込めません。設定ファイルを削除せず管理者へ相談してください。"); }
    }
    private void Write(StorageConfiguration config)
    {
        var directory = System.IO.Path.GetDirectoryName(ConfigPath)!;
        FileSystemBoundary.CreateManagedDirectory(_root, directory);
        var temporary = FileSystemBoundary.ValidateManagedPath(_root, System.IO.Path.Combine(directory, $"storage-{Guid.NewGuid():N}.tmp"));
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions)); file.Flush(true);
            }
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw Problem("保存先設定を確定できませんでした。元の設定を保ちました。空き容量と権限を確認してください。"); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void RequirePurpose(string purpose)
    {
        if (!Purposes.Contains(purpose, StringComparer.Ordinal)) throw Problem("保存先の用途が正しくありません。");
    }
    internal static bool Overlaps(string a, string b) => Contains(a, b) || Contains(b, a);
    private static bool Contains(string parent, string child) => string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static AppProblemException Problem(string message) => new(new AppProblem("PATH-001", message, "保存先の設定を確認して、もう一度お試しください。"));
}
