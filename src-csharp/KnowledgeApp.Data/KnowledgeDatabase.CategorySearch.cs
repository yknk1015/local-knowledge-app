using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    private const long SearchPageSize = 50;
    private static readonly HashSet<string> SearchStopWords =
    [
        "の", "が", "を", "に", "は", "で", "と", "へ", "も", "です", "ます"
    ];

    internal IReadOnlyList<CategorySummary> ListCategories() => ExecuteLocked(ListCategoriesUnlocked);

    internal CategorySummary CreateCategory(string name, string description, string? parentId)
    {
        var validatedName = ValidateCategoryName(name);
        var validatedDescription = ValidateCategoryDescription(description);
        return ExecuteLocked(() =>
        {
            using var transaction = _connection.BeginTransaction();
            var category = InsertCategory(
                transaction,
                Guid.CreateVersion7().ToString(),
                validatedName,
                validatedDescription,
                EmptyToNull(parentId));
            transaction.Commit();
            return category;
        });
    }

    internal CategorySummary UpdateCategory(
        string id,
        string name,
        string description,
        string? parentId)
    {
        var validatedName = ValidateCategoryName(name);
        var validatedDescription = ValidateCategoryDescription(description);
        parentId = EmptyToNull(parentId);
        return ExecuteLocked(() =>
        {
            using var transaction = _connection.BeginTransaction();
            string? currentParentId;
            long currentDepth;
            using (var current = _connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = "SELECT parent_id, depth FROM categories WHERE id = $id";
                current.Parameters.AddWithValue("$id", id);
                using var reader = current.ExecuteReader();
                if (!reader.Read())
                {
                    throw CategoryNotFound();
                }
                currentParentId = reader.IsDBNull(0) ? null : reader.GetString(0);
                currentDepth = reader.GetInt64(1);
            }

            if (parentId == id)
            {
                throw CategoryCycle();
            }
            if (parentId is not null)
            {
                using var descendant = _connection.CreateCommand();
                descendant.Transaction = transaction;
                descendant.CommandText = """
                    WITH RECURSIVE descendants(id) AS (
                        SELECT id FROM categories WHERE parent_id = $id
                        UNION ALL
                        SELECT child.id FROM categories child
                        JOIN descendants parent ON child.parent_id = parent.id
                    )
                    SELECT EXISTS(SELECT 1 FROM descendants WHERE id = $parent_id)
                    """;
                descendant.Parameters.AddWithValue("$id", id);
                descendant.Parameters.AddWithValue("$parent_id", parentId);
                if (Convert.ToInt64(descendant.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                {
                    throw CategoryCycle();
                }
            }

            var targetDepth = parentId is null ? 1 : CategoryDepth(transaction, parentId) + 1;
            using var relative = _connection.CreateCommand();
            relative.Transaction = transaction;
            relative.CommandText = """
                WITH RECURSIVE descendants(id, depth) AS (
                    SELECT id, depth FROM categories WHERE id = $id
                    UNION ALL
                    SELECT child.id, child.depth FROM categories child
                    JOIN descendants parent ON child.parent_id = parent.id
                )
                SELECT COALESCE(MAX(depth), $current_depth) - $current_depth FROM descendants
                """;
            relative.Parameters.AddWithValue("$id", id);
            relative.Parameters.AddWithValue("$current_depth", currentDepth);
            var subtreeRelativeDepth = Convert.ToInt64(relative.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (targetDepth + subtreeRelativeDepth > 5)
            {
                throw new AppProblemException(new AppProblem(
                    "CAT-003",
                    "移動すると分類が6階層以上になります。",
                    "より上の階層を移動先として選択してください。"));
            }

            var normalizedName = NormalizeSearchText(validatedName);
            EnsureCategoryNameAvailable(transaction, id, parentId, normalizedName, moving: true);
            var parentChanged = !string.Equals(currentParentId, parentId, StringComparison.Ordinal);
            var sortOrder = parentChanged
                ? NextCategorySortOrder(transaction, parentId, id)
                : CurrentCategorySortOrder(transaction, id);
            var depthDelta = targetDepth - currentDepth;
            var now = UtcNow();
            using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE categories
                       SET parent_id = $parent_id, name = $name, normalized_name = $normalized_name,
                           description = $description, depth = $depth, sort_order = $sort_order,
                           updated_at = $updated_at
                     WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$parent_id", (object?)parentId ?? DBNull.Value);
                update.Parameters.AddWithValue("$name", validatedName);
                update.Parameters.AddWithValue("$normalized_name", normalizedName);
                update.Parameters.AddWithValue("$description", validatedDescription);
                update.Parameters.AddWithValue("$depth", targetDepth);
                update.Parameters.AddWithValue("$sort_order", sortOrder);
                update.Parameters.AddWithValue("$updated_at", now);
                update.ExecuteNonQuery();
            }
            if (depthDelta != 0)
            {
                using var descendants = _connection.CreateCommand();
                descendants.Transaction = transaction;
                descendants.CommandText = """
                    WITH RECURSIVE descendants(id) AS (
                        SELECT id FROM categories WHERE parent_id = $id
                        UNION ALL
                        SELECT child.id FROM categories child
                        JOIN descendants parent ON child.parent_id = parent.id
                    )
                    UPDATE categories SET depth = depth + $delta, updated_at = $updated_at
                     WHERE id IN (SELECT id FROM descendants)
                    """;
                descendants.Parameters.AddWithValue("$id", id);
                descendants.Parameters.AddWithValue("$delta", depthDelta);
                descendants.Parameters.AddWithValue("$updated_at", now);
                descendants.ExecuteNonQuery();
            }
            transaction.Commit();
            return ListCategoriesUnlocked().Single(category => category.Id == id);
        });
    }

    internal IReadOnlyList<CategorySummary> ReorderCategory(string id, string direction) => ExecuteLocked(() =>
    {
        if (direction is not ("up" or "down"))
        {
            throw InvalidInput("分類の並べ替え方向が正しくありません。", "画面を再読み込みしてください。");
        }
        using var transaction = _connection.BeginTransaction();
        string? parentId;
        using (var parent = _connection.CreateCommand())
        {
            parent.Transaction = transaction;
            parent.CommandText = "SELECT parent_id FROM categories WHERE id = $id";
            parent.Parameters.AddWithValue("$id", id);
            var value = parent.ExecuteScalar();
            if (value is null)
            {
                throw CategoryNotFound();
            }
            parentId = value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        var siblings = new List<string>();
        using (var list = _connection.CreateCommand())
        {
            list.Transaction = transaction;
            list.CommandText = "SELECT id FROM categories WHERE parent_id IS $parent_id ORDER BY sort_order, id";
            list.Parameters.AddWithValue("$parent_id", (object?)parentId ?? DBNull.Value);
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                siblings.Add(reader.GetString(0));
            }
        }
        var position = siblings.IndexOf(id);
        if (position < 0)
        {
            throw CategoryNotFound();
        }
        var target = direction == "up" ? position - 1 : position + 1;
        if (target >= 0 && target < siblings.Count)
        {
            (siblings[position], siblings[target]) = (siblings[target], siblings[position]);
            var now = UtcNow();
            for (var index = 0; index < siblings.Count; index++)
            {
                using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE categories SET sort_order = $sort_order, updated_at = $updated_at WHERE id = $id";
                update.Parameters.AddWithValue("$id", siblings[index]);
                update.Parameters.AddWithValue("$sort_order", index);
                update.Parameters.AddWithValue("$updated_at", now);
                update.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        return ListCategoriesUnlocked();
    });

    internal void DeleteCategory(string id) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        using var counts = _connection.CreateCommand();
        counts.Transaction = transaction;
        counts.CommandText = """
            SELECT EXISTS(SELECT 1 FROM categories WHERE id = $id),
                   (SELECT COUNT(*) FROM categories WHERE parent_id = $id),
                   (SELECT COUNT(*) FROM articles WHERE category_id = $id)
            """;
        counts.Parameters.AddWithValue("$id", id);
        using var reader = counts.ExecuteReader();
        reader.Read();
        if (reader.GetInt64(0) == 0)
        {
            throw CategoryNotFound();
        }
        if (reader.GetInt64(1) > 0 || reader.GetInt64(2) > 0)
        {
            throw new AppProblemException(new AppProblem(
                "CAT-005",
                "配下分類またはFAQが残っているため、この分類は削除できません。",
                "配下分類とFAQを別の分類へ移動するか、先に削除してください。"));
        }
        reader.Close();
        using var delete = _connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM categories WHERE id = $id";
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();
        transaction.Commit();
    });

    internal SearchArticlePage SearchArticles(SearchArticlesInput input) => ExecuteLocked(() =>
    {
        ValidateSearchInput(input);
        var groups = ExpandedSearchQuery(input.Query);
        var ftsCandidates = FtsSearchCandidates(groups);
        var restrictToFts = groups.Count > 0 && groups.All(group =>
            group.Variants.All(variant => variant.Value.EnumerateRunes().Count() >= 3));
        var normalizedQuery = NormalizeSearchText(input.Query.Trim());
        var scored = new List<ScoredSearchCandidate>();
        foreach (var candidate in LoadSearchCandidates(input))
        {
            if (restrictToFts &&
                !ftsCandidates.Contains(candidate.Item.Id) &&
                candidate.Procedures.Length == 0 &&
                candidate.Cautions.Length == 0)
            {
                continue;
            }
            if (groups.Count == 0)
            {
                scored.Add(new ScoredSearchCandidate(candidate.Item, 0, 0));
                continue;
            }
            var result = ScoreSearchCandidate(candidate, groups, normalizedQuery);
            if (result is not null)
            {
                scored.Add(result);
            }
        }
        scored.Sort((left, right) => CompareScoredCandidates(left, right, input.Sort, groups.Count > 0));
        var total = scored.Count;
        var totalPages = Math.Max(1, (total + SearchPageSize - 1) / SearchPageSize);
        var page = Math.Clamp(input.Page, 1, totalPages);
        var items = scored
            .Skip(checked((int)((page - 1) * SearchPageSize)))
            .Take((int)SearchPageSize)
            .Select(result => result.Item)
            .ToArray();
        return new SearchArticlePage(items, total, page, SearchPageSize);
    });

    internal string RecordSearchLog(string query, string? categoryId, string scope, long resultCount) => ExecuteLocked(() =>
    {
        if (!SearchScopes.IsValid(scope) || resultCount < 0)
        {
            throw InvalidInput("検索履歴の入力が正しくありません。", "FAQ一覧へ戻り、もう一度検索してください。");
        }
        categoryId = EmptyToNull(categoryId);
        if (categoryId is not null && !CategoryExists(categoryId))
        {
            throw CategoryNotFound();
        }
        var id = Guid.CreateVersion7().ToString();
        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO search_logs(id, query_text, normalized_query, scope, category_id, result_count, created_at)
            VALUES ($id, $query_text, $normalized_query, $scope, $category_id, $result_count, $created_at)
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$query_text", query.Trim());
        insert.Parameters.AddWithValue("$normalized_query", NormalizeSearchText(query));
        insert.Parameters.AddWithValue("$scope", scope);
        insert.Parameters.AddWithValue("$category_id", (object?)categoryId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$result_count", resultCount);
        insert.Parameters.AddWithValue("$created_at", UtcNow());
        insert.ExecuteNonQuery();
        return id;
    });

    internal IReadOnlyList<SynonymGroupSummary> ListSynonymGroups() => ExecuteLocked(ListSynonymGroupsUnlocked);

    internal SynonymGroupSummary SaveSynonymGroup(
        string? id,
        string displayName,
        IReadOnlyList<string> terms,
        bool allowConflicts)
    {
        var values = ValidateSynonymValues(displayName, terms);
        id = EmptyToNull(id);
        return ExecuteLocked(() =>
        {
            var currentId = id ?? string.Empty;
            var normalizedValues = values.Select(value => value.Normalized).ToHashSet(StringComparer.Ordinal);
            var conflicts = new List<string>();
            using (var conflict = _connection.CreateCommand())
            {
                conflict.CommandText = """
                    SELECT synonym.normalized_term, synonym_group.display_name
                      FROM synonyms synonym
                      JOIN synonym_groups synonym_group ON synonym_group.id = synonym.group_id
                     WHERE synonym_group.id <> $id
                     ORDER BY synonym_group.display_name, synonym.normalized_term
                    """;
                conflict.Parameters.AddWithValue("$id", currentId);
                using var reader = conflict.ExecuteReader();
                while (reader.Read())
                {
                    if (normalizedValues.Contains(reader.GetString(0)))
                    {
                        conflicts.Add(reader.GetString(1));
                    }
                }
            }
            if (!allowConflicts && conflicts.Count > 0)
            {
                throw new AppProblemException(new AppProblem(
                    "SYN-003",
                    $"同じ語が別の同義語グループ（{string.Join('、', conflicts.Distinct(StringComparer.Ordinal))}）にも登録されています。",
                    "意図した重複であれば確認後に保存し、そうでなければ重複する語を外してください。"));
            }
            var groupId = id ?? Guid.CreateVersion7().ToString();
            using var transaction = _connection.BeginTransaction();
            var now = UtcNow();
            using (var save = _connection.CreateCommand())
            {
                save.Transaction = transaction;
                save.CommandText = id is null
                    ? "INSERT INTO synonym_groups(id, display_name, created_at, updated_at) VALUES ($id, $display_name, $now, $now)"
                    : "UPDATE synonym_groups SET display_name = $display_name, updated_at = $now WHERE id = $id";
                save.Parameters.AddWithValue("$id", groupId);
                save.Parameters.AddWithValue("$display_name", displayName.Trim());
                save.Parameters.AddWithValue("$now", now);
                if (save.ExecuteNonQuery() == 0)
                {
                    throw SynonymNotFound();
                }
            }
            using (var deleteTerms = _connection.CreateCommand())
            {
                deleteTerms.Transaction = transaction;
                deleteTerms.CommandText = "DELETE FROM synonyms WHERE group_id = $group_id";
                deleteTerms.Parameters.AddWithValue("$group_id", groupId);
                deleteTerms.ExecuteNonQuery();
            }
            foreach (var value in values)
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO synonyms(id, group_id, term, normalized_term) VALUES ($id, $group_id, $term, $normalized_term)";
                insert.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
                insert.Parameters.AddWithValue("$group_id", groupId);
                insert.Parameters.AddWithValue("$term", value.Term);
                insert.Parameters.AddWithValue("$normalized_term", value.Normalized);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return ListSynonymGroupsUnlocked().Single(group => group.Id == groupId);
        });
    }

    internal void DeleteSynonymGroup(string id) => ExecuteLocked(() =>
    {
        using var delete = _connection.CreateCommand();
        delete.CommandText = "DELETE FROM synonym_groups WHERE id = $id";
        delete.Parameters.AddWithValue("$id", id);
        if (delete.ExecuteNonQuery() == 0)
        {
            throw SynonymNotFound();
        }
    });

    public SyntheticCategorySearchFixture SeedSyntheticCategorySearchFixture()
    {
        const string networkId = "70000000-0000-7000-8000-000000000001";
        const string wifiId = "70000000-0000-7000-8000-000000000002";
        const string securityId = "70000000-0000-7000-8000-000000000003";
        const string meshId = "71000000-0000-7000-8000-000000000001";
        const string draftId = "71000000-0000-7000-8000-000000000002";
        const string hiddenId = "71000000-0000-7000-8000-000000000003";
        const string deletedId = "71000000-0000-7000-8000-000000000004";
        const string mergedId = "71000000-0000-7000-8000-000000000005";
        const string relatedId = "72000000-0000-7000-8000-000000000001";
        const string zoomTagId = "74000000-0000-7000-8000-000000000001";
        return ExecuteLocked(() =>
        {
            if (!CategoryExists(networkId))
            {
                using var transaction = _connection.BeginTransaction();
                InsertCategory(transaction, networkId, "ネットワーク", "ネットワーク全般の合成分類です。", null);
                InsertCategory(transaction, wifiId, "Wi-Fi", "無線LANの合成分類です。", networkId);
                InsertCategory(transaction, securityId, "セキュリティ", "安全確認用の合成分類です。", null);
                transaction.Commit();
            }
            if (CountById("articles", meshId) == 0)
            {
                using var transaction = _connection.BeginTransaction();
                InsertSyntheticArticle(transaction, meshId, wifiId,
                    "メッシュWi-FiでZoomが切れる原因と対処は？",
                    "アクセスポイント切替時の瞬断を抑える設定と配置を確認します。",
                    "パソコンのローミングとオンライン会議の通信を確認します。",
                    "published", 3, false, null,
                    "接続が切れる", "ローミング切替", "PC Zoom", "NET-100", "メッシュwifi 会議");
                InsertSyntheticArticle(transaction, draftId, wifiId,
                    "下書きのWi-Fi確認手順", "下書き表示の合成確認です。", "下書き本文です。",
                    "draft", 2, false, null, "通信不安定", "設定", "PC", "", "下書き");
                InsertSyntheticArticle(transaction, hiddenId, wifiId,
                    "非表示のWi-Fi記事", "通常検索から除外します。", "非表示本文です。",
                    "published", 1, true, null, "非表示", "", "", "", "秘匿検索語");
                InsertSyntheticArticle(transaction, deletedId, wifiId,
                    "削除済みのWi-Fi記事", "通常検索から除外します。", "削除済み本文です。",
                    "published", 1, false, "2026-08-01T00:00:00Z", "削除済み", "", "", "", "削除検索語");
                InsertSyntheticArticle(transaction, mergedId, wifiId,
                    "統合済みのWi-Fi記事", "通常検索から除外します。", "統合済み本文です。",
                    "published", 1, false, null, "統合済み", "", "", "", "統合検索語");
                using (var merge = _connection.CreateCommand())
                {
                    merge.Transaction = transaction;
                    merge.CommandText = """
                        INSERT INTO article_merge_relations(source_article_id, target_article_id, source_updated_at, merged_at)
                        VALUES ($source, $target, $updated_at, $merged_at)
                        """;
                    merge.Parameters.AddWithValue("$source", mergedId);
                    merge.Parameters.AddWithValue("$target", meshId);
                    merge.Parameters.AddWithValue("$updated_at", "2026-08-05T00:00:00Z");
                    merge.Parameters.AddWithValue("$merged_at", "2026-08-06T00:00:00Z");
                    merge.ExecuteNonQuery();
                }
                for (var index = 1; index <= 55; index++)
                {
                    InsertSyntheticArticle(transaction,
                        $"72000000-0000-7000-8000-{index:000000000000}", securityId,
                        $"ページング合成FAQ {index:00}",
                        "50件ページ分割を確認する合成データです。",
                        "ページング検索の本文です。",
                        "published", (index % 3) + 1, false, null,
                        "ページング", "合成試験", "画面", $"PAGE-{index:00}", "一覧 ページング");
                }
                using (var relation = _connection.CreateCommand())
                {
                    relation.Transaction = transaction;
                    relation.CommandText = """
                        INSERT INTO article_relations(source_article_id, target_article_id, sort_order)
                        VALUES ($source, $target, 0)
                        """;
                    relation.Parameters.AddWithValue("$source", meshId);
                    relation.Parameters.AddWithValue("$target", relatedId);
                    relation.ExecuteNonQuery();
                }
                InsertSyntheticTag(transaction, meshId, zoomTagId, "Zoom");
                transaction.Commit();
            }
            if (CountById("synonym_groups", "73000000-0000-7000-8000-000000000001") == 0)
            {
                InsertSyntheticSynonymGroup();
            }
            return new SyntheticCategorySearchFixture(
                networkId, wifiId, securityId, meshId, relatedId, draftId, hiddenId, deletedId, mergedId);
        });
    }

    internal long SearchLogCountForTest() => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM search_logs";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    internal long FtsRowCountForTest() => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM article_search_fts";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    private IReadOnlyList<CategorySummary> ListCategoriesUnlocked()
    {
        var categories = new List<CategorySummary>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE category_tree AS (
                SELECT id, parent_id, name, description, depth, sort_order,
                       printf('%08d', sort_order) AS sort_path
                  FROM categories WHERE parent_id IS NULL
                UNION ALL
                SELECT child.id, child.parent_id, child.name, child.description, child.depth,
                       child.sort_order, category_tree.sort_path || '.' || printf('%08d', child.sort_order)
                  FROM categories child JOIN category_tree ON child.parent_id = category_tree.id
            )
            SELECT tree.id, tree.parent_id, tree.name, tree.description, tree.depth, tree.sort_order,
                   (SELECT COUNT(*) FROM articles article
                     WHERE article.category_id = tree.id AND article.deleted_at IS NULL)
              FROM category_tree tree
             ORDER BY tree.sort_path, tree.name
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            categories.Add(new CategorySummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }
        return categories;
    }

    private static CategorySummary InsertCategory(
        SqliteTransaction transaction,
        string id,
        string name,
        string description,
        string? parentId)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        var depth = parentId is null ? 1 : CategoryDepth(transaction, parentId) + 1;
        if (depth > 5)
        {
            throw new AppProblemException(new AppProblem(
                "CAT-003", "分類は5階層まで作成できます。", "より上の階層を親として選択してください。"));
        }
        var normalizedName = NormalizeSearchText(name);
        EnsureCategoryNameAvailable(transaction, string.Empty, parentId, normalizedName, moving: false);
        var sortOrder = NextCategorySortOrder(transaction, parentId, string.Empty);
        var now = UtcNow();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO categories(id, parent_id, name, normalized_name, description, depth, sort_order, created_at, updated_at)
            VALUES ($id, $parent_id, $name, $normalized_name, $description, $depth, $sort_order, $now, $now)
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$parent_id", (object?)parentId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$normalized_name", normalizedName);
        insert.Parameters.AddWithValue("$description", description);
        insert.Parameters.AddWithValue("$depth", depth);
        insert.Parameters.AddWithValue("$sort_order", sortOrder);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
        return new CategorySummary(id, parentId, name, description, depth, sortOrder, 0);
    }

    private static void EnsureCategoryNameAvailable(
        SqliteTransaction transaction,
        string id,
        string? parentId,
        string normalizedName,
        bool moving)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        using var duplicate = connection.CreateCommand();
        duplicate.Transaction = transaction;
        duplicate.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM categories
                 WHERE id <> $id AND parent_id IS $parent_id AND normalized_name = $normalized_name
            )
            """;
        duplicate.Parameters.AddWithValue("$id", id);
        duplicate.Parameters.AddWithValue("$parent_id", (object?)parentId ?? DBNull.Value);
        duplicate.Parameters.AddWithValue("$normalized_name", normalizedName);
        if (Convert.ToInt64(duplicate.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
        {
            throw new AppProblemException(new AppProblem(
                "CAT-004",
                moving ? "移動先に同名の分類があります。" : "同じ場所に同名の分類があります。",
                moving ? "分類名または移動先を変更してください。" : "既存分類を選ぶか、別の分類名へ変更してください。"));
        }
    }

    private static long CategoryDepth(SqliteTransaction transaction, string id)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT depth FROM categories WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        if (value is null)
        {
            throw CategoryNotFound();
        }
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long NextCategorySortOrder(SqliteTransaction transaction, string? parentId, string excludedId)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM categories WHERE parent_id IS $parent_id AND id <> $id";
        command.Parameters.AddWithValue("$parent_id", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", excludedId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long CurrentCategorySortOrder(SqliteTransaction transaction, string id)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sort_order FROM categories WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<SearchCandidate> LoadSearchCandidates(SearchArticlesInput input)
    {
        var candidates = new List<SearchCandidate>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE selected_categories(id) AS (
                SELECT id FROM categories WHERE id = $category_id
                UNION ALL
                SELECT category.id FROM categories category
                JOIN selected_categories parent ON category.parent_id = parent.id
            )
            SELECT article.id, article.category_id, category.name, article.title, article.summary,
                   article.status, article.importance, article.new_badge_until, article.updated_badge_until,
                   article.is_hidden, article.updated_at,
                   document.title, document.summary, document.body, document.symptoms, document.causes,
                   document.targets, document.error_codes, document.tags, document.search_terms,
                   article.procedure_text, article.caution_text,
                   COALESCE((
                       SELECT GROUP_CONCAT(ordered_tags.name, char(10))
                         FROM (
                             SELECT tag.name FROM article_tags article_tag
                             JOIN tags tag ON tag.id = article_tag.tag_id
                             WHERE article_tag.article_id = article.id
                             ORDER BY tag.normalized_name, tag.id
                         ) ordered_tags
                   ), '')
              FROM articles article
              JOIN categories category ON category.id = article.category_id
              JOIN article_search_documents document ON document.article_id = article.id
             WHERE article.deleted_at IS NULL
               AND article.is_hidden = 0
               AND NOT EXISTS (
                   SELECT 1 FROM article_merge_relations relation
                    WHERE relation.source_article_id = article.id
               )
               AND ($include_drafts = 1 OR article.status = 'published')
               AND (
                   $scope = 'all'
                   OR ($scope = 'descendants' AND ($category_id IS NULL OR article.category_id IN (SELECT id FROM selected_categories)))
                   OR ($scope = 'current' AND $category_id IS NOT NULL AND article.category_id = $category_id)
               )
            """;
        command.Parameters.AddWithValue("$category_id", (object?)EmptyToNull(input.CategoryId) ?? DBNull.Value);
        command.Parameters.AddWithValue("$scope", input.Scope);
        command.Parameters.AddWithValue("$include_drafts", input.IncludeDrafts ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = new SearchArticleListItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt64(9) == 1, reader.GetString(10),
                SplitStoredValues(reader.GetString(22)), []);
            candidates.Add(new SearchCandidate(
                item, reader.GetString(11), reader.GetString(12), reader.GetString(13),
                reader.GetString(14), reader.GetString(15), reader.GetString(16), reader.GetString(17),
                reader.GetString(18), reader.GetString(19),
                NormalizeSearchText(reader.GetString(20)), NormalizeSearchText(reader.GetString(21))));
        }
        return candidates;
    }

    private List<SearchQueryGroup> ExpandedSearchQuery(string query)
    {
        var groups = SearchQueryGroups(query);
        if (groups.Count == 0)
        {
            return groups;
        }
        var synonyms = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT group_id, normalized_term FROM synonyms ORDER BY group_id, normalized_term";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var groupId = reader.GetString(0);
                if (!synonyms.TryGetValue(groupId, out var terms))
                {
                    terms = [];
                    synonyms.Add(groupId, terms);
                }
                terms.Add(reader.GetString(1));
            }
        }
        foreach (var group in groups)
        {
            var existing = group.Variants.Select(variant => variant.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var terms in synonyms.Values)
            {
                var matched = terms.FirstOrDefault(term => existing.Contains(term) || group.Display.Contains(term, StringComparison.Ordinal));
                if (matched is null)
                {
                    continue;
                }
                foreach (var term in terms.Where(term => !existing.Contains(term)))
                {
                    group.Variants.Add(new SearchVariant(term, matched));
                }
            }
        }
        return groups;
    }

    private HashSet<string> FtsSearchCandidates(IReadOnlyList<SearchQueryGroup> groups)
    {
        var terms = groups.SelectMany(group => group.Variants)
            .Select(variant => variant.Value)
            .Where(value => value.EnumerateRunes().Count() >= 3)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        if (terms.Length == 0)
        {
            return [];
        }
        var matchQuery = string.Join(" OR ", terms.Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT article_id FROM article_search_fts WHERE article_search_fts MATCH $match_query";
        command.Parameters.AddWithValue("$match_query", matchQuery);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    private static ScoredSearchCandidate? ScoreSearchCandidate(
        SearchCandidate candidate,
        IReadOnlyList<SearchQueryGroup> groups,
        string normalizedQuery)
    {
        long totalScore = 0;
        long matchedGroups = 0;
        var reasons = new List<(long Score, string Reason)>();
        foreach (var group in groups)
        {
            (long Score, string Reason)? best = null;
            foreach (var variant in group.Variants)
            {
                var fields = new (string Label, string Value, long Score)[]
                {
                    ("タイトル", candidate.NormalizedTitle, 8), ("症状", candidate.Symptoms, 8),
                    ("エラーコード", candidate.ErrorCodes, 8), ("タグ", candidate.NormalizedTags, 6),
                    ("検索用語", candidate.SearchTerms, 6), ("対象", candidate.Targets, 6),
                    ("概要", candidate.NormalizedSummary, 4), ("想定原因", candidate.Causes, 4),
                    ("回答", candidate.Body, 1), ("対応手順", candidate.Procedures, 1),
                    ("注意事項", candidate.Cautions, 1)
                };
                foreach (var field in fields)
                {
                    if (!field.Value.Contains(variant.Value, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var exactTitle = field.Label == "タイトル" && normalizedQuery.Length > 0 && candidate.NormalizedTitle == normalizedQuery;
                    var exactError = field.Label == "エラーコード" && candidate.ErrorCodes
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Contains(variant.Value, StringComparer.Ordinal);
                    var score = exactTitle || exactError ? 10 : field.Score;
                    if (variant.SynonymFrom is not null)
                    {
                        score = Math.Max(1, score - 1);
                    }
                    var reason = variant.SynonymFrom is not null
                        ? $"同義語：{variant.SynonymFrom} → {variant.Value}（{field.Label}）"
                        : $"{field.Label}「{group.Display}」";
                    if (best is null || score > best.Value.Score)
                    {
                        best = (score, reason);
                    }
                }
            }
            if (best is not null)
            {
                totalScore += best.Value.Score;
                matchedGroups++;
                reasons.Add(best.Value);
            }
        }
        if (matchedGroups == 0)
        {
            return null;
        }
        var matchReasons = reasons.OrderByDescending(reason => reason.Score)
            .ThenBy(reason => reason.Reason, StringComparer.Ordinal)
            .Select(reason => reason.Reason)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        return new ScoredSearchCandidate(candidate.Item with { MatchReasons = matchReasons }, totalScore, matchedGroups);
    }

    private static int CompareScoredCandidates(
        ScoredSearchCandidate left,
        ScoredSearchCandidate right,
        string sort,
        bool withRelevance)
    {
        if (withRelevance)
        {
            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0) return byScore;
            var byGroups = right.MatchedGroups.CompareTo(left.MatchedGroups);
            if (byGroups != 0) return byGroups;
        }
        var selected = sort switch
        {
            SearchSorts.UpdatedDesc => string.CompareOrdinal(right.Item.UpdatedAt, left.Item.UpdatedAt),
            SearchSorts.UpdatedAsc => string.CompareOrdinal(left.Item.UpdatedAt, right.Item.UpdatedAt),
            SearchSorts.ImportanceDesc => right.Item.Importance.CompareTo(left.Item.Importance),
            SearchSorts.ImportanceAsc => left.Item.Importance.CompareTo(right.Item.Importance),
            _ => 0
        };
        if (selected != 0) return selected;
        var updated = string.CompareOrdinal(right.Item.UpdatedAt, left.Item.UpdatedAt);
        return updated != 0 ? updated : string.CompareOrdinal(right.Item.Id, left.Item.Id);
    }

    private IReadOnlyList<SynonymGroupSummary> ListSynonymGroupsUnlocked()
    {
        var groups = new List<SynonymGroupSummary>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, display_name, updated_at FROM synonym_groups ORDER BY display_name, id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var displayName = reader.GetString(1);
            var normalizedDisplay = NormalizeSearchText(displayName);
            var terms = new List<string>();
            using var termCommand = _connection.CreateCommand();
            termCommand.CommandText = "SELECT term, normalized_term FROM synonyms WHERE group_id = $id ORDER BY normalized_term, id";
            termCommand.Parameters.AddWithValue("$id", id);
            using var termReader = termCommand.ExecuteReader();
            while (termReader.Read())
            {
                if (termReader.GetString(1) != normalizedDisplay)
                {
                    terms.Add(termReader.GetString(0));
                }
            }
            groups.Add(new SynonymGroupSummary(id, displayName, terms, reader.GetString(2)));
        }
        return groups;
    }

    private static List<(string Term, string Normalized)> ValidateSynonymValues(
        string displayName,
        IReadOnlyList<string> terms)
    {
        displayName = displayName.Trim();
        if (!ValidSingleLine(displayName, 100))
        {
            throw new AppProblemException(new AppProblem(
                "SYN-001", "代表語は1～100文字の1行テキストで入力してください。", "代表語を確認して、もう一度保存してください。"));
        }
        if (terms.Count > 50)
        {
            throw new AppProblemException(new AppProblem(
                "SYN-001", "同義語は50件以内で登録してください。", "不要な同義語を削除して、もう一度保存してください。"));
        }
        var values = new List<(string Term, string Normalized)>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in new[] { displayName }.Concat(terms).Select(term => term.Trim()))
        {
            if (!ValidSingleLine(term, 100))
            {
                throw new AppProblemException(new AppProblem(
                    "SYN-001", "同義語は1～100文字の1行テキストで入力してください。", "空の項目や改行を削除し、入力内容を短くしてください。"));
            }
            var normalized = NormalizeSearchText(term);
            if (unique.Add(normalized))
            {
                values.Add((term, normalized));
            }
        }
        return values;
    }

    private static List<SearchQueryGroup> SearchQueryGroups(string query)
    {
        var normalized = NormalizeSearchText(query);
        var segments = new List<string>();
        var current = new StringBuilder();
        bool? currentAscii = null;
        void Flush()
        {
            if (current.Length > 0)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
        }
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune))
            {
                Flush();
                currentAscii = null;
                continue;
            }
            var ascii = rune.IsAscii;
            if (currentAscii is not null && currentAscii != ascii)
            {
                Flush();
            }
            currentAscii = ascii;
            current.Append(rune.ToString());
        }
        Flush();
        var groups = new List<SearchQueryGroup>();
        foreach (var segment in segments.Where(segment => !SearchStopWords.Contains(segment)))
        {
            var runes = segment.EnumerateRunes().ToArray();
            var values = new List<string> { segment };
            if (runes.Length > 3 && runes.Any(rune => !rune.IsAscii))
            {
                for (var index = 0; index <= runes.Length - 3; index++)
                {
                    values.Add(string.Concat(runes.Skip(index).Take(3).Select(rune => rune.ToString())));
                }
            }
            groups.Add(new SearchQueryGroup(
                segment,
                values.Distinct(StringComparer.Ordinal).Select(value => new SearchVariant(value, null)).ToList()));
        }
        return groups;
    }

    private static string NormalizeSearchText(string value) => string.Join(
        ' ',
        value.Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void ValidateSearchInput(SearchArticlesInput input)
    {
        if (!SearchScopes.IsValid(input.Scope) || !SearchSorts.IsValid(input.Sort))
        {
            throw InvalidInput("検索条件が正しくありません。", "FAQ一覧を再読み込みしてください。");
        }
        if (input.Scope == SearchScopes.Current && string.IsNullOrWhiteSpace(input.CategoryId))
        {
            throw InvalidInput("現在の分類を検索するには分類の選択が必要です。", "分類を選択してください。");
        }
        if (input.Query.EnumerateRunes().Count() > 500)
        {
            throw InvalidInput("検索語は500文字以内で入力してください。", "検索語を短くしてください。");
        }
    }

    private static string ValidateCategoryName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.EnumerateRunes().Count() is < 1 or > 100)
        {
            throw new AppProblemException(new AppProblem(
                "CAT-001", "分類名は1～100文字で入力してください。", "分類名を確認して、もう一度保存してください。"));
        }
        return trimmed;
    }

    private static string ValidateCategoryDescription(string description)
    {
        var trimmed = description.Trim();
        if (trimmed.EnumerateRunes().Count() > 500)
        {
            throw new AppProblemException(new AppProblem(
                "CAT-001", "分類の説明は500文字以内で入力してください。", "説明を短くして、もう一度保存してください。"));
        }
        return trimmed;
    }

    private bool CategoryExists(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM categories WHERE id = $id)";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private long CountById(string table, string id)
    {
        var sql = table switch
        {
            "articles" => "SELECT COUNT(*) FROM articles WHERE id = $id",
            "synonym_groups" => "SELECT COUNT(*) FROM synonym_groups WHERE id = $id",
            _ => throw new InvalidOperationException("合成データの確認先が正しくありません。")
        };
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void InsertSyntheticArticle(
        SqliteTransaction transaction,
        string id,
        string categoryId,
        string title,
        string summary,
        string body,
        string status,
        long importance,
        bool isHidden,
        string? deletedAt,
        string symptoms,
        string causes,
        string targets,
        string errorCodes,
        string searchTerms)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        var suffix = id[^2..];
        var updatedAt = $"2026-08-{Math.Clamp(int.Parse(suffix, CultureInfo.InvariantCulture), 1, 28):00}T00:00:00Z";
        var bodyDocJson = JsonSerializer.Serialize(new
        {
            type = "doc",
            content = new[]
            {
                new
                {
                    type = "paragraph",
                    content = new[] { new { type = "text", text = body } }
                }
            }
        });
        using (var article = connection.CreateCommand())
        {
            article.Transaction = transaction;
            article.CommandText = """
                INSERT INTO articles(
                    id, category_id, title, normalized_title, summary, body_doc_json,
                    body_format_version, body_plain_text, procedure_text, caution_text,
                    status, importance, created_at, updated_at, deleted_at,
                    new_badge_until, updated_badge_until, is_hidden, created_by_user_id, updated_by_user_id
                ) VALUES (
                    $id, $category_id, $title, $normalized_title, $summary, $body_doc_json,
                    1, $body, '1. 合成手順を確認します。', '合成データ以外では実施しません。',
                    $status, $importance, $updated_at, $updated_at, $deleted_at,
                    NULL, NULL, $is_hidden, $user_id, $user_id
                )
                """;
            article.Parameters.AddWithValue("$id", id);
            article.Parameters.AddWithValue("$category_id", categoryId);
            article.Parameters.AddWithValue("$title", title);
            article.Parameters.AddWithValue("$normalized_title", NormalizeSearchText(title));
            article.Parameters.AddWithValue("$summary", summary);
            article.Parameters.AddWithValue("$body_doc_json", bodyDocJson);
            article.Parameters.AddWithValue("$body", body);
            article.Parameters.AddWithValue("$status", status);
            article.Parameters.AddWithValue("$importance", importance);
            article.Parameters.AddWithValue("$updated_at", updatedAt);
            article.Parameters.AddWithValue("$deleted_at", (object?)deletedAt ?? DBNull.Value);
            article.Parameters.AddWithValue("$is_hidden", isHidden ? 1 : 0);
            article.Parameters.AddWithValue("$user_id", InitialAdminUserId);
            article.ExecuteNonQuery();
        }
        InsertSyntheticArticleValues(transaction, "article_symptoms", id, symptoms);
        InsertSyntheticArticleValues(transaction, "article_causes", id, causes);
        InsertSyntheticArticleValues(transaction, "article_targets", id, targets);
        InsertSyntheticArticleValues(transaction, "article_error_codes", id, errorCodes);
        InsertSyntheticArticleValues(transaction, "article_search_terms", id, searchTerms);
        using (var document = connection.CreateCommand())
        {
            document.Transaction = transaction;
            document.CommandText = """
                INSERT INTO article_search_documents(
                    article_id, title, summary, body, symptoms, causes, targets, error_codes, tags, search_terms
                ) VALUES (
                    $id, $title, $summary, $body, $symptoms, $causes, $targets, $error_codes, '', $search_terms
                )
                """;
            document.Parameters.AddWithValue("$id", id);
            document.Parameters.AddWithValue("$title", NormalizeSearchText(title));
            document.Parameters.AddWithValue("$summary", NormalizeSearchText(summary));
            document.Parameters.AddWithValue("$body", NormalizeSearchText(body));
            document.Parameters.AddWithValue("$symptoms", NormalizeSearchText(symptoms));
            document.Parameters.AddWithValue("$causes", NormalizeSearchText(causes));
            document.Parameters.AddWithValue("$targets", NormalizeSearchText(targets));
            document.Parameters.AddWithValue("$error_codes", NormalizeSearchText(errorCodes));
            document.Parameters.AddWithValue("$search_terms", NormalizeSearchText(searchTerms));
            document.ExecuteNonQuery();
        }
        RebuildSyntheticFts(transaction, id);
    }

    private static void InsertSyntheticArticleValues(
        SqliteTransaction transaction,
        string table,
        string articleId,
        string storedValues)
    {
        var safeTable = table switch
        {
            "article_symptoms" => "article_symptoms",
            "article_causes" => "article_causes",
            "article_targets" => "article_targets",
            "article_error_codes" => "article_error_codes",
            "article_search_terms" => "article_search_terms",
            _ => throw new InvalidOperationException("合成FAQ検索情報の種類が正しくありません。")
        };
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        var values = SplitStoredValues(storedValues);
        for (var index = 0; index < values.Length; index++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO {safeTable}(id, article_id, value, normalized_value, sort_order)
                VALUES ($id, $article_id, $value, $normalized_value, $sort_order)
                """;
            insert.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
            insert.Parameters.AddWithValue("$article_id", articleId);
            insert.Parameters.AddWithValue("$value", values[index]);
            insert.Parameters.AddWithValue("$normalized_value", NormalizeSearchText(values[index]));
            insert.Parameters.AddWithValue("$sort_order", index);
            insert.ExecuteNonQuery();
        }
    }

    private static void InsertSyntheticTag(
        SqliteTransaction transaction,
        string articleId,
        string tagId,
        string name)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        using (var tag = connection.CreateCommand())
        {
            tag.Transaction = transaction;
            tag.CommandText = "INSERT INTO tags(id, name, normalized_name, created_at, updated_at) VALUES ($id, $name, $normalized_name, $now, $now)";
            tag.Parameters.AddWithValue("$id", tagId);
            tag.Parameters.AddWithValue("$name", name);
            tag.Parameters.AddWithValue("$normalized_name", NormalizeSearchText(name));
            tag.Parameters.AddWithValue("$now", UtcNow());
            tag.ExecuteNonQuery();
        }
        using (var link = connection.CreateCommand())
        {
            link.Transaction = transaction;
            link.CommandText = "INSERT INTO article_tags(article_id, tag_id) VALUES ($article_id, $tag_id)";
            link.Parameters.AddWithValue("$article_id", articleId);
            link.Parameters.AddWithValue("$tag_id", tagId);
            link.ExecuteNonQuery();
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE article_search_documents SET tags = $tags WHERE article_id = $article_id";
            update.Parameters.AddWithValue("$tags", NormalizeSearchText(name));
            update.Parameters.AddWithValue("$article_id", articleId);
            update.ExecuteNonQuery();
        }
        RebuildSyntheticFts(transaction, articleId);
    }

    private static void RebuildSyntheticFts(SqliteTransaction transaction, string articleId)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("DBトランザクションが終了しています。");
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM article_search_fts WHERE article_id = $id";
            delete.Parameters.AddWithValue("$id", articleId);
            delete.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO article_search_fts(
                article_id, title, summary, body, symptoms, causes, targets, error_codes, tags, search_terms
            )
            SELECT article_id, title, summary, body, symptoms, causes, targets, error_codes, tags, search_terms
              FROM article_search_documents WHERE article_id = $id
            """;
        insert.Parameters.AddWithValue("$id", articleId);
        insert.ExecuteNonQuery();
    }

    private void InsertSyntheticSynonymGroup()
    {
        const string groupId = "73000000-0000-7000-8000-000000000001";
        using var transaction = _connection.BeginTransaction();
        var now = UtcNow();
        using (var group = _connection.CreateCommand())
        {
            group.Transaction = transaction;
            group.CommandText = "INSERT INTO synonym_groups(id, display_name, created_at, updated_at) VALUES ($id, 'パソコン', $now, $now)";
            group.Parameters.AddWithValue("$id", groupId);
            group.Parameters.AddWithValue("$now", now);
            group.ExecuteNonQuery();
        }
        foreach (var term in new[] { "パソコン", "PC" })
        {
            using var synonym = _connection.CreateCommand();
            synonym.Transaction = transaction;
            synonym.CommandText = "INSERT INTO synonyms(id, group_id, term, normalized_term) VALUES ($id, $group_id, $term, $normalized)";
            synonym.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
            synonym.Parameters.AddWithValue("$group_id", groupId);
            synonym.Parameters.AddWithValue("$term", term);
            synonym.Parameters.AddWithValue("$normalized", NormalizeSearchText(term));
            synonym.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static string[] SplitStoredValues(string value) => value.Split(
        ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool ValidSingleLine(string value, int maximumRunes) =>
        value.EnumerateRunes().Count() is >= 1 &&
        value.EnumerateRunes().Count() <= maximumRunes &&
        !value.Contains('\r', StringComparison.Ordinal) &&
        !value.Contains('\n', StringComparison.Ordinal);

    private static AppProblemException CategoryNotFound() => new(new AppProblem(
        "CAT-002", "指定した分類が見つかりません。", "分類一覧を更新して、選び直してください。"));

    private static AppProblemException CategoryCycle() => new(new AppProblem(
        "CAT-006", "分類を自分自身または配下分類の下へ移動できません。", "別の分類を移動先として選択してください。"));

    private static AppProblemException SynonymNotFound() => new(new AppProblem(
        "SYN-002", "指定した同義語グループが見つかりません。", "同義語一覧を更新して、選び直してください。"));

    private static AppProblemException InvalidInput(string message, string action) =>
        new(new AppProblem("SYS-001", message, action));

    private sealed record SearchVariant(string Value, string? SynonymFrom);

    private sealed record SearchQueryGroup(string Display, List<SearchVariant> Variants);

    private sealed record SearchCandidate(
        SearchArticleListItem Item,
        string NormalizedTitle,
        string NormalizedSummary,
        string Body,
        string Symptoms,
        string Causes,
        string Targets,
        string ErrorCodes,
        string NormalizedTags,
        string SearchTerms,
        string Procedures,
        string Cautions);

    private sealed record ScoredSearchCandidate(SearchArticleListItem Item, long Score, long MatchedGroups);
}
