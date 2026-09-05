using System.Globalization;
using System.Text;

namespace KnowledgeApp.Data;

public sealed class ArticleEditingService
{
    private readonly KnowledgeDatabase _database;
    private readonly AuthenticationService _authentication;
    private readonly ArticleAttachmentService _attachments;

    public ArticleEditingService(
        KnowledgeDatabase database,
        AuthenticationService authentication,
        ArticleAttachmentService? attachments = null)
    {
        _database = database;
        _authentication = authentication;
        _attachments = attachments ?? new ArticleAttachmentService(
            Directory.GetParent(Path.GetDirectoryName(database.OpenInfo.DatabasePath)!)!.FullName);
    }

    public IReadOnlyList<TagMasterItem> ListTags()
    {
        _authentication.RequireUser();
        return _database.ListTags();
    }

    public TagMasterItem SaveTag(string? id, string name)
    {
        _authentication.RequireUser();
        return _database.SaveTag(id, name);
    }

    public void DeleteTag(string id)
    {
        _authentication.RequireUser();
        _database.DeleteTag(id);
    }

    public ArticleDetail SaveArticle(SaveArticleInput input)
    {
        var actor = _authentication.RequireUser();
        ValidateInput(input);
        var content = SafeRichContentValidator.Validate(input.BodyDoc);
        if (input.Status == ArticleStatuses.Published && string.IsNullOrWhiteSpace(content.PlainText))
        {
            throw ArticleInputProblem(
                "公開するFAQには回答が必要です。",
                "回答を入力するか、下書きとして保存してください。");
        }
        var isNew = string.IsNullOrWhiteSpace(input.Id);
        var articleId = isNew ? Guid.CreateVersion7().ToString() : input.Id!.Trim();
        var existing = isNew ? [] : _database.GetArticle(articleId).Attachments;
        var prepared = _attachments.Prepare(articleId, content.Attachments, existing);
        try
        {
            _database.SaveArticleCore(input, content, actor.Id, articleId, isNew, prepared.Records);
        }
        catch
        {
            _attachments.Rollback(prepared);
            throw;
        }
        _attachments.Commit(prepared);
        return _attachments.HydrateArticle(_database.GetArticle(articleId));
    }

    public ArticleDetail DuplicateArticle(string id)
    {
        var actor = _authentication.RequireUser();
        var source = _database.GetArticle(id);
        if (source.DeletedAt is not null)
        {
            throw new AppProblemException(new AppProblem(
                "ART-006",
                "削除済みFAQは複製できません。",
                "FAQを復元してから複製してください。"));
        }
        var sourceContent = SafeRichContentValidator.Validate(source.BodyDoc);
        var sourceAttachments = source.Attachments.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var stagedIds = new List<string>();
        try
        {
            foreach (var reference in sourceContent.Attachments.DistinctBy(item => item.Id))
            {
                if (!sourceAttachments.TryGetValue(reference.Id, out var attachment))
                {
                    throw new AppProblemException(new AppProblem(
                        "ATT-005",
                        "複製元FAQの画像が見つかりません。",
                        "複製元FAQを開いて画像を確認し、必要に応じて追加し直してください。"));
                }
                var staged = _attachments.StageCopyOfAttachment(attachment);
                replacements.Add(reference.Id, staged.Id);
                stagedIds.Add(staged.Id);
            }

            var bodyDocument = SafeRichContentValidator.RemapAttachmentIds(source.BodyDoc, replacements);
            var copiedContent = SafeRichContentValidator.Validate(bodyDocument);
            var articleId = Guid.CreateVersion7().ToString();
            var prepared = _attachments.Prepare(articleId, copiedContent.Attachments, []);
            try
            {
                _database.DuplicateArticleCore(
                    id, actor.Id, articleId, bodyDocument, copiedContent, prepared.Records);
            }
            catch
            {
                _attachments.Rollback(prepared);
                throw;
            }
            _attachments.Commit(prepared);
            return _attachments.HydrateArticle(_database.GetArticle(articleId));
        }
        catch
        {
            foreach (var stagedId in stagedIds)
            {
                _attachments.DiscardStage(stagedId);
            }
            throw;
        }
    }

    public ManagementArticlePage ListArticlesForManagement(ManagementArticlesInput input)
    {
        _authentication.RequireUser();
        if (input.Query.EnumerateRunes().Count() > 500 ||
            input.Status is not null && !ArticleStatuses.IsValid(input.Status))
        {
            throw ArticleInputProblem(
                "FAQ管理一覧の検索条件が正しくありません。",
                "絞り込み条件を確認して、もう一度お試しください。");
        }
        return _database.ListArticlesForManagement(input);
    }

