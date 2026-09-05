using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    internal TagMasterItem SaveTag(string? id, string name) => ExecuteLocked(() =>
    {
        var validatedName = ValidateTagMasterName(name);
        var normalizedName = NormalizeSearchText(validatedName);
        using var transaction = _connection.BeginTransaction();
        using (var duplicate = _connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT EXISTS(SELECT 1 FROM tags WHERE normalized_name = $name AND id <> $id)";
            duplicate.Parameters.AddWithValue("$name", normalizedName);
            duplicate.Parameters.AddWithValue("$id", id ?? string.Empty);
            if (Convert.ToInt64(duplicate.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                throw new AppProblemException(new AppProblem(
                    "TAG-002",
                    "同じ名前のタグがすでに登録されています。",
                    "既存のタグを使用するか、別の名前を入力してください。"));
            }
        }

        var tagId = id ?? Guid.CreateVersion7().ToString();
        var affectedArticleIds = new List<string>();
        if (id is not null)
        {
            GetTagUsageCount(transaction, tagId);
            using var affected = _connection.CreateCommand();
            affected.Transaction = transaction;
            affected.CommandText = "SELECT article_id FROM article_tags WHERE tag_id = $id ORDER BY article_id";
            affected.Parameters.AddWithValue("$id", tagId);
            using var reader = affected.ExecuteReader();
            while (reader.Read())
            {
                affectedArticleIds.Add(reader.GetString(0));
            }
        }

        var now = UtcNow();
        using (var save = _connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = id is null
                ? "INSERT INTO tags(id, name, normalized_name, created_at, updated_at) VALUES ($id, $name, $normalized, $now, $now)"
                : "UPDATE tags SET name = $name, normalized_name = $normalized, updated_at = $now WHERE id = $id";
            save.Parameters.AddWithValue("$id", tagId);
            save.Parameters.AddWithValue("$name", validatedName);
            save.Parameters.AddWithValue("$normalized", normalizedName);
            save.Parameters.AddWithValue("$now", now);
            save.ExecuteNonQuery();
        }

        foreach (var articleId in affectedArticleIds)
        {
            RefreshArticleTagSearchIndex(transaction, articleId);
        }
        var result = new TagMasterItem(tagId, validatedName, affectedArticleIds.Count, now);
        transaction.Commit();
        return result;
    });

    internal void DeleteTag(string id) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var usageCount = GetTagUsageCount(transaction, id);
        if (usageCount > 0)
        {
            throw new AppProblemException(new AppProblem(
                "TAG-003",
                $"このタグは{usageCount}件のFAQで使用されているため削除できません。",
                "タグ名を変更するか、使用中のFAQからタグを外してから削除してください。"));
        }
        using var delete = _connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM tags WHERE id = $id";
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();
        transaction.Commit();
    });

    private long GetTagUsageCount(SqliteTransaction transaction, string id)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT (SELECT COUNT(*) FROM article_tags WHERE tag_id = tags.id) FROM tags WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is null ? throw TagMasterNotFound() : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private void RefreshArticleTagSearchIndex(SqliteTransaction transaction, string articleId)
    {
        string title;
        string summary;
        string body;
        using (var article = _connection.CreateCommand())
        {
            article.Transaction = transaction;
            article.CommandText = "SELECT title, summary, body_plain_text FROM articles WHERE id = $id";
            article.Parameters.AddWithValue("$id", articleId);
            using var reader = article.ExecuteReader();
            if (!reader.Read())
            {
                throw ArticleNotFoundForEditing();
            }
            title = reader.GetString(0);
            summary = reader.GetString(1);
            body = reader.GetString(2);
        }

        var normalizedTags = new List<string>();
        using (var tags = _connection.CreateCommand())
        {
            tags.Transaction = transaction;
            tags.CommandText = """
                SELECT tag.normalized_name
                  FROM article_tags article_tag
                  JOIN tags tag ON tag.id = article_tag.tag_id
                 WHERE article_tag.article_id = $id
                 ORDER BY tag.id
                """;
            tags.Parameters.AddWithValue("$id", articleId);
            using var reader = tags.ExecuteReader();
            while (reader.Read())
            {
                normalizedTags.Add(reader.GetString(0));
            }
        }

        // タグ名は派生メタデータ。FAQ本文・旧版補足項目・更新者・更新日時は変更しない。
        UpsertArticleSearchDocument(transaction, articleId, title, summary, body, normalizedTags);
        RebuildArticleFts(transaction, articleId);
    }

    private static string ValidateTagMasterName(string name)
    {
        var value = name?.Trim() ?? string.Empty;
        if (CountRunes(value) is < 1 or > 100 || value.Contains('\r') || value.Contains('\n'))
        {
            throw new AppProblemException(new AppProblem(
                "TAG-001",
                "タグ名は1～100文字の1行テキストで入力してください。",
                "空欄、改行、長すぎる文字列を修正してください。"));
        }
        return value;
    }

    private static AppProblemException TagMasterNotFound() => new(new AppProblem(
        "TAG-004",
        "対象のタグが見つかりません。",
        "タグマスターを再読み込みして、もう一度お試しください。"));
}
