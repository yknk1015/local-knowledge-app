using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeApp.CSharp;
using KnowledgeApp.Data;

// No real data root, input path, mailbox or UI is accepted by this executable.
var roots = new List<string>();
var retainedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var passed = 0;
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 144 };
try
{
    if (args.Length != 0)
    {
        if (args is ["--write-codex-depth-fixture"])
        {
            var codexFixture = ManualCodexDepthFixture.Create(NewRoot);
            VerifyCodexDepthFixture(codexFixture);
            retainedRoots.Add(codexFixture.OutputRoot);
            Console.WriteLine($"SYNTHETIC_MANUAL_JSON={codexFixture.Path}");
            Console.WriteLine($"SHA256={codexFixture.Sha256}");
            Console.WriteLine($"MANUAL_FIXTURE_CHECKS={passed}; CATEGORY_COUNT=1; ARTICLE_COUNT=1; STATUS=published; CODES=CAT-00002/FAQ-00002");
            Console.WriteLine("Validated this emitted file in empty and synthetic C1-equivalent databases. No user data read; code 2 is not universally free.");
            return 0;
        }
        if (args is not ["--write-manual-fixture"]) throw new ArgumentException("Only --write-manual-fixture or --write-codex-depth-fixture is accepted; no data path is accepted.");
        var fixture = ManualLongDocumentFixture.CreateRetest(NewRoot);
        VerifyManualFixture(fixture);
        retainedRoots.Add(fixture.OutputRoot);
        Console.WriteLine($"SYNTHETIC_MANUAL_JSON={fixture.Path}");
        Console.WriteLine($"SHA256={fixture.Sha256}");
        Console.WriteLine($"MANAGEMENT_CODES={fixture.CategoryCode}/{fixture.ArticleCode}; STATUS=published; CATEGORY_COUNT=1; ARTICLE_COUNT=1; TEXT_LENGTH=100001");
        Console.WriteLine($"MANUAL_FIXTURE_CHECKS={passed}; validated this emitted file against an empty DB and synthetic C1 baseline with CAT-00001/FAQ-00001/PNG/blue settings.");
        Console.WriteLine("No user data read. Codes 2 are not guaranteed free in other databases. No users, credentials, settings, history, images or dummy rows exported.");
        return 0;
    }
    var deep = Paragraph("合成の深い本文");
    for (var index = 0; index < 24; index++) deep = "{\"type\":\"bulletList\",\"content\":[{\"type\":\"listItem\",\"content\":[" + deep + "]}]}";
    var boundary = "{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"合成の境界本文\",\"marks\":[{\"type\":\"bold\",\"attrs\":{}}]}]}";
    for (var index = 0; index < 30; index++) boundary = "{\"type\":\"bulletList\",\"content\":[{\"type\":\"listItem\",\"content\":[" + boundary + "]}]}";
    using (var boundaryDoc = JsonDocument.Parse(WithImage(boundary), StructuredJsonBoundary.DocumentOptions))
        Check(ContainerDepth(boundaryDoc.RootElement) == 128, "本文コンテナ深さ128ちょうどの合成fixture");
    foreach (var (label, body, plain) in new[]
    {
        ("100001文字", WithImage(Paragraph(new string('あ', 100_001))), new string('あ', 100_001)),
        ("10001段落", WithImage(string.Join(',', Enumerable.Repeat(Paragraph("合成の段落"), 10_001))), string.Join('\n', Enumerable.Repeat("合成の段落", 10_001))),
        ("JSON64超の深いリスト", WithImage(deep), "合成の深い本文"),
        ("JSON128境界", WithImage(boundary), "合成の境界本文")
    }) RunCase(label, body, plain);
    VerifyManualFixture(ManualLongDocumentFixture.CreateRetest(NewRoot));
    VerifyCodexDepthFixture(ManualCodexDepthFixture.Create(NewRoot));
    Console.WriteLine($"RichCompatibilityCheck: {passed} passed; generated temporary data only; no application or real database opened.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    foreach (var root in roots.Where(root => !retainedRoots.Contains(root))) FileSystemBoundary.DeleteSyntheticRoot(root);
}

string NewRoot()
{
    var root = FileSystemBoundary.ValidateSyntheticRoot(Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}"));
    roots.Add(root);
    return root;
}

void Check(bool success, string description)
{
    if (!success) throw new InvalidOperationException(description);
    passed++;
    Console.WriteLine($"PASS: {description}");
}

void Expect(string code, Action action, string description)
{
    try { action(); }
    catch (AppProblemException exception) when (exception.Problem.Code == code) { Check(true, description); return; }
    throw new InvalidOperationException($"Expected {code}: {description}");
}

void VerifyCodexDepthFixture(GeneratedManualJson fixture)
{
    using var json = JsonDocument.Parse(File.ReadAllBytes(fixture.Path), StructuredJsonBoundary.TransferOptions);
    var document = json.RootElement;
    var category = document.GetProperty("categories").EnumerateArray().Single();
    var article = document.GetProperty("articles").EnumerateArray().Single();
    var body = article.GetProperty("bodyDoc");
    Check(fixture.Counts == new JsonEntityCounts(1, 1, 0, 0, 0, 0) &&
        category.GetProperty("managementCode").GetString() == "CAT-00002" &&
        article.GetProperty("managementCode").GetString() == "FAQ-00002", "第15段階手動JSON: 実出力は分類1/FAQ1・管理ID2");
    Check(article.GetProperty("title").GetString() == ManualCodexDepthFixture.Title &&
        category.GetProperty("name").GetString() == ManualCodexDepthFixture.CategoryName &&
        article.GetProperty("status").GetString() == ArticleStatuses.Published, "第15段階手動JSON: 指定タイトル・分類・公開状態");
    Check(ContainerDepth(body) > 64 && ContainerDepth(body) < 120 &&
        SafeRichContentValidator.Validate(body).PlainText == ManualCodexDepthFixture.Text,
        "第15段階手動JSON: 旧64境界を超える安全な合成本文");
    foreach (var populated in new[] { false, true })
    {
        var root = NewRoot();
        using var database = KnowledgeDatabase.OpenRehearsalForTest(root);
        var authentication = new AuthenticationService(database);
        authentication.Login("0000", "");
        var baseline = populated ? SeedManualBaseline(database, authentication, root) : null;
        var baselineJson = baseline is null ? null : JsonSerializer.Serialize(baseline, jsonOptions);
        var imagePath = baseline is null ? null : new ArticleAttachmentService(root).ResolveManagedAttachmentForTest(baseline.Attachments.Single().AssetPath);
        var imageHash = imagePath is null ? null : Hash(imagePath);
        var settings = new SettingsService(database, authentication, root);
        var settingsJson = JsonSerializer.Serialize(settings.GetSettings());
        var transfer = new TransferService(database, authentication, root);
        var preview = transfer.InspectJson(fixture.Path);
        CheckManualPreview(preview, populated ? "第15段階C1相当" : "第15段階空環境");
        var result = transfer.ImportJson(new(fixture.Path, preview.FileSha256));
        Check(result.CreatedCount == 2 && result.UpdatedCount == 0 && File.Exists(result.SafetyBackupPath), "第15段階: 通常取込・安全退避");
        Check(JsonElement.DeepEquals(database.GetArticle(fixture.ArticleId).BodyDoc, body), "第15段階: 取込後の深い本文を保持");
        CheckRepeatedManualPreview(transfer.InspectJson(fixture.Path), "第15段階");
        var beforeDelegation = DbSnapshot(database);
        var delegation = new CodexProposalService(database, authentication).CreateDelegation(new("revise", [fixture.ArticleId]));
        using var exported = JsonDocument.Parse(File.ReadAllBytes(delegation.FilePath), StructuredJsonBoundary.TransferOptions);
        var exportedArticles = exported.RootElement.GetProperty("articles").EnumerateArray().ToArray();
        Check(exportedArticles.Length == 1 && exportedArticles[0].GetProperty("articleId").GetString() == fixture.ArticleId &&
            JsonElement.DeepEquals(exportedArticles[0].GetProperty("bodyDoc"), body), "第15段階: 明示選択した1件だけを深さごと委譲");
        Check(new FileInfo(delegation.FilePath).Length <= CodexProposalFiles.MaximumDelegationBytes &&
            DbSnapshot(database) == beforeDelegation, "第15段階: 5MB以内・委譲は元FAQとDBを更新しない");
        Check(JsonSerializer.Serialize(settings.GetSettings()) == settingsJson && (baseline is null ||
            JsonSerializer.Serialize(database.GetArticle(baseline.Id), jsonOptions) == baselineJson && Hash(imagePath!) == imageHash),
            "第15段階: 元FAQ・画像・設定を保持");
        Check(new BackupService(database, authentication, root).GetOverview().Counts ==
            new BackupCounts(populated ? 2 : 1, populated ? 2 : 1, populated ? 1 : 0, 0), "第15段階: 全体件数と添付数を維持");
    }
    Check(Hash(fixture.Path) == fixture.Sha256, "第15段階: 検証後も配布するJSON原本不変");
}

void VerifyManualFixture(GeneratedManualJson fixture)
{
    using (var json = JsonDocument.Parse(File.ReadAllBytes(fixture.Path), StructuredJsonBoundary.TransferOptions))
    {
        var document = json.RootElement;
        var category = document.GetProperty("categories").EnumerateArray().Single();
        var article = document.GetProperty("articles").EnumerateArray().Single();
        Check(fixture.Counts == new JsonEntityCounts(1, 1, 0, 0, 0, 0) &&
            category.GetProperty("managementCode").GetString() == "CAT-00002" &&
            article.GetProperty("managementCode").GetString() == "FAQ-00002", "手動JSON: 実出力は分類1/FAQ1だけ・管理ID2");
        Check(category.GetProperty("name").GetString() == ManualLongDocumentFixture.CategoryName &&
            article.GetProperty("title").GetString() == ManualLongDocumentFixture.Title &&
            article.GetProperty("status").GetString() == ArticleStatuses.Published, "手動JSON: 分類・質問文・公開状態を維持");
        var content = SafeRichContentValidator.Validate(article.GetProperty("bodyDoc"));
        Check(content.PlainText == ManualLongDocumentFixture.BodyText && content.PlainText.Length == 100_001 && content.Attachments.Count == 0,
            "手動JSON: 実出力は末尾付き100001字・画像なし");
        Check(document.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(
            new[] { "formatVersion", "exportedAt", "categories", "tags", "synonymGroups", "articles", "relations", "mergeRelations" }.Order(StringComparer.Ordinal)),
            "手動JSON: 認証・設定・履歴・ダミーを含めない正規形式");
    }
    var originalCodes = ManualLongDocumentFixture.CreateOriginalCodesForRegression(NewRoot);
    Check(originalCodes.CategoryCode == "CAT-00001" && originalCodes.ArticleCode == "FAQ-00001", "手動JSON: 旧生成方式の管理ID1を新規合成データだけで再現");

    var emptyRoot = NewRoot();
    using (var empty = KnowledgeDatabase.OpenRehearsalForTest(emptyRoot))
    {
        var authentication = new AuthenticationService(empty); authentication.Login("0000", "");
        var transfer = new TransferService(empty, authentication, emptyRoot);
        var preview = transfer.InspectJson(fixture.Path);
        CheckManualPreview(preview, "空環境");
        var result = transfer.ImportJson(new(fixture.Path, preview.FileSha256));
        Check(result.CreatedCount == 2 && result.UpdatedCount == 0 && File.Exists(result.SafetyBackupPath), "手動JSON: 空環境へ安全退避後2件追加");
        Check(empty.GetArticle(fixture.ArticleId).BodyPlainText == ManualLongDocumentFixture.BodyText, "手動JSON: 空環境の全文保持");
        CheckRepeatedManualPreview(transfer.InspectJson(fixture.Path), "空環境");
    }

    var root = NewRoot();
    var filesRoot = NewRoot(); Directory.CreateDirectory(filesRoot);
    var baselineArchive = Path.Combine(filesRoot, "Synthetic_C1_baseline.faqbackup");
    string baselineArticleId;
    string baselineArticleJson;
    string baselineCategoryJson;
    string baselineSettingsJson;
    string baselineImagePath;
    string baselineImageHash;
    string baselineArchiveHash;
    using (var database = KnowledgeDatabase.OpenRehearsalForTest(root))
    {
        var authentication = new AuthenticationService(database); authentication.Login("0000", "");
        var baseline = SeedManualBaseline(database, authentication, root);
        baselineArticleId = baseline.Id;
        baselineArticleJson = JsonSerializer.Serialize(baseline, jsonOptions);
        baselineCategoryJson = JsonSerializer.Serialize(new ClassificationSearchService(database, authentication).ListCategories().Single(), jsonOptions);
        var settings = new SettingsService(database, authentication, root);
        baselineSettingsJson = JsonSerializer.Serialize(settings.GetSettings());
        baselineImagePath = new ArticleAttachmentService(root).ResolveManagedAttachmentForTest(baseline.Attachments.Single().AssetPath);
        baselineImageHash = Hash(baselineImagePath);
        var backup = new BackupService(database, authentication, root);
        Check(backup.GetOverview().Counts == new BackupCounts(1, 1, 1, 0) && settings.GetSettings().ColorTheme == ColorThemes.Blue,
            "手動C1: FAQ1/分類1/PNG1・青の開始時状態");
        var savedBackup = backup.CreateFullBackup(new(baselineArchive, "合成C1開始前", false));
        baselineArchiveHash = Hash(baselineArchive);
        Check(savedBackup.Counts == new BackupCounts(1, 1, 1, 0), "手動C1: 通常サービスで開始前フルバックアップ作成");
        var transfer = new TransferService(database, authentication, root);
        var beforeRejectedImport = DbSnapshot(database);
        var originalPreview = transfer.InspectJson(originalCodes.Path);
        Check(originalPreview.Errors.Contains("分類管理IDが既存分類と衝突します。") && originalPreview.Errors.Contains("FAQ管理IDが既存FAQと衝突します。"),
            "手動C2再現: 旧生成ID1は既存の別UUIDのID1と衝突");
        Expect("JSON-004", () => transfer.ImportJson(new(originalCodes.Path, originalPreview.FileSha256)), "手動C2再現: 旧fixtureの確定を拒否");
        Check(DbSnapshot(database) == beforeRejectedImport && Hash(baselineImagePath) == baselineImageHash,
            "手動C2再現: 拒否後の開始時DB・PNG不変");

        var correctedPreview = transfer.InspectJson(fixture.Path);
        CheckManualPreview(correctedPreview, "開始時データあり");
        var imported = transfer.ImportJson(new(fixture.Path, correctedPreview.FileSha256));
        Check(imported.CreatedCount == 2 && imported.UpdatedCount == 0 && File.Exists(imported.SafetyBackupPath), "手動C2: 修正版だけを通常取込して2件追加");
        Check(JsonSerializer.Serialize(database.GetArticle(baseline.Id), jsonOptions) == baselineArticleJson &&
            JsonSerializer.Serialize(new ClassificationSearchService(database, authentication).ListCategories().Single(item => item.Id == baseline.CategoryId), jsonOptions) == baselineCategoryJson,
            "手動C2: 元FAQ全文・監査・メタデータ・分類不変");
        Check(Hash(baselineImagePath) == baselineImageHash && JsonSerializer.Serialize(settings.GetSettings()) == baselineSettingsJson,
            "手動C2: 元PNG実体と青設定不変");
        Check(backup.GetOverview().Counts == new BackupCounts(2, 2, 1, 0), "手動C2: 取込後はFAQ2/分類2/PNG1");
        CheckRepeatedManualPreview(transfer.InspectJson(fixture.Path), "開始時データあり");

        var editing = new ArticleEditingService(database, authentication);
        var article = database.GetArticle(fixture.ArticleId);
        Check(article.BodyPlainText == ManualLongDocumentFixture.BodyText && article.Status == ArticleStatuses.Published,
            "手動C2: 追加した100001字と公開状態を保持");
        using var changedBody = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[" + Paragraph(ManualLongDocumentFixture.BodyText + " 追記確認14") + "]}");
        editing.SaveArticle(Input(article) with { BodyDoc = changedBody.RootElement });
        var saved = database.GetArticle(article.Id);
        Check(saved.BodyPlainText == ManualLongDocumentFixture.BodyText + " 追記確認14", "手動C2: 通常編集で末尾追記・全文保存");
        var duplicated = editing.DuplicateArticle(article.Id);
        Check(duplicated.Id != article.Id && duplicated.Status == ArticleStatuses.Draft && JsonElement.DeepEquals(duplicated.BodyDoc, saved.BodyDoc),
            "手動C3: 長文を独立した下書きへ複製");
        using var duplicateBody = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[" + Paragraph(saved.BodyPlainText + " 複製側だけ14") + "]}");
        editing.SaveArticle(Input(duplicated) with { Title = "合成C3：複製だけの独立編集", BodyDoc = duplicateBody.RootElement });
        Check(database.GetArticle(article.Id).Title == ManualLongDocumentFixture.Title && database.GetArticle(duplicated.Id).Title == "合成C3：複製だけの独立編集",
            "手動C3: 複製の編集は元FAQへ反映しない");
        Check(database.GetArticle(article.Id).BodyPlainText == ManualLongDocumentFixture.BodyText + " 追記確認14" &&
            database.GetArticle(duplicated.Id).BodyPlainText == ManualLongDocumentFixture.BodyText + " 追記確認14 複製側だけ14",
            "手動C3: 元本文はC2のまま・複製本文の末尾だけ独立追記");
        Check(backup.GetOverview().Counts == new BackupCounts(3, 2, 1, 0) &&
            JsonSerializer.Serialize(database.GetArticle(baseline.Id), jsonOptions) == baselineArticleJson && Hash(baselineImagePath) == baselineImageHash,
            "手動C3: 追加と複製後も開始前FAQ・PNG不変");

        var restorePreview = backup.InspectBackup(baselineArchive);
        Check(restorePreview.ConfirmationToken is { Length: 64 } && restorePreview.Counts == new BackupCounts(1, 1, 1, 0),
            "手動C4: 開始前バックアップを通常プレビューして有効トークンを取得");
        var restored = backup.RestoreConfirmedBackup(new(baselineArchive, restorePreview.ConfirmationToken));
        Check(authentication.GetCurrentUser() is null && File.Exists(restored.SafetyBackupPath), "手動C4: 確認付き復元・直前安全退避・ログアウト");
        authentication.Login("0000", "");
        Check(backup.GetOverview().Counts == new BackupCounts(1, 1, 1, 0) &&
            JsonSerializer.Serialize(database.GetArticle(baseline.Id), jsonOptions) == baselineArticleJson,
            "手動C4: 開始前FAQと件数へ復帰し合成長文・複製だけ除去");
        Check(Hash(baselineImagePath) == baselineImageHash && JsonSerializer.Serialize(settings.GetSettings()) == baselineSettingsJson && Hash(baselineArchive) == baselineArchiveHash,
            "手動C4: PNG・青設定・開始前バックアップ原本を保持");
        var beforeRestoreState = backup.InspectBackup(restored.SafetyBackupPath);
        Check(beforeRestoreState.Counts == new BackupCounts(3, 2, 1, 0), "手動C4: 復元直前の長文と複製も安全バックアップへ保持");
    }
    using (var reopened = KnowledgeDatabase.OpenRehearsalForTest(root))
    {
        var authentication = new AuthenticationService(reopened);
        Check(authentication.GetCurrentUser() is null, "手動C4再起動: 認証状態は継承しない");
        authentication.Login("0000", "");
        Check(new BackupService(reopened, authentication, root).GetOverview().Counts == new BackupCounts(1, 1, 1, 0) &&
            JsonSerializer.Serialize(reopened.GetArticle(baselineArticleId), jsonOptions) == baselineArticleJson &&
            JsonSerializer.Serialize(new ClassificationSearchService(reopened, authentication).ListCategories().Single(), jsonOptions) == baselineCategoryJson,
            "手動C4再起動: 元FAQ・分類の本文と全メタデータ保持");
        Check(Hash(baselineImagePath) == baselineImageHash &&
            JsonSerializer.Serialize(new SettingsService(reopened, authentication, root).GetSettings()) == baselineSettingsJson && Hash(baselineArchive) == baselineArchiveHash,
            "手動C4再起動: PNG・青設定・バックアップ原本保持");
    }

    var occupiedRoot = NewRoot();
    using (var occupied = KnowledgeDatabase.OpenRehearsalForTest(occupiedRoot))
    {
        var authentication = new AuthenticationService(occupied); authentication.Login("0000", "");
        _ = SeedManualBaseline(occupied, authentication, occupiedRoot);
        var category = new ClassificationSearchService(occupied, authentication).CreateCategory("合成ID2占有", "既存別データの衝突試験", null);
        using var body = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[" + Paragraph("ID2を既に使う合成データ") + "]}");
        new ArticleEditingService(occupied, authentication).SaveArticle(new(null, category.Id, "合成ID2の既存FAQ", "既存IDは置換しません。",
            body.RootElement, ArticleStatuses.Published, 1, null, null, false, [], [], [], [], [], [], [], [], []));
        var transfer = new TransferService(occupied, authentication, occupiedRoot);
        var before = DbSnapshot(occupied);
        var preview = transfer.InspectJson(fixture.Path);
        Check(preview.Errors.Contains("分類管理IDが既存分類と衝突します。") && preview.Errors.Contains("FAQ管理IDが既存FAQと衝突します。"),
            "手動JSON: ID2も既存なら衝突を拒否・任意環境で安全とはしない");
        Expect("JSON-004", () => transfer.ImportJson(new(fixture.Path, preview.FileSha256)), "手動JSON: ID2使用済みの確定を拒否");
        Check(DbSnapshot(occupied) == before, "手動JSON: ID2拒否後の既存DB不変");
    }
    Check(Hash(fixture.Path) == fixture.Sha256 && Hash(originalCodes.Path) == originalCodes.Sha256,
        "手動JSON: 空/既存/衝突/C4検証後も生成JSON原本不変");
}

