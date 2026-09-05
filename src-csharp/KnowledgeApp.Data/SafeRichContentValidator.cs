using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KnowledgeApp.Data;

internal static class SafeRichContentValidator
{
    // serde_json 1.0.151 (the Rust build's locked version) starts with a
    // 128-container recursion budget. Use the same bounded scale for stored JSON.
    internal const int MaximumStoredJsonDepth = StructuredJsonBoundary.DocumentDepth;
    private static readonly JsonSerializerOptions DocumentWriteOptions = new() { MaxDepth = MaximumStoredJsonDepth };
    private static readonly HashSet<string> AllowedNodes =
    [
        "doc", "paragraph", "text", "hardBreak", "heading", "bulletList",
        "orderedList", "listItem", "table", "tableRow", "tableCell", "tableHeader",
        "copyBlock", "image"
    ];
    private static readonly HashSet<string> BlockNodes =
    [
        "paragraph", "heading", "listItem", "tableCell", "tableHeader"
    ];
    private static readonly HashSet<string> AllowedMarks = ["bold", "italic", "link"];

    internal static ValidatedRichContent Validate(JsonElement document) => Validate(document, capturePlainText: true);

    internal static ValidatedRichContent ValidateForBackup(JsonElement document) => Validate(document, capturePlainText: false);

    private static ValidatedRichContent Validate(JsonElement document, bool capturePlainText)
    {
        try { StructuredJsonBoundary.Validate(document, MaximumStoredJsonDepth); }
        catch (JsonException) { throw InvalidDocument(); }
        if (document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty("type", out var rootType) ||
            rootType.GetString() != "doc")
        {
            throw InvalidDocument();
        }

        var state = new ValidationState(capturePlainText);
        VisitNode(document, state, 0);
        // Normal Rust documents have no separate text/node-count limit. All paths
        // use the same safe structures and finite JSON depth; transport/file byte
        // limits remain enforced by their respective entry points.
        if (!capturePlainText) return new ValidatedRichContent(string.Empty, state.Attachments);
        var plainText = CollapseWhitespace(state.PlainText.ToString());
        return new ValidatedRichContent(plainText, state.Attachments);
    }

    internal static JsonElement RemapAttachmentIds(
        JsonElement document,
        IReadOnlyDictionary<string, string> replacements)
    {
        _ = ValidateForBackup(document);
        JsonNode root;
        try
        {
            root = JsonNode.Parse(document.GetRawText(), documentOptions: StructuredJsonBoundary.DocumentOptions) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw InvalidDocument();
        }
        RemapNode(root, replacements);
        using var remapped = JsonDocument.Parse(root.ToJsonString(DocumentWriteOptions), StructuredJsonBoundary.DocumentOptions);
        return remapped.RootElement.Clone();
    }

    internal static JsonElement WithoutImages(JsonElement document)
    {
        _ = ValidateForBackup(document);
        JsonNode root;
        try
        {
            root = JsonNode.Parse(document.GetRawText(), documentOptions: StructuredJsonBoundary.DocumentOptions) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw InvalidDocument();
        }
        RemoveImages(root);
        using var sanitized = JsonDocument.Parse(root.ToJsonString(DocumentWriteOptions), StructuredJsonBoundary.DocumentOptions);
        return sanitized.RootElement.Clone();
    }

