using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeApp.Data;
namespace KnowledgeApp.CSharp;
internal sealed partial class SharedHttpClient
{
    internal async Task<string> PrepareCodexExchange()
    {
        await RequireRole(editor: true);
        if (_deviceRoot is null) throw Problem("端末の連携先を確認できません。");
        var metadata = (JsonElement)(await Execute("shared_codex_exchange", JsonSerializer.SerializeToElement(new { })))!;
        var environmentId = metadata.GetProperty("environmentId").GetString()!;
        var generation = metadata.GetProperty("generation").GetInt64();
        if (!Guid.TryParseExact(environmentId, "D", out _) || generation < 1) throw Problem("連携環境の識別情報が正しくありません。");
        var root = FileSystemBoundary.ValidateManagedPath(_deviceRoot, Path.Combine(_deviceRoot, "shared-exchanges", environmentId, generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        StorageSettingsService.ValidateGitFreeDirectory(root);
        var existed = Directory.Exists(root);
        foreach (var directory in new[] { root, Path.Combine(root, "codex-inbox"), Path.Combine(root, "codex-bridge", "delegations"), Path.Combine(root, "codex-bridge", "mail-delegations") })
        { FileSystemBoundary.ValidateManagedPath(_deviceRoot, directory); Directory.CreateDirectory(directory); }
        if (!existed) ExchangePermissions.ProtectNew(root); else ExchangePermissions.Validate(root);
        var location = new CodexLocation(1, environmentId, generation, root);
        var owner = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, ".knowledgeapp-codex-owner.json"));
        if (File.Exists(owner) && JsonSerializer.Deserialize<CodexLocation>(await File.ReadAllTextAsync(owner, Encoding.UTF8), JsonOptions) != location)
            throw Problem("端末の連携先が別の環境に属しています。");
        AtomicExchangeWrite(root, owner, JsonSerializer.SerializeToUtf8Bytes(location, JsonOptions), true);
        var settings = Path.Combine(_deviceRoot, "device-settings");
        Directory.CreateDirectory(FileSystemBoundary.ValidateManagedPath(_deviceRoot, settings));
        using (var lease = new FileStream(Path.Combine(settings, "codex-location.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            CodexLocationService.PublishPluginLocation(_deviceRoot, location);
        var catalog = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "codex-bridge", "categories.json"));
        AtomicExchangeWrite(root, catalog, Encoding.UTF8.GetBytes(metadata.GetProperty("catalogJson").GetString()!), true);
        return root;
    }
    internal async Task<object?> ExecuteCodex(string command, JsonElement args)
    {
        var root = await PrepareCodexExchange();
        var inbox = Path.Combine(root, "codex-inbox");
        var rejected = new List<RejectedCodexProposal>();
        if (command == "list_codex_proposals")
        {
            foreach (var path in Directory.EnumerateFiles(inbox, "*.knowledge-proposal.json"))
            {
                try
                {
                FileSystemBoundary.ValidateManagedPath(root, path);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > CodexProposalFiles.MaximumProposalBytes) throw Problem("提案ファイルが1MBの上限を超えています。");
                var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
                using var proposal = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = CodexJson.MaximumDepth });
                await Execute("shared_submit_codex", JsonSerializer.SerializeToElement(new { proposal = proposal.RootElement }, JsonOptions));
                file.Close();
                // Preserve the submitted bytes as local history, never delete an unsubmitted proposal.
                File.Move(path, FileSystemBoundary.ValidateManagedPath(root, path + $".{Guid.NewGuid():N}.submitted"));
                }
                catch (Exception exception) when (exception is AppProblemException or JsonException or IOException)
                { rejected.Add(new(Path.GetFileName(path), exception is AppProblemException problem ? problem.Problem.Message : "提案を送信できませんでした。元ファイルを保持しています。")); }
            }
        }
        var result = await Execute(command, args);
        if (result is not JsonElement element) return result;
        var value = JsonNode.Parse(element.GetRawText())!.AsObject();
        if (command == "list_codex_proposals")
        {
            if (value["rejected"] is JsonArray invalid)
                foreach (var item in rejected) invalid.Add(JsonSerializer.SerializeToNode(item, JsonOptions));
            value["inboxPath"] = inbox; value["categoryCatalogPath"] = Path.Combine(root, "codex-bridge", "categories.json"); }
        if (command == "create_codex_delegation")
        {
            var id = value["delegationId"]!.GetValue<string>();
            if (!Guid.TryParseExact(id, "D", out _)) throw Problem("委譲番号が正しくありません。");
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/codex/delegations/" + id);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > CodexProposalFiles.MaximumDelegationBytes) throw Problem("委譲ファイルのサイズが上限を超えています。");
            var path = Path.Combine(root, "codex-bridge", "delegations", id + CodexProposalFiles.DelegationSuffix);
            AtomicExchangeWrite(root, path, bytes, false);
            value["filePath"] = path;
        }
        return JsonSerializer.SerializeToElement(value, JsonOptions);
    }
    private static void AtomicExchangeWrite(string root, string destination, byte[] bytes, bool overwrite)
    {
        FileSystemBoundary.ValidateManagedPath(root, destination);
        var temporary = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            FileSystemBoundary.ValidateManagedPath(root, destination);
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(FileSystemBoundary.ValidateManagedPath(root, temporary))) File.Delete(temporary); }
    }
}
