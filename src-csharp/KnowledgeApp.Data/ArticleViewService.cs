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
        _authentication.RequireUser();
        var article = _database.GetArticle(id);
        return _attachments is null ? article : _attachments.HydrateArticle(article);
    }

    public void RecordArticleView(string articleId, string? sourceSearchLogId)
    {
        _authentication.RequireUser();
        _database.RecordArticleView(articleId, sourceSearchLogId);
    }
}
