using System.Text.Json.Serialization;

namespace KnowledgeApp.Data;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AppSettings
{
    public string ColorTheme { get; init; } = ColorThemes.Green;

    public bool ShowTopCategoryInTitle { get; init; } = true;

    public bool ShowMascot { get; init; } = true;
}

public static class ColorThemes
{
    public const string Green = "green";
    public const string Blue = "blue";
}

public sealed record SystemInfo(
    string AppVersion,
    string DataRoot,
    string DatabasePath,
    string CodexCategoryCatalogPath,
    string CodexInboxPath);
