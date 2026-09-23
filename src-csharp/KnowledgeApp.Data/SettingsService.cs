namespace KnowledgeApp.Data;

public sealed class SettingsService
{
    internal const string CSharpAppVersion = "0.7.1";
    private readonly KnowledgeDatabase _database;
    private readonly AuthenticationService _authentication;
    private readonly string _dataRoot;
    public StorageSettingsService Storage { get; }
    public CodexLocationService CodexLocation { get; }

    public SettingsService(
        KnowledgeDatabase database,
        AuthenticationService authentication,
        string dataRoot)
    {
        _database = database;
        _authentication = authentication;
        _dataRoot = Path.GetFullPath(dataRoot);
        Storage = new StorageSettingsService(dataRoot, authentication);
        CodexLocation = new CodexLocationService(database, authentication);
    }

    public SystemInfo GetSystemInfo()
    {
        _authentication.RequireUser();
        return new SystemInfo(
            CSharpAppVersion,
            _dataRoot,
            _database.OpenInfo.DatabasePath,
            Path.Combine(CodexLocationService.Read(_dataRoot).Root, "codex-bridge", "categories.json"),
            Path.Combine(CodexLocationService.Read(_dataRoot).Root, "codex-inbox"));
    }

    public AppSettings GetSettings()
    {
        _authentication.RequireUser();
        return _database.GetAppearanceSettings(_authentication.RequireUser().Id);
    }

    public AppSettings SaveSettings(AppSettings settings)
    {
        _authentication.RequireUser();
        return _database.SaveAppearanceSettings(settings, _authentication.RequireUser().Id);
    }
}
