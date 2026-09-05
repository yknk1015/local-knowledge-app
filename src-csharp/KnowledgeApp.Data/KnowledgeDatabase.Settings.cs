using System.Globalization;
using System.Text.Json;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    private const string AppearanceSettingsKey = "appearance";
    private static readonly JsonSerializerOptions StrictSettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal AppSettings GetAppearanceSettings() => ExecuteLocked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", AppearanceSettingsKey);
        var stored = command.ExecuteScalar() as string;
        if (stored is null)
        {
            return new AppSettings();
        }
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(stored, StrictSettingsJsonOptions)
                ?? throw new JsonException();
            ValidateAppearanceSettings(settings, "SET-001");
            return settings;
        }
        catch (JsonException)
        {
            throw SettingsProblem(
                "SET-001",
                "画面の表示設定を読み込めませんでした。",
                "設定画面で表示設定を選び直して保存してください。");
        }
    });

    internal AppSettings SaveAppearanceSettings(AppSettings settings) => ExecuteLocked(() =>
    {
        ValidateAppearanceSettings(settings, "SET-002");
        string value;
        try
        {
            value = JsonSerializer.Serialize(settings, StrictSettingsJsonOptions);
        }
        catch (JsonException)
        {
            throw SettingsProblem(
                "SET-002",
                "画面の表示設定を保存できませんでした。",
                "設定内容を確認して、もう一度保存してください。");
        }
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(key, value_json, updated_at)
            VALUES ($key, $value, $updated_at)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$key", AppearanceSettingsKey);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue(
            "$updated_at",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        try
        {
            command.ExecuteNonQuery();
            return settings;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            throw SettingsProblem(
                "SET-002",
                "画面の表示設定を保存できませんでした。",
                "設定内容を確認して、もう一度保存してください。");
        }
    });

    private static void ValidateAppearanceSettings(AppSettings settings, string errorCode)
    {
        if (settings is null || settings.ColorTheme is not (ColorThemes.Green or ColorThemes.Blue))
        {
            throw errorCode == "SET-001"
                ? SettingsProblem(
                    "SET-001",
                    "画面の表示設定を読み込めませんでした。",
                    "設定画面で表示設定を選び直して保存してください。")
                : SettingsProblem(
                    "SET-002",
                    "画面の表示設定を保存できませんでした。",
                    "設定内容を確認して、もう一度保存してください。");
        }
    }

    private static AppProblemException SettingsProblem(string code, string message, string action) =>
        new(new AppProblem(code, message, action));
}
