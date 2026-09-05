using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    internal string CreateTransferSafetyBackup(string dataRoot, string transferKind) => ExecuteLocked(() =>
    {
        var resolvedRoot = FileSystemBoundary.ValidateManagedDataRoot(dataRoot);
        var expectedRoot = Directory.GetParent(Path.GetDirectoryName(OpenInfo.DatabasePath)!)!.FullName;
        if (!string.Equals(resolvedRoot, expectedRoot, StringComparison.OrdinalIgnoreCase) ||
            transferKind is not ("csv" or "json"))
        {
            throw BackupProblem("取込前バックアップの保存先を安全に確認できませんでした。");
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var kindLabel = transferKind == "csv" ? "csv" : "json";
        var displayName = $"KnowledgeApp_before_{kindLabel}_import_{timestamp}";
        var safetyDirectory = Path.Combine(resolvedRoot, "safety-backups");
        var temporaryRoot = Path.Combine(resolvedRoot, "temp");
        FileSystemBoundary.CreateManagedDirectory(resolvedRoot, safetyDirectory);
        FileSystemBoundary.CreateManagedDirectory(resolvedRoot, temporaryRoot);
        var destination = UniqueBackupPath(safetyDirectory, displayName);
        var workingDirectory = Path.Combine(temporaryRoot, $"transfer-backup-{Guid.CreateVersion7():D}");
        var snapshot = Path.Combine(workingDirectory, "data", "knowledge.db");
        var localArchive = Path.Combine(workingDirectory, "completed.faqbackup");
        FileSystemBoundary.CreateManagedDirectory(resolvedRoot, Path.GetDirectoryName(snapshot)!);

        try
        {
            using (var snapshotConnection = OpenSnapshot(snapshot))
            {
                _connection.BackupDatabase(snapshotConnection);
                QuickCheck(snapshotConnection);
                if (SchemaVersion(snapshotConnection) != MigrationCatalog.CurrentVersion)
                {
                    throw BackupProblem("取込前バックアップのDB版を確認できませんでした。");
                }
            }

            var sources = CollectBackupSources(resolvedRoot, snapshot);
            var entries = sources.Select(source => new TransferBackupFileEntry(
                source.ArchivePath,
                new FileInfo(source.SourcePath).Length,
                HashFile(source.SourcePath))).ToArray();
            var counts = ReadTransferBackupCounts();
            var manifest = new TransferBackupManifest(
                1,
                "0.4.4-csharp-migration",
                MigrationCatalog.CurrentVersion,
                2,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                displayName,
                entries.LongLength,
                entries.Sum(entry => entry.Size),
                counts,
                entries);
            WriteBackupArchive(localArchive, manifest, sources);
            VerifyBackupArchive(localArchive, manifest);
            FileSystemBoundary.ValidateManagedPath(resolvedRoot, localArchive);
            FileSystemBoundary.ValidateManagedPath(resolvedRoot, destination);
            File.Move(localArchive, destination);
            VerifyBackupArchive(destination, manifest);
            return destination;
        }
        catch (AppProblemException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException or JsonException)
        {
            throw BackupProblem("取込前の安全バックアップを作成できませんでした。");
        }
        finally
        {
            TryDeleteTransferWorkingDirectory(workingDirectory, temporaryRoot);
        }
    });

    private static SqliteConnection OpenSnapshot(string path)
    {
        FileSystemBoundary.ValidatePath(path, allowUnc: false);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            FileSystemBoundary.ValidatePath(path + suffix, allowUnc: false);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private TransferBackupCounts ReadTransferBackupCounts()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM articles WHERE deleted_at IS NULL),
                (SELECT COUNT(*) FROM categories),
                (SELECT COUNT(*) FROM article_attachments),
                (SELECT COUNT(*) FROM manuals)
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw BackupProblem("取込前バックアップの件数を確認できませんでした。");
        }
        return new TransferBackupCounts(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static IReadOnlyList<TransferBackupSource> CollectBackupSources(string dataRoot, string snapshot)
    {
        FileSystemBoundary.ValidateManagedDataRoot(dataRoot);
        FileSystemBoundary.ValidateManagedPath(dataRoot, snapshot);
        var sources = new List<TransferBackupSource>
        {
            new("data/knowledge.db", snapshot)
        };
        foreach (var (relativeRoot, archiveRoot) in new[]
        {
            (Path.Combine("attachments", "articles"), "attachments/articles"),
            ("manuals", "manuals"),
            ("settings", "settings")
        })
        {
            var root = Path.Combine(dataRoot, relativeRoot);
            foreach (var file in CollectSafeFilePaths(root))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
                {
                    throw BackupProblem("バックアップ対象のファイル位置を確認できませんでした。");
                }
                var archivePath = $"{archiveRoot}/{relative.Replace('\\', '/')}";
                sources.Add(new TransferBackupSource(archivePath, file));
            }
        }
        return sources.OrderBy(source => source.ArchivePath, StringComparer.Ordinal).ToArray();
    }

    private static void WriteBackupArchive(
        string path,
        TransferBackupManifest manifest,
        IReadOnlyList<TransferBackupSource> sources)
    {
        FileSystemBoundary.ValidatePath(path, allowUnc: false);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var manifestStream = manifestEntry.Open())
        {
            JsonSerializer.Serialize(manifestStream, manifest, options);
        }
        foreach (var source in sources)
        {
            FileSystemBoundary.ValidatePath(source.SourcePath, allowUnc: false);
            var entry = archive.CreateEntry(source.ArchivePath, CompressionLevel.Optimal);
            using var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static void VerifyBackupArchive(string path, TransferBackupManifest expectedManifest)
    {
        var manifest = ReadVerifiedBackupArchive(path);
        if (manifest.BackupFormatVersion != expectedManifest.BackupFormatVersion ||
            manifest.AppVersion != expectedManifest.AppVersion ||
            manifest.SchemaVersion != expectedManifest.SchemaVersion ||
            manifest.RichTextFormatVersion != expectedManifest.RichTextFormatVersion ||
            manifest.CreatedAt != expectedManifest.CreatedAt ||
            manifest.DisplayName != expectedManifest.DisplayName ||
            manifest.Counts != expectedManifest.Counts ||
            !manifest.Files.SequenceEqual(expectedManifest.Files) ||
            manifest.FileCount != manifest.Files.Count ||
            manifest.TotalBytes != manifest.Files.Sum(entry => entry.Size))
        {
            throw BackupProblem("バックアップの検証情報が一致しません。");
        }
    }

    private static string UniqueBackupPath(string directory, string displayName)
    {
        var path = Path.Combine(directory, $"{displayName}.faqbackup");
        return File.Exists(path)
            ? Path.Combine(directory, $"{displayName}_{Guid.CreateVersion7():D}.faqbackup")
            : path;
    }

    private static string HashFile(string path)
    {
        FileSystemBoundary.ValidatePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDeleteTransferWorkingDirectory(string workingDirectory, string temporaryRoot)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return;
        }
        var relative = Path.GetRelativePath(Path.GetFullPath(temporaryRoot), Path.GetFullPath(workingDirectory));
        if (relative.StartsWith("transfer-backup-", StringComparison.Ordinal) &&
            !relative.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathFullyQualified(relative))
        {
            try
            {
                FileSystemBoundary.DeleteManagedDirectory(temporaryRoot, workingDirectory);
            }
            catch (IOException)
            {
                // 合成データルート内の生成途中ファイルだけなので、次回の一時ファイル整理に任せる。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    private static AppProblemException BackupProblem(string message) => new(new AppProblem(
        "BK-004",
        message,
        "保存先の空き容量とアクセス権を確認して、もう一度お試しください。"));

    private sealed record TransferBackupSource(string ArchivePath, string SourcePath);

    private sealed record TransferBackupManifest(
        int BackupFormatVersion,
        string AppVersion,
        int SchemaVersion,
        int RichTextFormatVersion,
        string CreatedAt,
        string DisplayName,
        long FileCount,
        long TotalBytes,
        TransferBackupCounts Counts,
        IReadOnlyList<TransferBackupFileEntry> Files);

    private sealed record TransferBackupFileEntry(string Path, long Size, string Sha256);

    private sealed record TransferBackupCounts(long Articles, long Categories, long Attachments, long Manuals);
}