ArticleDetail SeedManualBaseline(KnowledgeDatabase database, AuthenticationService authentication, string root)
{
    var category = new ClassificationSearchService(database, authentication).CreateCategory("合成C1開始前分類", "既存データの非破壊試験", null);
    var images = new ArticleAttachmentService(root);
    var staged = images.StageBytes("合成C1保持画像.png", LegacyFixture.CreatePng());
    using var body = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        type = "doc",
        content = new object[]
        {
            new { type = "paragraph", content = new[] { new { type = "text", text = "開始前の合成FAQ本文とPNGを保持します。" } } },
            new { type = "image", attrs = new { attachmentId = staged.Id, src = "knowledge-attachment:" + staged.Id, alt = "合成C1保持画像" } }
        }
    }));
    var article = new ArticleEditingService(database, authentication, images).SaveArticle(new(null, category.Id,
        "合成C1：元の本文と画像を保持できますか？", "開始前の本文・監査・画像を保持します。", body.RootElement, ArticleStatuses.Published,
        3, "2099-12-31", "2099-12-30", false, [], [], [], [], [], [], [], [], []));
    new SettingsService(database, authentication, root).SaveSettings(new() { ColorTheme = ColorThemes.Blue, ShowTopCategoryInTitle = true, ShowMascot = true });
    return database.GetArticle(article.Id);
}

