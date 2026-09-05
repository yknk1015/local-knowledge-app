using System.Text;
using System.Text.Json;

namespace KnowledgeApp.CSharp;

public sealed record HostRequestEnvelope(string Id, string Command, JsonElement Args)
{
    public const int MaximumMessageBytes = 16 * 1024 * 1024;
    // A stored FAQ may be 128 JSON containers deep. The typed request adds
    // envelope/argument containers; the Data layer still checks body depth 128.
    public const int MaximumMessageDepth = 144;

    public static bool TryParse(string? json, out HostRequestEnvelope? request)
    {
        request = null;
        if (string.IsNullOrEmpty(json) || json.Length > MaximumMessageBytes ||
            Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumMessageDepth });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root)) return false;
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 3 || properties.Any(property => property.Name is not ("id" or "command" or "args")) ||
                !root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("command", out var commandValue) || commandValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            var id = idValue.GetString()!;
            var command = commandValue.GetString()!;
            if (!IsIdentifier(id) || !IsIdentifier(command)) return false;
            request = new(id, command, args.Clone());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Invalid escaped Unicode cannot be used as an identifier or argument string.
            return false;
        }
    }

    private static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= 100 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            _ = element.GetString();
        }
        return false;
    }
}
