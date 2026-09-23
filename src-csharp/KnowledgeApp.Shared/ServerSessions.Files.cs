using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KnowledgeApp.Data;

namespace KnowledgeApp.Shared;

public sealed partial class ServerSessions
{
    private const long MaximumTransferBytes = 10L * 1024 * 1024 * 1024;
    private static string Suffix(string kind) => kind switch { "csv" => ".knowledge-faq.csv", "json" => ".knowledge-export.json", "backup" => ".faqbackup", _ => throw Problem("ファイルの種類が正しくありません。") };
    private static bool IsFileCommand(string command) => command is "export_faq_csv" or "inspect_faq_csv" or "import_faq_csv" or
        "export_json" or "inspect_json" or "import_json" or "create_full_backup" or "inspect_backup" or "restore_backup";
    public async Task<string> Upload(string token, string kind, Stream input, long length, CancellationToken cancellation)
    {
        if (!await _operations.WaitAsync(TimeSpan.FromSeconds(15), cancellation)) throw Problem("共有サーバーが混雑しています。");
        try { return await UploadCore(token, kind, input, length, cancellation); }
        finally { _operations.Release(); }
    }
    private async Task<string> UploadCore(string token, string kind, Stream input, long length, CancellationToken cancellation)
    {
        var session = Resolve(token);
        session.Authentication.RequireAdmin();
        if (length is <= 0 or > MaximumTransferBytes || session.Files.Count >= 16) throw Problem("転送サイズまたはファイル数の上限です。");
        var directory = Path.Combine(root, "temp", "shared-transfers");
        FileSystemBoundary.CreateManagedDirectory(root, directory);
        if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < length + 64 * 1024 * 1024) throw Problem("サーバーの空き容量が不足しています。");
        var id = Guid.NewGuid().ToString("D");
        var path = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(directory, id + Suffix(kind)));
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536]; long copied = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellation);
                    if (count == 0) break;
                    copied += count;
                    if (copied > length) throw Problem("ファイルサイズが変更されています。");
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
                if (copied != length) throw Problem("転送が途中で中断しました。");
                file.Flush(true);
            }
            session.Authentication.RequireAdmin();
            if (!session.Files.TryAdd(id, new(path, kind, false))) throw Problem("ファイル番号が重複しました。");
            return id;
        }
        catch { File.Delete(FileSystemBoundary.ValidateManagedPath(root, path)); throw; }
    }
    public Stream Download(string token, string id)
    {
        var session = Resolve(token);
        session.Authentication.RequireAdmin();
        if (!session.Files.TryGetValue(id, out var file) || !file.Downloadable) throw Problem("この接続のダウンロード対象ではありません。");
        return new FileStream(FileSystemBoundary.ValidateManagedPath(root, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private object? ExecuteFileCommand(Session session, string command, JsonElement arguments)
    {
        session.Authentication.RequireAdmin();
        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        var kind = command.Contains("csv", StringComparison.Ordinal) ? "csv" : command.Contains("json", StringComparison.Ordinal) ? "json" : "backup";
        var exporting = command is "export_faq_csv" or "export_json" or "create_full_backup";
        if (exporting)
        {
            if (session.Files.Count >= 16) throw Problem("転送ファイル数の上限です。再ログインしてから実行してください。");
            var id = Guid.NewGuid().ToString("D");
            var directory = Path.Combine(root, "temp", "shared-transfers");
            FileSystemBoundary.CreateManagedDirectory(root, directory);
            var destination = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(directory, id + Suffix(kind)));
            node["input"]!["destinationPath"] = destination;
            var result = session.Dispatcher.Execute(command, JsonSerializer.SerializeToElement(node, JsonOptions));
            if (kind == "backup")
            {
                var retained = Path.Combine(root, "safety-backups", "shared-export-" + id + ".faqbackup");
                FileSystemBoundary.CreateManagedDirectory(root, Path.GetDirectoryName(retained)!);
                File.Move(destination, FileSystemBoundary.ValidateManagedPath(root, retained));
                destination = retained;
            }
            session.Files[id] = new(destination, kind, true);
            var json = JsonSerializer.SerializeToNode(result, JsonOptions)!.AsObject();
            json["destinationPath"] = "shared-file:" + id;
            using (var input = File.OpenRead(destination))
            { json["transferBytes"] = input.Length; json["transferSha256"] = Convert.ToHexStringLower(SHA256.HashData(input)); }
            if (kind == "backup")
            {
                // First retain a verified local archive; NAS failure cannot destroy it.
                try
                {
                    var targetDirectory = new StorageSettingsService(root, session.Authentication).ResolveForDialog("backup-export");
                    var target = Path.Combine(targetDirectory, $"KnowledgeApp_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{id}.faqbackup");
                    FileSystemBoundary.ValidatePath(target);
                    PublishCopy(destination, target);
                    json["serverCopyPath"] = target;
                }
                catch { json["serverCopyWarning"] = "サーバーの既定保存先への転送に失敗しました。検証済みのローカル退避を保持しています。ダウンロードを保存し、NASの接続と権限を確認してください。"; }
            }
            return JsonSerializer.SerializeToElement(json, JsonOptions);
        }
        var direct = command.StartsWith("inspect_", StringComparison.Ordinal);
        var property = direct ? "path" : command == "restore_backup" ? "path" : "sourcePath";
        var container = direct ? node : node["input"]!.AsObject();
        var ticket = container[property]?.GetValue<string>() ?? "";
        if (!ticket.StartsWith("shared-file:", StringComparison.Ordinal) || !session.Files.TryGetValue(ticket[12..], out var source) || source.Kind != kind || source.Downloadable)
            throw Problem("この接続でアップロード・確認したファイルではありません。");
        container[property] = source.Path;
        var output = session.Dispatcher.Execute(command, JsonSerializer.SerializeToElement(node, JsonOptions));
        if (command == "restore_backup")
        {
            database.SavePasswordPolicy(new(false));
            // RecoveryEpoch invalidates every existing session, including recovery-only clients.
        }
        var resultNode = JsonSerializer.SerializeToNode(output, JsonOptions);
        if (resultNode is JsonObject obj && obj.ContainsKey("sourcePath")) obj["sourcePath"] = ticket;
        return resultNode is null ? null : JsonSerializer.SerializeToElement(resultNode, JsonOptions);
    }
    private static void PublishCopy(string source, string target)
    {
        var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            string expected;
            using (var input = File.OpenRead(source))
            {
                expected = Convert.ToHexStringLower(SHA256.HashData(input)); input.Position = 0;
                using var output = new FileStream(FileSystemBoundary.ValidatePath(partial), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output); output.Flush(true);
            }
            using (var verify = File.OpenRead(FileSystemBoundary.ValidatePath(partial)))
                if (Convert.ToHexStringLower(SHA256.HashData(verify)) != expected) throw new IOException("Copy verification failed.");
            StorageSettingsService.ValidateGitFreeDirectory(Path.GetDirectoryName(target)!);
            File.Move(FileSystemBoundary.ValidatePath(partial), FileSystemBoundary.ValidatePath(target), false);
        }
        finally { try { File.Delete(FileSystemBoundary.ValidatePath(partial)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private void ClearTransferFiles(Session session)
    {
        foreach (var (id, file) in session.Files.ToArray())
        {
            session.Files.TryRemove(id, out _);
            // Verified backup archives are retained independently of the client session.
            if (file.Downloadable && file.Kind == "backup") continue;
            try
            {
                var directory = Path.Combine(root, "temp", "shared-transfers");
                File.Delete(FileSystemBoundary.ValidateManagedPath(directory, file.Path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AppProblemException) { }
        }
    }
    private static ArticleAttachmentService InitializeTransferArea(string root)
    {
        var directory = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "temp", "shared-transfers"));
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                foreach (var suffix in new[] { ".faqbackup", ".knowledge-faq.csv", ".knowledge-export.json" })
                    if (name.EndsWith(suffix, StringComparison.Ordinal) && Guid.TryParseExact(name[..^suffix.Length], "D", out _))
                    { File.Delete(FileSystemBoundary.ValidateManagedPath(directory, file)); break; }
            }
        return new(root);
    }
    private sealed record TransferFile(string Path, string Kind, bool Downloadable);
}
