using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeApp.Data;

public sealed class ArticleAttachmentService
{
    private const int MaximumImageBytes = 10 * 1024 * 1024;
    private const string FinalAssetOrigin = "https://knowledge-attachments.local";
    private const string StagedAssetOrigin = "https://knowledge-staged.local";
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _root;
    private readonly string _attachmentsRoot;
    private readonly string _stagedFilesRoot;
    private readonly string _stagedMetadataRoot;

    public ArticleAttachmentService(string dataRoot)
    {
        var root = FileSystemBoundary.ValidateManagedDataRoot(dataRoot);
        _root = root;
        _attachmentsRoot = Path.Combine(root, "attachments", "articles");
        _stagedFilesRoot = Path.Combine(root, "temp", "staged-article-images", "files");
        _stagedMetadataRoot = Path.Combine(root, "temp", "staged-article-images", "metadata");
        FileSystemBoundary.CreateManagedDirectory(root, _attachmentsRoot);
        FileSystemBoundary.CreateManagedDirectory(root, _stagedFilesRoot);
        FileSystemBoundary.CreateManagedDirectory(root, _stagedMetadataRoot);
        CleanupStaleStages();
    }

    public string AttachmentsRoot => _attachmentsRoot;

    public string StagedFilesRoot => _stagedFilesRoot;

