using System.Globalization;
using System.Text.Json;
using KnowledgeApp.Data;
using Microsoft.Data.Sqlite;

internal static class TagMasterCheck
{
    public static int Run()
    {
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.Combine(temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
        var passed = 0;
        void Check(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine($"PASS TagMaster {passed:D2}: {name}");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"TagMaster検証失敗: {name}", exception);
            }
        }

        try
        {
            using var database = KnowledgeDatabase.OpenSynthetic(root);
            var authentication = new AuthenticationService(database);
            var editing = new ArticleEditingService(database, authentication);
            var classification = new ClassificationSearchService(database, authentication);
            var viewing = new ArticleViewService(database, authentication);
            var path = database.OpenInfo.DatabasePath;
            Check("未認証の一覧・保存・削除を拒否", () =>
            {
                ExpectProblem(() => editing.ListTags(), "AUTH-002");
                ExpectProblem(() => editing.SaveTag(null, "未認証合成タグ"), "AUTH-002");
                ExpectProblem(() => editing.DeleteTag("unknown"), "AUTH-002");
            });

            authentication.Login("0000", string.Empty);
            authentication.CreateUser("tag-editor", "タグ合成一般利用者", "synthetic-tag-password", UserRoles.User);
            var category = classification.CreateCategory("タグ合成検証", string.Empty, null);
            Check("一般利用者の追加・名称変更・未使用削除", () =>
            {
                authentication.Logout();
                authentication.Login("tag-editor", "synthetic-tag-password");
                var tag = editing.SaveTag(null, "一般利用者作成");
                var changed = editing.SaveTag(tag.Id, "一般利用者変更");
                Assert(changed.Id == tag.Id && changed.Name == "一般利用者変更", "一般利用者の名称変更");
                editing.DeleteTag(tag.Id);
                Assert(editing.ListTags().All(item => item.Id != tag.Id), "一般利用者の削除");
            });

            Check("前後空白除去・ID・作成更新時刻", () =>
            {
                var tag = editing.SaveTag(null, "  合成タグ  ");
                Assert(tag.Name == "合成タグ" && tag.UsageCount == 0 && Guid.TryParse(tag.Id, out _), "新規タグの戻り値");
                Assert(DateTimeOffset.TryParse(tag.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "更新日時形式");
                Assert(Text(path, "SELECT created_at FROM tags WHERE id = $id", tag.Id) == tag.UpdatedAt, "作成日時");
            });
            Check("100文字境界とサロゲート文字の文字数", () =>
            {
                var maximum = new string('界', 100);
                var maximumRunes = string.Concat(Enumerable.Repeat("😀", 100));
                Assert(editing.SaveTag(null, maximum).Name == maximum, "100文字が保存できる");
                Assert(editing.SaveTag(null, maximumRunes).Name == maximumRunes, "100 Unicode scalar値が保存できる");
                ExpectProblem(() => editing.SaveTag(null, maximum + "界"), "TAG-001");
                ExpectProblem(() => editing.SaveTag(null, maximumRunes + "😀"), "TAG-001");
            });
            Check("空欄・改行・null入力拒否", () =>
            {
                foreach (var name in new[] { "", " \t　", "合成\r改行", "合成\n改行", "合成\r\n改行" })
                {
                    ExpectProblem(() => editing.SaveTag(null, name), "TAG-001");
                }
                ExpectProblem(() => editing.SaveTag(null, null!), "TAG-001");
            });

            var normalizedTag = editing.SaveTag(null, "ＷｉＦｉ　 ＴＡＧ");
            Check("NFKC・大小文字・連続空白・タブの同名拒否", () =>
            {
                foreach (var name in new[] { "wifi tag", "WIFI TAG", "ｗｉｆｉ ｔａｇ", " WiFi\t  Tag " })
                {
                    ExpectProblem(() => editing.SaveTag(null, name), "TAG-002");
                }
            });
            Check("自分自身の同じ正規化名への変更を許可", () =>
            {
                var changed = editing.SaveTag(normalizedTag.Id, "WiFi Tag");
                Assert(changed.Id == normalizedTag.Id && changed.Name == "WiFi Tag", "同一タグの表示名変更");
            });
            Check("別タグへの衝突時は元データ維持", () =>
            {
                var other = editing.SaveTag(null, "衝突前タグ");
                var before = Snapshot(path, "SELECT * FROM tags WHERE id = $id", other.Id);
                ExpectProblem(() => editing.SaveTag(other.Id, "WIFI TAG"), "TAG-002");
                Assert(Snapshot(path, "SELECT * FROM tags WHERE id = $id", other.Id) == before, "衝突拒否時にタグ不変");
            });
            Check("存在しないID・空IDの名称変更を拒否", () =>
            {
                ExpectProblem(() => editing.SaveTag(Guid.NewGuid().ToString(), "不存在変更"), "TAG-004");
                ExpectProblem(() => editing.SaveTag(string.Empty, "空ID変更"), "TAG-004");
            });
            Check("存在しないID・空IDの削除を拒否", () =>
            {
                ExpectProblem(() => editing.DeleteTag(Guid.NewGuid().ToString()), "TAG-004");
                ExpectProblem(() => editing.DeleteTag(string.Empty), "TAG-004");
            });
            Check("タグ名・IDをSQL文字列へ展開しない", () =>
            {
                const string literalName = "tag'); DROP TABLE tags;--";
                var tag = editing.SaveTag(null, literalName);
                Assert(tag.Name == literalName && editing.ListTags().Any(item => item.Id == tag.Id), "SQL風文字列は通常タグ名");
                ExpectProblem(() => editing.DeleteTag("' OR 1=1 --"), "TAG-004");
                ExpectProblem(() => editing.SaveTag("' OR 1=1 --", "SQL風ID変更"), "TAG-004");
                Assert(editing.ListTags().Any(item => item.Id == tag.Id), "SQL風IDで他タグが変更されない");
            });

            const string oldName = "OldSearchMarker 旧名称タグ";
            const string newName = "NewSearchMarker 新名称タグ";
            var shared = editing.SaveTag(null, oldName);
            var secondary = editing.SaveTag(null, "SecondarySearchMarker 補助タグ");
            authentication.Logout();
            authentication.Login("0000", string.Empty);
            var first = editing.SaveArticle(ArticleInput(null, category.Id, "合成FAQ一", ArticleStatuses.Published, [oldName, secondary.Name]));
            authentication.Logout();
            authentication.Login("tag-editor", "synthetic-tag-password");
            var second = editing.SaveArticle(ArticleInput(null, category.Id, "合成FAQ二", ArticleStatuses.Draft, [oldName]));
            Execute(path, """
                UPDATE articles SET procedure_text = 'legacyproceduremarker', caution_text = 'legacycautionmarker' WHERE id = $id;
                UPDATE article_search_documents SET symptoms = 'legacysymptommarker', causes = 'legacycausemarker',
                    targets = 'legacytargetmarker', error_codes = 'legacyerrormarker', search_terms = 'legacysearchmarker'
                 WHERE article_id = $id;
                """, first.Id);
            var firstArticleBefore = Snapshot(path, "SELECT * FROM articles WHERE id = $id", first.Id);
            var secondArticleBefore = Snapshot(path, "SELECT * FROM articles WHERE id = $id", second.Id);
            var searchFieldsBefore = Snapshot(path, """
                SELECT title, summary, body, symptoms, causes, targets, error_codes, search_terms
                  FROM article_search_documents WHERE article_id = $id
                """, first.Id);
            var linksBefore = Snapshot(path, "SELECT * FROM article_tags WHERE tag_id = $id ORDER BY article_id", shared.Id);
            var tagCreatedAt = Text(path, "SELECT created_at FROM tags WHERE id = $id", shared.Id);
            Check("公開・下書きの使用件数とタグ選択", () =>
            {
                Assert(editing.ListTags().Single(item => item.Id == shared.Id).UsageCount == 2, "公開と下書きを数える");
                Assert(viewing.GetArticle(first.Id).Tags.Contains(oldName) && viewing.GetArticle(second.Id).Tags.Contains(oldName), "マスターからFAQへ選択");
            });
            Check("使用中名称変更はID・関連・FAQ監査・本文を維持", () =>
            {
                var changed = editing.SaveTag(shared.Id, newName);
                Assert(changed.Id == shared.Id && changed.Name == newName && changed.UsageCount == 2, "名称変更結果");
                Assert(Text(path, "SELECT created_at FROM tags WHERE id = $id", shared.Id) == tagCreatedAt, "タグの作成日時維持");
                Assert(Snapshot(path, "SELECT * FROM articles WHERE id = $id", first.Id) == firstArticleBefore, "公開FAQの全列維持");
                Assert(Snapshot(path, "SELECT * FROM articles WHERE id = $id", second.Id) == secondArticleBefore, "下書きFAQの全列維持");
                Assert(Snapshot(path, "SELECT * FROM article_tags WHERE tag_id = $id ORDER BY article_id", shared.Id) == linksBefore, "関連ID維持");
                Assert(viewing.GetArticle(first.Id).Tags.Contains(newName) && viewing.GetArticle(second.Id).Tags.Contains(newName), "全FAQの名称が即時反映");
            });
            Check("新名称で全FAQ検索・旧名称は不一致・補助タグを維持", () =>
            {
                Assert(Search(classification, "NewSearchMarker").Total == 2, "新タグ検索");
                Assert(Search(classification, "新名称タグ").Total == 2, "日本語新タグ検索");
                Assert(Search(classification, "OldSearchMarker").Total == 0, "旧タグ検索が残らない");
                Assert(Search(classification, "SecondarySearchMarker").Items.Single().Id == first.Id, "同時選択タグ維持");
                Assert(Number(path, "SELECT COUNT(*) FROM article_search_fts WHERE article_search_fts MATCH $id", "newsearchmarker") == 2, "FTS新タグ索引");
                Assert(Number(path, "SELECT COUNT(*) FROM article_search_fts WHERE article_search_fts MATCH $id", "oldsearchmarker") == 0, "FTS旧タグ索引除去");
            });
            Check("旧版補足検索項目・手順注意のFTS索引を維持", () =>
            {
                Assert(Snapshot(path, """
                    SELECT title, summary, body, symptoms, causes, targets, error_codes, search_terms
                      FROM article_search_documents WHERE article_id = $id
                    """, first.Id) == searchFieldsBefore, "タグ以外の検索文書維持");
                foreach (var term in new[] { "legacyproceduremarker", "legacycautionmarker", "legacysymptommarker", "legacycausemarker", "legacytargetmarker", "legacyerrormarker", "legacysearchmarker" })
                {
                    Assert(Number(path, "SELECT COUNT(*) FROM article_search_fts WHERE article_search_fts MATCH $id", term) == 1, "補足項目のFTS検索");
                }
            });
            Check("使用中削除は件数と外す案内を表示", () =>
            {
                var problem = ExpectProblem(() => editing.DeleteTag(shared.Id), "TAG-003");
                Assert(problem.Problem.Message.Contains("2件", StringComparison.Ordinal), "使用件数表示");
                Assert(problem.Problem.Action.Contains("FAQからタグを外して", StringComparison.Ordinal), "削除前の対処案内");
                Assert(editing.ListTags().Single(item => item.Id == shared.Id).UsageCount == 2, "拒否後もタグ維持");
            });
            Check("論理削除FAQも使用数に含め復元可能性を維持", () =>
            {
                editing.DeleteArticle(second.Id);
                Assert(editing.ListTags().Single(item => item.Id == shared.Id).UsageCount == 2, "削除済みFAQも使用中");
                ExpectProblem(() => editing.DeleteTag(shared.Id), "TAG-003");
                var restored = editing.RestoreArticle(second.Id);
                Assert(restored.Tags.Contains(newName), "復元後にタグ維持");
            });
            Check("FAQから外すと件数減少・未使用になったタグのみ削除", () =>
            {
                editing.SaveArticle(ArticleInput(first.Id, category.Id, first.Title, ArticleStatuses.Published, [secondary.Name]));
                Assert(editing.ListTags().Single(item => item.Id == shared.Id).UsageCount == 1, "1件外した件数");
                ExpectProblem(() => editing.DeleteTag(shared.Id), "TAG-003");
                editing.SaveArticle(ArticleInput(second.Id, category.Id, second.Title, ArticleStatuses.Draft, []));
                Assert(editing.ListTags().Single(item => item.Id == shared.Id).UsageCount == 0, "全件外した件数");
                editing.DeleteTag(shared.Id);
                Assert(editing.ListTags().All(item => item.Id != shared.Id), "未使用削除");
                Assert(Search(classification, "NewSearchMarker").Total == 0, "外したタグの索引除去");
            });

            var rollbackTag = editing.SaveTag(null, "tagrollbackoriginal");
            var rollbackFirst = editing.SaveArticle(ArticleInput(null, category.Id, "巻戻し合成一", ArticleStatuses.Published, [rollbackTag.Name]));
            var rollbackSecond = editing.SaveArticle(ArticleInput(null, category.Id, "巻戻し合成二", ArticleStatuses.Published, [rollbackTag.Name]));
            var rollbackTagBefore = Snapshot(path, "SELECT * FROM tags WHERE id = $id", rollbackTag.Id);
            var rollbackDocumentsBefore = Snapshot(path, "SELECT * FROM article_search_documents ORDER BY article_id");
            var rollbackFtsBefore = Snapshot(path, "SELECT * FROM article_search_fts ORDER BY article_id");
            Check("2件目索引失敗でタグ・全検索文書・FTSを巻戻す", () =>
            {
                Execute(path, """
                    CREATE TRIGGER synthetic_tag_rename_failure BEFORE UPDATE OF tags ON article_search_documents
                    WHEN NEW.tags = 'tagrollbacksentinel' AND NEW.article_id = (
                        SELECT MAX(article_id) FROM article_tags WHERE tag_id = (
                            SELECT id FROM tags WHERE normalized_name = 'tagrollbacksentinel'))
                    BEGIN SELECT RAISE(ABORT, 'synthetic-sensitive-sql-detail'); END;
                    """);
                try
                {
                    var problem = ExpectProblem(() => editing.SaveTag(rollbackTag.Id, "tagrollbacksentinel"), "DB-001");
                    Assert(!problem.Message.Contains("synthetic-sensitive-sql-detail", StringComparison.Ordinal), "SQL内部情報を露出しない");
                    Assert(Snapshot(path, "SELECT * FROM tags WHERE id = $id", rollbackTag.Id) == rollbackTagBefore, "タグ全列巻戻し");
                    Assert(Snapshot(path, "SELECT * FROM article_search_documents ORDER BY article_id") == rollbackDocumentsBefore, "途中まで更新した全検索文書巻戻し");
                    Assert(Snapshot(path, "SELECT * FROM article_search_fts ORDER BY article_id") == rollbackFtsBefore, "途中まで再構築したFTS巻戻し");
                    Assert(viewing.GetArticle(rollbackFirst.Id).Tags.Contains(rollbackTag.Name) && viewing.GetArticle(rollbackSecond.Id).Tags.Contains(rollbackTag.Name), "両FAQのタグ維持");
                }
                finally
                {
                    Execute(path, "DROP TRIGGER synthetic_tag_rename_failure");
                }
            });
            Check("巻戻し後の再試行と検索が正常", () =>
            {
                editing.SaveTag(rollbackTag.Id, "tagrecoveredmarker");
                Assert(Search(classification, "tagrecoveredmarker").Total == 2, "再試行が成功");
                Assert(Search(classification, "tagrollbackoriginal").Total == 0, "再試行後旧索引除去");
            });
            Check("新規登録失敗時に部分データが残らない", () =>
            {
                Execute(path, """
                    CREATE TRIGGER synthetic_tag_create_failure BEFORE INSERT ON tags
                    WHEN NEW.name = 'createfailmarker'
                    BEGIN SELECT RAISE(ABORT, 'synthetic-create-failure'); END;
                    """);
                try
                {
                    ExpectProblem(() => editing.SaveTag(null, "createfailmarker"), "DB-001");
                    Assert(editing.ListTags().All(item => item.Name != "createfailmarker"), "登録失敗時にタグ不在");
                }
                finally
                {
                    Execute(path, "DROP TRIGGER synthetic_tag_create_failure");
                }
            });
            Check("未使用削除失敗時に元タグを維持", () =>
            {
                var tag = editing.SaveTag(null, "deletefailmarker");
                var before = Snapshot(path, "SELECT * FROM tags WHERE id = $id", tag.Id);
                Execute(path, """
                    CREATE TRIGGER synthetic_tag_delete_failure BEFORE DELETE ON tags
                    WHEN OLD.name = 'deletefailmarker'
                    BEGIN SELECT RAISE(ABORT, 'synthetic-delete-failure'); END;
                    """);
                try
                {
                    ExpectProblem(() => editing.DeleteTag(tag.Id), "DB-001");
                    Assert(Snapshot(path, "SELECT * FROM tags WHERE id = $id", tag.Id) == before, "削除失敗時にタグ全列維持");
                }
                finally
                {
                    Execute(path, "DROP TRIGGER synthetic_tag_delete_failure");
                }
                editing.DeleteTag(tag.Id);
            });
            Check("ログアウト後の拒否時にDBを変更しない", () =>
            {
                var before = Snapshot(path, "SELECT * FROM tags ORDER BY id");
                authentication.Logout();
                ExpectProblem(() => editing.SaveTag(rollbackTag.Id, "logoutmarker"), "AUTH-002");
                ExpectProblem(() => editing.DeleteTag(normalizedTag.Id), "AUTH-002");
                Assert(Snapshot(path, "SELECT * FROM tags ORDER BY id") == before, "ログアウト時の保存拒否");
            });
            Check("別接続から名称・件数・索引の確定を確認", () =>
            {
                using var reopened = KnowledgeDatabase.OpenSynthetic(root);
                var reopenedAuthentication = new AuthenticationService(reopened);
                reopenedAuthentication.Login("0000", string.Empty);
                var reopenedEditing = new ArticleEditingService(reopened, reopenedAuthentication);
                var reopenedSearch = new ClassificationSearchService(reopened, reopenedAuthentication);
                var tag = reopenedEditing.ListTags().Single(item => item.Id == rollbackTag.Id);
                Assert(tag.Name == "tagrecoveredmarker" && tag.UsageCount == 2, "確定したタグの永続性");
                Assert(Search(reopenedSearch, "tagrecoveredmarker").Total == 2, "確定した索引の永続性");
                Assert(Number(path, "SELECT MAX(version) FROM schema_migrations") == 7, "既存DB版を変更しない");
                Assert(Text(path, "PRAGMA quick_check") == "ok", "合成DB整合性");
                Assert(Snapshot(path, "PRAGMA foreign_key_check") == "[]", "合成DB外部キー整合性");
            });
            Console.WriteLine($"OK: TagMaster {passed} passed, 0 failed (合成DBのみ、タグCRUD・検索索引・監査保持・transaction巻戻し)");
            return passed;
        }
        finally
        {
            var fullRoot = Path.GetFullPath(root);
            var leaf = Path.GetFileName(fullRoot);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !leaf.StartsWith("knowledgeapp-data-check-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(leaf["knowledgeapp-data-check-".Length..], "D", out _))
            {
                throw new InvalidOperationException("合成タグ検証フォルダの削除範囲が不正です。");
            }
            if (Directory.Exists(fullRoot))
            {
                if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("合成タグ検証フォルダのリンクは削除しません。");
                }
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    private static SaveArticleInput ArticleInput(string? id, string categoryId, string title, string status, IReadOnlyList<string> tags)
    {
        using var document = JsonDocument.Parse("""
            {"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"タグ合成試験の回答です。"}]}]}
            """);
        return new SaveArticleInput(id, categoryId, title, "タグ合成試験の概要です。", document.RootElement.Clone(), status,
            1, null, null, false, [], [], [], [], [], [], tags, [], []);
    }

    private static SearchArticlePage Search(ClassificationSearchService service, string query) => service.SearchArticles(
        new SearchArticlesInput(query, null, SearchScopes.All, true, 1, SearchSorts.UpdatedDesc));

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql, string? id = null)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static string Text(string path, string sql, string? id = null)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long Number(string path, string sql, string? id = null) =>
        long.Parse(Text(path, sql, id), CultureInfo.InvariantCulture);

    private static string Snapshot(string path, string sql, string? id = null)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var column = 0; column < reader.FieldCount; column++)
            {
                row[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            }
            rows.Add(row);
        }
        return JsonSerializer.Serialize(rows);
    }

    private static AppProblemException ExpectProblem(Action action, string code)
    {
        try
        {
            action();
        }
        catch (AppProblemException exception) when (exception.Problem.Code == code)
        {
            return exception;
        }
        throw new InvalidOperationException($"想定したエラー {code} が発生しませんでした。");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
