using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace KnowledgeApp.Mail;

internal enum SafeMailReadFailure
{
    None,
    LockedOrDenied,
    InvalidFormat,
    UnsupportedEncoding,
    UnsupportedStructure
}

internal sealed record CompoundMsgPreview(
    string Subject,
    string Sender,
    string Recipients,
    DateTimeOffset? SentAt,
    string BodyText);

internal static partial class CompoundMsgFallbackReader
{
    private const int RootPropertyHeaderBytes = 32;
    private const int ChildPropertyHeaderBytes = 8;
    private const int PropertyEntryBytes = 16;
    private const int MaximumPropertyStreamBytes = 4 * 1024 * 1024;
    private const int MaximumTextStreamBytes = 800_002;
    private const int MaximumShortTextStreamBytes = 65_536;

    private const uint PidTagSubjectUnicode = 0x0037001F;
    private const uint PidTagSubjectAnsi = 0x0037001E;
    private const uint PidTagNormalizedSubjectUnicode = 0x0E1D001F;
    private const uint PidTagNormalizedSubjectAnsi = 0x0E1D001E;
    private const uint PidTagBodyUnicode = 0x1000001F;
    private const uint PidTagBodyAnsi = 0x1000001E;
    private const uint PidTagHtml = 0x10130102;
    private const uint PidTagSenderNameUnicode = 0x0C1A001F;
    private const uint PidTagSenderNameAnsi = 0x0C1A001E;
    private const uint PidTagSenderEmailUnicode = 0x0C1F001F;
    private const uint PidTagSenderEmailAnsi = 0x0C1F001E;
    private const uint PidTagSenderSmtpAddressUnicode = 0x5D01001F;
    private const uint PidTagSenderSmtpAddressAnsi = 0x5D01001E;
    private const uint PidTagRepresentingNameUnicode = 0x0042001F;
    private const uint PidTagRepresentingNameAnsi = 0x0042001E;
    private const uint PidTagRepresentingEmailUnicode = 0x0065001F;
    private const uint PidTagRepresentingEmailAnsi = 0x0065001E;
    private const uint PidTagRepresentingSmtpAddressUnicode = 0x5D02001F;
    private const uint PidTagRepresentingSmtpAddressAnsi = 0x5D02001E;
    private const uint PidTagDisplayNameUnicode = 0x3001001F;
    private const uint PidTagDisplayNameAnsi = 0x3001001E;
    private const uint PidTagEmailAddressUnicode = 0x3003001F;
    private const uint PidTagEmailAddressAnsi = 0x3003001E;
    private const uint PidTagSmtpAddressUnicode = 0x39FE001F;
    private const uint PidTagSmtpAddressAnsi = 0x39FE001E;
    private const uint PidTagMessageCodePage = 0x3FFD0003;
    private const uint PidTagInternetCodePage = 0x3FDE0003;
    private const uint PidTagRecipientType = 0x0C150003;
    private const uint PidTagClientSubmitTime = 0x00390040;
    private const uint PidTagMessageDeliveryTime = 0x0E060040;

