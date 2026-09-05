using System.Text.Json;

namespace KnowledgeApp.CSharp;

internal static class HostResponseJson
{
    // Historical stored documents allow JSON depth 128. Leave room for the
    // typed response envelope; this does not widen the incoming request parser.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 144
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
