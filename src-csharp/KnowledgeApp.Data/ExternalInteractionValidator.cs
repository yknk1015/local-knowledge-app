using System.Text;

namespace KnowledgeApp.Data;

public static class ExternalInteractionValidator
{
    public static Uri ValidateExternalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.EnumerateRunes().Count() > 2_048 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new AppProblemException(new AppProblem(
                "URL-001",
                "この参考URLは安全に開けません。",
                "http:// または https:// で始まるURLに修正してください。"));
        }
        return parsed;
    }

    public static string ValidateClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.EnumerateRunes().Count() > 4_000 ||
            text.Contains('\0', StringComparison.Ordinal))
        {
            throw new AppProblemException(new AppProblem(
                "ART-003",
                "コピー用テキストの形式が正しくありません。",
                "コピー用テキストを1～4,000文字で入力し直してください。"));
        }
        return text;
    }
}
