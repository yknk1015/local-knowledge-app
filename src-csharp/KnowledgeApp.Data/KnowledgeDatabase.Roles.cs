using System.Globalization;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    internal UserSummary SetUserRole(string id, string role) => ExecuteLocked(() =>
    {
        ValidateRole(role);
        var current = GetUserUnlocked(id);
        if (current.Role == role) return current;
        if (current.Role == UserRoles.Admin && current.IsActive)
        {
            using var count = _connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM users WHERE role = 'admin' AND is_active = 1";
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) <= 1)
                throw new AppProblemException(new AppProblem("USR-002", "最後の有効な管理者の権限は変更できません。", "別の管理者を追加してから変更してください。"));
        }
        using var update = _connection.CreateCommand();
        update.CommandText = "UPDATE users SET role = $role, updated_at = $now WHERE id = $id";
        update.Parameters.AddWithValue("$role", role);
        update.Parameters.AddWithValue("$now", UtcNow());
        update.Parameters.AddWithValue("$id", id);
        update.ExecuteNonQuery(); // Trigger invalidates sessions and recovery keys.
        return GetUserUnlocked(id);
    });

    internal bool IsPublicArticle(string id) => ExecuteLocked(() =>
    {
        using var query = _connection.CreateCommand();
        query.CommandText = """
            SELECT EXISTS(SELECT 1 FROM articles a WHERE a.id = $id AND a.status = 'published'
                AND a.deleted_at IS NULL AND a.is_hidden = 0
                AND NOT EXISTS(SELECT 1 FROM article_merge_relations r WHERE r.source_article_id = a.id))
            """;
        query.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(query.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    });

    internal void RequirePublicArticle(string id)
    {
        if (!IsPublicArticle(id)) throw ArticleNotFound();
    }
}
