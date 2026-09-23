using System.IO;
using System.Security.Cryptography;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeApp.Data;

namespace KnowledgeApp.CSharp;

internal sealed partial class SharedHttpClient
{
    private readonly Dictionary<string, string> _uploads = new(StringComparer.OrdinalIgnoreCase);
    internal async Task<object?> ExecuteFile(string command, JsonElement args)
    {
        await RequireRole(admin: true);
        var node = JsonNode.Parse(args.GetRawText())!.AsObject();
        var kind = command.Contains("csv", StringComparison.Ordinal) ? "csv" : command.Contains("json", StringComparison.Ordinal) ? "json" : "backup";
        var exporting = command is "export_faq_csv" or "export_json" or "create_full_backup";
        if (exporting)
        {
            var destination = ValidateTransferPath(node["input"]!["destinationPath"]!.GetValue<string>(), kind, false);
            var overwrite = command != "create_full_backup" || node["input"]?["overwrite"]?.GetValue<bool>() == true;
            if (File.Exists(destination) && !overwrite) throw new AppProblemException(new AppProblem("BK-002", "同じ名前のバックアップがあります。", "上書きを確認するか別の名前を指定してください。"));
            var result = await Execute(command, args);
            if (result is not JsonElement element) throw Problem("書出結果を確認できません。");
            var downloadTicket = element.GetProperty("destinationPath").GetString()!;
            if (!downloadTicket.StartsWith("shared-file:", StringComparison.Ordinal)) throw Problem("ダウンロード番号が正しくありません。");
            await DownloadFile(downloadTicket[12..], destination, overwrite, element.GetProperty("transferSha256").GetString()!, element.GetProperty("transferBytes").GetInt64());
            var output = JsonNode.Parse(element.GetRawText())!;
            output["destinationPath"] = destination;
            return JsonSerializer.SerializeToElement(output, JsonOptions);
        }
        var direct = command.StartsWith("inspect_", StringComparison.Ordinal);
        var property = direct || command == "restore_backup" ? "path" : "sourcePath";
        var container = direct ? node : node["input"]!.AsObject();
        var source = ValidateTransferPath(container[property]!.GetValue<string>(), kind, true);
        string ticket;
        if (direct)
        {
            ticket = "shared-file:" + await UploadFile(source, kind);
            _uploads[source] = ticket;
        }
        else if (!_uploads.TryGetValue(source, out ticket!)) throw Problem("この接続でファイルを確認していません。選び直してください。");
        container[property] = ticket;
        var response = await Execute(command, JsonSerializer.SerializeToElement(node, JsonOptions));
        if (response is not JsonElement responseElement) return response;
        var resultNode = JsonNode.Parse(responseElement.GetRawText())!;
        if (resultNode is JsonObject obj && obj.ContainsKey("sourcePath")) obj["sourcePath"] = source;
        return JsonSerializer.SerializeToElement(resultNode, JsonOptions);
    }
    private async Task<string> UploadFile(string path, string kind)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/files/" + kind);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StreamContent(source);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("fileId").GetString()!;
    }
    private async Task DownloadFile(string id, string destination, bool overwrite, string expectedHash, long expectedBytes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/files/" + id);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var partial = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536]; long count = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, timeout.Token);
                    if (read == 0) break;
                    count += read;
                    if (count > 10L * 1024 * 1024 * 1024) throw Problem("ダウンロードのサイズが上限を超えています。");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                output.Flush(true);
            }
            using (var verify = File.OpenRead(partial))
                if (verify.Length != expectedBytes || Convert.ToHexStringLower(SHA256.HashData(verify)) != expectedHash)
                    throw Problem("転送内容の検証に失敗しました。既存ファイルは変更していません。");
            StorageSettingsService.ValidateGitFreeDirectory(Path.GetDirectoryName(destination)!);
            FileSystemBoundary.ValidatePath(destination);
            FileSystemBoundary.ValidatePath(partial);
            File.Move(partial, destination, overwrite);
        }
        finally { if (File.Exists(FileSystemBoundary.ValidatePath(partial))) File.Delete(partial); }
    }
    private string ValidateTransferPath(string path, string kind, bool mustExist)
    {
        var full = FileSystemBoundary.ValidatePath(path);
        StorageSettingsService.ValidateGitFreeDirectory(Path.GetDirectoryName(full)!);
        if (!mustExist && _deviceRoot is not null)
            StorageSettingsService.ValidateScopedDestination(_deviceRoot, Path.GetDirectoryName(full)!, kind + "-export");
        var suffix = kind switch { "csv" => ".knowledge-faq.csv", "json" => ".knowledge-export.json", "backup" => ".faqbackup", _ => throw Problem("ファイルの種類が正しくありません。") };
        if (!full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || (mustExist && !File.Exists(full))) throw Problem("ファイルの場所または拡張子が正しくありません。");
        return full;
    }
}