void CheckManualPreview(JsonImportPreview preview, string label) => Check(
    preview.ErrorCount == 0 && preview.CreateCount == 2 && preview.UpdateCount == 0 && preview.UnchangedCount == 0 &&
    preview.Counts == new JsonEntityCounts(1, 1, 0, 0, 0, 0), "手動JSON: " + label + "のプレビューは新規2/更新0/エラー0");

void CheckRepeatedManualPreview(JsonImportPreview preview, string label) => Check(
    preview.ErrorCount == 0 && preview.CreateCount == 0 && preview.UpdateCount == 0 && preview.UnchangedCount == 2,
    "手動JSON: " + label + "で同一ファイル再プレビューは重複追加なし");

void RunCase(string label, string body, string plain)
{
    // The reusable DB-only legacy fixture has readable non-UUID IDs for tags/synonyms.
    // JSON transfer deliberately requires UUIDs, so use genuine app-style IDs here.
    var fixture = LegacyFixture.Create(NewRoot(), 5, bodyOverride: body, plainOverride: plain, mutation: """
        BEGIN;
        PRAGMA defer_foreign_keys=ON;
        UPDATE tags SET id='60000000-0000-4000-8000-000000000001' WHERE id='synthetic-tag';
        UPDATE article_tags SET tag_id='60000000-0000-4000-8000-000000000001' WHERE tag_id='synthetic-tag';
        UPDATE synonym_groups SET id='70000000-0000-4000-8000-000000000001' WHERE id='synthetic-synonym';
        UPDATE synonyms SET group_id='70000000-0000-4000-8000-000000000001' WHERE group_id='synthetic-synonym';
        COMMIT;
        """);
    var originalArchiveHash = Hash(fixture.Archive);
    var root = NewRoot();
    var filesRoot = NewRoot();
    Directory.CreateDirectory(filesRoot);
    using var db = KnowledgeDatabase.OpenRehearsalForTest(root);
    var auth = new AuthenticationService(db);
    auth.Login("0000", "");
    var backup = new BackupService(db, auth, root);
    backup.RestoreBackup(fixture.Archive);
    Check(auth.GetCurrentUser() is null, label + ": 復元後ログアウト");
    auth.Login("0000", "");
    var images = new ArticleAttachmentService(root);
    var editing = new ArticleEditingService(db, auth, images);
    var transfer = new TransferService(db, auth, root);
    var dispatcher = new KnowledgeCommandDispatcher(auth, new ClassificationSearchService(db, auth),
        new ArticleViewService(db, auth, images), editing, new HistoryService(db, auth), transfer,
        backup, new SettingsService(db, auth, root), new CodexProposalService(db, auth, images),
        () => throw new InvalidOperationException("Unexpected image UI"),
        _ => throw new InvalidOperationException("Unexpected save UI"),
        _ => throw new InvalidOperationException("Unexpected open UI"),
        _ => throw new InvalidOperationException("Unexpected clipboard"),
        _ => throw new InvalidOperationException("Unexpected URL"));
    var original = db.GetArticle(LegacyFixture.ArticleId);
    var sourceImage = Path.Combine(root, LegacyFixture.ImagePath.Replace('/', Path.DirectorySeparatorChar));
    Check(original.BodyDoc.GetRawText() == body && Hash(sourceImage) == HashBytes(fixture.Image), label + ": 復元原文と画像の保持");
    var beforeLegacy = LegacyColumns(db);

    using var editedBody = JsonDocument.Parse(body[..^2] + "," + Paragraph("合成の末尾を編集しました。") + "]}", StructuredJsonBoundary.DocumentOptions);
    var input = Input(original) with { Title = original.Title + " 更新確認", BodyDoc = editedBody.RootElement };
    var hostJson = JsonSerializer.Serialize(new { id = "synthetic-edit", command = "save_article", args = new { input } }, jsonOptions);
    Check(HostRequestEnvelope.TryParse(hostJson, out var request), label + ": 実ホスト要求の解析");
    var updated = (ArticleDetail)dispatcher.Execute(request!.Command, request.Args)!;
    Check(JsonElement.DeepEquals(updated.BodyDoc, input.BodyDoc), label + ": 実Dispatcherで長文末尾編集・本文構造保持");
    Check(updated.CreatedByUserId == original.CreatedByUserId && updated.CreatedAt == original.CreatedAt && updated.UpdatedByUserId == auth.RequireUser().Id,
        label + ": 作成監査保持・更新者記録");
    Check(beforeLegacy == LegacyColumns(db), label + ": 旧検索項目・関連・タグ保持");
    Check(Hash(sourceImage) == HashBytes(fixture.Image), label + ": 更新後の画像保持");
    original = updated;

    var sourceBeforeDuplicate = db.GetArticle(original.Id);
    var duplicate = editing.DuplicateArticle(original.Id);
    Check(duplicate.Id != original.Id && duplicate.Status == ArticleStatuses.Draft, label + ": 独立した下書き複製");
    var copiedAttachment = db.GetArticle(duplicate.Id).Attachments.Single();
    var copiedPath = Path.Combine(root, "attachments", "articles", copiedAttachment.AssetPath.Replace('/', Path.DirectorySeparatorChar));
    Check(copiedAttachment.Id != original.Attachments.Single().Id && Hash(copiedPath) == Hash(sourceImage), label + ": 画像UUID・実ファイルの独立複製");
    var remapped = SafeRichContentValidator.RemapAttachmentIds(duplicate.BodyDoc,
        new Dictionary<string, string> { [copiedAttachment.Id] = original.Attachments.Single().Id });
    Check(JsonElement.DeepEquals(remapped, original.BodyDoc), label + ": 複製本文は画像ID以外不変");
    Check(db.GetArticle(original.Id).UpdatedAt == sourceBeforeDuplicate.UpdatedAt && Hash(sourceImage) == HashBytes(fixture.Image), label + ": 複製元は不変");

    var csv = Path.Combine(filesRoot, "Synthetic.knowledge-faq.csv");
    transfer.ExportFaqCsv(new(csv));
    var csvBefore = db.GetArticle(original.Id);
    var preview = transfer.InspectFaqCsv(csv);
    Check(preview.ErrorCount == 0 && preview.BodyReplacementCount == 0 && preview.UnchangedCount == preview.TotalRows, label + ": CSV無変更プレビュー");
    var importedCsv = transfer.ImportFaqCsv(new(csv, preview.FileSha256));
    Check(File.Exists(importedCsv.SafetyBackupPath), label + ": CSV安全バックアップ");
    Check(db.GetArticle(original.Id).BodyDoc.GetRawText() == csvBefore.BodyDoc.GetRawText() && db.GetArticle(original.Id).UpdatedAt == csvBefore.UpdatedAt,
        label + ": CSV無変更で原文・監査不変");

    var json = Path.Combine(filesRoot, "Synthetic.knowledge-export.json");
    transfer.ExportJson(new(json));
    var exportedHash = Hash(json);
    Check(Hash(sourceImage) == HashBytes(fixture.Image) && db.GetArticle(original.Id).BodyDoc.GetRawText() == csvBefore.BodyDoc.GetRawText(), label + ": JSON書出しは元画像・本文を変更しない");
    using (var doc = JsonDocument.Parse(File.ReadAllBytes(json), StructuredJsonBoundary.TransferOptions))
    {
        var article = doc.RootElement.GetProperty("articles").EnumerateArray().Single(item => item.GetProperty("id").GetString() == original.Id);
        Check(JsonElement.DeepEquals(article.GetProperty("bodyDoc"), SafeRichContentValidator.WithoutImages(original.BodyDoc)), label + ": JSONは画像だけ除外・本文構造維持");
    }
    var importRoot = NewRoot();
    using (var target = KnowledgeDatabase.OpenRehearsalForTest(importRoot))
    {
        var targetAuth = new AuthenticationService(target); targetAuth.Login("0000", "");
        var targetTransfer = new TransferService(target, targetAuth, importRoot);
        var jsonPreview = targetTransfer.InspectJson(json);
        if (jsonPreview.ErrorCount != 0) Console.WriteLine(JsonSerializer.Serialize(jsonPreview, jsonOptions));
        Check(jsonPreview.ErrorCount == 0, label + ": JSON新環境への取込プレビュー");
        var importedJson = targetTransfer.ImportJson(new(json, jsonPreview.FileSha256));
        Check(File.Exists(importedJson.SafetyBackupPath), label + ": JSON安全バックアップ");
        var targetArticle = target.GetArticle(original.Id);
        Check(JsonElement.DeepEquals(targetArticle.BodyDoc, SafeRichContentValidator.WithoutImages(original.BodyDoc)) && targetArticle.Attachments.Count == 0,
            label + ": JSON再取込後の長文・深構造と画像除外");
        Check(targetArticle.Procedures.SequenceEqual(original.Procedures) && targetArticle.Tags.SequenceEqual(original.Tags), label + ": JSON旧補助項目往復");
        var again = targetTransfer.InspectJson(json);
        Check(again.ErrorCount == 0 && again.UpdateCount == 0, label + ": 同一JSON再検査で変更なし");
    }
    Check(Hash(json) == exportedHash, label + ": JSON入力原本不変");

    var records = Rfc4180Csv.Read(File.ReadAllBytes(csv)).Select(row => row.ToArray()).ToArray();
    var edited = records.Single(row => row[4] == updated.Title);
    var replacement = plain + "\n変更後の安全な段落\njavascript: text only";
    edited[6] = replacement;
    var changedCsv = Path.Combine(filesRoot, "Changed.knowledge-faq.csv");
    File.WriteAllBytes(changedCsv, Rfc4180Csv.Write(records));
    var changedPreview = transfer.InspectFaqCsv(changedCsv);
    Check(changedPreview.ErrorCount == 0 && changedPreview.BodyReplacementCount == 1, label + ": CSV変更時の構造解除警告");
    var changedResult = transfer.ImportFaqCsv(new(changedCsv, changedPreview.FileSha256));
    var changed = db.GetArticle(original.Id);
    Check(changed.BodyDoc.GetProperty("content").EnumerateArray().All(node => node.GetProperty("type").GetString() == "paragraph") &&
        SafeRichContentValidator.Validate(changed.BodyDoc).Attachments.Count == 0, label + ": CSVは安全な段落だけへ置換");
    Check(changed.BodyPlainText == replacement && Hash(sourceImage) == HashBytes(fixture.Image) && File.Exists(changedResult.SafetyBackupPath), label + ": CSV文字列と退避画像保持");
    Check(beforeLegacy == LegacyColumns(db), label + ": CSV変更でも旧補助項目を保持");

    VerifyRejectedInput(db, editing, transfer, filesRoot, changed, json, label);
    VerifyJsonSnapshot(json, filesRoot, original, label);
    Check(Hash(fixture.Archive) == originalArchiveHash, label + ": 旧バックアップ原本不変");
}

