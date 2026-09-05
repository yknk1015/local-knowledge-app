using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeApp.Data;

namespace KnowledgeApp.CSharp;

// A build manifest detects incomplete/mixed packages; it is not a code signature.
public static class UiBundleLocator
{
    public const string ManifestName = "ui-manifest.json";
    private const int MaximumFiles = 1024;
    private static readonly Regex AssetPath = new(
        @"\Aassets/[A-Za-z0-9_-]+\.(js|css|woff2?|png|jpe?g|webp|gif|ico)\z",
        RegexOptions.CultureInvariant);

    public static string Resolve(string executableDirectory)
    {
        var baseDirectory = FileSystemBoundary.ValidatePath(executableDirectory, allowUnc: false);
        var root = FileSystemBoundary.ValidateManagedPath(baseDirectory, Path.Combine(baseDirectory, "ui"));
        var manifestPath = FileSystemBoundary.ValidateManagedPath(baseDirectory, Path.Combine(baseDirectory, ManifestName));
        using var manifest = JsonDocument.Parse(ReadBounded(manifestPath, 1024 * 1024));
        var document = manifest.RootElement;
        RequireProperties(document, "formatVersion", "files");
        if (document.GetProperty("formatVersion").GetInt32() != 1) throw InvalidBundle();
        var files = document.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 3 or > MaximumFiles)
            throw InvalidBundle();

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? index = null;
        long total = 0;
        foreach (var file in files.EnumerateArray())
        {
            RequireProperties(file, "path", "size", "sha256");
            var relative = file.GetProperty("path").GetString() ?? throw InvalidBundle();
            if (relative != "index.html" && !AssetPath.IsMatch(relative)) throw InvalidBundle();
            if (!expected.Add(relative)) throw InvalidBundle();
            var maximum = relative == "index.html" ? 1024 * 1024 : 16 * 1024 * 1024;
            var size = file.GetProperty("size").GetInt64();
            var hash = file.GetProperty("sha256").GetString() ?? throw InvalidBundle();
            if (size <= 0 || size > maximum || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
                throw InvalidBundle();
            total += size;
            if (total > 128 * 1024 * 1024) throw InvalidBundle();
            var path = FileSystemBoundary.ValidateManagedPath(root,
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var bytes = ReadBounded(path, maximum);
            if (bytes.LongLength != size || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw InvalidBundle();
            if (relative == "index.html") index = new UTF8Encoding(false, true).GetString(bytes);
        }
        if (index is null || !index.Contains("id=\"root\"", StringComparison.Ordinal) ||
            !expected.Any(path => path.EndsWith(".js", StringComparison.Ordinal)) ||
            !expected.Any(path => path.EndsWith(".css", StringComparison.Ordinal))) throw InvalidBundle();
        // Only the packaged index and flat assets tree may accompany the UI.
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            FileSystemBoundary.ValidateManagedPath(root, entry.FullName);
            if (entry is DirectoryInfo directory)
            {
                if (directory.Name != "assets") throw InvalidBundle();
                foreach (var asset in directory.EnumerateFileSystemInfos())
                {
                    FileSystemBoundary.ValidateManagedPath(root, asset.FullName);
                    if (asset is DirectoryInfo || !actual.Add("assets/" + asset.Name)) throw InvalidBundle();
                }
            }
            else if (!actual.Add(entry.Name)) throw InvalidBundle();
        }
        if (!actual.SetEquals(expected)) throw InvalidBundle();
        foreach (Match reference in Regex.Matches(index, "(?:src|href)=\"([^\"]+)\"", RegexOptions.CultureInvariant))
        {
            var path = reference.Groups[1].Value;
            if (!path.StartsWith("/assets/", StringComparison.Ordinal) || !expected.Contains(path[1..]))
                throw InvalidBundle();
        }
        return root;
    }

    private static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidBundle();
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            actual.Except(names, StringComparer.Ordinal).Any()) throw InvalidBundle();
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximum) throw InvalidBundle();
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw InvalidBundle();
        return bytes;
    }

    private static InvalidDataException InvalidBundle() => new("同梱UIの構成または整合性を確認できません。");
}
