using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeApp.CSharp;
using KnowledgeApp.Data;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = Encoding.UTF8;
var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
var testRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var migrationRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var futureRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var corruptRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var searchRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var articleRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var editingRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var historyRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var transferRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var transferTargetRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var transferRestartRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var transferFilesRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var backupRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var backupFilesRoot = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var roots = new[]
{
    testRoot, migrationRoot, futureRoot, corruptRoot, searchRoot, articleRoot, editingRoot,
    historyRoot, transferRoot, transferTargetRoot, transferRestartRoot, transferFilesRoot,
    backupRoot, backupFilesRoot
};

try
{
    VerifyHostOperationPolicy();
    TagMasterCheck.Run();
    FileBoundaryCheck.Run();
    DispatcherTagCheck.Run();
    VerifyNewDatabaseAndAuthentication(testRoot);
    VerifyVersionOneMigration(migrationRoot);
    VerifyFutureVersionIsRejected(futureRoot);
    VerifyCorruptDatabaseIsRejected(corruptRoot);
    VerifyClassificationSearchAndFts(searchRoot);
    VerifyArticleViewAndHistory(articleRoot);
    VerifyArticleEditingAndAudit(editingRoot);
    VerifyHistoryBrowsingAndDeletion(historyRoot);
    VerifyCsvAndJsonTransfer(transferRoot, transferTargetRoot, transferRestartRoot, transferFilesRoot);
    VerifyFullBackupAndRestore(backupRoot, backupFilesRoot);
    ExpectProblem(
        () => KnowledgeDatabase.OpenSynthetic(Path.Combine(Directory.GetCurrentDirectory(), "knowledgeapp-data-check-outside-temp")),
        "DB-001");
    ExpectProblem(
        () => KnowledgeDatabase.OpenSynthetic(Path.Combine(
            temporaryRoot,
            "nested",
            $"knowledgeapp-data-check-{Guid.NewGuid():D}")),
        "DB-001");

    Console.WriteLine("OK: C#認証・権限、分類CRUD、同義語、日本語検索、FTS5、FAQ詳細、FAQ編集、リッチテキスト、画像確定・独立複製・除去・削除復元、URL・コピー境界、監査、関連FAQ、最大10件分の読取、検索・閲覧履歴、端末情報・表示設定、CSV・JSON、フルバックアップのSHA検査・上書き・Git拒否・破損拒否・安全退避・DB/画像/設定復元・復元後ログアウト、長時間I/O終了ガードの対象17件と対象外、DB第1～7版移行を合成DBで確認しました。");
    return 0;
}
finally
{
    foreach (var root in roots)
    {
        DeleteSyntheticRoot(root, temporaryRoot);
    }
}

static void VerifyHostOperationPolicy()
{
    string[] guardedCommands =
    [
        "create_full_backup", "inspect_backup", "restore_backup",
        "export_faq_csv", "inspect_faq_csv", "import_faq_csv",
        "export_json", "inspect_json", "import_json",
        "list_codex_proposals", "accept_codex_proposal", "reject_codex_proposal",
        "reopen_rejected_codex_proposal", "create_codex_delegation",
        "mark_codex_merge_sources", "clear_article_merge", "save_tag"
    ];
    foreach (var command in guardedCommands)
    {
        if (!HostOperationPolicy.PreventsWindowClose(command))
        {
            throw new InvalidOperationException($"長時間I/Oの終了ガード対象が欠けています: {command}");
        }
    }

    string[] unguardedCommands =
    [
        "select_article_image", "select_faq_csv_export_path", "select_faq_csv_import_path",
        "select_json_export_path", "select_json_import_path",
        "select_full_backup_destination", "select_restore_backup_source",
        "migration_probe", "login", "logout", "get_current_user", "get_settings",
        "save_settings", "get_backup_overview", "search_articles", "get_article",
        "save_article", "stage_article_image", "stage_article_image_bytes",
        "write_clipboard_text", "open_external_url", "", "unknown_command",
        "create_full_backup_extra", "prefix_restore_backup", "IMPORT_JSON",
        "get_codex_merge_publication_context", "accept_codex_proposal_extra", "delete_tag", "save_tag_extra"
    ];
    foreach (var command in unguardedCommands)
    {
        if (HostOperationPolicy.PreventsWindowClose(command))
        {
            throw new InvalidOperationException($"終了ガードが固定対象以外へ広がっています: {command}");
        }
    }
}

