using System.Reflection;
using System.Text;

namespace KnowledgeApp.Data;

internal static class MigrationCatalog
{
    internal const int CurrentVersion = 9;

    internal static string Load(int version)
    {
        if (version is < 1 or > CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        var suffix = $".{version:0000}_";
        var assembly = typeof(MigrationCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.Contains(suffix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidOperationException($"DB第{version}版のマイグレーションSQLが見つかりません。");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"DB第{version}版のマイグレーションSQLを開けません。");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }
}
