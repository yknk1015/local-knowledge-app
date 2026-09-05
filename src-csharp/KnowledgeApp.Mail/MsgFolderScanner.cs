using System.Text;
using MsgReader.Outlook;

namespace KnowledgeApp.Mail;

public sealed class MsgFolderScanner
{
    private const int MaximumFiles = 500;
    private const long MaximumFileBytes = 25L * 1024 * 1024;
    private const int MaximumPreviewCharacters = 200_000;

    static MsgFolderScanner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public MailScanResult ScanOnce(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Path.IsPathFullyQualified(folderPath))
        {
            throw new InvalidOperationException("走査するフォルダを絶対パスで指定してください。");
        }

        var root = Path.GetFullPath(folderPath);
        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException("指定したメールフォルダが見つかりません。");
        }
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("リンクまたは再解析ポイントのフォルダは走査できません。");
        }

        var files = directory
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pstFileCount = files.Count(file => file.Extension.Equals(".pst", StringComparison.OrdinalIgnoreCase));
        var msgFiles = files
            .Where(file => file.Extension.Equals(".msg", StringComparison.OrdinalIgnoreCase))
            .Take(MaximumFiles + 1)
            .ToArray();
        if (msgFiles.Length > MaximumFiles)
        {
            throw new InvalidOperationException($"1回に確認できる.msgは{MaximumFiles}件までです。フォルダを分けてください。");
        }

        var items = new List<MailPreviewItem>();
        var warnings = new List<string>();
        foreach (var file in msgFiles)
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"{file.Name}: リンクのため除外しました。");
                continue;
            }
            if (file.Length > MaximumFileBytes)
            {
                warnings.Add($"{file.Name}: 25MBを超えるため除外しました。");
                continue;
            }

            var hasSafePreview = CompoundMsgFallbackReader.TryRead(
                file.FullName,
                out var safePreview,
                out var safeFailure);
            if (hasSafePreview && safePreview is not null && !string.IsNullOrWhiteSpace(safePreview.BodyText))
            {
                AddSafePreview(file, safePreview, items);
                continue;
            }

            if (hasSafePreview && safePreview is not null)
            {
                try
                {
                    using var message = new Storage.Message(file.FullName, FileAccess.Read);
                    AddStandardPreview(file, message, items, warnings);
                }
                catch
                {
                    AddSafePreview(file, safePreview, items);
                    warnings.Add($"{file.Name}: 本文を標準解析でも取得できなかったため空欄で表示しました。内容を確認できない場合は委譲しないでください。（MAIL-READ-PARTIAL）");
                }
                continue;
            }

            warnings.Add($"{file.Name}: 内容を安全に読み取れないため除外しました。{FailureMessage(safeFailure)}");
        }

        if (pstFileCount > 0)
        {
            warnings.Add($".pstを{pstFileCount}件検出しましたが、非デグレ互換性確認前のため読み取っていません。");
        }

        return new MailScanResult(items, warnings, pstFileCount);
    }

    private static void AddSafePreview(
        FileInfo file,
        CompoundMsgPreview preview,
        ICollection<MailPreviewItem> items)
    {
        items.Add(new MailPreviewItem(
            sourcePath: file.FullName,
            sourceFileName: file.Name,
            subject: preview.Subject,
            sender: preview.Sender,
            recipients: preview.Recipients,
            sentAt: preview.SentAt,
            bodyText: TruncateBody(preview.BodyText)));
    }

    private static void AddStandardPreview(
        FileInfo file,
        Storage.Message message,
        ICollection<MailPreviewItem> items,
        ICollection<string> warnings)
    {
        var unreadableFields = new List<string>();
        var subject = ReadSafely(() => message.Subject, "件名", unreadableFields);
        var sender = ReadSafely(() => FormatSender(message.Sender), "送信者", unreadableFields);
        var recipients = ReadSafely(
            () => message.GetEmailRecipients(RecipientType.To, false, false),
            "宛先",
            unreadableFields);
        var sentAt = ReadSafely(() => message.SentOn, "日時", unreadableFields);
        var body = ReadSafely(() => message.BodyText, "本文", unreadableFields);

        items.Add(new MailPreviewItem(
            sourcePath: file.FullName,
            sourceFileName: file.Name,
            subject: subject,
            sender: sender,
            recipients: recipients,
            sentAt: sentAt,
            bodyText: TruncateBody(body)));

        if (unreadableFields.Count > 0)
        {
            warnings.Add($"{file.Name}: {string.Join("・", unreadableFields)}を取得できなかったため空欄で表示しました。委譲前に確認してください。（MAIL-READ-PARTIAL）");
        }
    }

    private static string ReadSafely(Func<string?> reader, string fieldName, ICollection<string> unreadableFields)
    {
        try
        {
            return reader() ?? string.Empty;
        }
        catch
        {
            unreadableFields.Add(fieldName);
            return string.Empty;
        }
    }

    private static DateTimeOffset? ReadSafely(
        Func<DateTimeOffset?> reader,
        string fieldName,
        ICollection<string> unreadableFields)
    {
        try
        {
            return reader();
        }
        catch
        {
            unreadableFields.Add(fieldName);
            return null;
        }
    }

    private static string TruncateBody(string body)
    {
        if (body.Length <= MaximumPreviewCharacters)
        {
            return body;
        }
        return body[..MaximumPreviewCharacters] + "\n\n※本文が長いため200,000文字で打ち切りました。委譲前に必要範囲を確認してください。";
    }

    private static string FailureMessage(SafeMailReadFailure failure) => failure switch
    {
        SafeMailReadFailure.LockedOrDenied => "Outlookなどで開いている場合は閉じ、読取権限を確認してください。（MAIL-READ-LOCKED）",
        SafeMailReadFailure.UnsupportedEncoding => "未対応の文字コードです。OutlookでUnicode形式の.msgとして保存し直してください。（MAIL-READ-ENCODING）",
        SafeMailReadFailure.InvalidFormat => "破損、暗号化、または.msg以外の内容でないか確認してください。（MAIL-READ-FORMAT）",
        _ => "暗号化、破損、または未対応の.msg構造を確認してください。（MAIL-READ-UNSUPPORTED）"
    };

    private static string FormatSender(Storage.Sender? sender)
    {
        if (sender is null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(sender.DisplayName) && !string.IsNullOrWhiteSpace(sender.Email))
        {
            return $"{sender.DisplayName} <{sender.Email}>";
        }
        return sender.DisplayName ?? sender.Email ?? string.Empty;
    }
}
