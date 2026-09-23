namespace KnowledgeApp.Data;

// A single-file app loads dependencies from its extraction cache while its
// executable remains elsewhere. Both locations retain the same storage boundary.
internal static class ApplicationDirectoryBoundary
{
    internal static bool OverlapsCurrent(string path) =>
        Overlaps(path, AppContext.BaseDirectory, Environment.ProcessPath);

    // Text-only seam: never probe a selected network path on the UI thread.
    internal static bool Overlaps(string path, string applicationDirectory, string? executablePath)
    {
        var full = FileSystemBoundary.ValidatePathSyntax(path);
        var runtimeDirectory = FileSystemBoundary.ValidatePathSyntax(applicationDirectory);
        if (StorageSettingsService.Overlaps(full, runtimeDirectory)) return true;
        // Some hosts do not expose their executable path. The known application
        // directory remains protected; no guessed path or filesystem search is used.
        if (executablePath is null) return false;
        var executable = FileSystemBoundary.ValidatePathSyntax(executablePath);
        var executableDirectory = Path.GetDirectoryName(executable)
            ?? throw StorageSettingsService.Problem("アプリの実行フォルダーを確認できません。");
        return StorageSettingsService.Overlaps(full, executableDirectory);
    }
}
