using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KnowledgeApp.Mail;

public sealed class MailDelegationWriter
{
    private const int MaximumSelectedMails = 20;
    private const int MaximumBodyCharacters = 200_000;
    private const int MaximumPayloadBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8WithoutBom = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly Func<string> _dataRootProvider;

    // Pure path resolution; no filesystem access and no legacy-root fallback.
    public static string ProductionDataRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "jp.local.webknowledgesystem.csharp");

    public MailDelegationWriter(Func<string>? dataRootProvider = null)
    {
        _dataRootProvider = dataRootProvider ?? (() => ProductionDataRootPath);
    }

    public MailDelegationResult WriteSelected(IReadOnlyCollection<MailPreviewItem> items)
    {
        var selected = items.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("FAQ候補に使うメールを1件以上選択してください。");
        }
        if (selected.Length > MaximumSelectedMails)
        {
            throw new InvalidOperationException($"1回に委譲できるメールは{MaximumSelectedMails}件までです。");
        }

        foreach (var item in selected)
        {
            if (string.IsNullOrWhiteSpace(item.BodyText))
            {
                throw new InvalidOperationException($"{item.SourceFileName}: 本文が空です。選択を外すか本文を入力してください。");
            }
            if (item.BodyText.Length > MaximumBodyCharacters)
            {
                throw new InvalidOperationException($"{item.SourceFileName}: 本文を{MaximumBodyCharacters:N0}文字以内にしてください。");
            }
            if (item.Subject.Length > 500 || item.Sender.Length > 500 || item.Recipients.Length > 2_000)
            {
                throw new InvalidOperationException($"{item.SourceFileName}: 件名または宛先情報が長すぎます。不要部分を削除してください。");
            }
        }

        var delegationId = Guid.NewGuid();
        var payload = new
        {
            formatVersion = 1,
            delegationId,
            createdAt = DateTimeOffset.UtcNow.ToString("O"),
            kind = "mail-create",
            reviewConfirmed = true,
            mails = selected.Select(item => new
            {
                mailId = Guid.NewGuid(),
                subject = item.Subject.Trim(),
                sender = item.Sender.Trim(),
                recipients = item.Recipients.Trim(),
                sentAt = item.SentAt?.ToUniversalTime().ToString("O"),
                bodyText = item.BodyText
            }).ToArray()
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = StrictUtf8WithoutBom.GetBytes(json);
        if (bytes.Length > MaximumPayloadBytes)
        {
            throw new InvalidOperationException("メール委譲は1MB以内にしてください。選択件数または本文を減らしてください。");
        }

        var delegationDirectory = ResolveDelegationDirectory(create: true);
        var target = Path.Combine(delegationDirectory, $"{delegationId:D}.knowledge-mail-delegation.json");
        var partial = target + $".{Guid.NewGuid():D}.partial";

        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(partial, target);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }

        var prompt = $"KnowledgeAppのメール委譲番号 {delegationId:D} からFAQ案を作ってください。";
        return new MailDelegationResult(delegationId, prompt, selected.Length);
    }

    public bool Delete(Guid delegationId)
    {
        if (delegationId == Guid.Empty) return false;
        var delegationDirectory = ResolveDelegationDirectory(create: false);
        var target = Path.Combine(delegationDirectory, $"{delegationId:D}.knowledge-mail-delegation.json");
        if (!File.Exists(target)) return false;

        var item = new FileInfo(target);
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("リンクされたメール委譲ファイルは破棄できません。");
        }
        File.Delete(target);
        return true;
    }

    private string ResolveDelegationDirectory(bool create)
    {
        var dataRootValue = _dataRootProvider();
        if (string.IsNullOrWhiteSpace(dataRootValue) || !Path.IsPathFullyQualified(dataRootValue))
        {
            throw new InvalidOperationException("Windowsのローカル利用者データフォルダを解決できません。");
        }

        var dataRoot = Path.GetFullPath(dataRootValue);
        var bridge = Path.Combine(dataRoot, "codex-bridge");
        var delegationDirectory = Path.Combine(bridge, "mail-delegations");
        foreach (var directory in new[] { dataRoot, bridge, delegationDirectory })
        {
            if (create) Directory.CreateDirectory(directory);
            if (Directory.Exists(directory) &&
                (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("リンクまたは再解析ポイントのメール委譲フォルダは使用できません。");
            }
        }
        return delegationDirectory;
    }
}