    private static void VisitNode(JsonElement node, ValidationState state, int depth)
    {
        if (node.ValueKind != JsonValueKind.Object || depth > MaximumStoredJsonDepth)
        {
            throw InvalidDocument();
        }
        ValidateNodePropertyNames(node);
        if (!node.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            throw InvalidDocument();
        }
        var nodeType = typeElement.GetString()!;
        if (!AllowedNodes.Contains(nodeType))
        {
            throw InvalidDocument();
        }

        if (nodeType == "image")
        {
            ValidateImage(node, state);
            return;
        }
        if (nodeType == "copyBlock")
        {
            ValidateCopyBlock(node, state);
            return;
        }

        ValidateAttributes(nodeType, node);
        ValidateMarks(node);
        if (nodeType == "text")
        {
            if (!node.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            {
                throw InvalidDocument();
            }
            var value = text.GetString()!;
            if (value.Contains('\0', StringComparison.Ordinal))
            {
                throw InvalidDocument();
            }
            state.AppendPlainText(value);
        }
        else if (node.TryGetProperty("text", out _))
        {
            throw InvalidDocument();
        }

        if (!node.TryGetProperty("content", out var content))
        {
            return;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            throw InvalidDocument();
        }
        foreach (var child in content.EnumerateArray())
        {
            VisitNode(child, state, depth + 1);
            if (child.ValueKind == JsonValueKind.Object &&
                child.TryGetProperty("type", out var childType) &&
                childType.ValueKind == JsonValueKind.String &&
                BlockNodes.Contains(childType.GetString()!))
            {
                state.AppendPlainText(" ");
            }
        }
    }

    private static void ValidateImage(JsonElement node, ValidationState state)
    {
        if (node.TryGetProperty("content", out _) || node.TryGetProperty("marks", out _) ||
            node.TryGetProperty("text", out _) || !node.TryGetProperty("attrs", out var attributes) ||
            attributes.ValueKind != JsonValueKind.Object)
        {
            throw InvalidImage("A01", "画像の属性情報がないか、オブジェクト形式ではありません。");
        }
        var allowed = new HashSet<string>(["src", "alt", "title", "attachmentId"]);
        var unexpected = attributes.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !allowed.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (unexpected.Length > 0)
        {
            throw InvalidImage("A02", $"許可されていない画像属性があります（属性名: {string.Join(", ", unexpected.Select(SafeAttributeName))}）。");
        }
        if (!attributes.TryGetProperty("attachmentId", out var idValue))
        {
            throw InvalidImage("I01", "画像の添付IDがありません。");
        }
        if (idValue.ValueKind != JsonValueKind.String)
        {
            throw InvalidImage("I02", "画像の添付IDが文字列形式ではありません。");
        }
        var id = idValue.GetString()!;
        if (!Guid.TryParseExact(id, "D", out _))
        {
            throw InvalidImage("I03", "画像の添付IDがUUID形式ではありません。");
        }
        if (!attributes.TryGetProperty("src", out var sourceValue))
        {
            throw InvalidImage("S01", "画像の保存用参照がありません。");
        }
        if (sourceValue.ValueKind != JsonValueKind.String)
        {
            throw InvalidImage("S02", "画像の保存用参照が文字列形式ではありません。");
        }
        if (sourceValue.GetString() != $"knowledge-attachment:{id}")
        {
            throw InvalidImage("S03", "画像の保存用参照と添付IDが一致しません。");
        }
        var altText = NullableLimitedImageText(attributes, "alt", "T01", "T02", "代替テキスト");
        _ = NullableLimitedImageText(attributes, "title", "T03", "T04", "画像タイトル");
        state.Attachments.Add(new AttachmentReference(id, altText));
        if (!string.IsNullOrWhiteSpace(altText))
        {
            state.AppendPlainText(altText);
        }
    }

    private static string NullableLimitedImageText(
        JsonElement attributes,
        string propertyName,
        string typeCode,
        string lengthCode,
        string label)
    {
        if (!attributes.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidImage(typeCode, $"{label}が文字列形式ではありません。");
        }
        var text = value.GetString()!;
        var length = text.EnumerateRunes().Count();
        if (length > 500)
        {
            throw InvalidImage(lengthCode, $"{label}が500文字を超えています（文字数: {length}）。");
        }
        return text;
    }

    private static void RemapNode(JsonNode? node, IReadOnlyDictionary<string, string> replacements)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>() == "image" && obj["attrs"] is JsonObject attributes &&
                attributes["attachmentId"] is JsonValue idValue && idValue.TryGetValue<string>(out var oldId) &&
                replacements.TryGetValue(oldId, out var newId))
            {
                attributes["attachmentId"] = newId;
                attributes["src"] = $"knowledge-attachment:{newId}";
            }
            foreach (var child in obj.ToArray())
            {
                RemapNode(child.Value, replacements);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                RemapNode(child, replacements);
            }
        }
    }

    private static void RemoveImages(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonArray array)
                {
                    for (var index = array.Count - 1; index >= 0; index--)
                    {
                        if (array[index] is JsonObject child && child["type"]?.GetValue<string>() == "image")
                        {
                            array.RemoveAt(index);
                        }
                        else
                        {
                            RemoveImages(array[index]);
                        }
                    }
                }
                else
                {
                    RemoveImages(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = array.Count - 1; index >= 0; index--)
            {
                if (array[index] is JsonObject child && child["type"]?.GetValue<string>() == "image")
                {
                    array.RemoveAt(index);
                }
                else
                {
                    RemoveImages(array[index]);
                }
            }
        }
    }

    private static void ValidateNodePropertyNames(JsonElement node)
    {
        foreach (var property in node.EnumerateObject())
        {
            if (property.Name is not ("type" or "content" or "attrs" or "marks" or "text"))
            {
                throw InvalidDocument();
            }
        }
    }

    private static void ValidateCopyBlock(JsonElement node, ValidationState state)
    {
        if (node.TryGetProperty("content", out _) || node.TryGetProperty("marks", out _) ||
            !node.TryGetProperty("attrs", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
        {
            throw InvalidDocument();
        }
        var properties = attributes.EnumerateObject().ToArray();
        if (properties.Length != 1 || properties[0].Name != "text" ||
            properties[0].Value.ValueKind != JsonValueKind.String)
        {
            throw InvalidDocument();
        }
        var text = properties[0].Value.GetString()!;
        if (text.Length == 0 || text.EnumerateRunes().Count() > 4_000 || text.Contains('\0', StringComparison.Ordinal))
        {
            throw InvalidDocument();
        }
        state.AppendPlainText(text);
    }

    private static void ValidateAttributes(string nodeType, JsonElement node)
    {
        if (!node.TryGetProperty("attrs", out var attributes) || attributes.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            throw InvalidDocument();
        }
        var allowed = nodeType switch
        {
            "heading" => new HashSet<string>(["level"]),
            "orderedList" => new HashSet<string>(["start", "type"]),
            "tableCell" or "tableHeader" => new HashSet<string>(["colspan", "rowspan", "colwidth", "align"]),
            _ => []
        };
        if (attributes.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
        {
            throw InvalidDocument();
        }
        if (nodeType == "heading" &&
            (!attributes.TryGetProperty("level", out var level) ||
             !level.TryGetInt32(out var headingLevel) || headingLevel is not (2 or 3)))
        {
            throw InvalidDocument();
        }
        if (nodeType == "orderedList" && attributes.TryGetProperty("start", out var start) &&
            (start.ValueKind != JsonValueKind.Number || !start.TryGetInt32(out var startNumber) || startNumber < 1))
        {
            throw InvalidDocument();
        }
        if (nodeType is "tableCell" or "tableHeader" && attributes.TryGetProperty("align", out var align) &&
            align.ValueKind != JsonValueKind.Null &&
            (align.ValueKind != JsonValueKind.String || align.GetString() is not ("left" or "center" or "right" or "justify")))
        {
            throw InvalidDocument();
        }
    }

    private static void ValidateMarks(JsonElement node)
    {
        if (!node.TryGetProperty("marks", out var marks))
        {
            return;
        }
        if (marks.ValueKind != JsonValueKind.Array)
        {
            throw InvalidDocument();
        }
        foreach (var mark in marks.EnumerateArray())
        {
            if (mark.ValueKind != JsonValueKind.Object ||
                mark.EnumerateObject().Any(property => property.Name is not ("type" or "attrs")) ||
                !mark.TryGetProperty("type", out var markTypeElement) ||
                markTypeElement.ValueKind != JsonValueKind.String)
            {
                throw InvalidDocument();
            }
            var markType = markTypeElement.GetString()!;
            if (!AllowedMarks.Contains(markType))
            {
                throw InvalidDocument();
            }
            if (markType == "link")
            {
                ValidateLink(mark);
            }
            else if (mark.TryGetProperty("attrs", out var attributes) &&
                     attributes.ValueKind != JsonValueKind.Null &&
                     (attributes.ValueKind != JsonValueKind.Object || attributes.EnumerateObject().Any()))
            {
                throw InvalidDocument();
            }
        }
    }

    private static void ValidateLink(JsonElement mark)
    {
        if (!mark.TryGetProperty("attrs", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
        {
            throw InvalidDocument();
        }
        var allowed = new HashSet<string>(["href", "target", "rel", "class", "title"]);
        if (attributes.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
            !attributes.TryGetProperty("href", out var hrefElement) || hrefElement.ValueKind != JsonValueKind.String)
        {
            throw InvalidDocument();
        }
        var href = hrefElement.GetString()!;
        if (href.EnumerateRunes().Count() > 2_048 || !Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw InvalidLink();
        }
        if (attributes.TryGetProperty("target", out var target) && target.ValueKind != JsonValueKind.Null &&
            (target.ValueKind != JsonValueKind.String || target.GetString() != "_blank"))
        {
            throw InvalidDocument();
        }
        if (attributes.TryGetProperty("rel", out var relation) && relation.ValueKind != JsonValueKind.Null)
        {
            if (relation.ValueKind != JsonValueKind.String || relation.GetString()!.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries).Any(token => token is not ("noopener" or "noreferrer" or "nofollow")))
            {
                throw InvalidDocument();
            }
        }
        foreach (var optionalText in new[] { "class", "title" })
        {
            if (!attributes.TryGetProperty(optionalText, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (value.ValueKind != JsonValueKind.String ||
                (optionalText == "title" && value.GetString()!.EnumerateRunes().Count() > 500))
            {
                throw InvalidDocument();
            }
        }
    }

    private static string CollapseWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(rune.ToString());
        }
        return result.ToString();
    }

    private static AppProblemException InvalidDocument() => new(new AppProblem(
        "ART-003",
        "回答に利用できない書式が含まれています。",
        "貼り付けた箇所を書式なしで貼り直すか、利用できない書式を解除してから、もう一度保存してください。URL文字列はそのまま保存できます。"));

    private static AppProblemException InvalidLink() => new(new AppProblem(
        "ART-005",
        "クリック可能な参考URLは、http:// または https:// で始めてください。",
        "URLを文字として保存する場合は、該当箇所を選択して「リンク解除」を押してから、もう一度保存してください。"));

    private static AppProblemException InvalidImage(string reason, string detail) => new(new AppProblem(
        $"ATT-004-{reason}",
        "回答内の画像参照が正しくありません。",
        $"画像診断 IMG-20260816-01/{reason}: {detail} この診断番号と説明だけを開発側へ連絡してください。FAQ本文、画像、ファイルパス、利用者データは送らないでください。"));

    private static string SafeAttributeName(string name)
    {
        var safe = new string(name.Take(32).Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' ? character : '?').ToArray());
        return name.Length > 32 ? safe + "…" : safe;
    }

    private sealed class ValidationState(bool capturePlainText)
    {
        internal StringBuilder PlainText { get; } = new();
        internal List<AttachmentReference> Attachments { get; } = [];
        internal void AppendPlainText(string value)
        {
            if (capturePlainText) PlainText.Append(value);
        }
    }
}