    static CompoundMsgFallbackReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    internal static bool TryRead(
        string filePath,
        out CompoundMsgPreview? preview,
        out SafeMailReadFailure failure)
    {
        preview = null;
        failure = SafeMailReadFailure.None;

        FileStream file;
        try
        {
            file = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (UnauthorizedAccessException)
        {
            failure = SafeMailReadFailure.LockedOrDenied;
            return false;
        }
        catch (IOException)
        {
            failure = SafeMailReadFailure.LockedOrDenied;
            return false;
        }

        try
        {
            using (file)
            {
                using var root = RootStorage.Open(file, StorageModeFlags.LeaveOpen);
                var encoding = ResolveAnsiEncoding(root);

                var subject = ReadText(root, encoding, PidTagSubjectUnicode, PidTagSubjectAnsi, MaximumShortTextStreamBytes);
                if (string.IsNullOrWhiteSpace(subject))
                {
                    subject = ReadText(root, encoding, PidTagNormalizedSubjectUnicode, PidTagNormalizedSubjectAnsi, MaximumShortTextStreamBytes);
                }

                var body = ReadText(root, encoding, PidTagBodyUnicode, PidTagBodyAnsi, MaximumTextStreamBytes);
                if (string.IsNullOrWhiteSpace(body))
                {
                    body = ReadHtmlAsPlainText(root, encoding);
                }

                var senderName = ReadText(root, encoding, PidTagSenderNameUnicode, PidTagSenderNameAnsi, MaximumShortTextStreamBytes);
                if (string.IsNullOrWhiteSpace(senderName))
                {
                    senderName = ReadText(root, encoding, PidTagRepresentingNameUnicode, PidTagRepresentingNameAnsi, MaximumShortTextStreamBytes);
                }

                var senderEmail = ReadText(root, encoding, PidTagSenderSmtpAddressUnicode, PidTagSenderSmtpAddressAnsi, MaximumShortTextStreamBytes);
                if (string.IsNullOrWhiteSpace(senderEmail))
                {
                    senderEmail = ReadText(root, encoding, PidTagSenderEmailUnicode, PidTagSenderEmailAnsi, MaximumShortTextStreamBytes);
                }
                if (string.IsNullOrWhiteSpace(senderEmail))
                {
                    senderEmail = ReadText(root, encoding, PidTagRepresentingSmtpAddressUnicode, PidTagRepresentingSmtpAddressAnsi, MaximumShortTextStreamBytes);
                }
                if (string.IsNullOrWhiteSpace(senderEmail))
                {
                    senderEmail = ReadText(root, encoding, PidTagRepresentingEmailUnicode, PidTagRepresentingEmailAnsi, MaximumShortTextStreamBytes);
                }

                var recipients = ReadRecipients(root, encoding);
                var sentAt = ReadFileTime(root, PidTagClientSubmitTime, RootPropertyHeaderBytes)
                    ?? ReadFileTime(root, PidTagMessageDeliveryTime, RootPropertyHeaderBytes);

                if (string.IsNullOrWhiteSpace(subject) &&
                    string.IsNullOrWhiteSpace(body) &&
                    string.IsNullOrWhiteSpace(senderName) &&
                    string.IsNullOrWhiteSpace(senderEmail) &&
                    string.IsNullOrWhiteSpace(recipients))
                {
                    failure = SafeMailReadFailure.UnsupportedStructure;
                    return false;
                }

                preview = new CompoundMsgPreview(
                    subject,
                    FormatAddress(senderName, senderEmail),
                    recipients,
                    sentAt,
                    body);
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            failure = SafeMailReadFailure.LockedOrDenied;
            return false;
        }
        catch (IOException)
        {
            failure = SafeMailReadFailure.InvalidFormat;
            return false;
        }
        catch (DecoderFallbackException)
        {
            failure = SafeMailReadFailure.UnsupportedEncoding;
            return false;
        }
        catch (NotSupportedException)
        {
            failure = SafeMailReadFailure.InvalidFormat;
            return false;
        }
        catch (InvalidDataException)
        {
            failure = SafeMailReadFailure.InvalidFormat;
            return false;
        }
        catch (ArgumentException)
        {
            failure = SafeMailReadFailure.InvalidFormat;
            return false;
        }
        catch (FormatException)
        {
            failure = SafeMailReadFailure.InvalidFormat;
            return false;
        }
        catch
        {
            failure = SafeMailReadFailure.UnsupportedStructure;
            return false;
        }
    }

    private static Encoding ResolveAnsiEncoding(Storage storage)
    {
        var codePage = ReadInt32(storage, PidTagMessageCodePage, RootPropertyHeaderBytes)
            ?? ReadInt32(storage, PidTagInternetCodePage, RootPropertyHeaderBytes);
        if (codePage is > 0)
        {
            try
            {
                return Encoding.GetEncoding(codePage.Value);
            }
            catch (ArgumentException)
            {
                // Outlookがコードページを保存していない、またはOSが未対応の場合は次の候補へ進む。
            }
        }

        var currentAnsiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        try
        {
            return Encoding.GetEncoding(currentAnsiCodePage);
        }
        catch (ArgumentException)
        {
            return Encoding.GetEncoding(1252);
        }
    }

    private static string ReadText(
        Storage storage,
        Encoding ansiEncoding,
        uint unicodeTag,
        uint ansiTag,
        int maximumBytes)
    {
        if (TryReadStream(storage, StreamName(unicodeTag), maximumBytes, out var unicodeBytes))
        {
            if ((unicodeBytes.Length & 1) != 0)
            {
                unicodeBytes = unicodeBytes[..^1];
            }
            return Encoding.Unicode.GetString(unicodeBytes).TrimEnd('\0');
        }

        if (TryReadStream(storage, StreamName(ansiTag), maximumBytes, out var ansiBytes))
        {
            return ansiEncoding.GetString(ansiBytes).TrimEnd('\0');
        }

        return string.Empty;
    }

    private static string ReadHtmlAsPlainText(Storage storage, Encoding ansiEncoding)
    {
        if (!TryReadStream(storage, StreamName(PidTagHtml), MaximumTextStreamBytes, out var bytes))
        {
            return string.Empty;
        }

        string html;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            html = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else
        {
            try
            {
                html = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                html = ansiEncoding.GetString(bytes);
            }
        }

        try
        {
            var safe = ScriptAndStyleRegex().Replace(html, string.Empty);
            safe = BlockBreakRegex().Replace(safe, "\n");
            safe = TagRegex().Replace(safe, string.Empty);
            return WebUtility.HtmlDecode(safe).TrimEnd('\0', '\r', '\n');
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    private static string ReadRecipients(Storage root, Encoding encoding)
    {
        var recipients = new List<string>();
        foreach (var entry in root.EnumerateEntries()
                     .Where(candidate => candidate.Type == EntryType.Storage &&
                         candidate.Name.StartsWith("__recip_version1.0_#", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase))
        {
            var storage = root.OpenStorage(entry.Name);
            var recipientType = ReadInt32(storage, PidTagRecipientType, ChildPropertyHeaderBytes);
            if (recipientType != 1)
            {
                continue;
            }

            var name = ReadText(storage, encoding, PidTagDisplayNameUnicode, PidTagDisplayNameAnsi, MaximumShortTextStreamBytes);
            var email = ReadText(storage, encoding, PidTagSmtpAddressUnicode, PidTagSmtpAddressAnsi, MaximumShortTextStreamBytes);
            if (string.IsNullOrWhiteSpace(email))
            {
                email = ReadText(storage, encoding, PidTagEmailAddressUnicode, PidTagEmailAddressAnsi, MaximumShortTextStreamBytes);
            }

            var formatted = FormatAddress(name, email);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                recipients.Add(formatted);
            }
        }

        return string.Join("; ", recipients);
    }

    private static string FormatAddress(string? displayName, string? email)
    {
        var name = displayName?.Trim() ?? string.Empty;
        var address = email?.Trim() ?? string.Empty;
        if (name.Length > 0 && address.Length > 0 && !name.Equals(address, StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} <{address}>";
        }
        return name.Length > 0 ? name : address;
    }

    private static int? ReadInt32(Storage storage, uint propertyTag, int propertyHeaderBytes)
    {
        var value = ReadFixedPropertyValue(storage, propertyTag, propertyHeaderBytes);
        return value is { Length: >= 4 } ? BinaryPrimitives.ReadInt32LittleEndian(value) : null;
    }

    private static DateTimeOffset? ReadFileTime(Storage storage, uint propertyTag, int propertyHeaderBytes)
    {
        var value = ReadFixedPropertyValue(storage, propertyTag, propertyHeaderBytes);
        if (value is not { Length: >= 8 })
        {
            return null;
        }

        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(value);
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[]? ReadFixedPropertyValue(Storage storage, uint propertyTag, int propertyHeaderBytes)
    {
        if (!TryReadStream(storage, "__properties_version1.0", MaximumPropertyStreamBytes, out var bytes) ||
            bytes.Length < propertyHeaderBytes)
        {
            return null;
        }

        for (var offset = propertyHeaderBytes; offset + PropertyEntryBytes <= bytes.Length; offset += PropertyEntryBytes)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) == propertyTag)
            {
                return bytes.AsSpan(offset + 8, 8).ToArray();
            }
        }

        return null;
    }

    private static bool TryReadStream(Storage storage, string name, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (!storage.TryOpenStream(name, out var stream) || stream is null)
        {
            return false;
        }

        using (stream)
        {
            var length = checked((int)Math.Min(stream.Length, maximumBytes));
            bytes = new byte[length];
            var totalRead = 0;
            while (totalRead < length)
            {
                var read = stream.Read(bytes, totalRead, length - totalRead);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }

            if (totalRead != bytes.Length)
            {
                Array.Resize(ref bytes, totalRead);
            }
            return true;
        }
    }

    private static string StreamName(uint propertyTag) => $"__substg1.0_{propertyTag:X8}";

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex ScriptAndStyleRegex();

    [GeneratedRegex(@"<(br\s*/?|/p|/div|/li|/tr)\s*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline, 1000)]
    private static partial Regex TagRegex();
}
