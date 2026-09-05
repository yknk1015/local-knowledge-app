using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using KnowledgeApp.Mail;
using OpenMcdf;

var testRoot = Path.Combine(Path.GetTempPath(), $"knowledgeapp-mail-test-{Guid.NewGuid():D}");
var resolvedTestRoot = Path.GetFullPath(testRoot);
var resolvedTemporaryRoot = Path.GetFullPath(Path.GetTempPath());
if (!resolvedTestRoot.StartsWith(resolvedTemporaryRoot, StringComparison.OrdinalIgnoreCase) ||
    !Path.GetFileName(resolvedTestRoot).StartsWith("knowledgeapp-mail-test-", StringComparison.Ordinal))
{
    throw new InvalidOperationException("テスト用フォルダがOS一時フォルダ外です。");
}

try
{
    var expectedProductionRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "jp.local.webknowledgesystem.csharp");
    if (MailDelegationWriter.ProductionDataRootPath != expectedProductionRoot)
    {
        throw new InvalidOperationException("メール委譲の既定保存先はC#専用の固定本番ルートでなければなりません。");
    }
    // This comparison is string-only. Do not create, enumerate or open real data.
    var syntheticMsgPath = Path.Combine(resolvedTestRoot, "fallback.msg");
    Directory.CreateDirectory(resolvedTestRoot);
    CreateSyntheticMsg(syntheticMsgPath);
    if (!CompoundMsgFallbackReader.TryRead(syntheticMsgPath, out var fallbackPreview, out var fallbackFailure) ||
        fallbackPreview is null)
    {
        throw new InvalidOperationException($"合成.msgの安全な簡易読取に失敗しました: {fallbackFailure}");
    }
    if (fallbackPreview.Subject != "日本語の合成件名" ||
        fallbackPreview.BodyText != "日本語の合成本文です。" ||
        !fallbackPreview.Sender.Contains("送信担当", StringComparison.Ordinal) ||
        !fallbackPreview.Recipients.Contains("受信担当", StringComparison.Ordinal) ||
        fallbackPreview.SentAt != DateTimeOffset.Parse("2026-08-30T01:02:03Z") ||
        fallbackPreview.BodyText.Contains("ATTACHMENT-SENTINEL", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("合成.msgの標準MAPI項目だけを読み取る境界が正しくありません。");
    }

    var ansiMsgPath = Path.Combine(resolvedTestRoot, "ansi.msg");
    CreateSyntheticAnsiMsg(ansiMsgPath);
    if (!CompoundMsgFallbackReader.TryRead(ansiMsgPath, out var ansiPreview, out var ansiFailure) ||
        ansiPreview is null ||
        ansiPreview.Subject != "日本語ANSI件名" ||
        ansiPreview.BodyText != "日本語ANSI本文です。")
    {
        throw new InvalidOperationException($"日本語ANSI .msgの簡易読取に失敗しました: {ansiFailure}");
    }

    var invalidMsgPath = Path.Combine(resolvedTestRoot, "invalid.msg");
    File.WriteAllBytes(invalidMsgPath, [0x01, 0x02, 0x03, 0x04]);
    if (CompoundMsgFallbackReader.TryRead(invalidMsgPath, out _, out var invalidFailure) ||
        invalidFailure != SafeMailReadFailure.InvalidFormat)
    {
        throw new InvalidOperationException("不正な.msgを安全な原因区分へ分類できませんでした。");
    }

    var scanResult = new MsgFolderScanner().ScanOnce(resolvedTestRoot);
    if (scanResult.Items.Count != 2 ||
        !scanResult.Items.Any(item => item.Subject == "日本語の合成件名") ||
        !scanResult.Items.Any(item => item.Subject == "日本語ANSI件名") ||
        !scanResult.Warnings.Any(warning => warning.Contains("MAIL-READ-FORMAT", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("フォルダ走査で読取可能メールの保持と不正形式の安全な除外を両立できませんでした。");
    }

    var item = new MailPreviewItem(
        sourcePath: @"C:\secret\original.msg",
        sourceFileName: "original.msg",
        subject: "日本語の合成件名",
        sender: "担当者（マスク済み）",
        recipients: "宛先（マスク済み）",
        sentAt: DateTimeOffset.Parse("2026-08-29T00:00:00Z"),
        bodyText: "合成メールの本文です。パスワードなどは含みません。")
    {
        IsSelected = true
    };
    var writer = new MailDelegationWriter(() => resolvedTestRoot);
    var result = writer.WriteSelected([item]);
    var target = Path.Combine(
        resolvedTestRoot,
        "codex-bridge",
        "mail-delegations",
        $"{result.DelegationId:D}.knowledge-mail-delegation.json");
    var bytes = File.ReadAllBytes(target);
    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
    {
        throw new InvalidOperationException("メール委譲にUTF-8 BOMがあります。");
    }

    var json = new UTF8Encoding(false, true).GetString(bytes);
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    if (root.GetProperty("formatVersion").GetInt32() != 1 ||
        root.GetProperty("kind").GetString() != "mail-create" ||
        root.GetProperty("mails").GetArrayLength() != 1)
    {
        throw new InvalidOperationException("メール委譲形式が正しくありません。");
    }
    foreach (var forbidden in new[] { @"C:\secret\original.msg", "original.msg", "sourcePath", "attachments" })
    {
        if (json.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"メール委譲に禁止情報があります: {forbidden}");
        }
    }
    if (!json.Contains("合成メールの本文です。", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("選択・確認済み本文が委譲へ保存されていません。");
    }
    if (!writer.Delete(result.DelegationId) || File.Exists(target))
    {
        throw new InvalidOperationException("明示的なメール委譲破棄に失敗しました。");
    }

    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine("OK: C#専用固定保存先の解決、合成.msgの標準MAPI読取、UTF-8固定形式の選択メール委譲、原本パス・ファイル名・添付の除外と明示破棄を確認しました。実データは開いていません。");
    return 0;
}
finally
{
    if (Directory.Exists(resolvedTestRoot) &&
        resolvedTestRoot.StartsWith(resolvedTemporaryRoot, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(resolvedTestRoot).StartsWith("knowledgeapp-mail-test-", StringComparison.Ordinal))
    {
        Directory.Delete(resolvedTestRoot, recursive: true);
    }
}

static void CreateSyntheticMsg(string path)
{
    using var root = RootStorage.Create(path);
    WriteUnicodeProperty(root, 0x0037001F, "日本語の合成件名");
    WriteUnicodeProperty(root, 0x1000001F, "日本語の合成本文です。");
    WriteUnicodeProperty(root, 0x0C1A001F, "送信担当");
    WriteUnicodeProperty(root, 0x0C1F001F, "sender@example.invalid");
    WriteFixedProperties(
        root,
        32,
        (0x3FFD0003, Int32Bytes(65001)),
        (0x00390040, Int64Bytes(DateTimeOffset.Parse("2026-08-30T01:02:03Z").ToFileTime())));

    var recipient = root.CreateStorage("__recip_version1.0_#00000000");
    WriteUnicodeProperty(recipient, 0x3001001F, "受信担当");
    WriteUnicodeProperty(recipient, 0x39FE001F, "recipient@example.invalid");
    WriteFixedProperties(recipient, 8, (0x0C150003, Int32Bytes(1)));

    var attachment = root.CreateStorage("__attach_version1.0_#00000000");
    WriteStream(attachment, "__substg1.0_37010102", Encoding.UTF8.GetBytes("ATTACHMENT-SENTINEL"));
    root.Flush(true);
}

static void CreateSyntheticAnsiMsg(string path)
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var shiftJis = Encoding.GetEncoding(932);
    using var root = RootStorage.Create(path);
    WriteStream(root, "__substg1.0_0037001E", shiftJis.GetBytes("日本語ANSI件名\0"));
    WriteStream(root, "__substg1.0_1000001E", shiftJis.GetBytes("日本語ANSI本文です。\0"));
    WriteFixedProperties(root, 32, (0x3FFD0003, Int32Bytes(932)));
    root.Flush(true);
}

static void WriteUnicodeProperty(Storage storage, uint propertyTag, string value)
{
    WriteStream(storage, $"__substg1.0_{propertyTag:X8}", Encoding.Unicode.GetBytes(value + '\0'));
}

static void WriteFixedProperties(Storage storage, int headerBytes, params (uint Tag, byte[] Value)[] properties)
{
    var bytes = new byte[headerBytes + (16 * properties.Length)];
    for (var index = 0; index < properties.Length; index++)
    {
        var offset = headerBytes + (index * 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), properties[index].Tag);
        properties[index].Value.AsSpan(0, Math.Min(8, properties[index].Value.Length)).CopyTo(bytes.AsSpan(offset + 8, 8));
    }
    WriteStream(storage, "__properties_version1.0", bytes);
}

static byte[] Int32Bytes(int value)
{
    var bytes = new byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
    return bytes;
}

static byte[] Int64Bytes(long value)
{
    var bytes = new byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
    return bytes;
}

static void WriteStream(Storage storage, string name, byte[] bytes)
{
    using var stream = storage.CreateStream(name);
    stream.Write(bytes);
}
