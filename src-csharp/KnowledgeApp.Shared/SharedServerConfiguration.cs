using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeApp.Data;
namespace KnowledgeApp.Shared;
public sealed record SharedServerConfiguration(int Version, string ServerId, string HostName, int Port, string CertificateThumbprint)
{
    public static SharedServerConfiguration Read(string root)
    {
        var path = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "device-settings", "server.json"));
        var config = JsonSerializer.Deserialize<SharedServerConfiguration>(FileSystemBoundary.ReadBoundedFile(path, 65536),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        if (config is null || config.Version != 1 || !Guid.TryParseExact(config.ServerId, "D", out _) ||
            config.Port is < 1024 or > 65535 || Uri.CheckHostName(config.HostName) == UriHostNameType.Unknown ||
            config.CertificateThumbprint is null || config.CertificateThumbprint.Length != 40 || !config.CertificateThumbprint.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("サーバー設定を確認してください。");
        return config;
    }
}