void VerifyJsonSnapshot(string originalJson, string filesRoot, ArticleDetail expected, string label)
{
    foreach (var replaceWithUnsafe in new[] { false, true })
    {
        var path = Path.Combine(filesRoot, $"Snapshot-{Guid.NewGuid():D}.knowledge-export.json");
        File.Copy(originalJson, path);
        var root = NewRoot();
        using var database = KnowledgeDatabase.OpenRehearsalForTest(root);
        var authentication = new AuthenticationService(database); authentication.Login("0000", "");
        var service = new TransferService(database, authentication, root);
        var preview = service.InspectJson(path);
        var replacement = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8), documentOptions: StructuredJsonBoundary.TransferOptions)!;
        var article = replacement["articles"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == expected.Id)!;
        article["title"] = "検証後に差し替えた未承認の合成タイトル";
        if (replaceWithUnsafe) article["bodyDoc"] = JsonNode.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"script\"}]}");
        var calls = 0;
        database.JsonImportSnapshotValidatedForTest = () =>
        {
            calls++;
            File.WriteAllText(path, replacement.ToJsonString(jsonOptions), new UTF8Encoding(false));
        };
        JsonImportResult result;
        try { result = service.ImportJson(new(path, preview.FileSha256)); }
        finally { database.JsonImportSnapshotValidatedForTest = null; }
        var imported = database.GetArticle(expected.Id);
        Check(calls == 1 && Hash(path) != preview.FileSha256, label + ": 検証直後のJSON差替を決定的に再現");
        Check(imported.Title == expected.Title && JsonElement.DeepEquals(imported.BodyDoc, SafeRichContentValidator.WithoutImages(expected.BodyDoc)),
            label + (replaceWithUnsafe ? ": 危険JSONへ差替後も検証済snapshotだけ適用" : ": 別の正常JSONへ差替後も検証済snapshotだけ適用"));
        Check(File.Exists(result.SafetyBackupPath) && result.CreatedCount == preview.CreateCount,
            label + ": snapshot取込の安全バックアップ・実件数保持");
    }
}

