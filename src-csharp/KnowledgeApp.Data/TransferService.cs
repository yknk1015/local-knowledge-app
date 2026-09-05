namespace KnowledgeApp.Data;

public sealed class TransferService
{
    private readonly KnowledgeDatabase _database;
    private readonly AuthenticationService _authentication;
    private readonly string _dataRoot;

    public TransferService(
        KnowledgeDatabase database,
        AuthenticationService authentication,
        string dataRoot)
    {
        _database = database;
        _authentication = authentication;
        _dataRoot = Path.GetFullPath(dataRoot);
    }

    public CsvExportResult ExportFaqCsv(ExportFaqCsvInput input)
    {
        _authentication.RequireUser();
        var destination = TransferPathValidator.ValidateCsv(input.DestinationPath, mustExist: false);
        return _database.ExportFaqCsv(destination);
    }

    public CsvImportPreview InspectFaqCsv(string path)
    {
        _authentication.RequireUser();
        var source = TransferPathValidator.ValidateCsv(path, mustExist: true);
        return _database.InspectFaqCsv(source);
    }

    public CsvImportResult ImportFaqCsv(ImportFaqCsvInput input)
    {
        var actor = _authentication.RequireUser();
        var source = TransferPathValidator.ValidateCsv(input.SourcePath, mustExist: true);
        var preview = _database.InspectFaqCsv(source);
        EnsureCsvCanImport(preview, input.ExpectedFileSha256);
        var safetyPath = _database.CreateTransferSafetyBackup(_dataRoot, "csv");
        return _database.ImportFaqCsv(source, input.ExpectedFileSha256, actor.Id, safetyPath);
    }

    public JsonExportResult ExportJson(ExportJsonInput input)
    {
        _authentication.RequireUser();
        var destination = TransferPathValidator.ValidateJson(input.DestinationPath, mustExist: false);
        return _database.ExportJson(destination);
    }

    public JsonImportPreview InspectJson(string path)
    {
        _authentication.RequireUser();
        var source = TransferPathValidator.ValidateJson(path, mustExist: true);
        return _database.InspectJson(source);
    }

    public JsonImportResult ImportJson(ImportJsonInput input)
    {
        var actor = _authentication.RequireUser();
        var source = TransferPathValidator.ValidateJson(input.SourcePath, mustExist: true);
        var preview = _database.InspectJson(source);
        EnsureJsonCanImport(preview, input.ExpectedFileSha256);
        var safetyPath = _database.CreateTransferSafetyBackup(_dataRoot, "json");
        return _database.ImportJson(source, input.ExpectedFileSha256, actor.Id, safetyPath);
    }

    private static void EnsureCsvCanImport(CsvImportPreview preview, string expectedSha256)
    {
        if (!string.Equals(preview.FileSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new AppProblemException(new AppProblem(
                "CSV-007",
                "確認後にCSVファイルが変更されています。",
                "CSVをもう一度プレビューしてから取り込んでください。"));
        }
        if (preview.ErrorCount > 0)
        {
            throw new AppProblemException(new AppProblem(
                "CSV-004",
                "エラーがあるためCSVを取り込めません。",
                "プレビューに表示された行を修正し、もう一度選択してください。"));
        }
    }

    private static void EnsureJsonCanImport(JsonImportPreview preview, string expectedSha256)
    {
        if (!string.Equals(preview.FileSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new AppProblemException(new AppProblem(
                "JSON-007",
                "確認後にJSONファイルが変更されています。",
                "JSONをもう一度プレビューしてから取り込んでください。"));
        }
        if (preview.ErrorCount > 0)
        {
            throw new AppProblemException(new AppProblem(
                "JSON-004",
                "エラーがあるためJSONを取り込めません。",
                "プレビューに表示された内容を修正し、もう一度選択してください。"));
        }
    }
}
