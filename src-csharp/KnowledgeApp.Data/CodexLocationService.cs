using System.Text.Json;

namespace KnowledgeApp.Data;

public sealed record CodexLocation(int Version, string EnvironmentId, long Generation, string Root);
public sealed record ChangeCodexLocationInput(string? Path, bool InvalidateOutstandingDelegations);

public sealed class CodexLocationService(KnowledgeDatabase database, AuthenticationService authentication)
{
    private string DataRoot => Directory.GetParent(Path.GetDirectoryName(database.OpenInfo.DatabasePath)!)!.FullName;
    private static string ConfigPath(string root) => FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "device-settings", "codex-local-location.json"));
    public static void ActivateLocal(string root)
    {
        var location = Read(root);
        var directory = Path.Combine(root, "device-settings");
        FileSystemBoundary.CreateManagedDirectory(root, directory);
        using var lease = new FileStream(Path.Combine(directory, "codex-location.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var active = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(directory, "codex-location.json"));
        if (location.Generation == 0)
        {
            if (File.Exists(active)) File.Move(active, active + $".{Guid.NewGuid():N}.inactive");
        }
        else PublishPluginLocation(root, location);
    }
    public static void PublishPluginLocation(string root, CodexLocation location)
    {
        var directory = Path.Combine(root, "device-settings");
        FileSystemBoundary.CreateManagedDirectory(root, directory);
        var active = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(directory, "codex-location.json"));
        var partial = FileSystemBoundary.ValidateManagedPath(root, active + $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(location, CodexJson.Options)); output.Flush(true); }
            File.Move(partial, active, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public static string ResolveExchangeRoot(string root) => Read(FileSystemBoundary.ValidateManagedDataRoot(root)).Root;

    internal static CodexLocation Read(string root)
    {
        var path = ConfigPath(root);
        if (!File.Exists(path)) return new(1, string.Empty, 0, root);
        try
        {
            var config = JsonSerializer.Deserialize<CodexLocation>(FileSystemBoundary.ReadBoundedFile(path, 65536), CodexJson.Options)
                ?? throw new JsonException();
            if (config.Version != 1 || !Guid.TryParseExact(config.EnvironmentId, "D", out _) || config.Generation < 1)
                throw new JsonException();
            var full = StorageSettingsService.ValidateGitFreeDirectory(config.Root);
            // Moving a portable executable can make a previously accepted exchange
            // overlap its current location. Reject before reading its owner or contents.
            if (ApplicationDirectoryBoundary.OverlapsCurrent(full))
                throw Problem("Codex連携先がアプリの実行フォルダーと重複しています。アプリを連携先の外へ移動してから起動してください。");
            // Codex exchange is device-local. Network service users exchange explicit payloads through the API.
            if (new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed) throw new IOException();
            var owner = JsonSerializer.Deserialize<CodexLocation>(FileSystemBoundary.ReadBoundedFile(Path.Combine(full, ".knowledgeapp-codex-owner.json"), 65536), CodexJson.Options);
            if (owner != config) throw new IOException();
            ExchangePermissions.Validate(full);
            return config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw Problem("Codex連携先の設定または環境識別情報を確認できません。別の場所へ自動接続せず停止しました。"); }
    }
    public CodexLocation Get()
    {
        authentication.RequireEditor();
        return Read(DataRoot);
    }
    public CodexLocation Change(ChangeCodexLocationInput input) => authentication.ExecuteForAdminSession(actor => database.InOperation(() =>
    {
        authentication.RequireAdmin();
        var previous = Read(DataRoot);
        if (database.ListPendingCodexProposals().Count != 0)
            throw Problem("確認待ちの提案があります。承認または却下を完了してから保存先を変更してください。");
        var oldInbox = Path.Combine(previous.Root, "codex-inbox");
        if (Directory.Exists(oldInbox) && Directory.EnumerateFileSystemEntries(oldInbox).Any())
            throw Problem("提案箱に未処理ファイルがあります。提案画面で内容を確認・整理してから変更してください。");
        var oldBridge = Path.Combine(previous.Root, "codex-bridge");
        if (Directory.Exists(oldBridge) && !input.InvalidateOutstandingDelegations &&
            new[] { "delegations", "mail-delegations" }.Any(name => Directory.Exists(Path.Combine(oldBridge, name)) && Directory.EnumerateFileSystemEntries(Path.Combine(oldBridge, name)).Any()))
            throw Problem("発行済みの委譲があります。進行中の依頼を完了するか、委譲の無効化を確認してから変更してください。");
        var id = previous.EnvironmentId.Length == 0 ? Guid.NewGuid().ToString("D") : previous.EnvironmentId;
        var generation = checked(previous.Generation + 1);
        var parent = input.Path ?? Path.Combine(DataRoot, "codex-locations");
        if (ApplicationDirectoryBoundary.OverlapsCurrent(parent))
            throw Problem("Codex連携先には、Git・アプリ・管理データ領域の外にあるローカルフォルダーを選択してください。");
        parent = StorageSettingsService.ValidateGitFreeDirectory(parent);
        if (new DriveInfo(Path.GetPathRoot(parent)!).DriveType != DriveType.Fixed ||
            (StorageSettingsService.Overlaps(parent, DataRoot) && input.Path is not null))
            throw Problem("Codex連携先には、Git・アプリ・管理データ領域の外にあるローカルフォルダーを選択してください。");
        if (input.Path is null) FileSystemBoundary.CreateManagedDirectory(DataRoot, parent);
        if (!Directory.Exists(parent)) throw Problem("指定した親フォルダーが見つかりません。");
        var target = Path.Combine(parent, $"KnowledgeApp-Codex-{id}-{generation}");
        if (Directory.Exists(target) || File.Exists(target)) throw Problem("移設先が既に存在します。既存データへ上書き・結合はしません。");
        var settings = Path.GetDirectoryName(ConfigPath(DataRoot))!;
        FileSystemBoundary.CreateManagedDirectory(DataRoot, settings);
        // New plugin commands hold a shared lease for their complete read/write operation.
        using var lease = new FileStream(Path.Combine(settings, "codex-location.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var next = new CodexLocation(1, id, generation, target);
        FileSystemBoundary.CreateManagedDirectory(target, target);
        ExchangePermissions.ProtectNew(target);
        foreach (var directory in new[] { "codex-inbox", "codex-bridge", "codex-bridge/delegations", "codex-bridge/mail-delegations" })
            FileSystemBoundary.CreateManagedDirectory(target, Path.Combine(target, directory));
        long total = 0;
        var count = 0;
        // Copy only the dedicated JSON hand-off data. Originals remain available for recovery.
        foreach (var directory in new[] { "delegations", "mail-delegations" })
        {
            var source = Path.Combine(oldBridge, directory);
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source))
            {
                FileSystemBoundary.ValidateManagedPath(previous.Root, file);
                var name = Path.GetFileName(file);
                var suffix = directory == "delegations" ? ".knowledge-delegation.json" : ".knowledge-mail-delegation.json";
                if (!name.EndsWith(suffix, StringComparison.Ordinal) || !Guid.TryParseExact(name[..^suffix.Length], "D", out _))
                    throw Problem("連携領域に想定外のファイルがあります。自動移動せず停止しました。");
                var bytes = FileSystemBoundary.ReadBoundedFile(file, CodexProposalFiles.MaximumDelegationBytes);
                total += bytes.Length;
                if (++count > 10000 || total > 256L * 1024 * 1024) throw Problem("連携履歴が移設上限を超えています。");
                File.WriteAllBytes(Path.Combine(target, "codex-bridge", directory, name), bytes);
            }
        }
        var bytesConfig = JsonSerializer.SerializeToUtf8Bytes(next, CodexJson.Options);
        File.WriteAllBytes(Path.Combine(target, ".knowledgeapp-codex-owner.json"), bytesConfig);
        var partial = Path.Combine(settings, $"codex-location-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytesConfig); output.Flush(true); }
            File.Move(partial, ConfigPath(DataRoot), true);
            PublishPluginLocation(DataRoot, next);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
        new CodexProposalFiles(database).WriteCategoryCatalog(database.ListCategories());
        return next;
    }));

    internal static AppProblemException Problem(string message) => new(new AppProblem("CDX-024", message,
        "進行中のCodex依頼と保存先を確認してください。失敗した移設先と元の履歴は自動削除しません。"));
}
