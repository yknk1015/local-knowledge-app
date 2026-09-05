namespace KnowledgeApp.Data;

public sealed class ClassificationSearchService
{
    private readonly KnowledgeDatabase _database;
    private readonly AuthenticationService _authentication;
    private readonly Action? _categoriesChanged;

    public ClassificationSearchService(
        KnowledgeDatabase database,
        AuthenticationService authentication,
        Action? categoriesChanged = null)
    {
        _database = database;
        _authentication = authentication;
        _categoriesChanged = categoriesChanged;
    }

    public IReadOnlyList<CategorySummary> ListCategories()
    {
        _authentication.RequireUser();
        return _database.ListCategories();
    }

    public CategorySummary CreateCategory(string name, string description, string? parentId)
    {
        _authentication.RequireUser();
        var result = _database.CreateCategory(name, description, parentId);
        _categoriesChanged?.Invoke();
        return result;
    }

    public CategorySummary UpdateCategory(string id, string name, string description, string? parentId)
    {
        _authentication.RequireUser();
        var result = _database.UpdateCategory(id, name, description, parentId);
        _categoriesChanged?.Invoke();
        return result;
    }

    public IReadOnlyList<CategorySummary> ReorderCategory(string id, string direction)
    {
        _authentication.RequireUser();
        var result = _database.ReorderCategory(id, direction);
        _categoriesChanged?.Invoke();
        return result;
    }

    public void DeleteCategory(string id)
    {
        _authentication.RequireUser();
        _database.DeleteCategory(id);
        _categoriesChanged?.Invoke();
    }

    public SearchArticlePage SearchArticles(SearchArticlesInput input)
    {
        _authentication.RequireUser();
        return _database.SearchArticles(input);
    }

    public string RecordSearchLog(string query, string? categoryId, string scope, long resultCount)
    {
        _authentication.RequireUser();
        return _database.RecordSearchLog(query, categoryId, scope, resultCount);
    }

    public IReadOnlyList<SynonymGroupSummary> ListSynonymGroups()
    {
        _authentication.RequireUser();
        return _database.ListSynonymGroups();
    }

    public SynonymGroupSummary SaveSynonymGroup(
        string? id,
        string displayName,
        IReadOnlyList<string> terms,
        bool allowConflicts)
    {
        _authentication.RequireUser();
        return _database.SaveSynonymGroup(id, displayName, terms, allowConflicts);
    }

    public void DeleteSynonymGroup(string id)
    {
        _authentication.RequireUser();
        _database.DeleteSynonymGroup(id);
    }
}
