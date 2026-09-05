using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    internal bool IsCodexProposalAccepted(string requestId) => ExecuteLocked(() =>
    {
        using var command = CodexCommand(null,
            "SELECT EXISTS(SELECT 1 FROM codex_proposal_receipts WHERE request_id = $id)",
            ("$id", requestId));
        return CodexExists(command);
    });

    internal void RecordCodexProposal(CodexFaqProposal proposal) => ExecuteLocked(() =>
    {
        var payload = SerializeCodexProposal(proposal);
        using var transaction = _connection.BeginTransaction();
        using (var existing = CodexCommand(transaction,
                   "SELECT payload_json FROM codex_proposal_history WHERE request_id = $id",
                   ("$id", proposal.RequestId)))
        {
            if (existing.ExecuteScalar() is string existingPayload)
            {
                EnsureSameCodexProposal(ParseStoredCodexProposal(existingPayload), proposal);
                transaction.Commit();
                return;
            }
        }
        using var insert = CodexCommand(transaction, """
            INSERT INTO codex_proposal_history(
                request_id, series_id, proposal_kind, payload_json, status, received_at
            ) VALUES ($id, $series, $kind, $payload, 'pending', $now)
            """,
            ("$id", proposal.RequestId), ("$series", proposal.SeriesId ?? proposal.RequestId),
            ("$kind", proposal.ProposalKind), ("$payload", payload), ("$now", UtcNow()));
        insert.ExecuteNonQuery();
        transaction.Commit();
    });

    internal CodexFaqProposal GetPendingCodexProposal(string requestId) => ExecuteLocked(() =>
    {
        using var command = CodexCommand(null,
            "SELECT payload_json FROM codex_proposal_history WHERE request_id = $id AND status = 'pending'",
            ("$id", requestId));
        return ParseStoredCodexProposal(command.ExecuteScalar() as string ?? throw PendingCodexProposalNotFound());
    });

    internal IReadOnlyList<CodexFaqProposal> ListPendingCodexProposals() => ExecuteLocked(() =>
    {
        using var command = CodexCommand(null,
            "SELECT payload_json FROM codex_proposal_history WHERE status = 'pending' ORDER BY history_id DESC");
        using var reader = command.ExecuteReader();
        var proposals = new List<CodexFaqProposal>();
        while (reader.Read())
        {
            proposals.Add(ParseStoredCodexProposal(reader.GetString(0)));
        }
        return proposals;
    });

    internal IReadOnlyList<CodexProposalHistoryItem> ListCodexProposalHistory() => ExecuteLocked(() =>
    {
        using var command = CodexCommand(null, """
            SELECT h.history_id, h.payload_json, h.status, h.received_at, h.decided_at,
                   h.accepted_article_id,
                   CASE WHEN h.status = 'rejected' AND NOT EXISTS (
                       SELECT 1 FROM codex_proposal_history newer
                        WHERE newer.series_id = h.series_id AND newer.history_id > h.history_id
                   ) THEN 1 ELSE 0 END
              FROM codex_proposal_history h
             WHERE h.status <> 'pending'
             ORDER BY h.history_id DESC
            """);
        using var reader = command.ExecuteReader();
        var history = new List<CodexProposalHistoryItem>();
        while (reader.Read())
        {
            history.Add(new CodexProposalHistoryItem(
                reader.GetInt64(0), ParseStoredCodexProposal(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6) == 1));
        }
        return history;
    });

    internal void RejectCodexProposal(string requestId) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        using var command = CodexCommand(transaction,
            "UPDATE codex_proposal_history SET status = 'rejected', decided_at = $now WHERE request_id = $id AND status = 'pending'",
            ("$id", requestId), ("$now", UtcNow()));
        if (command.ExecuteNonQuery() != 1)
        {
            throw PendingCodexProposalNotFound();
        }
        transaction.Commit();
    });

    internal void ReopenRejectedCodexProposal(string requestId) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        long historyId;
        string seriesId;
        using (var command = CodexCommand(transaction,
                   "SELECT history_id, series_id, status FROM codex_proposal_history WHERE request_id = $id",
                   ("$id", requestId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw CodexProblem("CDX-014", "指定したCodex提案履歴が見つかりません。",
                    "履歴一覧を更新し、もう一度選択してください。");
            }
            if (reader.GetString(2) != "rejected")
            {
                throw CodexProblem("CDX-014", "このCodex提案は再検討へ戻せません。",
                    "却下済みの提案を選択してください。");
            }
            historyId = reader.GetInt64(0);
            seriesId = reader.GetString(1);
        }
        using (var newer = CodexCommand(transaction,
                   "SELECT EXISTS(SELECT 1 FROM codex_proposal_history WHERE series_id = $series AND history_id > $history)",
                   ("$series", seriesId), ("$history", historyId)))
        {
            if (CodexExists(newer))
            {
                throw CodexProblem("CDX-015", "同じ依頼に、これより新しいCodex提案があります。",
                    "取り違えを防ぐため、同じ依頼では最新の却下案だけを再検討できます。");
            }
        }
        using var update = CodexCommand(transaction,
            "UPDATE codex_proposal_history SET status = 'pending', decided_at = NULL WHERE request_id = $id AND status = 'rejected'",
            ("$id", requestId));
        if (update.ExecuteNonQuery() != 1)
        {
            throw PendingCodexProposalNotFound();
        }
        transaction.Commit();
    });

    internal (ArticleDetail Article, CategorySummary? CreatedCategory) AcceptCodexProposal(
        CodexFaqProposal proposal,
        string articleId,
        string categoryId,
        bool createProposedCategory,
        string bodyPlainText,
        string actorUserId) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        EnsureCodexProposalCanBeAccepted(transaction, proposal);
        switch (proposal.ProposalKind)
        {
            case CodexProposalKinds.Create when proposal.SourceArticles.Count == 0:
                break;
            case CodexProposalKinds.Merge when proposal.SourceArticles.Count is >= 2 and <= 10:
                VerifyCodexSourceVersions(transaction, proposal.SourceArticles);
                break;
            default:
                throw CodexProblem("CDX-002", "新規・統合提案の元FAQ情報が正しくありません。",
                    "KnowledgeAppで対象を選び直し、Codexへ再依頼してください。");
        }

        CategorySummary? createdCategory = null;
        if (createProposedCategory)
        {
            var category = proposal.NewCategoryProposal ?? throw CodexProblem(
                "CDX-005", "この提案に新規分類案がありません。", "現在の分類から所属分類を選択してください。");
            createdCategory = InsertCategory(transaction, categoryId,
                ValidateCategoryName(category.Name), ValidateCategoryDescription(category.Description),
                EmptyToNull(category.ParentCategoryId));
        }
        else
        {
            using var category = CodexCommand(transaction,
                "SELECT EXISTS(SELECT 1 FROM categories WHERE id = $id)", ("$id", categoryId));
            if (!CodexExists(category))
            {
                throw CodexProblem("CDX-005", "選択した分類が現在の分類一覧にありません。",
                    "提案一覧を更新し、所属分類を選び直してください。");
            }
        }

        var now = UtcNow();
        using (var insert = CodexCommand(transaction, """
                   INSERT INTO articles(
                       id, category_id, title, normalized_title, summary, body_doc_json,
                       body_format_version, body_plain_text, status, importance, created_at, updated_at,
                       new_badge_until, updated_badge_until, is_hidden,
                       created_by_user_id, updated_by_user_id
                   ) VALUES ($id, $category, $title, $normalized_title, $summary, $body_doc,
                       2, $body, 'draft', $importance, $now, $now, NULL, NULL, 0, $actor, $actor)
                   """,
                   ("$id", articleId), ("$category", categoryId), ("$title", proposal.Faq.Title.Trim()),
                   ("$normalized_title", NormalizeSearchText(proposal.Faq.Title.Trim())),
                   ("$summary", proposal.Faq.Summary.Trim()), ("$body_doc", proposal.Faq.BodyDoc.GetRawText()),
                   ("$body", bodyPlainText), ("$importance", proposal.Faq.Importance),
                   ("$now", now), ("$actor", actorUserId)))
        {
            insert.ExecuteNonQuery();
        }
        UpsertCodexSearchDocument(transaction, articleId, proposal.Faq, bodyPlainText);
        RebuildArticleFts(transaction, articleId);
        AcceptCodexHistoryAndReceipt(transaction, proposal.RequestId, articleId, now);
        transaction.Commit();
        return (GetArticle(articleId), createdCategory is null ? null : createdCategory with { ArticleCount = 1 });
    });

    internal ArticleDetail AcceptCodexRevision(
        CodexFaqProposal proposal,
        string bodyPlainText,
        string actorUserId) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        EnsureCodexProposalCanBeAccepted(transaction, proposal);
        if (proposal.ProposalKind != CodexProposalKinds.Revise || proposal.SourceArticles.Count != 1)
        {
            throw CodexProblem("CDX-002", "修正提案には1件の元FAQが必要です。",
                "KnowledgeAppで修正対象を選び直し、Codexへ再依頼してください。");
        }
        VerifyCodexSourceVersions(transaction, proposal.SourceArticles);
        var articleId = proposal.SourceArticles[0].ArticleId;
        using (var currentBody = CodexCommand(transaction,
                   "SELECT body_doc_json FROM articles WHERE id = $id", ("$id", articleId)))
        {
            using var document = JsonDocument.Parse(currentBody.ExecuteScalar() as string ?? throw ArticleNotFound(), StructuredJsonBoundary.DocumentOptions);
            CodexProposalFiles.ValidatePreservedImages(document.RootElement, proposal.Faq.BodyDoc);
        }
        var now = UtcNow();
        using (var update = CodexCommand(transaction, """
                   UPDATE articles SET title = $title, normalized_title = $normalized_title,
                       summary = $summary, body_doc_json = $body_doc, body_format_version = 2,
                       body_plain_text = $body, importance = $importance,
                       updated_at = $now, updated_by_user_id = $actor
                    WHERE id = $id AND deleted_at IS NULL
                   """,
                   ("$id", articleId), ("$title", proposal.Faq.Title.Trim()),
                   ("$normalized_title", NormalizeSearchText(proposal.Faq.Title.Trim())),
                   ("$summary", proposal.Faq.Summary.Trim()), ("$body_doc", proposal.Faq.BodyDoc.GetRawText()),
                   ("$body", bodyPlainText), ("$importance", proposal.Faq.Importance),
                   ("$now", now), ("$actor", actorUserId)))
        {
            if (update.ExecuteNonQuery() != 1)
            {
                throw ArticleNotFound();
            }
        }
        // Only these four search fields are replaced. Legacy search fields, tags,
        // related articles, state, badges and attachment records remain untouched.
        UpsertCodexSearchDocument(transaction, articleId, proposal.Faq, bodyPlainText);
        RebuildArticleFts(transaction, articleId);
        AcceptCodexHistoryAndReceipt(transaction, proposal.RequestId, articleId, now);
        transaction.Commit();
        return GetArticle(articleId);
    });

    internal CodexMergePublicationContext? GetCodexMergePublicationContext(string targetArticleId) =>
        ExecuteLocked(() =>
        {
            string? payload;
            using (var command = CodexCommand(null, """
                       SELECT payload_json FROM codex_proposal_history
                        WHERE accepted_article_id = $id AND proposal_kind = 'merge' AND status = 'accepted'
                        ORDER BY history_id DESC LIMIT 1
                       """, ("$id", targetArticleId)))
            {
                payload = command.ExecuteScalar() as string;
            }
            if (payload is null)
            {
                return null;
            }
            var proposal = ParseStoredCodexProposal(payload);
            var sources = new List<CodexMergeSourcePreview>();
            var canMarkMerged = proposal.SourceArticles.Count > 0;
            var allSourcesMerged = proposal.SourceArticles.Count > 0;
            foreach (var source in proposal.SourceArticles)
            {
                var title = source.ArticleId;
                string? status = null;
                string? currentUpdatedAt = null;
                string? deletedAt = null;
                string? mergeTargetId = null;
                using (var current = CodexCommand(null, """
                           SELECT article.title, article.status, article.updated_at, article.deleted_at,
                                  merge_relation.target_article_id
                             FROM articles article
                             LEFT JOIN article_merge_relations merge_relation ON merge_relation.source_article_id = article.id
                            WHERE article.id = $id
                           """, ("$id", source.ArticleId)))
                using (var reader = current.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        title = reader.GetString(0);
                        status = reader.GetString(1);
                        currentUpdatedAt = reader.GetString(2);
                        deletedAt = reader.IsDBNull(3) ? null : reader.GetString(3);
                        mergeTargetId = reader.IsDBNull(4) ? null : reader.GetString(4);
                    }
                }
                var isCurrent = currentUpdatedAt == source.SourceUpdatedAt && deletedAt is null;
                var isMerged = mergeTargetId == targetArticleId;
                var hasConflictingMerge = mergeTargetId is not null && !isMerged;
                canMarkMerged &= isMerged || (isCurrent && !hasConflictingMerge);
                allSourcesMerged &= isMerged;
                sources.Add(new CodexMergeSourcePreview(
                    source.ArticleId, title, status, source.SourceUpdatedAt, currentUpdatedAt,
                    deletedAt, isCurrent, isMerged));
            }
            return new CodexMergePublicationContext(
                targetArticleId, sources, canMarkMerged && !allSourcesMerged, allSourcesMerged);
        });

    internal bool RequiresNewBadgeForMergePublication(
        string targetArticleId, SqliteTransaction? transaction = null) => ExecuteLocked(() =>
    {
        using var command = CodexCommand(transaction, """
            SELECT EXISTS(
                SELECT 1 FROM articles article
                 WHERE article.id = $id AND article.deleted_at IS NULL AND article.status <> 'published'
                   AND EXISTS (
                       SELECT 1 FROM codex_proposal_history history
                        WHERE history.accepted_article_id = article.id
                          AND history.proposal_kind = 'merge' AND history.status = 'accepted'
                   )
            )
            """, ("$id", targetArticleId));
        return CodexExists(command);
    });

    internal MarkCodexMergeSourcesResult MarkCodexMergeSources(string targetArticleId) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        using (var target = CodexCommand(transaction,
                   "SELECT status, new_badge_until, deleted_at, is_hidden FROM articles WHERE id = $id",
                   ("$id", targetArticleId)))
        using (var reader = target.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw ArticleNotFound();
            }
            if (!reader.IsDBNull(2) || reader.GetString(0) != ArticleStatuses.Published || reader.GetInt64(3) != 0)
            {
                throw CodexProblem("CDX-016", "統合元FAQを統合済みにする前に、統合FAQを公開して表示してください。",
                    "統合FAQの内容を確認し、公開かつ非表示OFFで保存してからもう一度お試しください。");
            }
            if (reader.IsDBNull(1))
            {
                throw CodexProblem("CDX-016", "統合FAQに新着フラグの表示終了日がありません。",
                    "統合FAQを編集し、新着フラグと表示終了日を設定してください。");
            }
        }
        CodexFaqProposal proposal;
        using (var history = CodexCommand(transaction, """
                   SELECT payload_json FROM codex_proposal_history
                    WHERE accepted_article_id = $id AND proposal_kind = 'merge' AND status = 'accepted'
                    ORDER BY history_id DESC LIMIT 1
                   """, ("$id", targetArticleId)))
        {
            proposal = ParseStoredCodexProposal(history.ExecuteScalar() as string ?? throw CodexProblem(
                "CDX-016", "このFAQの統合元情報が見つかりません。",
                "Codex提案履歴を確認し、元FAQはFAQ管理画面から整理してください。"));
        }
        var now = UtcNow();
        long markedCount = 0;
        foreach (var source in proposal.SourceArticles)
        {
            string updatedAt;
            string? deletedAt;
            string? mergeTargetId;
            using (var current = CodexCommand(transaction, """
                       SELECT article.updated_at, article.deleted_at, merge_relation.target_article_id
                         FROM articles article
                         LEFT JOIN article_merge_relations merge_relation ON merge_relation.source_article_id = article.id
                        WHERE article.id = $id
                       """, ("$id", source.ArticleId)))
            using (var reader = current.ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw CodexProblem("CDX-012", "統合元FAQが見つかりません。",
                        "元FAQは変更せず、FAQ管理画面で現在の状態を確認してください。");
                }
                updatedAt = reader.GetString(0);
                deletedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
                mergeTargetId = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
            if (mergeTargetId == targetArticleId)
            {
                continue;
            }
            if (mergeTargetId is not null)
            {
                throw CodexProblem("CDX-017", "統合元FAQの一部が別のFAQへ統合済みです。",
                    "FAQ管理画面で統合先を確認し、対象を整理してください。");
            }
            if (deletedAt is not null || updatedAt != source.SourceUpdatedAt)
            {
                throw CodexProblem("CDX-012", "統合案の承認後に、統合元FAQが変更または削除されています。",
                    "現在の内容を誤って非表示にしないため、自動整理を中止しました。FAQ管理画面で確認してください。");
            }
            using var insert = CodexCommand(transaction, """
                INSERT INTO article_merge_relations(source_article_id, target_article_id, source_updated_at, merged_at)
                VALUES ($source, $target, $updated, $now)
                """, ("$source", source.ArticleId), ("$target", targetArticleId),
                ("$updated", source.SourceUpdatedAt), ("$now", now));
            insert.ExecuteNonQuery();
            markedCount++;
        }
        transaction.Commit();
        return new MarkCodexMergeSourcesResult(targetArticleId, markedCount);
    });

    internal ArticleDetail ClearArticleMerge(string sourceArticleId) => ExecuteLocked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        using var command = CodexCommand(transaction,
            "DELETE FROM article_merge_relations WHERE source_article_id = $id", ("$id", sourceArticleId));
        if (command.ExecuteNonQuery() != 1)
        {
            throw CodexProblem("CDX-018", "このFAQは統合済みではありません。",
                "FAQの詳細を更新して、現在の状態を確認してください。");
        }
        transaction.Commit();
        return GetArticle(sourceArticleId);
    });

    private void EnsureCodexProposalCanBeAccepted(SqliteTransaction transaction, CodexFaqProposal proposal)
    {
        using (var receipt = CodexCommand(transaction,
                   "SELECT EXISTS(SELECT 1 FROM codex_proposal_receipts WHERE request_id = $id)",
                   ("$id", proposal.RequestId)))
        {
            if (CodexExists(receipt))
            {
                throw CodexProblem("CDX-006", "このCodex提案はすでに反映済みです。",
                    "提案一覧を更新してください。同じ提案は重複反映されていません。");
            }
        }
        using var pending = CodexCommand(transaction,
            "SELECT payload_json FROM codex_proposal_history WHERE request_id = $id AND status = 'pending'",
            ("$id", proposal.RequestId));
        var payload = pending.ExecuteScalar() as string ?? throw PendingCodexProposalNotFound();
        EnsureSameCodexProposal(ParseStoredCodexProposal(payload), proposal);
    }

    private void VerifyCodexSourceVersions(SqliteTransaction transaction, IReadOnlyList<CodexSourceArticle> sources)
    {
        var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (!uniqueIds.Add(source.ArticleId))
            {
                throw CodexProblem("CDX-002", "Codex提案の元FAQが重複しています。",
                    "KnowledgeAppで対象を選び直し、Codexへ再依頼してください。");
            }
            using var command = CodexCommand(transaction,
                "SELECT updated_at, deleted_at FROM articles WHERE id = $id", ("$id", source.ArticleId));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw CodexProblem("CDX-012", "Codexへ委譲した元FAQが見つかりません。",
                    "現在のFAQを選び直して、新しい委譲番号で再依頼してください。");
            }
            if (!reader.IsDBNull(1) || reader.GetString(0) != source.SourceUpdatedAt)
            {
                throw CodexProblem("CDX-012", "Codexへの委譲後に元FAQが変更または削除されています。",
                    "古い提案の上書きを防ぐため反映を中止しました。現在のFAQから再度Codexへ依頼してください。");
            }
        }
    }

    private void UpsertCodexSearchDocument(
        SqliteTransaction transaction, string articleId, CodexFaqDraft faq, string plainText)
    {
        using var command = CodexCommand(transaction, """
            INSERT INTO article_search_documents(article_id, title, summary, body)
            VALUES ($id, $title, $summary, $body)
            ON CONFLICT(article_id) DO UPDATE SET
                title = excluded.title, summary = excluded.summary, body = excluded.body
            """, ("$id", articleId), ("$title", NormalizeSearchText(faq.Title.Trim())),
            ("$summary", NormalizeSearchText(faq.Summary.Trim())), ("$body", NormalizeSearchText(plainText)));
        command.ExecuteNonQuery();
    }

    private void AcceptCodexHistoryAndReceipt(
        SqliteTransaction transaction, string requestId, string articleId, string decidedAt)
    {
        using (var receipt = CodexCommand(transaction, """
                   INSERT INTO codex_proposal_receipts(request_id, article_id, accepted_at)
                   VALUES ($request, $article, $now)
                   """, ("$request", requestId), ("$article", articleId), ("$now", decidedAt)))
        {
            receipt.ExecuteNonQuery();
        }
        using var history = CodexCommand(transaction, """
            UPDATE codex_proposal_history SET status = 'accepted', decided_at = $now, accepted_article_id = $article
             WHERE request_id = $request AND status = 'pending'
            """, ("$request", requestId), ("$article", articleId), ("$now", decidedAt));
        if (history.ExecuteNonQuery() != 1)
        {
            throw new AppProblemException(AppProblem.Database("Codex提案履歴の確定状態を更新できませんでした。"));
        }
    }

    private SqliteCommand CodexCommand(
        SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        return command;
    }

    private static bool CodexExists(SqliteCommand command) =>
        Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;

    private static CodexFaqProposal ParseStoredCodexProposal(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<CodexFaqProposal>(payload, CodexJson.Options)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new AppProblemException(AppProblem.Database("保存済みのCodex提案履歴を読み取れませんでした。"));
        }
    }

    private static string SerializeCodexProposal(CodexFaqProposal proposal)
    {
        try
        {
            return JsonSerializer.Serialize(proposal, CodexJson.Options);
        }
        catch (JsonException)
        {
            throw CodexProblem("CDX-002", "Codex提案を履歴へ記録できませんでした。",
                "提案一覧を更新し、もう一度お試しください。");
        }
    }

    private static void EnsureSameCodexProposal(CodexFaqProposal existing, CodexFaqProposal received)
    {
        // Rust and C# serialize Unicode, property order and optional defaults
        // differently. Normalize the typed contract, then compare JSON values.
        using var first = JsonDocument.Parse(SerializeCodexProposal(existing), CodexJson.DocumentOptions);
        using var second = JsonDocument.Parse(SerializeCodexProposal(received), CodexJson.DocumentOptions);
        if (!JsonElement.DeepEquals(first.RootElement, second.RootElement))
        {
            throw CodexProblem("CDX-013", "同じ受付番号で内容の異なるCodex提案が届いています。",
                "Codexへ新しい受付番号で提案を作り直すよう依頼してください。");
        }
    }

    private static AppProblemException PendingCodexProposalNotFound() => CodexProblem(
        "CDX-003", "確認待ちのCodex提案が見つかりません。",
        "提案一覧または履歴を更新し、もう一度選択してください。");

    private static AppProblemException CodexProblem(string code, string message, string action) =>
        new(new AppProblem(code, message, action));
}
