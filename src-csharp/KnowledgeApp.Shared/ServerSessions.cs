using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeApp.CSharp;
using KnowledgeApp.Data;

namespace KnowledgeApp.Shared;

public sealed record SharedRequest(string Id, string Command, JsonElement Args);
public sealed record SharedResponse(bool Ok, object? Result, AppProblem? Error);

// Each client owns an AuthenticationService, recovery grant, and file capability set.
// A bounded gate serializes complete business operations, including file/DB transitions.
public sealed partial class ServerSessions(KnowledgeDatabase database, string root, string environmentId)
{
    private readonly object _registryLock = new();
    private readonly ArticleAttachmentService _attachments = InitializeTransferArea(root);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<string, (int Count, DateTimeOffset Blocked)> _loginFailures = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 144 };
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "login", "logout", "get_current_user", "get_settings", "save_settings", "get_system_info",
        "get_recovery_key_status", "issue_recovery_key", "skip_recovery_setup", "verify_recovery_key",
        "complete_password_recovery", "cancel_password_recovery", "list_users", "create_user", "set_user_active", "set_user_role",
        "reset_user_password", "get_password_policy", "save_password_policy", "list_categories", "create_category", "update_category", "reorder_category", "delete_category",
        "search_articles", "record_search_log", "get_article", "record_article_view", "list_tags", "save_tag", "delete_tag",
        "save_article", "duplicate_article", "stage_article_image_bytes", "discard_staged_article_image",
        "list_articles_for_management", "delete_article", "restore_article", "list_synonym_groups", "save_synonym_group", "delete_synonym_group",
        "list_search_logs", "list_view_logs", "delete_history", "get_backup_overview",
        "get_storage_folders", "save_storage_folder", "check_storage_folder",
        "export_faq_csv", "inspect_faq_csv", "import_faq_csv", "export_json", "inspect_json", "import_json",
        "create_full_backup", "inspect_backup", "restore_backup",
        "shared_codex_exchange", "shared_submit_codex", "list_codex_proposals", "accept_codex_proposal", "reject_codex_proposal",
        "reopen_rejected_codex_proposal", "create_codex_delegation", "get_codex_merge_publication_context", "mark_codex_merge_sources", "clear_article_merge"
    };
    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
    {
        "get_current_user", "get_settings", "get_system_info", "get_recovery_key_status", "list_users", "get_password_policy",
        "list_categories", "search_articles", "get_article", "list_tags", "list_articles_for_management", "list_synonym_groups",
        "list_search_logs", "list_view_logs", "get_backup_overview", "get_storage_folders"
    };
    public string Create()
    {
        lock (_registryLock)
        {
            Prune();
            if (_sessions.Count >= 256) throw Problem("接続数の上限です。少し待ってから接続してください。");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _sessions.Add(Hash(token), new Session(database, root, _attachments, environmentId));
            return token;
        }
    }
    private Session Resolve(string token)
    {
        lock (_registryLock)
        {
            Prune();
            if (token.Length != 64 || !_sessions.TryGetValue(Hash(token), out var session)) throw new AppProblemException(AppProblem.LoginRequired());
            session.LastUsed = DateTimeOffset.UtcNow;
            return session;
        }
    }
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _sessions.Where(pair => now - pair.Value.Created > TimeSpan.FromHours(8) ||
                     now - pair.Value.LastUsed > TimeSpan.FromMinutes(pair.Value.Authenticated ? 30 : 2)).Select(pair => pair.Key).ToArray())
        {
            ClearTransferFiles(_sessions[key]);
            _sessions.Remove(key);
        }
    }
    public async Task<SharedResponse> Execute(string token, SharedRequest request, CancellationToken cancellationToken = default)
    {
        var session = Resolve(token);
        if (!Guid.TryParseExact(request.Id, "D", out _) || !Allowed.Contains(request.Command))
            return new(false, null, new("SHARE-001", "共有サーバーでは許可されていない操作です。", "対応したクライアントを使用してください。"));
        if (!await _operations.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken))
            return new(false, null, new("SHARE-002", "共有サーバーが混雑しています。処理は開始していません。", "少し待ってから再実行してください。"));
        try
        {
            if (request.Command == "login")
            {
                session.Authentication.Logout(); ClearTransferFiles(session); session.Results.Clear(); session.Staged.Clear(); session.Authenticated = false;
            }
            else if (session.Authenticated && session.Authentication.GetCurrentUser() is null)
            {
                ClearTransferFiles(session); session.Results.Clear(); session.Staged.Clear(); session.Authenticated = false;
                throw new AppProblemException(AppProblem.LoginRequired());
            }
            // Once a write begins, finish and retain its outcome even if the client disconnects.
            var fingerprint = Hash(request.Command + "\n" + request.Args.GetRawText());
            if (session.Results.TryGetValue(request.Id, out var prior))
            {
                if (prior.Fingerprint != fingerprint) throw Problem("同じ処理番号で異なる内容が送信されました。");
                return prior.Response ?? throw Problem("この処理番号の結果は保持期限を過ぎています。FAQや履歴を確認してください。");
            }
            if (session.Results.Count >= 10000) throw Problem("この接続の処理数上限です。保存状態を確認して再ログインしてください。");
            SharedResponse response;
            try
            {
                if (request.Command == "login") CheckLoginLimit(request.Args);
                if (request.Command == "save_article") ValidateStagedReferences(session, request.Args);
                if (request.Command == "discard_staged_article_image" &&
                    !session.Staged.Contains(request.Args.GetProperty("id").GetString()!)) throw Problem("この接続で登録した画像ではありません。");
                if (request.Command == "logout") { ClearTransferFiles(session); session.Results.Clear(); session.Staged.Clear(); }
                var result = IsCodexCommand(request.Command) ? ExecuteCodex(session, request.Command, request.Args) : IsFileCommand(request.Command) ? ExecuteFileCommand(session, request.Command, request.Args) : session.Dispatcher.Execute(request.Command, request.Args);
                session.Authenticated = session.Authentication.GetCurrentUser() is not null;
                if (result is StagedArticleImage stage) session.Staged.Add(stage.Id);
                if (request.Command == "login") ClearLoginFailures(request.Args);
                if (result is SystemInfo info) result = info with { DataRoot = "共有サーバー", DatabasePath = "サーバー内で管理", CodexCategoryCatalogPath = "", CodexInboxPath = "" };
                response = new(true, result, null);
            }
            catch (AppProblemException exception)
            {
                if (request.Command == "login" && exception.Problem.Code == "AUTH-001") RecordLoginFailure(request.Args);
                response = new(false, null, exception.Problem);
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            { response = new(false, null, new("SYS-001", "入力形式を確認できません。", "画面を開き直してください。")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            { response = new(false, null, new("SHARE-004", "処理を完了できませんでした。", "入力内容とFAQ・履歴を確認してください。同じ処理番号では再実行しません。")); }
            if (!Reads.Contains(request.Command))
            {
                session.Results.Add(request.Id, new(fingerprint, response));
                // Bounded response memory; retain IDs to refuse replay of evicted operations.
                while (session.Results.Values.Count(v => v.Response is not null) > 64)
                {
                    var first = session.Results.First(pair => pair.Value.Response is not null);
                    session.Results[first.Key] = first.Value with { Response = null };
                }
            }
            return response;
        }
        finally { _operations.Release(); }
    }
    private void ValidateStagedReferences(Session session, JsonElement args)
    {
        session.Authentication.RequireEditor();
        var input = args.GetProperty("input").Deserialize<SaveArticleInput>(JsonOptions) ?? throw new JsonException();
        var existing = input.Id is null ? [] : database.GetArticle(input.Id).Attachments.Select(a => a.Id).ToHashSet();
        foreach (var attachment in SafeRichContentValidator.Validate(input.BodyDoc).Attachments)
            if (!existing.Contains(attachment.Id) && !session.Staged.Contains(attachment.Id)) throw Problem("他の接続で登録した未保存画像は使用できません。");
    }
    public async Task<(byte[] Bytes, string ContentType)> ReadImage(string token, string relativePath, bool staged)
    {
        var session = Resolve(token);
        if (!await _operations.WaitAsync(TimeSpan.FromSeconds(15))) throw Problem("サーバーが混雑しています。");
        try
        {
            session.Authentication.RequireUser();
            var parts = relativePath.Split('/');
            if (parts.Length != (staged ? 1 : 2) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(parts[^1]), "D", out var imageId))
                throw Problem("画像の指定が正しくありません。");
            if (staged)
            {
                session.Authentication.RequireEditor();
                if (!session.Staged.Contains(imageId.ToString("D"))) throw Problem("この接続の画像ではありません。");
            }
            else new ArticleViewService(database, session.Authentication).RequireImageAccess(relativePath);
            var contentType = Path.GetExtension(parts[^1]) switch
            { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => throw Problem("画像形式が正しくありません。") };
            var directory = Path.Combine(root, staged ? "temp/staged-article-images/files" : "attachments/articles");
            var path = FileSystemBoundary.ValidateManagedPath(directory, Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return (FileSystemBoundary.ReadBoundedFile(path, 10 * 1024 * 1024), contentType);
        }
        finally { _operations.Release(); }
    }
    private static string LoginKey(JsonElement args) => Hash((args.GetProperty("input").GetProperty("loginId").GetString() ?? "").Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant());
    private void CheckLoginLimit(JsonElement args)
    {
        if (_loginFailures.TryGetValue(LoginKey(args), out var failure) && failure.Blocked > DateTimeOffset.UtcNow)
            throw Problem("ログイン試行の上限です。15分後に再試行するか管理者へ相談してください。");
    }
    private void ClearLoginFailures(JsonElement args) => _loginFailures.Remove(LoginKey(args));
    private void RecordLoginFailure(JsonElement args)
    {
        var key = LoginKey(args);
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _loginFailures.Where(v => v.Value.Blocked < now - TimeSpan.FromMinutes(15)).Select(v => v.Key).ToArray()) _loginFailures.Remove(expired);
        if (_loginFailures.Count >= 10000) return;
        _loginFailures.TryGetValue(key, out var previous);
        var count = previous.Blocked < now && previous.Count >= 5 ? 1 : previous.Count + 1;
        _loginFailures[key] = (count, count >= 5 ? now.AddMinutes(15) : now);
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static AppProblemException Problem(string text) => new(new AppProblem("SHARE-003", text, "接続と保存状態を確認して再実行してください。"));
    private sealed record CachedResult(string Fingerprint, SharedResponse? Response);
    private sealed class Session
    {
        internal DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        internal DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
        internal bool Authenticated { get; set; }
        internal AuthenticationService Authentication { get; }
        internal KnowledgeCommandDispatcher Dispatcher { get; }
        internal Func<CodexLocation> CodexLocation { get; }
        internal Dictionary<string, CachedResult> Results { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Staged { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, TransferFile> Files { get; } = new(StringComparer.Ordinal);
        internal Session(KnowledgeDatabase db, string root, ArticleAttachmentService attachments, string environmentId)
        {
            Authentication = new(db);
            CodexLocation = () => MakeCodexLocation(db, Authentication, root, environmentId);
            var codex = new CodexProposalService(db, Authentication, attachments, CodexLocation);
            Dispatcher = new(Authentication, new(db, Authentication), new(db, Authentication, attachments), new(db, Authentication, attachments),
                new(db, Authentication), new(db, Authentication, root), new(db, Authentication, root), new(db, Authentication, root), codex,
                () => throw Problem("サーバー側のファイル選択は使用できません。"), _ => null, _ => null, _ => { }, _ => { });
        }
    }
}
