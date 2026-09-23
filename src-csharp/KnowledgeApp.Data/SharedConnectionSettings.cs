using System.Text.Json;
namespace KnowledgeApp.Data;

public sealed record SharedConnectionSettings(int Version, string Url, string ServerId)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string Config(string root) => FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "device-settings", "shared-connection.json"));
    public static SharedConnectionSettings? Read(string root)
    {
        var file = Config(root);
        if (!File.Exists(file)) return null;
        var settings = JsonSerializer.Deserialize<SharedConnectionSettings>(FileSystemBoundary.ReadBoundedFile(file, 4096), JsonOptions);
        if (settings is not null) settings.Validate();
        return settings;
    }
    public Uri Validate()
    {
        if (Version != 1 || !Guid.TryParseExact(ServerId, "D", out _) || !Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new AppProblemException(new AppProblem("SHARE-001", "共有接続先の指定が正しくありません。", "HTTPSのサーバーURLと管理者から案内されたサーバーIDを指定してください。"));
        return uri;
    }
    public static void Save(string root, SharedConnectionSettings? settings)
    {
        settings?.Validate();
        var file = Config(root);
        var directory = Path.GetDirectoryName(file)!;
        FileSystemBoundary.CreateManagedDirectory(root, directory);
        var partial = Path.Combine(directory, $"connection-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions)); output.Flush(true); }
            File.Move(partial, file, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