void VerifyRejectedInput(KnowledgeDatabase db, ArticleEditingService editing, TransferService transfer, string filesRoot, ArticleDetail current, string json, string label)
{
    var before = DbSnapshot(db);
    foreach (var unsafeBody in new[]
    {
        "{\"type\":\"script\"}",
        "{\"type\":\"doc\",\"type\":\"doc\"}",
        "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"合成\",\"marks\":[{\"type\":\"link\",\"attrs\":{\"href\":\"javascript:alert(1)\"}}]}]}]}"
    })
    {
        using var doc = JsonDocument.Parse(unsafeBody);
        try { editing.SaveArticle(Input(current) with { BodyDoc = doc.RootElement }); throw new InvalidOperationException("Unsafe body accepted"); }
        catch (AppProblemException exception) when (exception.Problem.Code is "ART-003" or "ART-005") { Check(true, label + ": 危険・重複本文を拒否"); }
        Check(DbSnapshot(db) == before, label + ": 拒否後DB不変");
    }
    var tooDeep = "{\"type\":\"text\",\"text\":\"合成\"}";
    for (var i = 0; i < 65; i++) tooDeep = "{\"type\":\"paragraph\",\"content\":[" + tooDeep + "]}";
    using (var doc = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[" + tooDeep + "]}", new JsonDocumentOptions { MaxDepth = 256 }))
        Expect("ART-003", () => editing.SaveArticle(Input(current) with { BodyDoc = doc.RootElement }), label + ": parse済み128超JSONも拒否");
    Check(DbSnapshot(db) == before, label + ": 深さ拒否後DB不変");
    var tampered = Path.Combine(filesRoot, "Tampered.knowledge-export.json");
    File.Copy(json, tampered);
    var preview = transfer.InspectJson(tampered);
    File.AppendAllText(tampered, "\n", new UTF8Encoding(false));
    Expect("JSON-007", () => transfer.ImportJson(new(tampered, preview.FileSha256)), label + ": JSON確認後変更を拒否");
    Check(DbSnapshot(db) == before, label + ": JSON変更拒否後DB不変");
    var duplicateJson = Path.Combine(filesRoot, "Duplicate.knowledge-export.json");
    var text = File.ReadAllText(json, Encoding.UTF8);
    File.WriteAllText(duplicateJson, text.Insert(text.IndexOf('{') + 1, "\"formatVersion\":1,"), new UTF8Encoding(false));
    Expect("JSON-002", () => transfer.InspectJson(duplicateJson), label + ": JSON転送重複キー拒否");
    Check(DbSnapshot(db) == before, label + ": JSON重複拒否後DB不変");

    foreach (var invalidBody in new[] { "{\"type\":\"doc\",\"content\":[{\"type\":\"script\"}]}", "{\"type\":\"doc\",\"content\":[" + tooDeep + "]}" })
    {
        var invalidPath = Path.Combine(filesRoot, $"Unsafe-{Guid.NewGuid():D}.knowledge-export.json");
        var node = JsonNode.Parse(text, documentOptions: StructuredJsonBoundary.TransferOptions)!;
        node["articles"]![0]!["bodyDoc"] = JsonNode.Parse(invalidBody, documentOptions: StructuredJsonBoundary.TransferOptions);
        File.WriteAllText(invalidPath, node.ToJsonString(jsonOptions), new UTF8Encoding(false));
        var invalidPreview = transfer.InspectJson(invalidPath);
        Check(invalidPreview.ErrorCount > 0, label + ": JSON取込も危険書式・本文128超を拒否");
        Expect("JSON-004", () => transfer.ImportJson(new(invalidPath, invalidPreview.FileSha256)), label + ": JSON不正本文を確定不可");
        Check(DbSnapshot(db) == before, label + ": JSON不正本文拒否後DB不変");
    }
    var tamperedCsv = Path.Combine(filesRoot, "Tampered.knowledge-faq.csv");
    transfer.ExportFaqCsv(new(tamperedCsv));
    var csvPreview = transfer.InspectFaqCsv(tamperedCsv);
    File.AppendAllText(tamperedCsv, "\r\n", new UTF8Encoding(false));
    Expect("CSV-007", () => transfer.ImportFaqCsv(new(tamperedCsv, csvPreview.FileSha256)), label + ": CSV確認後変更を拒否");
    Check(DbSnapshot(db) == before, label + ": CSV変更拒否後DB不変");
}

