namespace KnowledgeApp.Data;

internal static class TransferPathValidator
{
    internal static string ValidateCsv(string path, bool mustExist) => Validate(
        path,
        ".knowledge-faq.csv",
        mustExist,
        "CSV",
        "CSV-001",
        "CSV-006");

    internal static string ValidateJson(string path, bool mustExist) => Validate(
        path,
        ".knowledge-export.json",
        mustExist,
        "JSON",
        "JSON-001",
        "JSON-006");

    private static string Validate(
        string path,
        string requiredSuffix,
        bool mustExist,
        string label,
        string pathCode,
        string gitCode)
    {
        string fullPath;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException();
            }
            fullPath = FileSystemBoundary.ValidatePath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw PathProblem(label, requiredSuffix, pathCode);
        }

        if (!fullPath.EndsWith(requiredSuffix, StringComparison.OrdinalIgnoreCase) ||
            mustExist && !File.Exists(fullPath))
        {
            throw PathProblem(label, requiredSuffix, pathCode);
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new AppProblemException(new AppProblem(
                pathCode,
                $"{label}の保存先フォルダが見つかりません。",
                "既存のフォルダを選択してください。"));
        }
        if (FindGitRoot(parent) is not null)
        {
            throw new AppProblemException(new AppProblem(
                gitCode,
                $"Git管理フォルダ内の{label}は使用できません。",
                "FAQデータの誤登録を防ぐため、デスクトップやドキュメントなどGit管理外を選択してください。"));
        }
        return fullPath;
    }

    private static string? FindGitRoot(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }

    private static AppProblemException PathProblem(string label, string suffix, string code) =>
        new(new AppProblem(
            code,
            $"{label}ファイルの場所またはファイル名が正しくありません。",
            $"絶対パスにある「{suffix}」で終わるファイルを指定してください。"));
}
