namespace KnowledgeApp.Data;

public sealed class ArticleViewService
{
    private readonly KnowledgeDatabase _database;
    private readonly AuthenticationService _authentication;
    private readonly ArticleAttachmentService? _attachments;

    public ArticleViewService(
        KnowledgeDatabase database,
        AuthenticationService authentication,
        ArticleAttachmentService? attachments = null)
    {
        _database = database;
        _authentication = authentication;
        _attachments = attachments;
    }

    public ArticleDetail GetArticle(string id)
    {
        var actor = _authentication.RequireUser();
        var article = _database.GetArticle(id);
        if (actor.Role == UserRoles.Viewer)
        {
            _database.RequirePublicArticle(id);
            article = article with { RelatedArticles = article.RelatedArticles.Where(item => _database.IsPublicArticle(item.Id)).ToArray() };
        }
        return _attachments is null ? article : _attachments.HydrateArticle(article);
    }

    public void RequireImageAccess(string relativePath)
    {
        var id = relativePath.Replace('\\', '/').Split('/')[0];
        var article = GetArticle(id);
        if (!article.Attachments.Any(item => item.AssetPath.Replace('\\', '/').EndsWith("/" + relativePath.Replace('\\', '/'), StringComparison.Ordinal) || item.AssetPath == relativePath))
            throw new AppProblemException(new AppProblem("ATT-005", "画像を参照できません。", "FAQを開き直してください。"));
    }

    public void RecordArticleView(string articleId, string? sourceSearchLogId)
    {
        var actor = _authentication.RequireUser();
        if (actor.Role == UserRoles.Viewer) _database.RequirePublicArticle(articleId);
        _database.RecordArticleView(articleId, sourceSearchLogId, actor.Id);
    }
}
