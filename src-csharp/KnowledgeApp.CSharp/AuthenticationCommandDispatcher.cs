using System.IO;
using System.Text.Json;
using KnowledgeApp.Data;

namespace KnowledgeApp.CSharp;

internal sealed class KnowledgeCommandDispatcher
{
    private static readonly JsonSerializerOptions InputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        // Stored body JSON is checked separately at depth 128; allow its DTO wrappers.
        MaxDepth = 144
    };

    private readonly AuthenticationService _authentication;
    private readonly ClassificationSearchService _classificationSearch;
    private readonly ArticleViewService _articleView;
    private readonly ArticleEditingService _articleEditing;
    private readonly HistoryService _history;
    private readonly TransferService _transfer;
    private readonly BackupService _backup;
    private readonly SettingsService _settings;
    private readonly CodexProposalService _codex;
    private readonly Func<string?> _selectArticleImage;
    private readonly Func<string, string?> _selectTransferSaveFile;
    private readonly Func<string, string?> _selectTransferOpenFile;
    private readonly Action<string> _writeClipboardText;
    private readonly Action<Uri> _openExternalUrl;
    private readonly Func<string?> _selectStorageFolder;

    internal KnowledgeCommandDispatcher(
        AuthenticationService authentication,
        ClassificationSearchService classificationSearch,
        ArticleViewService articleView,
        ArticleEditingService articleEditing,
        HistoryService history,
        TransferService transfer,
        BackupService backup,
        SettingsService settings,
        CodexProposalService codex,
        Func<string?> selectArticleImage,
        Func<string, string?> selectTransferSaveFile,
        Func<string, string?> selectTransferOpenFile,
        Action<string> writeClipboardText,
        Action<Uri> openExternalUrl,
        Func<string?>? selectStorageFolder = null)
    {
        _authentication = authentication;
        _classificationSearch = classificationSearch;
        _articleView = articleView;
        _articleEditing = articleEditing;
        _history = history;
        _transfer = transfer;
        _backup = backup;
        _settings = settings;
        _codex = codex;
        _selectArticleImage = selectArticleImage;
        _selectTransferSaveFile = selectTransferSaveFile;
        _selectTransferOpenFile = selectTransferOpenFile;
        _writeClipboardText = writeClipboardText;
        _openExternalUrl = openExternalUrl;
        _selectStorageFolder = selectStorageFolder ?? (() => null);
    }

    internal object? Execute(string command, JsonElement arguments) => command switch
    {
        "login" => Login(arguments),
        "get_recovery_key_status" => _authentication.GetRecoveryKeyStatus(),
        "issue_recovery_key" => _authentication.IssueRecoveryKey(ReadInput<IssueRecoveryKeyInput>(arguments)),
        "skip_recovery_setup" => _authentication.SkipRecoverySetup(),
        "verify_recovery_key" => _authentication.VerifyRecoveryKey(ReadInput<VerifyRecoveryKeyInput>(arguments)),
        "complete_password_recovery" => _authentication.CompletePasswordRecovery(ReadInput<CompletePasswordRecoveryInput>(arguments)),
        "cancel_password_recovery" => _authentication.CancelPasswordRecovery(),
        "logout" => Logout(),
        "get_current_user" => _authentication.GetCurrentUser(),
        "get_codex_location" => _settings.CodexLocation.Get(),
        "change_codex_location" => _settings.CodexLocation.Change(ReadInput<ChangeCodexLocationInput>(arguments)),
        "get_storage_folders" => _settings.Storage.GetFolders(),
        "save_storage_folder" => _settings.Storage.Save(ReadInput<SaveStorageFolderInput>(arguments)),
        "check_storage_folder" => _settings.Storage.Check(ReadInput<SaveStorageFolderInput>(arguments)),
        "select_storage_folder" => SelectStorageFolder(),
        "get_system_info" => _settings.GetSystemInfo(),
        "get_settings" => _settings.GetSettings(),
        "save_settings" => _settings.SaveSettings(ReadInput<AppSettings>(arguments)),
        "list_users" => _authentication.ListUsers(),
        "create_user" => CreateUser(arguments),
        "set_user_active" => SetUserActive(arguments),
        "set_user_role" => SetUserRole(arguments),
        "reset_user_password" => ResetUserPassword(arguments),
        "get_password_policy" => _authentication.GetPasswordPolicy(),
        "save_password_policy" => SavePasswordPolicy(arguments),
        "list_categories" => _classificationSearch.ListCategories(),
        "create_category" => CreateCategory(arguments),
        "update_category" => UpdateCategory(arguments),
        "reorder_category" => ReorderCategory(arguments),
        "delete_category" => DeleteCategory(arguments),
        "search_articles" => SearchArticles(arguments),
        "record_search_log" => RecordSearchLog(arguments),
        "get_article" => _articleView.GetArticle(ReadDirectId(arguments)),
        "record_article_view" => RecordArticleView(arguments),
        "get_codex_merge_publication_context" =>
            _codex.GetMergePublicationContext(ReadDirectArticleId(arguments)),
        "list_codex_proposals" => _codex.ListProposals(),
        "accept_codex_proposal" => _codex.AcceptProposal(ReadInput<AcceptCodexProposalInput>(arguments)),
        "create_codex_delegation" => _codex.CreateDelegation(ReadInput<CreateCodexDelegationInput>(arguments)),
        "reject_codex_proposal" => RejectCodexProposal(arguments),
        "reopen_rejected_codex_proposal" => ReopenCodexProposal(arguments),
        "mark_codex_merge_sources" => _codex.MarkMergeSources(ReadDirectArticleId(arguments)),
        "clear_article_merge" => _codex.ClearArticleMerge(ReadDirectArticleId(arguments)),
        "list_tags" => _articleEditing.ListTags(),
        "save_tag" => SaveTag(arguments),
        "delete_tag" => DeleteTag(arguments),
        "save_article" => SaveArticle(arguments),
        "duplicate_article" => _articleEditing.DuplicateArticle(ReadDirectId(arguments)),
        "select_article_image" => SelectArticleImage(),
        "stage_article_image" => _articleEditing.StageArticleImage(ReadDirectString(arguments, "path")),
        "stage_article_image_bytes" =>
            _articleEditing.StageArticleImageBase64(ReadInput<StageArticleImageBase64Input>(arguments)),
        "discard_staged_article_image" => DiscardStagedArticleImage(arguments),
        "open_external_url" => OpenExternalUrl(arguments),
        "write_clipboard_text" => WriteClipboardText(arguments),
        "list_articles_for_management" => ListArticlesForManagement(arguments),
        "delete_article" => _articleEditing.DeleteArticle(ReadDirectId(arguments)),
        "restore_article" => _articleEditing.RestoreArticle(ReadDirectId(arguments)),
        "list_synonym_groups" => _classificationSearch.ListSynonymGroups(),
        "save_synonym_group" => SaveSynonymGroup(arguments),
        "delete_synonym_group" => DeleteSynonymGroup(arguments),
        "list_search_logs" => _history.ListSearchLogs(ReadInput<ListSearchLogsInput>(arguments)),
        "list_view_logs" => _history.ListViewLogs(ReadInput<ListViewLogsInput>(arguments)),
        "delete_history" => _history.DeleteHistory(ReadInput<DeleteHistoryInput>(arguments)),
        "select_faq_csv_export_path" => SelectTransferSaveFile("csv", arguments),
        "select_faq_csv_import_path" => SelectTransferOpenFile("csv"),
        "export_faq_csv" => _transfer.ExportFaqCsv(ReadInput<ExportFaqCsvInput>(arguments)),
        "inspect_faq_csv" => _transfer.InspectFaqCsv(ReadDirectString(arguments, "path")),
        "import_faq_csv" => _transfer.ImportFaqCsv(ReadInput<ImportFaqCsvInput>(arguments)),
        "select_json_export_path" => SelectTransferSaveFile("json", arguments),
        "select_json_import_path" => SelectTransferOpenFile("json"),
        "export_json" => _transfer.ExportJson(ReadInput<ExportJsonInput>(arguments)),
        "inspect_json" => _transfer.InspectJson(ReadDirectString(arguments, "path")),
        "import_json" => _transfer.ImportJson(ReadInput<ImportJsonInput>(arguments)),
        "select_full_backup_destination" => SelectBackupSaveFile(arguments),
        "select_restore_backup_source" => SelectBackupOpenFile(),
        "get_backup_overview" => _backup.GetOverview(),
        "create_full_backup" => _backup.CreateFullBackup(ReadInput<CreateFullBackupInput>(arguments)),
        "inspect_backup" => _backup.InspectBackup(ReadDirectString(arguments, "path")),
        "restore_backup" => _backup.RestoreConfirmedBackup(ReadInput<RestoreConfirmedBackupInput>(arguments)),
        _ => throw new AppProblemException(new AppProblem(
            "MIG-001",
            "この機能はC#版へまだ移植されていません。",
            "現行Tauri版を使用するか、次の移植段階が完了するまでお待ちください。"))
    };

    private string? SelectStorageFolder()
    {
        _authentication.RequireAdmin();
        return _selectStorageFolder();
    }

    private object? RejectCodexProposal(JsonElement arguments)
    {
        _codex.RejectProposal(ReadDirectString(arguments, "requestId"));
        return null;
    }

    private object? ReopenCodexProposal(JsonElement arguments)
    {
        _codex.ReopenRejectedProposal(ReadDirectString(arguments, "requestId"));
        return null;
    }

    private AuthenticatedUser Login(JsonElement arguments)
    {
        var input = ReadInput<LoginInput>(arguments);
        return _authentication.Login(input.LoginId, input.Password ?? string.Empty);
    }

    private object? Logout()
    {
        _authentication.Logout();
        return null;
    }

    private UserSummary CreateUser(JsonElement arguments)
    {
        var input = ReadInput<CreateUserInput>(arguments);
        return _authentication.CreateUser(
            input.LoginId,
            input.DisplayName,
            input.Password ?? string.Empty,
            input.Role);
    }

    private UserSummary SetUserRole(JsonElement arguments)
    {
        var input = ReadInput<SetUserRoleInput>(arguments);
        return _authentication.SetUserRole(input.Id, input.Role);
    }

    private sealed record SetUserRoleInput(string Id, string Role);

    private UserSummary SetUserActive(JsonElement arguments)
    {
        var input = ReadInput<SetUserActiveInput>(arguments);
        return _authentication.SetUserActive(input.Id, input.IsActive);
    }

    private UserSummary ResetUserPassword(JsonElement arguments)
    {
        var input = ReadInput<ResetUserPasswordInput>(arguments);
        return _authentication.ResetUserPassword(input.Id, input.Password ?? string.Empty);
    }

    private PasswordPolicySettings SavePasswordPolicy(JsonElement arguments)
    {
        var input = ReadInput<PasswordPolicySettings>(arguments);
        return _authentication.SavePasswordPolicy(input);
    }

    private CategorySummary CreateCategory(JsonElement arguments)
    {
        var input = ReadInput<CreateCategoryInput>(arguments);
        return _classificationSearch.CreateCategory(input.Name, input.Description, input.ParentId);
    }

    private CategorySummary UpdateCategory(JsonElement arguments)
    {
        var input = ReadInput<UpdateCategoryInput>(arguments);
        return _classificationSearch.UpdateCategory(
            input.Id,
            input.Name,
            input.Description,
            input.ParentId);
    }

    private IReadOnlyList<CategorySummary> ReorderCategory(JsonElement arguments)
    {
        var input = ReadInput<ReorderCategoryInput>(arguments);
        return _classificationSearch.ReorderCategory(input.Id, input.Direction);
    }

    private object? DeleteCategory(JsonElement arguments)
    {
        _classificationSearch.DeleteCategory(ReadDirectId(arguments));
        return null;
    }

    private SearchArticlePage SearchArticles(JsonElement arguments) =>
        _classificationSearch.SearchArticles(ReadInput<SearchArticlesInput>(arguments));

    private string RecordSearchLog(JsonElement arguments)
    {
        var input = ReadInput<RecordSearchLogInput>(arguments);
        return _classificationSearch.RecordSearchLog(
            input.Query,
            input.CategoryId,
            input.Scope,
            input.ResultCount);
    }

    private object? RecordArticleView(JsonElement arguments)
    {
        var input = ReadInput<RecordArticleViewInput>(arguments);
        _articleView.RecordArticleView(input.ArticleId, input.SourceSearchLogId);
        return null;
    }

    private ArticleDetail SaveArticle(JsonElement arguments) =>
        _articleEditing.SaveArticle(ReadInput<SaveArticleInput>(arguments));

    private TagMasterItem SaveTag(JsonElement arguments)
    {
        var input = ReadInput<SaveTagInput>(arguments);
        return _articleEditing.SaveTag(input.Id, input.Name);
    }

    private object? DeleteTag(JsonElement arguments)
    {
        _articleEditing.DeleteTag(ReadDirectId(arguments));
        return null;
    }

    private ManagementArticlePage ListArticlesForManagement(JsonElement arguments) =>
        _articleEditing.ListArticlesForManagement(ReadInput<ManagementArticlesInput>(arguments));

    private StagedArticleImage? SelectArticleImage()
    {
        _authentication.RequireEditor();
        var selected = _selectArticleImage();
        return string.IsNullOrWhiteSpace(selected) ? null : _articleEditing.StageArticleImage(selected);
    }

    private object? DiscardStagedArticleImage(JsonElement arguments)
    {
        _articleEditing.DiscardStagedArticleImage(ReadDirectId(arguments));
        return null;
    }

    private object? OpenExternalUrl(JsonElement arguments)
    {
        _authentication.RequireUser();
        var uri = ExternalInteractionValidator.ValidateExternalUrl(ReadDirectString(arguments, "url"));
        try
        {
            _openExternalUrl(uri);
        }
        catch
        {
            throw new AppProblemException(new AppProblem(
                "URL-002",
                "参考URLを既定ブラウザーで開けませんでした。",
                "Windowsの既定ブラウザー設定を確認して、もう一度お試しください。"));
        }
        return null;
    }

    private object? WriteClipboardText(JsonElement arguments)
    {
        _authentication.RequireUser();
        var text = ExternalInteractionValidator.ValidateClipboardText(
            ReadDirectString(arguments, "text"));
        try
        {
            _writeClipboardText(text);
        }
        catch
        {
            throw new AppProblemException(new AppProblem(
                "SYS-001",
                "コピーできませんでした。",
                "文字列を選択して、Windowsのコピー操作をお試しください。"));
        }
        return null;
    }

    private string? SelectTransferSaveFile(string kind, JsonElement arguments)
    {
        _authentication.RequireAdmin();
        var defaultName = ReadDirectString(arguments, "defaultName");
        if (defaultName.Length is < 1 or > 200 ||
            defaultName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.GetFileName(defaultName) != defaultName)
        {
            throw new AppProblemException(new AppProblem(
                "SYS-001",
                "保存ファイル名の指定が正しくありません。",
                "画面を再読み込みして、もう一度お試しください。"));
        }
        return _selectTransferSaveFile($"{kind}|{defaultName}");
    }

    private string? SelectTransferOpenFile(string kind)
    {
        _authentication.RequireAdmin();
        return _selectTransferOpenFile(kind);
    }

    private string? SelectBackupSaveFile(JsonElement arguments)
    {
        _authentication.RequireAdmin();
        var defaultPath = ReadDirectString(arguments, "defaultPath");
        if (defaultPath.Length is < 1 or > 32_767)
        {
            throw new AppProblemException(new AppProblem(
                "BK-001",
                "バックアップの保存場所またはファイル名が正しくありません。",
                "保存先とファイル名を選び直してください。"));
        }
        return _selectTransferSaveFile($"backup|{defaultPath}");
    }

    private string? SelectBackupOpenFile()
    {
        _authentication.RequireAdmin();
        return _selectTransferOpenFile("backup");
    }

    private SynonymGroupSummary SaveSynonymGroup(JsonElement arguments)
    {
        var input = ReadInput<SaveSynonymGroupInput>(arguments);
        return _classificationSearch.SaveSynonymGroup(
            input.Id,
            input.DisplayName,
            input.Terms,
            input.AllowConflicts);
    }

    private object? DeleteSynonymGroup(JsonElement arguments)
    {
        _classificationSearch.DeleteSynonymGroup(ReadDirectId(arguments));
        return null;
    }

    private static T ReadInput<T>(JsonElement arguments)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("input", out var input))
            {
                throw new JsonException();
            }
            return input.Deserialize<T>(InputJsonOptions) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new AppProblemException(new AppProblem(
                "SYS-001",
                "C#処理へ渡された入力形式が正しくありません。",
                "画面を再読み込みして、もう一度お試しください。"));
        }
    }

    private static string ReadDirectId(JsonElement arguments)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()))
            {
                throw new JsonException();
            }
            return id.GetString()!;
        }
        catch (JsonException)
        {
            throw new AppProblemException(new AppProblem(
                "SYS-001",
                "C#処理へ渡された入力形式が正しくありません。",
                "画面を再読み込みして、もう一度お試しください。"));
        }
    }

    private static string ReadDirectArticleId(JsonElement arguments)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("articleId", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()))
            {
                throw new JsonException();
            }
            return id.GetString()!;
        }
        catch (JsonException)
        {
            throw new AppProblemException(new AppProblem(
                "SYS-001",
                "C#処理へ渡された入力形式が正しくありません。",
                "画面を再読み込みして、もう一度お試しください。"));
        }
    }

    private static string ReadDirectString(JsonElement arguments, string propertyName)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                throw new JsonException();
            }
            return value.GetString()!;
        }
        catch (JsonException)
        {
            throw new AppProblemException(new AppProblem(
                "SYS-001",
                "C#処理へ渡された入力形式が正しくありません。",
                "画面を再読み込みして、もう一度お試しください。"));
        }
    }

    private sealed record LoginInput(string LoginId, string? Password);

    private sealed record SaveTagInput(string? Id, string Name);

    private sealed record CreateUserInput(
        string LoginId,
        string DisplayName,
        string? Password,
        string Role);

    private sealed record SetUserActiveInput(string Id, bool IsActive);

    private sealed record ResetUserPasswordInput(string Id, string? Password);

    private sealed record CreateCategoryInput(string Name, string Description, string? ParentId);

    private sealed record UpdateCategoryInput(string Id, string Name, string Description, string? ParentId);

    private sealed record ReorderCategoryInput(string Id, string Direction);

    private sealed record RecordSearchLogInput(
        string Query,
        string? CategoryId,
        string Scope,
        long ResultCount);

    private sealed record RecordArticleViewInput(
        string ArticleId,
        string? SourceSearchLogId);

    private sealed record SaveSynonymGroupInput(
        string? Id,
        string DisplayName,
        IReadOnlyList<string> Terms,
        bool AllowConflicts);
}
