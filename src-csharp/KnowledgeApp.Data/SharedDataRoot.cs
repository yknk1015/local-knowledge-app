namespace KnowledgeApp.Data;

public static class SharedDataRoot
{
    public static string FixedPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KnowledgeApp.Shared");
    internal static string Validate(string path)
    {
        var full = StorageSettingsService.ValidateGitFreeDirectory(path);
        if (!string.Equals(full, FixedPath, StringComparison.OrdinalIgnoreCase) || new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed)
            throw ProductionDataRoot.Problem();
        return full;
    }
}