static void VerifyNewDatabaseAndAuthentication(string root)
{
    using var database = KnowledgeDatabase.OpenSynthetic(root);
    if (!database.OpenInfo.Created ||
        database.OpenInfo.PreviousSchemaVersion != 0 ||
        database.OpenInfo.CurrentSchemaVersion != 7 ||
        database.OpenInfo.MigrationBackupPath is not null ||
        database.SchemaVersionForTest() != 7 ||
        database.QuickCheckForTest() != "ok")
    {
        throw new InvalidOperationException("新規合成DBの初期化結果が正しくありません。");
    }

    var service = new AuthenticationService(database);
    if (service.GetCurrentUser() is not null)
    {
        throw new InvalidOperationException("起動直後のセッションが空ではありません。");
    }
    ExpectProblem(() => service.ListUsers(), "AUTH-002");
    ExpectProblem(() => service.Login("0000", "incorrect-synthetic-password"), "AUTH-001");

    var initialAdmin = service.Login("００００", string.Empty);
    if (initialAdmin.Id != KnowledgeDatabase.InitialAdminUserId ||
        initialAdmin.LoginId != "0000" ||
        initialAdmin.Role != UserRoles.Admin)
    {
        throw new InvalidOperationException("初期管理者のNFKC認証または権限が正しくありません。");
    }
    var initialHash = database.PasswordHashForTest(initialAdmin.Id);
    if (!initialHash.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", StringComparison.Ordinal) ||
        initialHash.Contains("0000", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("初期管理者のパスワードが互換Argon2id形式で保存されていません。");
    }

    const string specialPassword = "Synthetic\\*Password!";
    var user = service.CreateUser("Ｔｅｓｔ", "合成一般利用者", specialPassword, UserRoles.User);
    if (user.Role != UserRoles.User || !user.IsActive)
    {
        throw new InvalidOperationException("一般利用者を正しい権限で作成できませんでした。");
    }
    ExpectProblem(
        () => service.CreateUser("test", "重複利用者", "another-password", UserRoles.User),
        "USR-001");
    if (!Argon2PasswordCodec.Verify(specialPassword, database.PasswordHashForTest(user.Id)) ||
        Argon2PasswordCodec.Verify("wrong", database.PasswordHashForTest(user.Id)))
    {
        throw new InvalidOperationException("Argon2idの検証結果が正しくありません。");
    }

    service.Logout();
    var normalizedUser = service.Login("test", specialPassword);
    if (normalizedUser.Id != user.Id)
    {
        throw new InvalidOperationException("正規化ログインIDで同じ利用者へログインできませんでした。");
    }
    ExpectProblem(() => service.ListUsers(), "AUTH-003");
    service.Logout();

    service.Login("0000", string.Empty);
    const string resetPassword = "Synthetic\\*Reset!";
    service.ResetUserPassword(user.Id, resetPassword);
    service.Logout();
    ExpectProblem(() => service.Login("test", specialPassword), "AUTH-001");
    service.Login("test", resetPassword);
    service.Logout();

    service.Login("0000", string.Empty);
    service.SetUserActive(user.Id, false);
    service.Logout();
    ExpectProblem(() => service.Login("test", resetPassword), "AUTH-001");
    service.Login("0000", string.Empty);
    ExpectProblem(() => service.SetUserActive(initialAdmin.Id, false), "USR-002");

    var secondAdmin = service.CreateUser("admin2", "合成追加管理者", "admin-password", UserRoles.Admin);
    service.Logout();
    service.Login("admin2", "admin-password");
    service.SetUserActive(initialAdmin.Id, false);
    ExpectProblem(() => service.SetUserActive(secondAdmin.Id, false), "USR-002");
    service.SetUserActive(initialAdmin.Id, true);
    service.Logout();

    service.Login("0000", string.Empty);
    var policy = service.SavePasswordPolicy(new PasswordPolicySettings(false));
    if (policy.AllowEmptyPasswords || service.GetPasswordPolicy().AllowEmptyPasswords)
    {
        throw new InvalidOperationException("空パスワード方針を保存できませんでした。");
    }
    ExpectProblem(
        () => service.CreateUser("empty", "空欄拒否", string.Empty, UserRoles.User),
        "USR-004");
    ExpectProblem(() => service.ResetUserPassword(secondAdmin.Id, string.Empty), "USR-004");
    service.Logout();

    // 方針をOFFにしても、既存の空パスワード利用者は引き続き認証できる。
    service.Login("0000", string.Empty);
    service.Logout();
    if (service.GetCurrentUser() is not null)
    {
        throw new InvalidOperationException("ログアウト後もセッションが残っています。");
    }
}

static void VerifyVersionOneMigration(string root)
{
    var databasePath = CreateVersionOneDatabase(root, includeArticle: true);
    using var database = KnowledgeDatabase.OpenSynthetic(root);
    if (database.OpenInfo.Created ||
        database.OpenInfo.PreviousSchemaVersion != 1 ||
        database.OpenInfo.CurrentSchemaVersion != 7 ||
        database.OpenInfo.MigrationBackupPath is null ||
        !File.Exists(database.OpenInfo.MigrationBackupPath))
    {
        throw new InvalidOperationException("DB第1版から第7版への移行情報が正しくありません。");
    }

    using (var current = Open(databasePath, SqliteOpenMode.ReadOnly))
    {
        if (ScalarInt64(current, "SELECT MAX(version) FROM schema_migrations") != 7 ||
            ScalarInt64(current, "SELECT COUNT(*) FROM articles WHERE id = 'article-v1'") != 1 ||
            ScalarString(current, "SELECT created_by_user_id FROM articles WHERE id = 'article-v1'") != KnowledgeDatabase.InitialAdminUserId ||
            ScalarString(current, "SELECT updated_by_user_id FROM articles WHERE id = 'article-v1'") != KnowledgeDatabase.InitialAdminUserId)
        {
            throw new InvalidOperationException("移行後に版、FAQ、監査利用者を保持できませんでした。");
        }
    }

    using var backup = Open(database.OpenInfo.MigrationBackupPath, SqliteOpenMode.ReadOnly);
    if (ScalarInt64(backup, "SELECT MAX(version) FROM schema_migrations") != 1 ||
        ScalarInt64(backup, "SELECT COUNT(*) FROM articles WHERE id = 'article-v1'") != 1 ||
        ScalarString(backup, "PRAGMA quick_check") != "ok")
    {
        throw new InvalidOperationException("移行前安全バックアップが旧版データを保持していません。");
    }
}

static void VerifyFutureVersionIsRejected(string root)
{
    var databasePath = CreateVersionOneDatabase(root, includeArticle: false);
    using (var connection = Open(databasePath, SqliteOpenMode.ReadWrite))
    {
        Execute(connection, "INSERT INTO schema_migrations(version, applied_at) VALUES (8, 'synthetic')");
    }
    ExpectProblem(() => KnowledgeDatabase.OpenSynthetic(root), "DB-001");
    using var check = Open(databasePath, SqliteOpenMode.ReadOnly);
    if (ScalarInt64(check, "SELECT MAX(version) FROM schema_migrations") != 8)
    {
        throw new InvalidOperationException("将来版拒否時に元DBが変更されました。");
    }
}

static void VerifyCorruptDatabaseIsRejected(string root)
{
    var dataDirectory = Path.Combine(root, "data");
    Directory.CreateDirectory(dataDirectory);
    File.WriteAllBytes(Path.Combine(dataDirectory, "knowledge.db"), [0x01, 0x02, 0x03, 0x04]);
    ExpectProblem(() => KnowledgeDatabase.OpenSynthetic(root), "DB-001");
}

static void VerifyClassificationSearchAndFts(string root)
{
    using var database = KnowledgeDatabase.OpenSynthetic(root);
    var fixture = database.SeedSyntheticCategorySearchFixture();
    if (database.FtsRowCountForTest() < 60)
    {
        throw new InvalidOperationException("FTS5合成インデックスへ必要なFAQが登録されていません。");
    }

    var authentication = new AuthenticationService(database);
    var service = new ClassificationSearchService(database, authentication);
    ExpectProblem(() => service.ListCategories(), "AUTH-002");
    authentication.Login("0000", string.Empty);

    var categories = service.ListCategories();
    var networkIndex = IndexOf(categories, fixture.NetworkCategoryId);
    var wifiIndex = IndexOf(categories, fixture.WifiCategoryId);
    if (networkIndex < 0 || wifiIndex != networkIndex + 1 ||
        categories[wifiIndex].ParentId != fixture.NetworkCategoryId ||
        categories[wifiIndex].Depth != 2 ||
        categories[wifiIndex].ArticleCount != 4)
    {
        throw new InvalidOperationException("分類の階層順、深さ、FAQ件数が正しくありません。");
    }

    var duplicateBase = service.CreateCategory("合成重複確認", "説明", null);
    ExpectProblem(() => service.CreateCategory("　合成重複確認　", "", null), "CAT-004");
    var level2 = service.CreateCategory("合成2階層", "", duplicateBase.Id);
    var level3 = service.CreateCategory("合成3階層", "", level2.Id);
    var level4 = service.CreateCategory("合成4階層", "", level3.Id);
    var level5 = service.CreateCategory("合成5階層", "", level4.Id);
    ExpectProblem(() => service.CreateCategory("合成6階層", "", level5.Id), "CAT-003");
    ExpectProblem(
        () => service.UpdateCategory(duplicateBase.Id, duplicateBase.Name, duplicateBase.Description, level3.Id),
        "CAT-006");

    var orderA = service.CreateCategory("合成並びA", "", null);
    var orderB = service.CreateCategory("合成並びB", "", null);
    var reordered = service.ReorderCategory(orderB.Id, "up");
    if (IndexOf(reordered, orderB.Id) >= IndexOf(reordered, orderA.Id))
    {
        throw new InvalidOperationException("同一親内の分類並べ替えが反映されませんでした。");
    }
    var empty = service.CreateCategory("合成削除対象", "削除できる空分類", null);
    service.DeleteCategory(empty.Id);
    ExpectProblem(() => service.DeleteCategory(fixture.NetworkCategoryId), "CAT-005");

    var currentWifi = Search(service, "Zoom", fixture.WifiCategoryId, SearchScopes.Current, false, 1);
    var descendantNetwork = Search(service, "Zoom", fixture.NetworkCategoryId, SearchScopes.Descendants, false, 1);
    var currentSecurity = Search(service, "ページング", fixture.SecurityCategoryId, SearchScopes.Current, false, 1);
    if (currentWifi.Total != 1 || currentWifi.Items[0].Id != fixture.PublishedMeshArticleId ||
        descendantNetwork.Total != 1 || currentSecurity.Total != 55 || currentSecurity.Items.Count != 50 ||
        currentSecurity.PageSize != 50)
    {
        throw new InvalidOperationException("分類範囲または50件ページング検索が正しくありません。");
    }
    var secondPage = Search(service, "ページング", fixture.SecurityCategoryId, SearchScopes.Current, false, 2);
    if (secondPage.Total != 55 || secondPage.Items.Count != 5 || secondPage.Page != 2)
    {
        throw new InvalidOperationException("検索結果の2ページ目が正しくありません。");
    }

    var nfkc = Search(service, "ＰＣ", null, SearchScopes.All, false, 1);
    var synonym = Search(service, "パソコン", null, SearchScopes.All, false, 1);
    if (!nfkc.Items.Any(item => item.Id == fixture.PublishedMeshArticleId) ||
        !synonym.Items.Any(item => item.Id == fixture.PublishedMeshArticleId) ||
        !synonym.Items.SelectMany(item => item.MatchReasons).Any(reason => reason.StartsWith("同義語：", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("NFKC正規化または同義語展開検索が正しくありません。");
    }

    var withoutDraft = Search(service, "下書き", null, SearchScopes.All, false, 1);
    var withDraft = Search(service, "下書き", null, SearchScopes.All, true, 1);
    if (withoutDraft.Total != 0 || withDraft.Total != 1 || withDraft.Items[0].Id != fixture.DraftArticleId)
    {
        throw new InvalidOperationException("下書きの検索条件が正しくありません。");
    }
    foreach (var excludedQuery in new[] { "秘匿検索語", "削除検索語", "統合検索語" })
    {
        if (Search(service, excludedQuery, null, SearchScopes.All, true, 1).Total != 0)
        {
            throw new InvalidOperationException("非表示・削除済み・統合済みFAQが通常検索へ混入しました。");
        }
    }

    var importanceDesc = Search(service, string.Empty, fixture.SecurityCategoryId, SearchScopes.Current, false, 1, SearchSorts.ImportanceDesc);
    var importanceAsc = Search(service, string.Empty, fixture.SecurityCategoryId, SearchScopes.Current, false, 1, SearchSorts.ImportanceAsc);
    if (importanceDesc.Items[0].Importance < importanceDesc.Items[^1].Importance ||
        importanceAsc.Items[0].Importance > importanceAsc.Items[^1].Importance)
    {
        throw new InvalidOperationException("重要度の並べ替えが正しくありません。");
    }

    var logCount = database.SearchLogCountForTest();
    var logId = service.RecordSearchLog(" ＰＣ ", fixture.WifiCategoryId, SearchScopes.Current, nfkc.Total);
    if (string.IsNullOrWhiteSpace(logId) || database.SearchLogCountForTest() != logCount + 1)
    {
        throw new InvalidOperationException("明示検索時の検索履歴記録が正しくありません。");
    }
    Search(service, "'; DROP TABLE categories; --", null, SearchScopes.All, false, 1);
    if (service.ListCategories().Count == 0)
    {
        throw new InvalidOperationException("検索入力がSQLとして実行されました。");
    }

    var addedSynonym = service.SaveSynonymGroup(null, "端末", ["デバイス"], false);
    if (!addedSynonym.Terms.Contains("デバイス", StringComparer.Ordinal))
    {
        throw new InvalidOperationException("同義語グループを保存できませんでした。");
    }
    ExpectProblem(() => service.SaveSynonymGroup(null, "競合", ["PC"], false), "SYN-003");
    service.DeleteSynonymGroup(addedSynonym.Id);
}

static void VerifyArticleViewAndHistory(string root)
{
    using var database = KnowledgeDatabase.OpenSynthetic(root);
    var fixture = database.SeedSyntheticCategorySearchFixture();
    var authentication = new AuthenticationService(database);
    var articleService = new ArticleViewService(database, authentication);
    var searchService = new ClassificationSearchService(database, authentication);

    ExpectProblem(() => articleService.GetArticle(fixture.PublishedMeshArticleId), "AUTH-002");
    ExpectProblem(() => articleService.RecordArticleView(fixture.PublishedMeshArticleId, null), "AUTH-002");
    authentication.Login("0000", string.Empty);

    var beforeRead = database.ViewLogCountForTest();
    var mesh = articleService.GetArticle(fixture.PublishedMeshArticleId);
    if (database.ViewLogCountForTest() != beforeRead ||
        mesh.CategoryId != fixture.WifiCategoryId ||
        mesh.CategoryName != "Wi-Fi" ||
        mesh.Title != "メッシュWi-FiでZoomが切れる原因と対処は？" ||
        mesh.Status != "published" || mesh.Importance != 3 || mesh.IsHidden ||
        mesh.CreatedByUserId != KnowledgeDatabase.InitialAdminUserId ||
        mesh.CreatedByDisplayName != "初期管理者" ||
        mesh.UpdatedByUserId != KnowledgeDatabase.InitialAdminUserId ||
        mesh.UpdatedByDisplayName != "初期管理者" ||
        mesh.DeletedAt is not null || mesh.MergeInfo is not null ||
        mesh.Attachments.Count != 0 ||
        !mesh.Symptoms.SequenceEqual(["接続が切れる"]) ||
        !mesh.Causes.SequenceEqual(["ローミング切替"]) ||
        !mesh.Targets.SequenceEqual(["PC Zoom"]) ||
        !mesh.ErrorCodes.SequenceEqual(["NET-100"]) ||
        !mesh.Procedures.SequenceEqual(["1. 合成手順を確認します。"]) ||
        !mesh.Cautions.SequenceEqual(["合成データ以外では実施しません。"]) ||
        !mesh.Tags.SequenceEqual(["Zoom"]) ||
        !mesh.SearchTerms.SequenceEqual(["メッシュwifi 会議"]) ||
        mesh.RelatedArticles.Count != 1 ||
        mesh.RelatedArticles[0].Id != fixture.RelatedArticleId)
    {
        throw new InvalidOperationException("FAQ詳細の基本項目、監査表示、検索情報、タグ、関連FAQが正しくありません。");
    }
    if (mesh.BodyDoc.GetProperty("type").GetString() != "doc" ||
        mesh.BodyDoc.GetProperty("content")[0].GetProperty("type").GetString() != "paragraph" ||
        mesh.BodyDoc.GetProperty("content")[0].GetProperty("content")[0].GetProperty("text").GetString() != mesh.BodyPlainText)
    {
        throw new InvalidOperationException("Tiptap JSONとプレーン本文の読取互換性が正しくありません。");
    }

    var merged = articleService.GetArticle(fixture.MergedArticleId);
    if (merged.MergeInfo?.TargetArticleId != fixture.PublishedMeshArticleId ||
        merged.MergeInfo.TargetArticleTitle != mesh.Title ||
        string.IsNullOrWhiteSpace(merged.MergeInfo.MergedAt))
    {
        throw new InvalidOperationException("統合済みFAQの統合先情報が正しくありません。");
    }
    var deleted = articleService.GetArticle(fixture.DeletedArticleId);
    if (deleted.DeletedAt is null)
    {
        throw new InvalidOperationException("削除済みFAQの直接表示情報が正しくありません。");
    }

    var tenArticleIds = new[] { fixture.PublishedMeshArticleId }
        .Concat(Enumerable.Range(1, 9).Select(index => $"72000000-0000-7000-8000-{index:000000000000}"))
        .ToArray();
    var tenDetails = tenArticleIds.Select(articleService.GetArticle).ToArray();
    if (tenDetails.Length != 10 || tenDetails.Select(article => article.Id).Distinct(StringComparer.Ordinal).Count() != 10)
    {
        throw new InvalidOperationException("異なるFAQを最大10件分読み取れませんでした。");
    }

    var searchLogId = searchService.RecordSearchLog(
        "Zoom", fixture.WifiCategoryId, SearchScopes.Current, 1);
    articleService.RecordArticleView(fixture.PublishedMeshArticleId, searchLogId);
    var recorded = database.LastViewLogForTest();
    if (database.ViewLogCountForTest() != beforeRead + 1 ||
        recorded.ArticleId != fixture.PublishedMeshArticleId ||
        recorded.SourceSearchLogId != searchLogId)
    {
        throw new InvalidOperationException("FAQ詳細取得成功後の閲覧履歴と遷移元検索履歴が正しくありません。");
    }
    ExpectProblem(() => articleService.GetArticle("'; DROP TABLE articles; --"), "ART-004");
    ExpectProblem(() => articleService.RecordArticleView("missing-article", null), "ART-004");
    ExpectProblem(
        () => articleService.RecordArticleView(fixture.PublishedMeshArticleId, "missing-search-log"),
        "LOG-001");
    if (articleService.GetArticle(fixture.PublishedMeshArticleId).Id != fixture.PublishedMeshArticleId)
    {
        throw new InvalidOperationException("FAQ ID入力がSQLとして実行されました。");
    }
}

static void VerifyHistoryBrowsingAndDeletion(string root)
{
    using var database = KnowledgeDatabase.OpenSynthetic(root);
    var articleFixture = database.SeedSyntheticCategorySearchFixture();
    var fixture = database.SeedSyntheticHistoryFixture(
        articleFixture.PublishedMeshArticleId,
        articleFixture.WifiCategoryId);
    var authentication = new AuthenticationService(database);
    var history = new HistoryService(database, authentication);

    var allSearch = new ListSearchLogsInput(string.Empty, null, null, false, 1);
    var allViews = new ListViewLogsInput(string.Empty, null, null, 1);
    ExpectProblem(() => history.ListSearchLogs(allSearch), "AUTH-002");
    ExpectProblem(() => history.ListViewLogs(allViews), "AUTH-002");
    ExpectProblem(
        () => history.DeleteHistory(new DeleteHistoryInput(HistoryTargets.Search, null, null, true)),
        "AUTH-002");
    authentication.Login("0000", string.Empty);

    var firstPage = history.ListSearchLogs(allSearch);
    var lastPage = history.ListSearchLogs(allSearch with { Page = 999 });
    if (firstPage.Total != 54 || firstPage.Page != 1 || firstPage.PageSize != 50 || firstPage.Items.Count != 50 ||
        lastPage.Page != 2 || lastPage.Items.Count != 4)
    {
        throw new InvalidOperationException("検索履歴の50件ページングまたは最終ページ補正が正しくありません。");
    }

    var normalized = history.ListSearchLogs(allSearch with { Query = "ｚｏｏｍ" });
    var zeroResults = history.ListSearchLogs(allSearch with { ZeroResultsOnly = true });
    var oneDay = history.ListSearchLogs(allSearch with { StartDate = "2026-08-30", EndDate = "2026-08-30" });
    if (normalized.Total != 2 ||
        zeroResults.Total != 1 || zeroResults.Items.Single().Id != fixture.ZeroSearchId ||
        oneDay.Total != 1 || oneDay.Items.Single().Id != fixture.ZeroSearchId)
    {
        throw new InvalidOperationException("検索履歴のNFKC、0件のみ、日付範囲の絞り込みが正しくありません。");
    }
    if (history.ListSearchLogs(allSearch with { Query = "%_" }).Total != 0 ||
        history.ListSearchLogs(allSearch with { Query = "'; DROP TABLE search_logs; --" }).Total != 0 ||
        history.ListSearchLogs(allSearch).Total != 54)
    {
        throw new InvalidOperationException("検索履歴のLIKEエスケープまたはSQLパラメーター化が正しくありません。");
    }
    ExpectProblem(
        () => history.ListSearchLogs(allSearch with { Query = new string('長', 501) }),
        "LOG-001");
    ExpectProblem(
        () => history.ListSearchLogs(allSearch with { StartDate = "2026/08/30" }),
        "LOG-002");
    ExpectProblem(
        () => history.ListSearchLogs(allSearch with { StartDate = "2026-08-31", EndDate = "2026-08-30" }),
        "LOG-002");

    var viewPage = history.ListViewLogs(allViews);
    var titleFiltered = history.ListViewLogs(allViews with { Query = "メッシュ" });
    var sourceFiltered = history.ListViewLogs(allViews with { Query = "ｚｏｏｍ 失敗" });
    if (viewPage.Total != 2 || viewPage.Items.Count != 2 ||
        titleFiltered.Total != 2 ||
        sourceFiltered.Total != 1 || sourceFiltered.Items.Single().Id != fixture.ViewFromSearchId ||
        viewPage.Items.Single(item => item.Id == fixture.DirectViewId).SourceQueryText is not null)
    {
        throw new InvalidOperationException("閲覧履歴のFAQタイトル・遷移元検索語の絞り込みが正しくありません。");
    }

    ExpectProblem(
        () => history.DeleteHistory(new DeleteHistoryInput("invalid", null, null, true)),
        "LOG-002");
    ExpectProblem(
        () => history.DeleteHistory(new DeleteHistoryInput(HistoryTargets.Search, null, null, false)),
        "LOG-002");
    ExpectProblem(
        () => history.DeleteHistory(new DeleteHistoryInput(
            HistoryTargets.Search, "2026-08-31", "2026-08-30", false)),
        "LOG-002");

    var deletedSearch = history.DeleteHistory(new DeleteHistoryInput(
        HistoryTargets.Search, "2026-08-30", "2026-08-30", false));
    var viewAfterSourceDeletion = history.ListViewLogs(allViews);
    if (deletedSearch != 1 || history.ListSearchLogs(allSearch).Total != 53 ||
        viewAfterSourceDeletion.Total != 2 ||
        viewAfterSourceDeletion.Items.Single(item => item.Id == fixture.ViewFromSearchId).SourceQueryText is not null)
    {
        throw new InvalidOperationException("検索履歴の期間削除または閲覧履歴の独立保持が正しくありません。");
    }

    var deletedView = history.DeleteHistory(new DeleteHistoryInput(
        HistoryTargets.View, "2026-08-30", "2026-08-30", false));
    var deletedSearchAll = history.DeleteHistory(new DeleteHistoryInput(
        HistoryTargets.Search, null, null, true));
    var deletedViewAll = history.DeleteHistory(new DeleteHistoryInput(
        HistoryTargets.View, null, null, true));
    if (deletedView != 1 || deletedSearchAll != 53 || deletedViewAll != 1 ||
        history.ListSearchLogs(allSearch).Total != 0 || history.ListViewLogs(allViews).Total != 0)
    {
        throw new InvalidOperationException("検索・閲覧履歴の期間削除または全件削除が正しくありません。");
    }

    authentication.Logout();
    ExpectProblem(() => history.ListSearchLogs(allSearch), "AUTH-002");
}

static void VerifyArticleEditingAndAudit(string root)
{
    using var database = KnowledgeDatabase.OpenSynthetic(root);
    var fixture = database.SeedSyntheticCategorySearchFixture();
    var authentication = new AuthenticationService(database);
    var attachments = new ArticleAttachmentService(root);
    var editing = new ArticleEditingService(database, authentication, attachments);
    var search = new ClassificationSearchService(database, authentication);

    var draftInput = ArticleInput(
        null,
        fixture.WifiCategoryId,
        "合成編集下書きFAQ",
        string.Empty,
        RichDocument(),
        ArticleStatuses.Draft,
        2,
        "2026-09-06",
        null,
        false,
        ["Zoom"]);
    ExpectProblem(() => editing.ListTags(), "AUTH-002");
    ExpectProblem(() => editing.SaveArticle(draftInput), "AUTH-002");
    authentication.Login("0000", string.Empty);

    var tags = editing.ListTags();
    if (tags.Count != 1 || tags[0].Name != "Zoom" || tags[0].UsageCount != 1)
    {
        throw new InvalidOperationException("FAQ編集で使用するタグマスターを読み取れませんでした。");
    }

    var created = editing.SaveArticle(draftInput);
    if (created.Status != ArticleStatuses.Draft || created.Importance != 2 ||
        created.NewBadgeUntil != "2026-09-06" || created.UpdatedBadgeUntil is not null ||
        created.CreatedByUserId != KnowledgeDatabase.InitialAdminUserId ||
        created.UpdatedByUserId != KnowledgeDatabase.InitialAdminUserId ||
        !created.Tags.SequenceEqual(["Zoom"]) ||
        !created.BodyPlainText.Contains("確認手順 PCを再起動します", StringComparison.Ordinal) ||
        !database.ManagementCodeForTest(created.Id).StartsWith("FAQ-", StringComparison.Ordinal) ||
        database.SearchDocumentCountForTest(created.Id) != 1 || database.FtsCountForArticleForTest(created.Id) != 1)
    {
        throw new InvalidOperationException("FAQ下書きの作成、本文、タグ、監査、管理IDまたは検索索引が正しくありません。");
    }
    if (!Search(search, "合成編集", null, SearchScopes.All, true, 1).Items.Any(article => article.Id == created.Id))
    {
        throw new InvalidOperationException("作成した下書きFAQが下書き込み検索へ反映されませんでした。");
    }

    ExpectProblem(
        () => editing.SaveArticle(ArticleInput(
            null, fixture.WifiCategoryId, "概要なし公開", string.Empty, RichDocument(),
            ArticleStatuses.Published, 1, null, null, false, [])),
        "ART-001");
    ExpectProblem(
        () => editing.SaveArticle(ArticleInput(
            null, fixture.WifiCategoryId, "本文なし公開", "概要", EmptyDocument(),
            ArticleStatuses.Published, 1, null, null, false, [])),
        "ART-001");
    ExpectProblem(
        () => editing.SaveArticle(ArticleInput(
            null, fixture.WifiCategoryId, "危険URL", "概要", JavaScriptLinkDocument(),
            ArticleStatuses.Draft, 1, null, null, false, [])),
        "ART-005");
    ExpectProblem(
        () => editing.SaveArticle(ArticleInput(
            null, fixture.WifiCategoryId, "画像保留", "概要", ImageDocument(),
            ArticleStatuses.Draft, 1, null, null, false, [])),
        "ATT-005");
    ExpectProblem(
        () => editing.SaveArticle(ArticleInput(
            null, fixture.WifiCategoryId, "存在しないタグ", "概要", RichDocument(),
            ArticleStatuses.Draft, 1, null, null, false, ["未登録タグ"])),
        "TAG-004");
    if (editing.ListArticlesForManagement(new ManagementArticlesInput(
            "存在しないタグ", null, null, false, 1)).Total != 0)
    {
        throw new InvalidOperationException("タグ検証失敗時にFAQが部分保存されました。");
    }

    VerifyRichTextAttachments(root, fixture, editing, attachments);

    var sourceBefore = database.GetArticle(fixture.PublishedMeshArticleId);
    var secondUser = authentication.CreateUser(
        "editor", "合成編集担当", "synthetic-editor-password", UserRoles.User);
    authentication.Logout();
    authentication.Login("editor", "synthetic-editor-password");
    var updated = editing.SaveArticle(new SaveArticleInput(
        sourceBefore.Id,
        sourceBefore.CategoryId,
        "メッシュWi-Fi FAQ（C#編集済み）",
        "C#版で概要を更新しました。",
        RichDocument(),
        ArticleStatuses.Published,
        3,
        null,
        "2026-09-07",
        false,
        ["変更してはいけない症状"],
        ["変更してはいけない原因"],
        ["変更してはいけない対象"],
        ["CHANGE-001"],
        ["変更してはいけない手順"],
        ["変更してはいけない注意"],
        ["Zoom"],
        ["変更してはいけない検索語"],
        []));
    if (updated.CreatedAt != sourceBefore.CreatedAt ||
        updated.CreatedByUserId != sourceBefore.CreatedByUserId ||
        updated.UpdatedByUserId != secondUser.Id ||
        updated.UpdatedByDisplayName != secondUser.DisplayName ||
        !updated.Symptoms.SequenceEqual(sourceBefore.Symptoms) ||
        !updated.Causes.SequenceEqual(sourceBefore.Causes) ||
        !updated.Targets.SequenceEqual(sourceBefore.Targets) ||
        !updated.ErrorCodes.SequenceEqual(sourceBefore.ErrorCodes) ||
        !updated.Procedures.SequenceEqual(sourceBefore.Procedures) ||
        !updated.Cautions.SequenceEqual(sourceBefore.Cautions) ||
        !updated.SearchTerms.SequenceEqual(sourceBefore.SearchTerms) ||
        !updated.RelatedArticles.Select(article => article.Id).SequenceEqual(
            sourceBefore.RelatedArticles.Select(article => article.Id)))
    {
        throw new InvalidOperationException("FAQ更新で作成監査、旧検索情報または既存関連FAQを保持できませんでした。");
    }
    if (!Search(search, "c#編集済み", null, SearchScopes.All, false, 1).Items.Any(article => article.Id == updated.Id))
    {
        throw new InvalidOperationException("FAQ更新後のタイトルが検索索引へ反映されませんでした。");
    }

    var titleBeforeInvalidTag = updated.Title;
    ExpectProblem(
        () => editing.SaveArticle(ArticleInput(
            updated.Id, updated.CategoryId, "ロールバックされるタイトル", updated.Summary,
            updated.BodyDoc, updated.Status, updated.Importance, updated.NewBadgeUntil,
            updated.UpdatedBadgeUntil, updated.IsHidden, ["未登録タグ"])),
        "TAG-004");
    if (database.GetArticle(updated.Id).Title != titleBeforeInvalidTag)
    {
        throw new InvalidOperationException("FAQ更新失敗時にトランザクションがロールバックされませんでした。");
    }

    var copy = editing.DuplicateArticle(updated.Id);
    if (copy.Id == updated.Id || copy.Status != ArticleStatuses.Draft ||
        copy.Title != "メッシュWi-Fi FAQ（C#編集済み）（コピー）" ||
        copy.NewBadgeUntil is not null || copy.UpdatedBadgeUntil is not null ||
        copy.IsHidden != updated.IsHidden || copy.CreatedByUserId != secondUser.Id ||
        copy.UpdatedByUserId != secondUser.Id ||
        !copy.Symptoms.SequenceEqual(updated.Symptoms) ||
        !copy.Tags.SequenceEqual(updated.Tags) ||
        !copy.RelatedArticles.Select(article => article.Id).SequenceEqual(
            updated.RelatedArticles.Select(article => article.Id)) ||
        database.FtsCountForArticleForTest(copy.Id) != 1)
    {
        throw new InvalidOperationException("FAQ複製の独立ID、下書き化、監査、旧検索情報、タグまたは関連FAQが正しくありません。");
    }

    var management = editing.ListArticlesForManagement(new ManagementArticlesInput(
        "C#編集済み", fixture.NetworkCategoryId, null, false, 1));
    var managedSource = management.Items.SingleOrDefault(article => article.Id == updated.Id);
    if (management.Total < 2 || managedSource is null ||
        managedSource.CreatedByDisplayName != "初期管理者" ||
        managedSource.UpdatedByDisplayName != secondUser.DisplayName)
    {
        throw new InvalidOperationException("FAQ管理一覧の検索、配下分類または作成者・更新者表示が正しくありません。");
    }

    editing.DeleteArticle(copy.Id);
    if (database.GetArticle(copy.Id).DeletedAt is null || database.FtsCountForArticleForTest(copy.Id) != 0 ||
        Search(search, "c#編集済み", null, SearchScopes.All, true, 1).Items.Any(article => article.Id == copy.Id))
    {
        throw new InvalidOperationException("FAQ論理削除後の保持または検索除外が正しくありません。");
    }
    var deletedPage = editing.ListArticlesForManagement(new ManagementArticlesInput(
        "C#編集済み", null, null, true, 1));
    if (!deletedPage.Items.Any(article => article.Id == copy.Id && article.DeletedAt is not null))
    {
        throw new InvalidOperationException("削除済みFAQを管理一覧で確認できませんでした。");
    }
    var restored = editing.RestoreArticle(copy.Id);
    if (restored.DeletedAt is not null || restored.UpdatedByUserId != secondUser.Id ||
        database.FtsCountForArticleForTest(copy.Id) != 1 ||
        !Search(search, "c#編集済み", null, SearchScopes.All, true, 1).Items.Any(article => article.Id == copy.Id))
    {
        throw new InvalidOperationException("FAQ復元後の監査または検索索引再構築が正しくありません。");
    }

    ExpectProblem(() => editing.DeleteArticle(fixture.PublishedMeshArticleId), "ART-009");
    ExpectProblem(() => editing.DeleteArticle("'; DROP TABLE articles; --"), "ART-004");
    editing.ListArticlesForManagement(new ManagementArticlesInput("'; DROP TABLE articles; --", null, null, false, 1));
    if (editing.ListArticlesForManagement(new ManagementArticlesInput(string.Empty, null, null, false, 1)).Total == 0)
    {
        throw new InvalidOperationException("FAQ管理入力がSQLとして実行されました。");
    }
}

static void VerifyCsvAndJsonTransfer(string root, string targetRoot, string restartRoot, string filesRoot)
{
    Directory.CreateDirectory(filesRoot);
    using var database = KnowledgeDatabase.OpenSynthetic(root);
    var fixture = database.SeedSyntheticCategorySearchFixture();
    var authentication = new AuthenticationService(database);
    authentication.Login("0000", string.Empty);
    var attachments = new ArticleAttachmentService(root);
    var editing = new ArticleEditingService(database, authentication, attachments);
    var viewing = new ArticleViewService(database, authentication, attachments);
    var sourceArticle = viewing.GetArticle(fixture.PublishedMeshArticleId);
    editing.SaveArticle(new SaveArticleInput(
        sourceArticle.Id,
        sourceArticle.CategoryId,
        "CSV入出力合成FAQ",
        "CSV変更前の合成概要",
        TransferRichDocument(),
        ArticleStatuses.Published,
        sourceArticle.Importance,
        sourceArticle.NewBadgeUntil,
        sourceArticle.UpdatedBadgeUntil,
        sourceArticle.IsHidden,
        sourceArticle.Symptoms,
        sourceArticle.Causes,
        sourceArticle.Targets,
        sourceArticle.ErrorCodes,
        sourceArticle.Procedures,
        sourceArticle.Cautions,
        sourceArticle.Tags,
        sourceArticle.SearchTerms,
        sourceArticle.RelatedArticles.Select(article => article.Id).ToArray()));
    var transfer = new TransferService(database, authentication, root);
    var csvPath = Path.Combine(filesRoot, "synthetic.knowledge-faq.csv");
    var csvExport = transfer.ExportFaqCsv(new ExportFaqCsvInput(csvPath));
    var csvBytes = File.ReadAllBytes(csvPath);
    if (csvExport.ExportedCount <= 0 || csvBytes.Length < 3 ||
        csvBytes[0] != 0xEF || csvBytes[1] != 0xBB || csvBytes[2] != 0xBF ||
        csvBytes.Select((value, index) => (value, index)).Any(item =>
            item.value == (byte)'\n' && (item.index == 0 || csvBytes[item.index - 1] != (byte)'\r')))
    {
        throw new InvalidOperationException("CSVのUTF-8 BOM、CRLFまたは書出件数が正しくありません。");
    }
    var csvText = File.ReadAllText(csvPath, new UTF8Encoding(false, true));
    if (!csvText.StartsWith("形式バージョン,FAQ管理ID,分類管理ID,分類パス", StringComparison.Ordinal) ||
        csvText.Contains(fixture.PublishedMeshArticleId, StringComparison.Ordinal) ||
        csvText.Contains(database.PasswordHashForTest(KnowledgeDatabase.InitialAdminUserId), StringComparison.Ordinal))
    {
        throw new InvalidOperationException("CSVの固定見出しまたは内部ID・認証情報の除外が正しくありません。");
    }

    var originalPreview = transfer.InspectFaqCsv(csvPath);
    if (originalPreview.ErrorCount != 0 || originalPreview.UnchangedCount != originalPreview.TotalRows)
    {
        throw new InvalidOperationException("書出直後CSVのプレビューが変更なしになりませんでした。");
    }
    csvText = csvText.Replace("CSV変更前の合成概要", "CSV変更後の合成概要", StringComparison.Ordinal);
    File.WriteAllText(csvPath, csvText, new UTF8Encoding(true));
    ExpectProblem(
        () => transfer.ImportFaqCsv(new ImportFaqCsvInput(csvPath, originalPreview.FileSha256)),
        "CSV-007");
    var summaryPreview = transfer.InspectFaqCsv(csvPath);
    if (summaryPreview.UpdateCount != 1 || summaryPreview.BodyReplacementCount != 0)
    {
        throw new InvalidOperationException("CSV概要変更の更新件数または本文構造保持判定が正しくありません。");
    }
    var summaryResult = transfer.ImportFaqCsv(new ImportFaqCsvInput(csvPath, summaryPreview.FileSha256));
    VerifySafetyBackup(summaryResult.SafetyBackupPath);
    var summaryUpdated = viewing.GetArticle(fixture.PublishedMeshArticleId);
    if (summaryUpdated.Summary != "CSV変更後の合成概要" ||
        summaryUpdated.BodyDoc.GetProperty("content")[0].GetProperty("type").GetString() != "table")
    {
        throw new InvalidOperationException("CSVで概要だけを変更したときに構造化本文を保持できませんでした。");
    }

    transfer.ExportFaqCsv(new ExportFaqCsvInput(csvPath));
    csvText = File.ReadAllText(csvPath, new UTF8Encoding(false, true))
        .Replace("CSV構造保持本文", "CSV段落置換本文", StringComparison.Ordinal);
    File.WriteAllText(csvPath, csvText, new UTF8Encoding(true));
    var bodyPreview = transfer.InspectFaqCsv(csvPath);
    if (bodyPreview.BodyReplacementCount != 1 || bodyPreview.UpdateCount != 1)
    {
        throw new InvalidOperationException("CSV回答変更の本文置換判定が正しくありません。");
    }
    transfer.ImportFaqCsv(new ImportFaqCsvInput(csvPath, bodyPreview.FileSha256));
    var bodyUpdated = viewing.GetArticle(fixture.PublishedMeshArticleId);
    if (bodyUpdated.BodyPlainText != "CSV段落置換本文" ||
        bodyUpdated.BodyDoc.GetProperty("content")[0].GetProperty("type").GetString() != "paragraph")
    {
        throw new InvalidOperationException("CSV回答変更を安全なプレーンテキスト段落へ置換できませんでした。");
    }

    transfer.ExportFaqCsv(new ExportFaqCsvInput(csvPath));
    var lines = File.ReadAllText(csvPath, new UTF8Encoding(false, true))
        .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
        .ToList();
    var templateLine = lines.Single(line => line.Contains("CSV入出力合成FAQ", StringComparison.Ordinal));
    var newCells = ParseCsvLineForCheck(templateLine).ToArray();
    if (newCells.Length != 17)
    {
        throw new InvalidOperationException("CSVの固定17列を読み取れませんでした。");
    }
    newCells[1] = string.Empty;
    newCells[4] = "=CSV新規合成FAQ";
    newCells[5] = "CSV新規行の概要";
    newCells[6] = "CSV新規行の回答";
    newCells[7] = string.Empty;
    newCells[15] = string.Empty;
    newCells[16] = string.Empty;
    lines.Add(string.Join(',', newCells.Select(EncodeCsvCellForCheck)));
    File.WriteAllText(csvPath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));
    var createPreview = transfer.InspectFaqCsv(csvPath);
    if (createPreview.CreateCount != 1 || createPreview.ErrorCount != 0)
    {
        throw new InvalidOperationException("FAQ管理ID空欄のCSV行を新規FAQとして判定できませんでした。");
    }
    transfer.ImportFaqCsv(new ImportFaqCsvInput(csvPath, createPreview.FileSha256));
    transfer.ExportFaqCsv(new ExportFaqCsvInput(csvPath));
    if (!File.ReadAllText(csvPath, new UTF8Encoding(false, true)).Contains("'=CSV新規合成FAQ", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("CSVのExcel数式先頭文字を無害化できませんでした。");
    }

    var repositoryCsvPath = Path.Combine(Directory.GetCurrentDirectory(), "unsafe.knowledge-faq.csv");
    ExpectProblem(() => transfer.ExportFaqCsv(new ExportFaqCsvInput(repositoryCsvPath)), "CSV-006");
    var badCsvPath = Path.Combine(filesRoot, "bad.knowledge-faq.csv");
    File.WriteAllText(badCsvPath, "wrong,header\r\n", new UTF8Encoding(true));
    ExpectProblem(() => transfer.InspectFaqCsv(badCsvPath), "CSV-002");

    var jsonPath = Path.Combine(filesRoot, "synthetic.knowledge-export.json");
    var jsonExport = transfer.ExportJson(new ExportJsonInput(jsonPath));
    var jsonBytes = File.ReadAllBytes(jsonPath);
    var jsonText = new UTF8Encoding(false, true).GetString(jsonBytes);
    if (jsonExport.Counts.Articles <= 0 ||
        jsonBytes.Length >= 3 && jsonBytes[0] == 0xEF && jsonBytes[1] == 0xBB && jsonBytes[2] == 0xBF ||
        jsonText.Contains(database.PasswordHashForTest(KnowledgeDatabase.InitialAdminUserId), StringComparison.Ordinal) ||
        jsonText.Contains("searchLogs", StringComparison.Ordinal) || jsonText.Contains("viewLogs", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("JSONのUTF-8 no BOM、件数または除外項目が正しくありません。");
    }
    var sameDatabasePreview = transfer.InspectJson(jsonPath);
    var totalEntities = jsonExport.Counts.Categories + jsonExport.Counts.Articles + jsonExport.Counts.Tags +
        jsonExport.Counts.SynonymGroups + jsonExport.Counts.Relations + jsonExport.Counts.MergeRelations;
    if (sameDatabasePreview.ErrorCount != 0 || sameDatabasePreview.UnchangedCount != totalEntities)
    {
        throw new InvalidOperationException("書出直後JSONの厳格検査または変更なし件数が正しくありません。");
    }

    using (var restartDatabase = KnowledgeDatabase.OpenSynthetic(restartRoot))
    {
        var restartFixture = restartDatabase.SeedSyntheticCategorySearchFixture();
        var restartAuthentication = new AuthenticationService(restartDatabase);
        restartAuthentication.Login("0000", string.Empty);
        var restartTransfer = new TransferService(restartDatabase, restartAuthentication, restartRoot);
        var restartPreview = restartTransfer.InspectJson(jsonPath);
        if (restartPreview.ErrorCount != 0)
        {
            throw new InvalidOperationException(
                $"再起動相当の合成DBで正常JSONをプレビューできませんでした: {string.Join(" / ", restartPreview.Errors)}");
        }
        var restartImport = restartTransfer.ImportJson(
            new ImportJsonInput(jsonPath, restartPreview.FileSha256));
        VerifySafetyBackup(restartImport.SafetyBackupPath);
        var restartViewing = new ArticleViewService(
            restartDatabase,
            restartAuthentication,
            new ArticleAttachmentService(restartRoot));
        if (restartViewing.GetArticle(restartFixture.PublishedMeshArticleId).Title != "CSV入出力合成FAQ")
        {
            throw new InvalidOperationException("再起動相当の合成DBへJSON更新を反映できませんでした。");
        }
    }

    using var targetDatabase = KnowledgeDatabase.OpenSynthetic(targetRoot);
    var targetAuthentication = new AuthenticationService(targetDatabase);
    targetAuthentication.Login("0000", string.Empty);
    var targetTransfer = new TransferService(targetDatabase, targetAuthentication, targetRoot);
    var targetPreview = targetTransfer.InspectJson(jsonPath);
    if (targetPreview.ErrorCount != 0 || targetPreview.CreateCount <= 0)
    {
        throw new InvalidOperationException("空の合成DBに対するJSON新規プレビューが正しくありません。");
    }
    var jsonImport = targetTransfer.ImportJson(new ImportJsonInput(jsonPath, targetPreview.FileSha256));
    VerifySafetyBackup(jsonImport.SafetyBackupPath);
    var targetViewing = new ArticleViewService(
        targetDatabase,
        targetAuthentication,
        new ArticleAttachmentService(targetRoot));
    var importedArticle = targetViewing.GetArticle(fixture.PublishedMeshArticleId);
    if (importedArticle.Title != "CSV入出力合成FAQ" || importedArticle.BodyPlainText != "CSV段落置換本文")
    {
        throw new InvalidOperationException("JSONでFAQ本文と構造を別の合成DBへ移行できませんでした。");
    }
    var importedPreview = targetTransfer.InspectJson(jsonPath);
    if (importedPreview.ErrorCount != 0 || importedPreview.UpdateCount != 0)
    {
        throw new InvalidOperationException("JSON取込後の再検査が変更なしになりませんでした。");
    }

    var changedRoot = JsonNode.Parse(jsonText)!.AsObject();
    changedRoot["articles"]!.AsArray()[0]!["summary"] = "JSON更新後の概要";
    File.WriteAllText(jsonPath, changedRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    ExpectProblem(
        () => targetTransfer.ImportJson(new ImportJsonInput(jsonPath, importedPreview.FileSha256)),
        "JSON-007");
    var changedPreview = targetTransfer.InspectJson(jsonPath);
    if (changedPreview.ErrorCount != 0 || changedPreview.UpdateCount != 1)
    {
        throw new InvalidOperationException("JSON更新のプレビュー件数が正しくありません。");
    }
    targetTransfer.ImportJson(new ImportJsonInput(jsonPath, changedPreview.FileSha256));

    var dangerousPath = Path.Combine(filesRoot, "dangerous.knowledge-export.json");
    var dangerous = JsonNode.Parse(jsonText)!.AsObject();
    dangerous["articles"]!.AsArray()[0]!["bodyDoc"] = JsonNode.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"script\"}]}");
    File.WriteAllText(dangerousPath, dangerous.ToJsonString(), new UTF8Encoding(false));
    if (targetTransfer.InspectJson(dangerousPath).ErrorCount == 0)
    {
        throw new InvalidOperationException("JSON内の許可されていないTiptapノードを拒否できませんでした。");
    }
    var unknownPath = Path.Combine(filesRoot, "unknown.knowledge-export.json");
    var unknown = JsonNode.Parse(jsonText)!.AsObject();
    unknown["unexpected"] = true;
    File.WriteAllText(unknownPath, unknown.ToJsonString(), new UTF8Encoding(false));
    ExpectProblem(() => targetTransfer.InspectJson(unknownPath), "JSON-002");
    var versionPath = Path.Combine(filesRoot, "version.knowledge-export.json");
    var version = JsonNode.Parse(jsonText)!.AsObject();
    version["formatVersion"] = 99;
    File.WriteAllText(versionPath, version.ToJsonString(), new UTF8Encoding(false));
    ExpectProblem(() => targetTransfer.InspectJson(versionPath), "JSON-003");
    var datePath = Path.Combine(filesRoot, "invalid-date.knowledge-export.json");
    var invalidDate = JsonNode.Parse(jsonText)!.AsObject();
    invalidDate["exportedAt"] = "2026-08-30 12:00:00Z";
    File.WriteAllText(datePath, invalidDate.ToJsonString(), new UTF8Encoding(false));
    if (targetTransfer.InspectJson(datePath).ErrorCount == 0)
    {
        throw new InvalidOperationException("RFC 3339ではないJSON日時が受け入れられました。");
    }
    var repositoryJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "unsafe.knowledge-export.json");
    ExpectProblem(() => transfer.ExportJson(new ExportJsonInput(repositoryJsonPath)), "JSON-006");
}

static JsonElement TransferRichDocument() => ParseJson("""
{
  "type": "doc",
  "content": [
    {
      "type": "table",
      "content": [
        {
          "type": "tableRow",
          "content": [
            {
              "type": "tableCell",
              "content": [
                { "type": "paragraph", "content": [{ "type": "text", "text": "CSV構造保持本文" }] }
              ]
            }
          ]
        }
      ]
    }
  ]
}
""");

static IReadOnlyList<string> ParseCsvLineForCheck(string line)
{
    var cells = new List<string>();
    var value = new StringBuilder();
    var quoted = false;
    for (var index = 0; index < line.Length; index++)
    {
        var character = line[index];
        if (quoted && character == '"' && index + 1 < line.Length && line[index + 1] == '"')
        {
            value.Append('"');
            index++;
        }
        else if (character == '"')
        {
            quoted = !quoted;
        }
        else if (character == ',' && !quoted)
        {
            cells.Add(value.ToString());
            value.Clear();
        }
        else
        {
            value.Append(character);
        }
    }
    cells.Add(value.ToString());
    return cells;
}

static string EncodeCsvCellForCheck(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
    ? value
    : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

static void VerifySafetyBackup(string path)
{
    if (!File.Exists(path) || !path.EndsWith(".faqbackup", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("取込前安全バックアップが作成されていません。");
    }
    using var archive = ZipFile.OpenRead(path);
    if (archive.GetEntry("manifest.json") is null || archive.GetEntry("data/knowledge.db") is null)
    {
        throw new InvalidOperationException("取込前安全バックアップの検証情報またはDBが不足しています。");
    }
}

static void VerifyFullBackupAndRestore(string root, string filesRoot)
{
    Directory.CreateDirectory(filesRoot);
    using var database = KnowledgeDatabase.OpenSynthetic(root);
    var fixture = database.SeedSyntheticCategorySearchFixture();
    var authentication = new AuthenticationService(database);
    authentication.Login("0000", string.Empty);
    var attachments = new ArticleAttachmentService(root);
    var editing = new ArticleEditingService(database, authentication, attachments);
    var classification = new ClassificationSearchService(database, authentication);
    var settings = new SettingsService(database, authentication, root);
    var systemInfo = settings.GetSystemInfo();
    if (!string.Equals(systemInfo.DataRoot, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(systemInfo.DatabasePath, database.OpenInfo.DatabasePath, StringComparison.OrdinalIgnoreCase) ||
        !systemInfo.CodexCategoryCatalogPath.EndsWith(Path.Combine("codex-bridge", "categories.json"), StringComparison.Ordinal) ||
        !systemInfo.CodexInboxPath.EndsWith("codex-inbox", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("C#設定画面へ合成データルートの端末情報を返せませんでした。");
    }
    var defaults = settings.GetSettings();
    if (defaults.ColorTheme != ColorThemes.Green || !defaults.ShowTopCategoryInTitle || !defaults.ShowMascot)
    {
        throw new InvalidOperationException("C#表示設定の既定値が現行版と一致しませんでした。");
    }
    settings.SaveSettings(new AppSettings
    {
        ColorTheme = ColorThemes.Blue,
        ShowTopCategoryInTitle = false,
        ShowMascot = false
    });
    ExpectProblem(
        () => settings.SaveSettings(new AppSettings { ColorTheme = "unknown" }),
        "SET-002");

    const string restoredUserPassword = "Backup\\*Synthetic!";
    authentication.CreateUser("backup-user", "バックアップ合成利用者", restoredUserPassword, UserRoles.User);
    byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0];
    var staged = attachments.StageBytes("バックアップ合成.png", png);
    var article = editing.SaveArticle(ArticleInput(
        null,
        fixture.WifiCategoryId,
        "バックアップ復元合成FAQ",
        "画像とDBをまとめて復元する合成確認",
        ManagedImageDocument(staged.Id, "バックアップ復元合成画像"),
        ArticleStatuses.Published,
        3,
        null,
        null,
        false,
        ["Zoom"]));
    var attachmentPath = Path.Combine(root, "attachments", "articles", article.Id, $"{staged.Id}.png");
    if (!File.Exists(attachmentPath))
    {
        throw new InvalidOperationException("フルバックアップ試験用の添付画像を確定できませんでした。");
    }

    var manualsRoot = Path.Combine(root, "manuals");
    var settingsRoot = Path.Combine(root, "settings");
    Directory.CreateDirectory(manualsRoot);
    Directory.CreateDirectory(settingsRoot);
    var manualPath = Path.Combine(manualsRoot, "synthetic.txt");
    var settingPath = Path.Combine(settingsRoot, "synthetic.json");
    File.WriteAllText(manualPath, "復元前の合成manuals", new UTF8Encoding(false));
    File.WriteAllText(settingPath, "{\"value\":\"復元前\"}", new UTF8Encoding(false));

    var service = new BackupService(database, authentication, root);
    var overview = service.GetOverview();
    if (overview.EstimatedBytes <= 0 || overview.Counts.Attachments != 1 || overview.Counts.Manuals != 0 ||
        overview.DefaultDirectory is not null)
    {
        throw new InvalidOperationException("フルバックアップ概要の件数または初回保存先が正しくありません。");
    }
    authentication.Logout();
    authentication.Login("backup-user", restoredUserPassword);
    ExpectProblem(() => service.GetOverview(), "AUTH-003");
    authentication.Logout();
    authentication.Login("0000", string.Empty);

    var backupPath = Path.Combine(filesRoot, "KnowledgeApp_synthetic.faqbackup");
    var created = service.CreateFullBackup(new CreateFullBackupInput(
        backupPath,
        "KnowledgeApp_synthetic",
        false));
    if (!File.Exists(created.DestinationPath) || created.Counts.Attachments != 1 || created.TotalBytes <= 0)
    {
        throw new InvalidOperationException("フルバックアップの作成結果が正しくありません。");
    }
    ExpectProblem(
        () => service.CreateFullBackup(new CreateFullBackupInput(
            backupPath,
            "KnowledgeApp_synthetic",
            false)),
        "BK-002");
    service.CreateFullBackup(new CreateFullBackupInput(
        backupPath,
        "KnowledgeApp_synthetic",
        true));
    var remembered = service.GetOverview();
    if (!string.Equals(remembered.DefaultDirectory, Path.GetFullPath(filesRoot), StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("正常終了したバックアップ先を記憶できませんでした。");
    }

    var preview = service.InspectBackup(backupPath);
    if (preview.BackupFormatVersion != 1 || preview.SchemaVersion != MigrationCatalog.CurrentVersion ||
        preview.DisplayName != "KnowledgeApp_synthetic" || preview.Counts.Attachments != 1)
    {
        throw new InvalidOperationException("フルバックアップの事前検査結果が正しくありません。");
    }
    var brokenPath = Path.Combine(filesRoot, "broken.faqbackup");
    File.WriteAllText(brokenPath, "not-a-knowledgeapp-backup", new UTF8Encoding(false));
    ExpectProblem(() => service.InspectBackup(brokenPath), "BK-006");
    var repositoryBackup = Path.Combine(Directory.GetCurrentDirectory(), "unsafe.faqbackup");
    ExpectProblem(
        () => service.CreateFullBackup(new CreateFullBackupInput(
            repositoryBackup,
            "unsafe",
            false)),
        "BK-008");

    editing.SaveArticle(ArticleInput(
        article.Id,
        article.CategoryId,
        "復元後に消える変更",
        article.Summary,
        RichDocument(),
        article.Status,
        article.Importance,
        article.NewBadgeUntil,
        article.UpdatedBadgeUntil,
        article.IsHidden,
        article.Tags));
    var mutationCategory = classification.CreateCategory("復元後に消える分類", "復元前には存在しない", null);
    settings.SaveSettings(new AppSettings
    {
        ColorTheme = ColorThemes.Green,
        ShowTopCategoryInTitle = true,
        ShowMascot = true
    });
    File.WriteAllText(manualPath, "復元後に消えるmanuals変更", new UTF8Encoding(false));
    File.WriteAllText(settingPath, "{\"value\":\"復元後に消える\"}", new UTF8Encoding(false));
    if (File.Exists(attachmentPath))
    {
        throw new InvalidOperationException("本文から除去した添付画像が削除されませんでした。");
    }

    var restored = service.RestoreBackup(backupPath);
    if (authentication.GetCurrentUser() is not null || !File.Exists(restored.SafetyBackupPath) ||
        !File.Exists(backupPath))
    {
        throw new InvalidOperationException("復元後ログアウト、安全バックアップ、元バックアップ保持のいずれかが正しくありません。");
    }
    authentication.Login("0000", string.Empty);
    var restoredArticle = new ArticleViewService(database, authentication, attachments).GetArticle(article.Id);
    if (restoredArticle.Title != "バックアップ復元合成FAQ" || restoredArticle.Attachments.Count != 1 ||
        restoredArticle.Attachments[0].AltText != "バックアップ復元合成画像" ||
        !File.Exists(attachmentPath) || !File.ReadAllBytes(attachmentPath).SequenceEqual(png) ||
        File.ReadAllText(manualPath, Encoding.UTF8) != "復元前の合成manuals" ||
        File.ReadAllText(settingPath, Encoding.UTF8) != "{\"value\":\"復元前\"}" ||
        classification.ListCategories().Any(category => category.Id == mutationCategory.Id))
    {
        throw new InvalidOperationException("DB、分類、添付画像、manualsまたは設定をバックアップ時点へ復元できませんでした。");
    }
    var restoredSettings = settings.GetSettings();
    if (restoredSettings.ColorTheme != ColorThemes.Blue || restoredSettings.ShowTopCategoryInTitle ||
        restoredSettings.ShowMascot)
    {
        throw new InvalidOperationException("バックアップ時点の配色・タイトル分類表示・マスコット表示を復元できませんでした。");
    }
    var safetyPreview = service.InspectBackup(restored.SafetyBackupPath);
    if (safetyPreview.Counts.Attachments != 0)
    {
        throw new InvalidOperationException("復元前安全バックアップの内容件数が正しくありません。");
    }
    authentication.Logout();
    var restoredUser = authentication.Login("backup-user", restoredUserPassword);
    if (restoredUser.Role != UserRoles.User)
    {
        throw new InvalidOperationException("バックアップ時点の利用者認証を復元できませんでした。");
    }
}

static void VerifyRichTextAttachments(
    string root,
    SyntheticCategorySearchFixture fixture,
    ArticleEditingService editing,
    ArticleAttachmentService attachments)
{
    byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0];
    var staged = attachments.StageBytes("合成画面.fake", png);
    if (staged.MediaType != "image/png" || !staged.OriginalName.EndsWith(".png", StringComparison.Ordinal) ||
        !staged.AssetPath.StartsWith("https://knowledge-staged.local/", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("合成画像の形式検証または一時表示URLが正しくありません。");
    }
    var imageArticle = editing.SaveArticle(ArticleInput(
        null, fixture.WifiCategoryId, "合成画像付きFAQ", "画像確定の合成確認",
        ManagedImageDocument(staged.Id, "合成設定画面"), ArticleStatuses.Draft, 2,
        null, null, false, ["Zoom"]));
    var finalPath = Path.Combine(root, "attachments", "articles", imageArticle.Id, $"{staged.Id}.png");
    var stagedPath = Path.Combine(root, "temp", "staged-article-images", "files", $"{staged.Id}.png");
    if (imageArticle.Attachments.Count != 1 || imageArticle.Attachments[0].AltText != "合成設定画面" ||
        !imageArticle.Attachments[0].AssetPath.StartsWith("https://knowledge-attachments.local/", StringComparison.Ordinal) ||
        !File.Exists(finalPath) || File.Exists(stagedPath))
    {
        throw new InvalidOperationException("画像の保存時確定、DB記録、一時ファイル削除または安全な表示URLが正しくありません。");
    }

    var copy = editing.DuplicateArticle(imageArticle.Id);
    if (copy.Attachments.Count != 1 || copy.Attachments[0].Id == staged.Id ||
        copy.BodyDoc.GetRawText().Contains(staged.Id, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("添付付きFAQの複製で画像IDを独立化できませんでした。");
    }
    var copyPath = Path.Combine(root, "attachments", "articles", copy.Id, $"{copy.Attachments[0].Id}.png");
    if (!File.Exists(copyPath) || !File.ReadAllBytes(copyPath).SequenceEqual(png))
    {
        throw new InvalidOperationException("添付付きFAQの複製画像が独立ファイルとして保存されませんでした。");
    }
    editing.DeleteArticle(copy.Id);
    if (!File.Exists(copyPath) || editing.RestoreArticle(copy.Id).Attachments.Count != 1)
    {
        throw new InvalidOperationException("論理削除・復元で添付画像を保持できませんでした。");
    }

    var withoutImage = editing.SaveArticle(ArticleInput(
        imageArticle.Id, imageArticle.CategoryId, imageArticle.Title, imageArticle.Summary,
        RichDocument(), imageArticle.Status, imageArticle.Importance,
        imageArticle.NewBadgeUntil, imageArticle.UpdatedBadgeUntil, imageArticle.IsHidden, ["Zoom"]));
    if (withoutImage.Attachments.Count != 0 || File.Exists(finalPath) || !File.Exists(copyPath))
    {
        throw new InvalidOperationException("本文から外した画像だけを保存成功後に削除できませんでした。");
    }

    var rollbackStage = attachments.StageBytes("ロールバック.png", png);
    var rollbackFinal = Path.Combine(root, "attachments", "articles");
    ExpectProblem(
        () => editing.SaveArticle(ArticleInput(
            null, fixture.WifiCategoryId, "画像ロールバック", "概要",
            ManagedImageDocument(rollbackStage.Id, "ロールバック画像"), ArticleStatuses.Draft,
            1, null, null, false, ["未登録タグ"])),
        "TAG-004");
    if (Directory.EnumerateFiles(rollbackFinal, $"{rollbackStage.Id}.png", SearchOption.AllDirectories).Any())
    {
        throw new InvalidOperationException("DB保存失敗時に確定画像が残りました。");
    }
    attachments.DiscardStage(rollbackStage.Id);

    foreach (var sample in new[]
             {
                 (Name: "合成.jpg", Bytes: new byte[] { 0xff, 0xd8, 0xff, 0x00 }, Media: "image/jpeg"),
                 (Name: "合成.gif", Bytes: Encoding.ASCII.GetBytes("GIF89a0000"), Media: "image/gif"),
                 (Name: "合成.webp", Bytes: Encoding.ASCII.GetBytes("RIFF0000WEBP"), Media: "image/webp")
             })
    {
        var verified = attachments.StageBytes(sample.Name, sample.Bytes);
        if (verified.MediaType != sample.Media)
        {
            throw new InvalidOperationException($"{sample.Media}のマジックバイト判定が正しくありません。");
        }
        attachments.DiscardStage(verified.Id);
    }
    var base64Stage = attachments.StageBase64(new StageArticleImageBase64Input(
        "貼り付け.png", Convert.ToBase64String(png)));
    if (base64Stage.MediaType != "image/png")
    {
        throw new InvalidOperationException("WebView2貼り付け用Base64画像を検証できませんでした。");
    }
    attachments.DiscardStage(base64Stage.Id);

    ExpectProblem(() => attachments.StageBytes("偽画像.png", [1, 2, 3, 4]), "ATT-002");
    ExpectProblem(() => attachments.StageBytes("巨大画像.png", new byte[10 * 1024 * 1024 + 1]), "ATT-001");
    ExpectProblem(() => ExternalInteractionValidator.ValidateExternalUrl("file:///C:/Synthetic/manual.html"), "URL-001");
    ExpectProblem(() => ExternalInteractionValidator.ValidateExternalUrl("https://user@example.com/"), "URL-001");
    if (ExternalInteractionValidator.ValidateExternalUrl("https://example.com/help").Host != "example.com" ||
        ExternalInteractionValidator.ValidateClipboardText(@"C:\Synthetic Folder\手順.txt") != @"C:\Synthetic Folder\手順.txt")
    {
        throw new InvalidOperationException("HTTP/HTTPS URLまたはコピー専用テキストの安全境界が正しくありません。");
    }
}

static SaveArticleInput ArticleInput(
    string? id,
    string categoryId,
    string title,
    string summary,
    System.Text.Json.JsonElement bodyDoc,
    string status,
    long importance,
    string? newBadgeUntil,
    string? updatedBadgeUntil,
    bool isHidden,
    IReadOnlyList<string> tags) => new(
        id, categoryId, title, summary, bodyDoc, status, importance,
        newBadgeUntil, updatedBadgeUntil, isHidden,
        [], [], [], [], [], [], tags, [], []);

static System.Text.Json.JsonElement RichDocument() => ParseJson("""
    {
      "type": "doc",
      "content": [
        {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"確認手順"}]},
        {"type":"orderedList","attrs":{"start":1,"type":null},"content":[
          {"type":"listItem","content":[{"type":"paragraph","content":[
            {"type":"text","text":"PCを再起動します","marks":[{"type":"bold"}]}
          ]}]}
        ]},
        {"type":"paragraph","content":[{"type":"text","text":"参考","marks":[{
          "type":"link","attrs":{"href":"https://example.com/help","target":"_blank","rel":"noopener noreferrer nofollow","class":null,"title":null}
        }]}]},
        {"type":"table","content":[{"type":"tableRow","content":[
          {"type":"tableCell","attrs":{"colspan":1,"rowspan":1,"colwidth":null,"align":null},"content":[{"type":"paragraph","content":[{"type":"text","text":"合成表"}]}]}
        ]}]},
        {"type":"copyBlock","attrs":{"text":"C:\\\\Synthetic\\Manual.txt"}}
      ]
    }
    """);

static System.Text.Json.JsonElement EmptyDocument() =>
    ParseJson("""{"type":"doc","content":[{"type":"paragraph"}]}""");

static System.Text.Json.JsonElement JavaScriptLinkDocument() => ParseJson("""
    {"type":"doc","content":[{"type":"paragraph","content":[{
      "type":"text","text":"危険","marks":[{"type":"link","attrs":{"href":"javascript:alert(1)"}}]
    }]}]}
    """);

static System.Text.Json.JsonElement ImageDocument() => ManagedImageDocument(
    "00000000-0000-7000-8000-000000000001", "画像");

static System.Text.Json.JsonElement ManagedImageDocument(string attachmentId, string altText) => ParseJson($$$"""
    {"type":"doc","content":[{"type":"image","attrs":{
      "src":"knowledge-attachment:{{{attachmentId}}}",
      "alt":"{{{altText}}}","title":null,"attachmentId":"{{{attachmentId}}}"
    }}]}
    """);

static System.Text.Json.JsonElement ParseJson(string json)
{
    using var document = System.Text.Json.JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

static SearchArticlePage Search(
    ClassificationSearchService service,
    string query,
    string? categoryId,
    string scope,
    bool includeDrafts,
    long page,
    string sort = SearchSorts.UpdatedDesc) => service.SearchArticles(new SearchArticlesInput(
        query, categoryId, scope, includeDrafts, page, sort));

static int IndexOf(IReadOnlyList<CategorySummary> categories, string id)
{
    for (var index = 0; index < categories.Count; index++)
    {
        if (categories[index].Id == id)
        {
            return index;
        }
    }
    return -1;
}

static string CreateVersionOneDatabase(string root, bool includeArticle)
{
    var dataDirectory = Path.Combine(root, "data");
    Directory.CreateDirectory(dataDirectory);
    var databasePath = Path.Combine(dataDirectory, "knowledge.db");
    using var connection = Open(databasePath, SqliteOpenMode.ReadWriteCreate);
    Execute(connection, MigrationCatalog.Load(1));
    if (includeArticle)
    {
        using var transaction = connection.BeginTransaction();
        using (var category = connection.CreateCommand())
        {
            category.Transaction = transaction;
            category.CommandText = """
                INSERT INTO categories(id, parent_id, name, normalized_name, depth, sort_order, created_at, updated_at)
                VALUES ('category-v1', NULL, '合成分類', '合成分類', 1, 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
                """;
            category.ExecuteNonQuery();
        }
        using (var article = connection.CreateCommand())
        {
            article.Transaction = transaction;
            article.CommandText = """
                INSERT INTO articles(
                    id, category_id, title, normalized_title, summary, body_doc_json,
                    body_format_version, body_plain_text, procedure_text, caution_text,
                    status, importance, created_at, updated_at, deleted_at
                ) VALUES (
                    'article-v1', 'category-v1', '合成FAQ', '合成faq', '概要',
                    '{"type":"doc","content":[]}', 1, '本文', '', '',
                    'published', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', NULL
                )
                """;
            article.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    return databasePath;
}

static SqliteConnection Open(string path, SqliteOpenMode mode)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString());
    connection.Open();
    return connection;
}

static void Execute(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static long ScalarInt64(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
}

static string ScalarString(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar() as string ?? string.Empty;
}

static void ExpectProblem(Action action, string expectedCode)
{
    try
    {
        action();
    }
    catch (AppProblemException exception) when (exception.Problem.Code == expectedCode)
    {
        return;
    }
    throw new InvalidOperationException($"期待したエラーコード {expectedCode} が返りませんでした。");
}

static void DeleteSyntheticRoot(string root, string temporaryRoot)
{
    var resolvedRoot = Path.GetFullPath(root);
    var relative = Path.GetRelativePath(temporaryRoot, resolvedRoot);
    if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative) ||
        !Path.GetFileName(resolvedRoot).StartsWith("knowledgeapp-data-check-", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("合成DBテスト用フォルダの削除範囲が不正です。");
    }
    if (Directory.Exists(resolvedRoot))
    {
        Directory.Delete(resolvedRoot, recursive: true);
    }
}
