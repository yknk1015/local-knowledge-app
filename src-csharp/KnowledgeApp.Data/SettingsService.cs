namespace KnowledgeApp.Data;

public sealed class SettingsService
{
    private const string CSharpAppVersion = "0.4.4-csharp-migration";
    private readonly KnowledgeDatabase _database;
    private readonly AuthenticationService _authentication;
    private readonly string _dataRoot;

    public SettingsService(
        KnowledgeDatabase database,
        AuthenticationService authentication,
        string dataRoot)
    {
        _database = database;
        _authentication = authentication;
        _dataRoot = Path.GetFullPath(dataRoot);
    }

    public SystemInfo GetSystemInfo()
    {
        _authentication.RequireUser();
        return new SystemInfo(
            CSharpAppVersion,
            _dataRoot,
            _database.OpenInfo.DatabasePath,
            Path.Combine(_dataRoot, "codex-bridge", "categories.json"),
            Path.Combine(_dataRoot, "codex-inbox"));
    }

    public AppSettings GetSettings()
    {
        _authentication.RequireUser();
        return _database.GetAppearanceSettings();
    }

    public AppSettings SaveSettings(AppSettings settings)
    {
        _authentication.RequireUser();
        return _database.SaveAppearanceSettings(settings);
    }
}