    public ArticleDetail DeleteArticle(string id)
    {
        var actor = _authentication.RequireUser();
        _database.DeleteArticleCore(id, actor.Id);
        return _attachments.HydrateArticle(_database.GetArticle(id));
    }

    public ArticleDetail RestoreArticle(string id)
    {
        var actor = _authentication.RequireUser();
        _database.RestoreArticleCore(id, actor.Id);
        return _attachments.HydrateArticle(_database.GetArticle(id));
    }

    public StagedArticleImage StageArticleImage(string path)
    {
        _authentication.RequireUser();
        return _attachments.StageFromPath(path);
    }

    public StagedArticleImage StageArticleImageBase64(StageArticleImageBase64Input input)
    {
        _authentication.RequireUser();
        return _attachments.StageBase64(input);
    }

    public void DiscardStagedArticleImage(string id)
    {
        _authentication.RequireUser();
        _attachments.DiscardStage(id);
    }

    public CodexMergePublicationContext? GetCodexMergePublicationContext(string articleId)
    {
        _authentication.RequireUser();
        return _database.GetCodexMergePublicationContext(articleId);
    }

    private static void ValidateInput(SaveArticleInput input)
    {
        if (input.Title is null || input.CategoryId is null || input.Summary is null || input.Status is null ||
            input.Tags is null || input.Symptoms is null || input.Causes is null || input.Targets is null ||
            input.ErrorCodes is null || input.Procedures is null || input.Cautions is null ||
            input.SearchTerms is null || input.RelatedArticleIds is null)
        {
            throw ArticleInputProblem(
                "FAQの入力形式が正しくありません。",
                "画面を再読み込みして、もう一度入力してください。");
        }
        if (CountRunes(input.Title.Trim()) is < 1 or > 200)
        {
            throw ArticleInputProblem(
                "タイトルは1～200文字で入力してください。",
                "タイトルを確認して、もう一度保存してください。");
        }
        if (string.IsNullOrWhiteSpace(input.CategoryId))
        {
            throw ArticleInputProblem(
                "所属分類を選択してください。",
                "分類を選択して、もう一度保存してください。");
        }
        if (!ArticleStatuses.IsValid(input.Status))
        {
            throw ArticleInputProblem(
                "FAQの状態が正しくありません。",
                "下書き、公開、廃止のいずれかを選択してください。");
        }
        if (input.Status == ArticleStatuses.Published && string.IsNullOrWhiteSpace(input.Summary))
        {
            throw ArticleInputProblem(
                "公開するFAQには概要が必要です。",
                "概要を入力するか、下書きとして保存してください。");
        }
        if (CountRunes(input.Summary.Trim()) > 500)
        {
            throw ArticleInputProblem(
                "概要は500文字以内で入力してください。",
                "概要を短くして、もう一度保存してください。");
        }
        if (input.Importance is < 1 or > 3)
        {
            throw ArticleInputProblem(
                "重要度は1～3から選択してください。",
                "重要度を選び直してください。");
        }
        ValidateBadgeDate(input.NewBadgeUntil, "新着");
        ValidateBadgeDate(input.UpdatedBadgeUntil, "更新");
        ValidateTags(input.Tags);
    }

    private static void ValidateTags(IReadOnlyList<string> tags)
    {
        if (tags.Count > 50)
        {
            throw ArticleDetailProblem("タグは50件以内で登録してください。", "不要なタグを外してください。");
        }
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            var value = tag?.Trim() ?? string.Empty;
            if (CountRunes(value) is < 1 or > 100 || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
            {
                throw ArticleDetailProblem(
                    "タグは1～100文字の1行テキストで指定してください。",
                    "タグマスターから選び直してください。");
            }
            if (!normalized.Add(Normalize(value)))
            {
                throw ArticleDetailProblem(
                    "同じタグが重複しています。",
                    "重複しているタグを1件にまとめてください。");
            }
        }
    }

    private static void ValidateBadgeDate(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw ArticleInputProblem(
                $"{label}フラグの表示終了日が正しくありません。",
                "日付を選び直して、もう一度保存してください。");
        }
    }

    private static int CountRunes(string value) => value.EnumerateRunes().Count();

    private static string Normalize(string value) => string.Join(
        ' ',
        value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));

    private static AppProblemException ArticleInputProblem(string message, string action) =>
        new(new AppProblem("ART-001", message, action));

    private static AppProblemException ArticleDetailProblem(string message, string action) =>
        new(new AppProblem("ART-009", message, action));
}
