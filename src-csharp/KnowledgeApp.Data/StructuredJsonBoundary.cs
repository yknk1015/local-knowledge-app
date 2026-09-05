using System.Text.Json;

namespace KnowledgeApp.Data;

internal static class StructuredJsonBoundary
{
    internal const int DocumentDepth = 128;
    // A transfer envelope adds categories/articles/bodyDoc containers around a
    // stored document. The stored body still has its own independent 128 limit.
    internal const int TransferDepth = 144;
    internal static JsonDocumentOptions DocumentOptions => new() { MaxDepth = DocumentDepth };
    internal static JsonDocumentOptions TransferOptions => new() { MaxDepth = TransferDepth };

    internal static void Validate(JsonElement value, int maximumDepth) => Visit(value, 0, maximumDepth);

    private static void Visit(JsonElement value, int depth, int maximumDepth)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return;
        if (++depth > maximumDepth) throw new JsonException("JSON nesting exceeds the supported boundary.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                Visit(property.Value, depth, maximumDepth);
            }
        }
        else
        {
            foreach (var item in value.EnumerateArray()) Visit(item, depth, maximumDepth);
        }
    }
}
