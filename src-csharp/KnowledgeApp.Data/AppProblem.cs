namespace KnowledgeApp.Data;

public sealed record AppProblem(string Code, string Message, string Action)
{
    public static AppProblem Authentication() => new(
        "AUTH-001",
        "ログインIDまたはパスワードが正しくありません。",
        "入力内容を確認してください。利用停止中の場合は管理者へ連絡してください。");

    public static AppProblem LoginRequired() => new(
        "AUTH-002",
        "ログインが必要です。",
        "ログイン画面からログインしてください。");

    public static AppProblem AdminRequired() => new(
        "AUTH-003",
        "この操作には管理者権限が必要です。",
        "管理者ユーザーでログインしてください。");

    public static AppProblem Database(string message) => new(
        "DB-001",
        message,
        "アプリを終了し、データ保存先へアクセスできることを確認してから再起動してください。");

    public static AppProblem System(string message) => new(
        "SYS-001",
        message,
        "アプリを再起動してください。解決しない場合は診断情報を確認してください。");
}

public sealed class AppProblemException : Exception
{
    public AppProblemException(AppProblem problem)
        : base($"{problem.Code}: {problem.Message}")
    {
        Problem = problem;
    }

    public AppProblem Problem { get; }
}
