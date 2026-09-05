using System.Text;

namespace KnowledgeApp.Data;

public sealed class HistoryService
{
    private readonly KnowledgeDatabase _database;
    private readonly AuthenticationService _authentication;

    public HistoryService(KnowledgeDatabase database, AuthenticationService authentication)
    {
        _database = database;
        _authentication = authentication;
    }

    public SearchLogPage ListSearchLogs(ListSearchLogsInput input)
    {
        _authentication.RequireUser();
        ValidateQuery(input.Query);
        return _database.ListSearchLogs(input);
    }

    public ViewLogPage ListViewLogs(ListViewLogsInput input)
    {
        _authentication.RequireUser();
        ValidateQuery(input.Query);
        return _database.ListViewLogs(input);
    }

    public long DeleteHistory(DeleteHistoryInput input)
    {
        _authentication.RequireUser();
        if (!HistoryTargets.IsValid(input.Target))
        {
            throw HistoryInputProblem(
                "削除する履歴の種類が正しくありません。",
                "検索履歴または閲覧履歴を選び直してください。");
        }
        return _database.DeleteHistory(input);
    }

    private static void ValidateQuery(string? query)
    {
        if ((query ?? string.Empty).EnumerateRunes().Count() > 500)
        {
            throw new AppProblemException(new AppProblem(
                "LOG-001",
                "絞り込み文字列は500文字以内で入力してください。",
                "文字列を短くして、もう一度お試しください。"));
        }
    }

    internal static AppProblemException HistoryInputProblem(string message, string action) =>
        new(new AppProblem("LOG-002", message, action));
}
