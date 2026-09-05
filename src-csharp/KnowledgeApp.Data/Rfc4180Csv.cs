using System.Text;

namespace KnowledgeApp.Data;

internal static class Rfc4180Csv
{
    internal static byte[] Write(IReadOnlyList<IReadOnlyList<string>> records)
    {
        var builder = new StringBuilder();
        foreach (var record in records)
        {
            for (var index = 0; index < record.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                WriteCell(builder, record[index]);
            }
            builder.Append("\r\n");
        }
        var body = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(builder.ToString());
        var output = new byte[body.Length + 3];
        output[0] = 0xEF;
        output[1] = 0xBB;
        output[2] = 0xBF;
        body.CopyTo(output, 3);
        return output;
    }

    internal static IReadOnlyList<IReadOnlyList<string>> Read(ReadOnlySpan<byte> source)
    {
        if (source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF)
        {
            source = source[3..];
        }
        string text;
        try
        {
            text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(source);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidCsv("CSVがUTF-8ではありません。");
        }

        var records = new List<IReadOnlyList<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var quoted = false;
        var afterQuote = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                        afterQuote = true;
                    }
                }
                else
                {
                    cell.Append(character);
                }
                continue;
            }

            if (afterQuote)
            {
                if (character is not (',' or '\r' or '\n'))
                {
                    throw InvalidCsv("CSVの引用符が正しくありません。");
                }
                afterQuote = false;
            }

            if (character == '"')
            {
                if (cell.Length != 0 || quoted)
                {
                    throw InvalidCsv("CSVの引用符が正しくありません。");
                }
                quoted = true;
                inQuotes = true;
            }
            else if (character == ',')
            {
                record.Add(cell.ToString());
                cell.Clear();
                quoted = false;
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
                record.Add(cell.ToString());
                cell.Clear();
                records.Add(record.ToArray());
                record = [];
                quoted = false;
            }
            else
            {
                if (quoted)
                {
                    throw InvalidCsv("CSVの引用符が正しくありません。");
                }
                cell.Append(character);
            }
        }

        if (inQuotes)
        {
            throw InvalidCsv("CSVの引用符が閉じられていません。");
        }
        if (cell.Length > 0 || record.Count > 0 || quoted)
        {
            record.Add(cell.ToString());
            records.Add(record.ToArray());
        }
        return records;
    }

    private static void WriteCell(StringBuilder output, string value)
    {
        var quoted = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        if (!quoted)
        {
            output.Append(value);
            return;
        }
        output.Append('"');
        output.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        output.Append('"');
    }

    private static AppProblemException InvalidCsv(string message) => new(new AppProblem(
        "CSV-003",
        message,
        "KnowledgeAppから書き出したUTF-8のCSVを選択してください。"));
}
