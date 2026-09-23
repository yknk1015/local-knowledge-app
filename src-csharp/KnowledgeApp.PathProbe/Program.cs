using System.Text.Json;
using KnowledgeApp.Data;
try
{
    var line = Console.ReadLine();
    if (line is null || line.Length > 65536) return 1;
    using var json = JsonDocument.Parse(line);
    var root = StorageSettingsService.ValidateSettingsRoot(json.RootElement.GetProperty("root").GetString()!);
    var purpose = json.RootElement.GetProperty("purpose").GetString()!;
    var path = StorageSettingsService.ValidateScopedDestination(root, json.RootElement.GetProperty("path").GetString()!, purpose);
    Console.WriteLine(JsonSerializer.Serialize(StorageSettingsService.Probe(path, !purpose.EndsWith("-import", StringComparison.Ordinal))));
    return 0;
}
catch { return 1; }