SaveArticleInput Input(ArticleDetail article) => new(article.Id, article.CategoryId, article.Title, article.Summary,
    article.BodyDoc, article.Status, article.Importance, article.NewBadgeUntil, article.UpdatedBadgeUntil, article.IsHidden,
    [], [], [], [], [], [], article.Tags, [], []);
string DbSnapshot(KnowledgeDatabase db)
{
    using var connection = LegacyFixture.Open(db.OpenInfo.DatabasePath);
    return JsonSerializer.Serialize(LegacyFixture.Snapshot(connection));
}
string LegacyColumns(KnowledgeDatabase db)
{
    using var connection = LegacyFixture.Open(db.OpenInfo.DatabasePath);
    var snapshot = LegacyFixture.Snapshot(connection);
    return string.Join('\n', new[] { "article_symptoms", "article_causes", "article_targets", "article_error_codes", "article_search_terms", "article_relations", "article_manual_links" }
        .Select(table => JsonSerializer.Serialize(snapshot[table].Rows.Where(row => row.Contains(LegacyFixture.ArticleId, StringComparison.Ordinal)))));
}
static string Hash(string path) => HashBytes(File.ReadAllBytes(path));
static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string Paragraph(string text) => JsonSerializer.Serialize(new { type = "paragraph", content = new[] { new { type = "text", text } } });
static string WithImage(string content) => "{\"type\":\"doc\",\"content\":[" + content + ",{\"type\":\"image\",\"attrs\":{\"attachmentId\":\"" + LegacyFixture.ImageId + "\",\"src\":\"knowledge-attachment:" + LegacyFixture.ImageId + "\",\"alt\":\"合成旧版の青緑チェック画像\"}}]}";
static int ContainerDepth(JsonElement value) => value.ValueKind switch
{
    JsonValueKind.Object => 1 + value.EnumerateObject().Select(item => ContainerDepth(item.Value)).DefaultIfEmpty().Max(),
    JsonValueKind.Array => 1 + value.EnumerateArray().Select(ContainerDepth).DefaultIfEmpty().Max(),
    _ => 0
};