    public StagedArticleImage StageFromPath(string sourcePath)
    {
        try
        {
            var source = FileSystemBoundary.ValidatePath(sourcePath);
            var info = new FileInfo(source);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw ImageReadError();
            }
            if (info.Length > MaximumImageBytes)
            {
                throw ImageTooLarge();
            }
            return StageBytes(info.Name, ReadImageBytes(source));
        }
        catch (AppProblemException)
        {
            throw;
        }
        catch
        {
            throw ImageReadError();
        }
    }

    public StagedArticleImage StageBase64(StageArticleImageBase64Input input)
    {
        if (input.OriginalName is null || input.BytesBase64 is null ||
            input.BytesBase64.Length > 14 * 1024 * 1024)
        {
            throw UnsupportedImage();
        }
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(input.BytesBase64);
        }
        catch (FormatException)
        {
            throw UnsupportedImage();
        }
        return StageBytes(input.OriginalName, bytes);
    }

    public StagedArticleImage StageBytes(string originalName, byte[] bytes)
    {
        if (bytes.Length > MaximumImageBytes)
        {
            throw ImageTooLarge();
        }
        var imageType = DetectImageType(bytes) ?? throw UnsupportedImage();
        var id = Guid.CreateVersion7().ToString();
        var safeName = SafeOriginalName(originalName, imageType.Extension);
        var altText = string.Concat(Path.GetFileNameWithoutExtension(safeName).EnumerateRunes()
            .Take(500).Select(rune => rune.ToString()));
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        var metadata = new StagedImageMetadata(
            id, safeName, imageType.MediaType, bytes.LongLength, sha256, altText, createdAt);
        var imagePath = Path.Combine(_stagedFilesRoot, $"{id}.{imageType.Extension}");
        var partialPath = Path.Combine(_stagedFilesRoot, $".{id}.partial");
        var metadataPath = Path.Combine(_stagedMetadataRoot, $"{id}.json");
        try
        {
            FileSystemBoundary.ValidateManagedPath(_root, partialPath);
            FileSystemBoundary.ValidateManagedPath(_root, imagePath);
            FileSystemBoundary.ValidateManagedPath(_root, metadataPath);
            using (var partial = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                partial.Write(bytes);
                partial.Flush(true);
            }
            FileSystemBoundary.ValidateManagedPath(_root, imagePath);
            File.Move(partialPath, imagePath, false);
            FileSystemBoundary.ValidateManagedPath(_root, metadataPath);
            using var metadataFile = new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(metadataFile, metadata, MetadataJsonOptions);
            metadataFile.Flush(true);
        }
        catch
        {
            TryDelete(partialPath);
            TryDelete(imagePath);
            TryDelete(metadataPath);
            throw ImageWriteError();
        }
        return new StagedArticleImage(
            id, safeName, imageType.MediaType, bytes.LongLength, sha256, altText,
            $"{StagedAssetOrigin}/{id}.{imageType.Extension}");
    }

    public StagedArticleImage StageCopyOfAttachment(ArticleAttachmentSummary attachment)
    {
        var bytes = VerifiedExistingBytes(attachment);
        return StageBytes(attachment.OriginalName, bytes);
    }

    public void DiscardStage(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed))
        {
            return;
        }
        var canonical = parsed.ToString();
        TryDelete(Path.Combine(_stagedMetadataRoot, $"{canonical}.json"));
        foreach (var extension in new[] { "png", "jpg", "webp", "gif" })
        {
            TryDelete(Path.Combine(_stagedFilesRoot, $"{canonical}.{extension}"));
        }
    }

    internal PreparedAttachments Prepare(
        string articleId,
        IReadOnlyList<AttachmentReference> references,
        IReadOnlyList<ArticleAttachmentSummary> existing)
    {
        if (!Guid.TryParseExact(articleId, "D", out _))
        {
            throw AttachmentReferenceError();
        }
        var existingById = existing.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<AttachmentRecord>();
        var createdFiles = new List<string>();
        var stagedIds = new List<string>();
        try
        {
            foreach (var reference in references)
            {
                if (!seen.Add(reference.Id))
                {
                    continue;
                }
                if (existingById.TryGetValue(reference.Id, out var current))
                {
                    _ = VerifiedExistingBytes(current);
                    records.Add(new AttachmentRecord(
                        current.Id, current.AssetPath, current.OriginalName, current.MediaType,
                        current.ByteSize, current.Sha256, reference.AltText, current.CreatedAt));
                    continue;
                }
                var staged = LoadAndVerifyStage(reference.Id);
                var imageType = ImageTypeForMedia(staged.Metadata.MediaType) ?? throw UnsupportedImage();
                var destinationDirectory = Path.Combine(_attachmentsRoot, articleId);
                FileSystemBoundary.CreateManagedDirectory(_root, destinationDirectory);
                var destination = FileSystemBoundary.ValidateManagedPath(_root,
                    Path.Combine(destinationDirectory, $"{reference.Id}.{imageType.Extension}"));
                var relativePath = $"{articleId}/{reference.Id}.{imageType.Extension}";
                if (File.Exists(destination))
                {
                    var existingBytes = ReadImageBytes(destination);
                    if (Convert.ToHexStringLower(SHA256.HashData(existingBytes)) != staged.Metadata.Sha256)
                    {
                        throw ImageWriteError();
                    }
                }
                else
                {
                    var partial = FileSystemBoundary.ValidateManagedPath(_root,
                        Path.Combine(destinationDirectory, $".{reference.Id}.partial"));
                    using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(staged.Bytes);
                        stream.Flush(true);
                    }
                    FileSystemBoundary.ValidateManagedPath(_root, destination);
                    File.Move(partial, destination, false);
                    createdFiles.Add(destination);
                }
                records.Add(new AttachmentRecord(
                    reference.Id, relativePath, staged.Metadata.OriginalName,
                    staged.Metadata.MediaType, staged.Metadata.ByteSize, staged.Metadata.Sha256,
                    reference.AltText, staged.Metadata.CreatedAt));
                stagedIds.Add(reference.Id);
            }
        }
        catch (AppProblemException)
        {
            foreach (var path in createdFiles)
            {
                TryDelete(path);
            }
            throw;
        }
        catch
        {
            foreach (var path in createdFiles)
            {
                TryDelete(path);
            }
            throw ImageWriteError();
        }
        string[] removedFiles;
        try
        {
            removedFiles = existing
                .Where(item => !seen.Contains(item.Id))
                .Select(item => ResolveManagedAttachment(item.AssetPath))
                .ToArray();
        }
        catch
        {
            foreach (var path in createdFiles)
            {
                TryDelete(path);
            }
            throw;
        }
        return new PreparedAttachments(records, createdFiles, removedFiles, stagedIds);
    }

    internal void Rollback(PreparedAttachments prepared)
    {
        foreach (var path in prepared.CreatedFiles)
        {
            TryDelete(path);
        }
    }

    internal void Commit(PreparedAttachments prepared)
    {
        foreach (var id in prepared.StagedIds)
        {
            DiscardStage(id);
        }
        foreach (var path in prepared.RemovedFiles)
        {
            TryDelete(path);
            TryDeleteEmptyParent(path);
        }
    }

    public ArticleDetail HydrateArticle(ArticleDetail article)
    {
        var hydrated = article.Attachments.Select(attachment =>
        {
            _ = ResolveManagedAttachment(attachment.AssetPath);
            return attachment with
            {
                AssetPath = $"{FinalAssetOrigin}/{attachment.AssetPath.Replace('\\', '/')}"
            };
        }).ToArray();
        return article with { Attachments = hydrated };
    }

    internal string ResolveManagedAttachmentForTest(string relativePath) => ResolveManagedAttachment(relativePath);

    private byte[] VerifiedExistingBytes(ArticleAttachmentSummary attachment)
    {
        var path = ResolveManagedAttachment(attachment.AssetPath);
        byte[] bytes;
        try
        {
            bytes = ReadImageBytes(path);
        }
        catch
        {
            throw StagedImageMissing();
        }
        var detected = DetectImageType(bytes);
        if (detected is null || detected.MediaType != attachment.MediaType ||
            bytes.LongLength != attachment.ByteSize ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != attachment.Sha256)
        {
            throw StagedImageMissing();
        }
        return bytes;
    }

    private LoadedStage LoadAndVerifyStage(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed))
        {
            throw AttachmentReferenceError();
        }
        var canonical = parsed.ToString();
        StagedImageMetadata metadata;
        try
        {
            var metadataPath = FileSystemBoundary.ValidateManagedPath(_root,
                Path.Combine(_stagedMetadataRoot, $"{canonical}.json"));
            using var metadataStream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (metadataStream.Length > 64 * 1024) throw StagedImageMissing();
            metadata = JsonSerializer.Deserialize<StagedImageMetadata>(
                metadataStream,
                MetadataJsonOptions) ?? throw new JsonException();
        }
        catch
        {
            throw StagedImageMissing();
        }
        if (metadata.Id != canonical || metadata.ByteSize is < 0 or > MaximumImageBytes)
        {
            throw StagedImageMissing();
        }
        var imageType = ImageTypeForMedia(metadata.MediaType) ?? throw UnsupportedImage();
        var imagePath = FileSystemBoundary.ValidateManagedPath(_root,
            Path.Combine(_stagedFilesRoot, $"{canonical}.{imageType.Extension}"));
        byte[] bytes;
        try
        {
            bytes = ReadImageBytes(imagePath);
        }
        catch
        {
            throw StagedImageMissing();
        }
        var detected = DetectImageType(bytes);
        if (detected is null || detected.MediaType != metadata.MediaType ||
            bytes.LongLength != metadata.ByteSize ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != metadata.Sha256)
        {
            throw StagedImageMissing();
        }
        return new LoadedStage(metadata, bytes);
    }

    private string ResolveManagedAttachment(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Replace('\\', '/').Split('/').Any(part => part is ".." or "." or ""))
        {
            throw AttachmentReferenceError();
        }
        var resolved = Path.GetFullPath(Path.Combine(_attachmentsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _attachmentsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw AttachmentReferenceError();
        }
        try { return FileSystemBoundary.ValidateManagedPath(_root, resolved); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { throw AttachmentReferenceError(); }
    }

    private void CleanupStaleStages()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var directory in new[] { _stagedFilesRoot, _stagedMetadataRoot })
        {
            FileSystemBoundary.ValidateManagedPath(_root, directory);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                try
                {
                    if (!IsGeneratedStageName(Path.GetFileName(path), directory == _stagedMetadataRoot)) continue;
                    FileSystemBoundary.ValidateManagedPath(_root, path);
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // 次回起動時に再試行する。FAQ本体や確定画像には触れない。
                }
            }
        }
    }

    private static bool IsGeneratedStageName(string name, bool metadata)
    {
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (metadata) return extension == ".json" && Guid.TryParseExact(stem, "D", out _);
        if (extension == ".partial" && stem.StartsWith('.')) stem = stem[1..];
        return extension is ".png" or ".jpg" or ".webp" or ".gif" or ".partial" && Guid.TryParseExact(stem, "D", out _);
    }

    private static byte[] ReadImageBytes(string path)
    {
        FileSystemBoundary.ValidatePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumImageBytes) throw ImageTooLarge();
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw ImageTooLarge();
        return bytes;
    }

    private static ImageType? DetectImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return new ImageType("image/png", "png");
        }
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
        {
            return new ImageType("image/jpeg", "jpg");
        }
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            return new ImageType("image/gif", "gif");
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return new ImageType("image/webp", "webp");
        }
        return null;
    }

    private static ImageType? ImageTypeForMedia(string mediaType) => mediaType switch
    {
        "image/png" => new ImageType("image/png", "png"),
        "image/jpeg" => new ImageType("image/jpeg", "jpg"),
        "image/webp" => new ImageType("image/webp", "webp"),
        "image/gif" => new ImageType("image/gif", "gif"),
        _ => null
    };

    private static string SafeOriginalName(string name, string extension)
    {
        var fileName = Path.GetFileName(name ?? string.Empty).Trim();
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "image";
        }
        var safeStem = string.Concat(stem.EnumerateRunes().Take(200).Select(rune => rune.ToString()));
        return $"{safeStem}.{extension}";
    }

    private void TryDeleteEmptyParent(string path)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is not null && !string.Equals(parent, _attachmentsRoot, StringComparison.OrdinalIgnoreCase) &&
                !Directory.EnumerateFileSystemEntries(FileSystemBoundary.ValidateManagedPath(_root, parent)).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch
        {
            // DB保存は成功済みのため、残ファイルは次回整合確認の対象とする。
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(FileSystemBoundary.ValidateManagedPath(_root, path)); }
        catch { }
    }

    private static AppProblemException ImageTooLarge() => new(new AppProblem(
        "ATT-001", "画像のサイズが10MBを超えています。",
        "画像を圧縮または縮小してから、もう一度追加してください。"));

    private static AppProblemException UnsupportedImage() => new(new AppProblem(
        "ATT-002", "このファイルは対応している画像形式ではありません。",
        "PNG、JPEG、WebP、GIFのいずれかを選択してください。拡張子だけを変更したファイルは使用できません。"));

    private static AppProblemException ImageReadError() => new(new AppProblem(
        "ATT-003", "選択した画像を読み込めませんでした。",
        "画像が移動・削除されていないことを確認して、もう一度選択してください。"));

    private static AppProblemException ImageWriteError() => new(new AppProblem(
        "ATT-003", "画像をアプリの管理フォルダへ保存できませんでした。",
        "データ保存先の空き容量と書き込み権限を確認して、もう一度追加してください。"));

    private static AppProblemException StagedImageMissing() => new(new AppProblem(
        "ATT-005", "保存前の画像が見つからないか、内容が変更されています。",
        "回答内の画像を削除し、画像追加ボタンから選び直してください。"));

    private static AppProblemException AttachmentReferenceError() => new(new AppProblem(
        "ATT-004", "FAQの画像参照が正しくありません。",
        "画像を一度削除し、画像追加ボタンから選び直してください。"));

    private sealed record ImageType(string MediaType, string Extension);
    private sealed record StagedImageMetadata(
        string Id, string OriginalName, string MediaType, long ByteSize,
        string Sha256, string AltText, string CreatedAt);
    private sealed record LoadedStage(StagedImageMetadata Metadata, byte[] Bytes);
}

internal sealed record PreparedAttachments(
    IReadOnlyList<AttachmentRecord> Records,
    IReadOnlyList<string> CreatedFiles,
    IReadOnlyList<string> RemovedFiles,
    IReadOnlyList<string> StagedIds);
