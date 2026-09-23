using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    private const int BackupFormatVersion = 1;
    private const int RichTextBackupFormatVersion = 2;
    private const int MaximumBackupFiles = 10_000;
    private const long MaximumBackupBytes = 10L * 1024 * 1024 * 1024;
    private const long MinimumBackupCapacityMargin = 1024 * 1024;
    private const long MaximumManifestBytes = 5L * 1024 * 1024;
    private const string CSharpBackupAppVersion = SettingsService.CSharpAppVersion;
    private static readonly JsonSerializerOptions StrictBackupJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal BackupOverview GetFullBackupOverview(string dataRoot) => ExecuteLocked(() =>
    {
        var resolvedRoot = ValidateFullBackupDataRoot(dataRoot);
        var counts = ToBackupCounts(ReadTransferBackupCounts());
        return new BackupOverview(
            EstimateBackupSourceBytes(resolvedRoot),
            counts,
            ReadLastSuccessfulBackupDirectory(resolvedRoot));
    });

    internal BackupResult CreateFullBackup(
        string dataRoot,
        CreateFullBackupInput input,
        bool rememberDestination) => ExecuteLocked(() => CreateFullBackupUnlocked(
            ValidateFullBackupDataRoot(dataRoot),
            input,
            rememberDestination));

    internal VerifiedBackupPreview InspectFullBackupWithIdentity(string dataRoot, string sourcePath) => ExecuteLocked(() =>
    {
        var resolvedRoot = ValidateFullBackupDataRoot(dataRoot);
        var source = ValidateBackupSource(sourcePath);
        using var file = OpenBackupReadHandle(source);
        var preview = InspectFullBackupUnlocked(resolvedRoot, source, file);
        return new VerifiedBackupPreview(preview, HashBackupHandle(file));
    });

    internal Action? BackupSourceVerifiedForTest { get; set; }
    internal Action<string>? RestoreCheckpointForTest { get; set; }

    internal RestoreResult RestoreFullBackup(string dataRoot, string sourcePath, string? expectedFileSha256 = null) => ExecuteLocked(() =>
    {
        var resolvedRoot = ValidateFullBackupDataRoot(dataRoot);
        var source = ValidateBackupSource(sourcePath);
        var stagingRoot = Path.Combine(resolvedRoot, "restore-staging");
        FileSystemBoundary.CreateManagedDirectory(resolvedRoot, stagingRoot);
        var staging = Path.Combine(stagingRoot, $"restore-{Guid.CreateVersion7():D}");
        FileSystemBoundary.CreateManagedDirectory(resolvedRoot, staging);
        var journalStarted = false;

        try
        {
            var manifest = ExtractVerifiedBackupArchive(source, staging, expectedFileSha256, BackupSourceVerifiedForTest);
            var restoredDatabase = Path.Combine(staging, "data", "knowledge.db");
            PrepareBackupSnapshotForRestore(restoredDatabase, manifest);
            RequireSharedRestoreAdministrator(restoredDatabase);
            foreach (var relative in new[] { Path.Combine("attachments", "articles"), "manuals", "settings" })
            {
                FileSystemBoundary.CreateManagedDirectory(resolvedRoot, Path.Combine(staging, relative));
            }

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var safetyName = $"KnowledgeApp_before_restore_{timestamp}";
            var safetyDirectory = Path.Combine(resolvedRoot, "safety-backups");
            FileSystemBoundary.CreateManagedDirectory(resolvedRoot, safetyDirectory);
            var safetyPath = UniqueBackupPath(safetyDirectory, safetyName);
            CreateFullBackupUnlocked(
                resolvedRoot,
                new CreateFullBackupInput(safetyPath, safetyName, false),
                rememberDestination: false);

            RecoveryEpoch = Guid.NewGuid();
            BeginRestoreJournal(resolvedRoot, safetyPath);
            journalStarted = true;
            RestoreCheckpointForTest?.Invoke("journal-written");
            RestoreDatabaseFromSnapshot(restoredDatabase);
            RestoreCheckpointForTest?.Invoke("database-restored");
            ReplaceManagedBackupDirectories(resolvedRoot, staging, RestoreCheckpointForTest);
            VerifyRestoredManagedFiles(resolvedRoot, manifest);
            FlushRestoredDatabase();
            RestoreCheckpointForTest?.Invoke("restore-verified");
            CompleteRestoreJournal(resolvedRoot);
            journalStarted = false;

            return new RestoreResult(
                source,
                safetyPath,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ToBackupCounts(manifest.Counts));
        }
        catch (Exception exception)
        {
            if (journalStarted)
            {
                try
                {
                    RecoverRestoreFromSafetyArchive(resolvedRoot, RestoreDatabaseFromSnapshot,
                        FlushRestoredDatabase, RestoreCheckpointForTest);
                }
                catch
                {
                    _restoreRecoveryRequired = true;
                    throw RestoreRecoveryProblem();
                }
            }
            if (exception is AppProblemException) throw;
            throw RestoreProblem("バックアップを安全に復元できませんでした。");
        }
        finally
        {
            TryDeleteGeneratedDirectory(staging, stagingRoot, "restore-");
        }
    });

    private BackupResult CreateFullBackupUnlocked(
        string dataRoot,
        CreateFullBackupInput input,
        bool rememberDestination)
    {
        var sourceVersion = SchemaVersion(_connection);
        var estimatedBytes = EstimateBackupSourceBytes(dataRoot);
        var requiredArchiveBytes = RequiredArchiveCapacity(estimatedBytes);
        var destination = ValidateBackupDestination(
            input.DestinationPath,
            input.DisplayName,
            input.Overwrite,
            requiredArchiveBytes);
        ValidateAvailableBackupCapacity(
            Path.Combine(dataRoot, "temp"),
            checked(estimatedBytes + requiredArchiveBytes + MinimumBackupCapacityMargin));

        var temporaryRoot = Path.Combine(dataRoot, "temp");
        FileSystemBoundary.CreateManagedDirectory(dataRoot, temporaryRoot);
        var workingDirectory = Path.Combine(temporaryRoot, $"full-backup-{Guid.CreateVersion7():D}");
        var snapshot = Path.Combine(workingDirectory, "data", "knowledge.db");
        var localArchive = Path.Combine(workingDirectory, "completed.faqbackup");
        FileSystemBoundary.CreateManagedDirectory(dataRoot, Path.GetDirectoryName(snapshot)!);

        try
        {
            using (var snapshotConnection = OpenSnapshot(snapshot))
            {
                _connection.BackupDatabase(snapshotConnection);
                QuickCheck(snapshotConnection);
                if (SchemaVersion(snapshotConnection) != sourceVersion)
                {
                    throw BackupReadProblem("バックアップ用データベースの版を確認できませんでした。");
                }
            }

            var sources = CollectBackupSources(dataRoot, snapshot);
            if (sources.Count > MaximumBackupFiles)
            {
                throw BackupReadProblem("バックアップ対象のファイル数が多すぎます。");
            }
            var entries = sources.Select(source => new TransferBackupFileEntry(
                source.ArchivePath,
                new FileInfo(source.SourcePath).Length,
                HashFile(source.SourcePath))).ToArray();
            var totalBytes = entries.Sum(entry => entry.Size);
            if (totalBytes > MaximumBackupBytes)
            {
                throw BackupReadProblem("バックアップ対象の合計サイズが上限を超えています。");
            }
            var counts = ReadTransferBackupCounts();
            var manifest = new TransferBackupManifest(
                BackupFormatVersion,
                CSharpBackupAppVersion,
                sourceVersion,
                RichTextBackupFormatVersion,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                input.DisplayName.Trim(),
                entries.LongLength,
                totalBytes,
                counts,
                entries);
            WriteBackupArchive(localArchive, manifest, sources);
            VerifyBackupArchive(localArchive, manifest);
            PublishBackupArchive(dataRoot, localArchive, destination, input.Overwrite);
            var published = InspectFullBackupUnlocked(dataRoot, destination);
            if (rememberDestination)
            {
                RememberSuccessfulBackupDirectory(dataRoot, destination);
            }
            return new BackupResult(
                destination,
                published.DisplayName,
                published.CreatedAt,
                totalBytes,
                ToBackupCounts(counts));
        }
        catch (AppProblemException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or SqliteException)
        {
            throw BackupWriteProblem();
        }
        finally
        {
            TryDeleteGeneratedDirectory(workingDirectory, temporaryRoot, "full-backup-");
        }
    }

    private BackupPreview InspectFullBackupUnlocked(string dataRoot, string sourcePath, FileStream? openedFile = null)
    {
        var source = ValidateBackupSource(sourcePath);
        using var ownedFile = openedFile is null ? OpenBackupReadHandle(source) : null;
        var file = openedFile ?? ownedFile!;
        file.Position = 0;
        using var archive = OpenBackupArchive(file);
        var manifest = ReadVerifiedBackupArchive(source, archive);
        var temporaryRoot = Path.Combine(dataRoot, "temp");
        FileSystemBoundary.CreateManagedDirectory(dataRoot, temporaryRoot);
        var inspection = Path.Combine(temporaryRoot, $"inspect-backup-{Guid.CreateVersion7():D}");
        var snapshot = Path.Combine(inspection, "data", "knowledge.db");
        FileSystemBoundary.CreateManagedDirectory(dataRoot, Path.GetDirectoryName(snapshot)!);
        try
        {
            ExtractNamedBackupEntry(source, "data/knowledge.db", snapshot,
                manifest.Files.Single(entry => string.Equals(entry.Path, "data/knowledge.db", StringComparison.OrdinalIgnoreCase)), archive);
            PrepareBackupSnapshotForRestore(snapshot, manifest);
            RequireSharedRestoreAdministrator(snapshot);
        }
        finally
        {
            TryDeleteGeneratedDirectory(inspection, temporaryRoot, "inspect-backup-");
        }
        return new BackupPreview(
            source,
            manifest.DisplayName,
            manifest.CreatedAt,
            manifest.AppVersion,
            manifest.SchemaVersion,
            manifest.BackupFormatVersion,
            manifest.TotalBytes,
            ToBackupCounts(manifest.Counts));
    }

    private static TransferBackupManifest ReadVerifiedBackupArchive(string source, ZipArchive? openedArchive = null)
    {
        try
        {
            FileSystemBoundary.ValidatePath(source);
            using var file = openedArchive is null
                ? new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            using var ownedArchive = file is null ? null : new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            var archive = openedArchive ?? ownedArchive!;
            if (archive.Entries.Count is < 2 or > MaximumBackupFiles + 1)
            {
                throw BackupInvalidProblem("バックアップ内のファイル数が正しくありません。");
            }

            var manifestEntries = archive.Entries
                .Where(entry => string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (manifestEntries.Length != 1 || manifestEntries[0].Length > MaximumManifestBytes)
            {
                throw BackupInvalidProblem("バックアップに有効な検証情報が含まれていません。");
            }
            TransferBackupManifest manifest;
            using (var stream = manifestEntries[0].Open())
            {
                manifest = JsonSerializer.Deserialize<TransferBackupManifest>(stream, StrictBackupJsonOptions)
                    ?? throw BackupInvalidProblem("バックアップの検証情報が正しくありません。");
            }
            ValidateBackupManifest(manifest);

            var expected = new Dictionary<string, TransferBackupFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Files)
            {
                ValidateBackupArchivePath(entry.Path);
                if (entry.Size < 0 || !IsSha256(entry.Sha256) || !expected.TryAdd(entry.Path, entry))
                {
                    throw BackupInvalidProblem("バックアップのファイル一覧が正しくありません。");
                }
            }
            if (!expected.ContainsKey("data/knowledge.db"))
            {
                throw BackupInvalidProblem("バックアップにFAQデータベースが含まれていません。");
            }

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var archived in archive.Entries)
            {
                ValidateBackupArchivePath(archived.FullName, allowManifest: true);
                if (!found.Add(archived.FullName))
                {
                    throw BackupInvalidProblem("バックアップ内に同名ファイルがあります。");
                }
                if (string.Equals(archived.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!expected.TryGetValue(archived.FullName, out var expectedEntry))
                {
                    throw BackupInvalidProblem("バックアップに未登録のファイルが含まれています。");
                }
                if (archived.Length != expectedEntry.Size)
                {
                    throw BackupInvalidProblem("バックアップ内のファイルサイズが一致しません。");
                }
                using var stream = archived.Open();
                var hash = HashBoundedBackupEntry(stream, expectedEntry.Size);
                if (!string.Equals(hash, expectedEntry.Sha256, StringComparison.Ordinal))
                {
                    throw BackupInvalidProblem("バックアップ内のファイルが破損または変更されています。");
                }
            }
            if (expected.Keys.Any(path => !found.Contains(path)))
            {
                throw BackupInvalidProblem("バックアップ内に不足しているファイルがあります。");
            }
            return manifest;
        }
        catch (AppProblemException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            throw BackupInvalidProblem("バックアップファイルの形式が正しくありません。");
        }
    }

    private static void ValidateBackupManifest(TransferBackupManifest manifest)
    {
        if (manifest.BackupFormatVersion != BackupFormatVersion ||
            manifest.SchemaVersion is < 1 or > MigrationCatalog.CurrentVersion ||
            manifest.RichTextFormatVersion is < 1 or > RichTextBackupFormatVersion)
        {
            throw new AppProblemException(new AppProblem(
                "BK-009",
                "このバックアップは現在のアプリでは復元できない形式です。",
                "KnowledgeAppを最新版へ更新してから、もう一度お試しください。"));
        }
        if (string.IsNullOrWhiteSpace(manifest.AppVersion) || manifest.AppVersion.Length > 100 ||
            !ValidRfc3339(manifest.CreatedAt) ||
            !IsValidBackupName(manifest.DisplayName) ||
            manifest.Files is null || manifest.FileCount != manifest.Files.Count ||
            manifest.FileCount is < 1 or > MaximumBackupFiles ||
            manifest.Counts is null || manifest.Counts.Articles < 0 || manifest.Counts.Categories < 0 ||
            manifest.Counts.Attachments < 0 || manifest.Counts.Manuals < 0)
        {
            throw BackupInvalidProblem("バックアップの検証情報が正しくありません。");
        }
        long total;
        try
        {
            total = checked(manifest.Files.Sum(entry => entry.Size));
        }
        catch (OverflowException)
        {
            throw BackupInvalidProblem("バックアップの合計サイズが正しくありません。");
        }
        if (total != manifest.TotalBytes || total > MaximumBackupBytes)
        {
            throw BackupInvalidProblem("バックアップの合計サイズが正しくありません。");
        }
    }

    private static TransferBackupManifest ExtractVerifiedBackupArchive(string source, string destination,
        string? expectedFileSha256, Action? sourceVerifiedForTest)
    {
        FileSystemBoundary.ValidatePath(source);
        FileSystemBoundary.ValidatePath(destination, allowUnc: false);
        using var file = OpenBackupReadHandle(source);
        if (expectedFileSha256 is not null && !string.Equals(HashBackupHandle(file), expectedFileSha256, StringComparison.Ordinal))
            throw BackupService.ReconfirmationRequired();
        if (expectedFileSha256 is not null) sourceVerifiedForTest?.Invoke();
        using var archive = OpenBackupArchive(file);
        // Keep the same read handle open for verification and extraction so a
        // replaced archive cannot be applied after validating another file.
        var manifest = ReadVerifiedBackupArchive(source, archive);
        foreach (var archived in archive.Entries)
        {
            if (string.Equals(archived.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ValidateBackupArchivePath(archived.FullName);
            var output = Path.Combine(destination, archived.FullName.Replace('/', Path.DirectorySeparatorChar));
            var fullOutput = Path.GetFullPath(output);
            var relative = Path.GetRelativePath(Path.GetFullPath(destination), fullOutput);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
            {
                throw BackupInvalidProblem("バックアップに安全でないファイルパスが含まれています。");
            }
            FileSystemBoundary.CreateManagedDirectory(destination, Path.GetDirectoryName(fullOutput)!);
            FileSystemBoundary.ValidateManagedPath(destination, fullOutput);
            using var input = archived.Open();
            using var target = new FileStream(fullOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(target);
            target.Flush(flushToDisk: true);
        }
        return manifest;
    }

    private static void ExtractNamedBackupEntry(
        string source, string name, string destination, TransferBackupFileEntry expected, ZipArchive archive)
    {
        FileSystemBoundary.ValidatePath(source);
        FileSystemBoundary.ValidatePath(destination, allowUnc: false);
        var archived = archive.Entries.SingleOrDefault(entry =>
            string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase))
            ?? throw BackupInvalidProblem("バックアップにFAQデータベースが含まれていません。");
        if (archived.Length != expected.Size) throw BackupInvalidProblem("確認中にバックアップが変更されています。");
        using var input = archived.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (HashBoundedBackupEntry(input, expected.Size, output) != expected.Sha256)
            throw BackupInvalidProblem("確認中にバックアップが変更されています。");
        output.Flush(flushToDisk: true);
    }

    private static FileStream OpenBackupReadHandle(string source)
    {
        try { return new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw BackupInvalidProblem("バックアップを読み取れません。使用中のアプリを閉じて選び直してください。"); }
    }

    private static ZipArchive OpenBackupArchive(FileStream file)
    {
        try { return new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true); }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        { throw BackupInvalidProblem("バックアップファイルの形式が正しくありません。"); }
    }

    private static string HashBackupHandle(FileStream file)
    {
        file.Position = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(file));
        file.Position = 0;
        return hash;
    }

    private static string HashBoundedBackupEntry(Stream input, long expectedSize, Stream? output = null)
    {
        if (expectedSize is < 0 or > MaximumBackupBytes) throw BackupInvalidProblem("バックアップのファイルサイズが正しくありません。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long count = 0;
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            count = checked(count + read);
            if (count > expectedSize) throw BackupInvalidProblem("バックアップの展開サイズが検証情報を超えています。");
            hash.AppendData(buffer, 0, read);
            output?.Write(buffer, 0, read);
        }
        if (count != expectedSize) throw BackupInvalidProblem("バックアップの展開サイズが検証情報と一致しません。");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void ValidateBackupSnapshot(string path, int expectedSchemaVersion)
    {
        try
        {
            FileSystemBoundary.ValidatePath(path, allowUnc: false);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            connection.Open();
            QuickCheck(connection);
            using var required = connection.CreateCommand();
            required.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('schema_migrations', 'categories', 'articles')";
            if (Convert.ToInt64(required.ExecuteScalar(), CultureInfo.InvariantCulture) != 3)
            {
                throw new AppProblemException(new AppProblem(
                    "BK-007",
                    "このファイルはKnowledgeAppのバックアップではありません。",
                    "拡張子が.faqbackupの正しいファイルを選択してください。"));
            }
            if (SchemaVersion(connection) != expectedSchemaVersion)
            {
                throw BackupInvalidProblem("バックアップ内のDB版が検証情報と一致しません。");
            }
            using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read())
            {
                throw BackupInvalidProblem("バックアップ内のデータ関係が破損しています。");
            }
        }
        catch (AppProblemException exception) when (exception.Problem.Code == "DB-001")
        {
            throw BackupInvalidProblem("バックアップ内のデータベースが破損しています。");
        }
        catch (SqliteException)
        {
            throw BackupInvalidProblem("バックアップ内のデータベースを検査できませんでした。");
        }
    }

    private void RestoreDatabaseFromSnapshot(string sourcePath)
    {
        FileSystemBoundary.ValidatePath(sourcePath, allowUnc: false);
        FileSystemBoundary.ValidatePath(OpenInfo.DatabasePath, allowUnc: false);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        source.Open();
        // Only a fully prepared snapshot (or the current-version rollback copy)
        // may reach the live connection. Never migrate or bootstrap live data here.
        RequireCurrentRehearsalVersion(ValidateExisting(source));
        ValidatePersistentUsers(source);
        source.BackupDatabase(_connection);
        ExecuteNonQuery(_connection, "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        ValidateExisting(_connection);
        ValidatePersistentUsers(_connection);
        QuickCheck(_connection);
    }

    private static void ReplaceManagedBackupDirectories(string dataRoot, string staging, Action<string>? checkpoint = null)
    {
        var rollbackRoot = Path.Combine(staging, "rollback-files");
        FileSystemBoundary.CreateManagedDirectory(dataRoot, rollbackRoot);
        var plans = new[]
        {
            new DirectorySwapPlan(Path.Combine(staging, "attachments", "articles"), Path.Combine(dataRoot, "attachments", "articles"), Path.Combine(rollbackRoot, "attachments")),
            new DirectorySwapPlan(Path.Combine(staging, "manuals"), Path.Combine(dataRoot, "manuals"), Path.Combine(rollbackRoot, "manuals")),
            new DirectorySwapPlan(Path.Combine(staging, "settings"), Path.Combine(dataRoot, "settings"), Path.Combine(rollbackRoot, "settings"))
        };
        var completed = new List<CompletedDirectorySwap>();
        try
        {
            foreach (var plan in plans)
            {
                FileSystemBoundary.ValidateManagedPath(dataRoot, plan.Target);
                FileSystemBoundary.ValidateManagedPath(dataRoot, plan.Incoming);
                FileSystemBoundary.ValidateManagedPath(dataRoot, plan.Rollback);
                FileSystemBoundary.ValidateTree(plan.Target);
                FileSystemBoundary.ValidateTree(plan.Incoming);
                FileSystemBoundary.CreateManagedDirectory(dataRoot, Path.GetDirectoryName(plan.Target)!);
                var hadTarget = Directory.Exists(plan.Target);
                if (hadTarget)
                {
                    Directory.Move(plan.Target, plan.Rollback);
                }
                checkpoint?.Invoke("directory-displaced-" + Path.GetFileName(plan.Rollback));
                try
                {
                    Directory.Move(plan.Incoming, plan.Target);
                    completed.Add(new CompletedDirectorySwap(plan.Target, plan.Rollback, hadTarget));
                    checkpoint?.Invoke("directory-restored-" + Path.GetFileName(plan.Rollback));
                }
                catch
                {
                    if (hadTarget && Directory.Exists(plan.Rollback) && !Directory.Exists(plan.Target))
                    {
                        Directory.Move(plan.Rollback, plan.Target);
                    }
                    throw;
                }
            }
        }
        catch
        {
            if (!RollbackDirectorySwaps(completed, staging))
            {
                throw RestoreProblem("添付ファイルの復元に失敗し、復元前状態への戻しも完了できませんでした。");
            }
            throw RestoreProblem("バックアップの添付ファイルまたは設定を配置できませんでした。");
        }
    }

    private static bool RollbackDirectorySwaps(IReadOnlyList<CompletedDirectorySwap> completed, string staging)
    {
        try
        {
            for (var index = completed.Count - 1; index >= 0; index--)
            {
                var item = completed[index];
                FileSystemBoundary.ValidatePath(item.Target, allowUnc: false);
                FileSystemBoundary.ValidatePath(item.Rollback, allowUnc: false);
                FileSystemBoundary.ValidatePath(staging, allowUnc: false);
                if (Directory.Exists(item.Target))
                {
                    Directory.Move(item.Target, Path.Combine(staging, $"failed-new-{index}"));
                }
                if (item.HadTarget && Directory.Exists(item.Rollback))
                {
                    Directory.Move(item.Rollback, item.Target);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ValidateBackupDestination(
        string destinationPath,
        string displayName,
        bool overwrite,
        long requiredBytes)
    {
        var destination = ValidateBackupPath(destinationPath);
        ValidateBackupName(displayName);
        var parent = Path.GetDirectoryName(destination)!;
        if (FindGitRootForBackup(parent) is not null)
        {
            throw new AppProblemException(new AppProblem(
                "BK-008",
                "ソースコードの管理フォルダ内にはバックアップを保存できません。",
                "ドキュメント、外付けドライブ、ネットワークドライブなど別の保存先を選択してください。"));
        }
        if (File.Exists(destination))
        {
            if (!string.IsNullOrEmpty(new FileInfo(destination).LinkTarget))
            {
                throw BackupLinkProblem();
            }
            if (!overwrite)
            {
                throw new AppProblemException(new AppProblem(
                    "BK-002",
                    "同じ名前のバックアップがすでに存在します。",
                    "上書きする場合は確認画面で承認するか、別の名前を指定してください。"));
            }
        }
        VerifyBackupDestinationWrite(parent);
        ValidateAvailableBackupCapacity(parent, requiredBytes);
        return destination;
    }

    private static string ValidateBackupSource(string sourcePath)
    {
        var source = ValidateBackupPath(sourcePath);
        if (!File.Exists(source))
        {
            throw BackupInvalidProblem("選択したバックアップファイルが見つかりません。");
        }
        if (!string.IsNullOrEmpty(new FileInfo(source).LinkTarget))
        {
            throw BackupLinkProblem();
        }
        return source;
    }

    private static string ValidateBackupPath(string path)
    {
        string fullPath;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException();
            }
            fullPath = FileSystemBoundary.ValidatePath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw BackupPathProblem();
        }
        if (!fullPath.EndsWith(".faqbackup", StringComparison.OrdinalIgnoreCase))
        {
            throw BackupPathProblem();
        }
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw BackupWriteProblem();
        }
        ValidateBackupName(Path.GetFileNameWithoutExtension(fullPath));
        return fullPath;
    }

    private static void ValidateBackupName(string name)
    {
        if (!IsValidBackupName(name))
        {
            throw BackupPathProblem("バックアップ名に使用できない文字または名前が含まれています。");
        }
    }

    private static bool IsValidBackupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        var trimmed = name.Trim();
        if (trimmed.EnumerateRunes().Count() > 120 || trimmed.EndsWith('.') || trimmed.EndsWith(' ') ||
            trimmed.Any(character => character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(character)))
        {
            return false;
        }
        return !new[]
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        }.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateBackupArchivePath(string path, bool allowManifest = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') || path.Contains(':'))
        {
            throw BackupInvalidProblem("バックアップに安全でないファイルパスが含まれています。");
        }
        var parts = path.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw BackupInvalidProblem("バックアップに安全でないファイルパスが含まれています。");
        }
        if (allowManifest && string.Equals(path, "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var allowed = string.Equals(path, "data/knowledge.db", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("attachments/articles/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("manuals/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("settings/", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
        {
            throw BackupInvalidProblem("バックアップに管理対象外のファイルパスが含まれています。");
        }
    }

    private static long EstimateBackupSourceBytes(string dataRoot)
    {
        FileSystemBoundary.ValidateManagedDataRoot(dataRoot);
        FileSystemBoundary.ValidateManagedPath(dataRoot, Path.Combine(dataRoot, "data", "knowledge.db"));
        long total = File.Exists(Path.Combine(dataRoot, "data", "knowledge.db"))
            ? new FileInfo(Path.Combine(dataRoot, "data", "knowledge.db")).Length
            : 0;
        foreach (var relative in new[] { Path.Combine("attachments", "articles"), "manuals", "settings" })
        {
            foreach (var file in CollectSafeFilePaths(Path.Combine(dataRoot, relative)))
            {
                total = checked(total + new FileInfo(file).Length);
            }
        }
        return total;
    }

    private static IReadOnlyList<string> CollectSafeFilePaths(string root)
    {
        try { FileSystemBoundary.ValidatePath(root, allowUnc: false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { throw BackupLinkProblem(); }
        if (!Directory.Exists(root))
        {
            return [];
        }
        var files = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        try
        {
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                FileSystemBoundary.ValidatePath(current.FullName, allowUnc: false);
                foreach (var entry in current.EnumerateFileSystemInfos())
                {
                    FileSystemBoundary.ValidatePath(entry.FullName, allowUnc: false);
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || !string.IsNullOrEmpty(entry.LinkTarget))
                    {
                        throw BackupLinkProblem();
                    }
                    if (entry is DirectoryInfo directory)
                    {
                        pending.Push(directory);
                    }
                    else if (entry is FileInfo file)
                    {
                        files.Add(file.FullName);
                    }
                }
            }
        }
        catch (AppProblemException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw BackupReadProblem("バックアップ対象のデータを読み取れませんでした。");
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    private static long RequiredArchiveCapacity(long estimatedBytes) => checked(
        estimatedBytes + Math.Max(estimatedBytes / 20, MinimumBackupCapacityMargin));

    private static void ValidateAvailableBackupCapacity(string path, long requiredBytes)
    {
        FileSystemBoundary.ValidatePath(path);
        Directory.CreateDirectory(path);
        FileSystemBoundary.ValidatePath(path);
        if (!GetDiskFreeSpaceEx(path, out var available, out _, out _))
        {
            throw new AppProblemException(new AppProblem(
                "BK-003",
                "バックアップ先の空き容量を確認できませんでした。",
                "ネットワーク接続と保存先の権限を確認するか、別の保存先を選択してください。"));
        }
        if (available < (ulong)requiredBytes)
        {
            var requiredMb = (requiredBytes + 1024 * 1024 - 1) / (1024 * 1024);
            var availableMb = available / (1024 * 1024);
            throw new AppProblemException(new AppProblem(
                "BK-010",
                $"バックアップ先の空き容量が不足しています（必要約{requiredMb} MB、空き約{availableMb} MB）。",
                "不要なファイルを整理するか、十分な空き容量がある別の保存先を選択してください。"));
        }
    }

    private static void VerifyBackupDestinationWrite(string parent)
    {
        FileSystemBoundary.ValidatePath(parent);
        var probe = Path.Combine(parent, $".knowledgeapp-backup-write-test-{Guid.CreateVersion7():D}.tmp");
        try
        {
            FileSystemBoundary.ValidatePath(probe);
            using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write("knowledge-app-backup-write-test"u8);
                file.Flush(flushToDisk: true);
            }
            File.Delete(probe);
        }
        catch
        {
            TryDeleteFile(probe);
            throw BackupWriteProblem();
        }
    }

    private static void PublishBackupArchive(
        string dataRoot,
        string localArchive,
        string destination,
        bool overwrite)
    {
        FileSystemBoundary.ValidatePath(localArchive, allowUnc: false);
        FileSystemBoundary.ValidatePath(destination);
        var partial = $"{destination}.{Guid.CreateVersion7():D}.partial";
        var replaced = $"{destination}.{Guid.CreateVersion7():D}.replace";
        var hadExisting = File.Exists(destination);
        try
        {
            FileSystemBoundary.ValidatePath(partial);
            FileSystemBoundary.ValidatePath(replaced);
            File.Copy(localArchive, partial, overwrite: false);
            using (var copied = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                copied.Flush(flushToDisk: true);
            }
            if (new FileInfo(localArchive).Length != new FileInfo(partial).Length)
            {
                throw BackupWriteProblem();
            }
            if (hadExisting)
            {
                if (!overwrite)
                {
                    throw new AppProblemException(new AppProblem(
                        "BK-002",
                        "同じ名前のバックアップがすでに存在します。",
                        "上書きする場合は確認画面で承認するか、別の名前を指定してください。"));
                }
                File.Move(destination, replaced);
            }
            FileSystemBoundary.ValidatePath(destination);
            FileSystemBoundary.ValidatePath(partial);
            File.Move(partial, destination);
            try
            {
                ReadVerifiedBackupArchive(destination);
            }
            catch
            {
                TryDeleteFile(destination);
                if (hadExisting && File.Exists(replaced))
                {
                    File.Move(replaced, destination);
                }
                throw;
            }
            TryDeleteFile(replaced);
        }
        catch (AppProblemException)
        {
            TryDeleteFile(partial);
            if (hadExisting && File.Exists(replaced) && !File.Exists(destination))
            {
                File.Move(replaced, destination);
            }
            throw;
        }
        catch
        {
            TryDeleteFile(partial);
            if (hadExisting && File.Exists(replaced) && !File.Exists(destination))
            {
                File.Move(replaced, destination);
            }
            throw BackupWriteProblem();
        }
    }

    private static string? ReadLastSuccessfulBackupDirectory(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "settings", "backup.json");
        try
        {
            FileSystemBoundary.ValidateManagedPath(dataRoot, path);
            if (!File.Exists(path))
            {
                return null;
            }
            var settings = JsonSerializer.Deserialize<BackupDirectorySettings>(
                FileSystemBoundary.ReadBoundedFile(path, 64 * 1024),
                StrictBackupJsonOptions);
            return settings is not null && Path.IsPathFullyQualified(settings.LastSuccessfulDirectory) &&
                !string.IsNullOrWhiteSpace(settings.LastSuccessfulDirectory)
                ? FileSystemBoundary.ValidatePathSyntax(settings.LastSuccessfulDirectory)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RememberSuccessfulBackupDirectory(string dataRoot, string destination)
    {
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }
        var settingsRoot = Path.Combine(dataRoot, "settings");
        FileSystemBoundary.CreateManagedDirectory(dataRoot, settingsRoot);
        var finalPath = Path.Combine(settingsRoot, "backup.json");
        var temporaryPath = Path.Combine(settingsRoot, $"backup-{Guid.CreateVersion7():D}.partial");
        try
        {
            FileSystemBoundary.ValidateManagedPath(dataRoot, finalPath);
            FileSystemBoundary.ValidateManagedPath(dataRoot, temporaryPath);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new BackupDirectorySettings(parent), StrictBackupJsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private string ValidateFullBackupDataRoot(string dataRoot)
    {
        string resolved;
        try { resolved = FileSystemBoundary.ValidateManagedDataRoot(dataRoot); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { throw BackupLinkProblem(); }
        var databaseDirectory = Path.GetDirectoryName(OpenInfo.DatabasePath)
            ?? throw BackupReadProblem("FAQデータの保存先を確認できませんでした。");
        var expected = Directory.GetParent(databaseDirectory)?.FullName
            ?? throw BackupReadProblem("FAQデータの保存先を確認できませんでした。");
        if (!string.Equals(resolved, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
        {
            throw BackupReadProblem("FAQデータの保存先が現在のデータベースと一致しません。");
        }
        return resolved;
    }

    private static string? FindGitRootForBackup(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => char.IsAsciiHexDigit(character) && !char.IsUpper(character));

    private static BackupCounts ToBackupCounts(TransferBackupCounts counts) => new(
        counts.Articles,
        counts.Categories,
        counts.Attachments,
        counts.Manuals);

    private static void TryDeleteGeneratedDirectory(string path, string allowedParent, string prefix)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        var resolvedParent = Path.GetFullPath(allowedParent);
        var resolved = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(resolvedParent, resolved);
        if (!Path.IsPathFullyQualified(relative) && !relative.StartsWith("..", StringComparison.Ordinal) &&
            !relative.Contains(Path.DirectorySeparatorChar) && Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal))
        {
            try
            {
                FileSystemBoundary.DeleteManagedDirectory(resolvedParent, resolved);
            }
            catch (IOException)
            {
                // 合成データルート内の一時ファイルだけなので次回整理に任せる。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(FileSystemBoundary.ValidatePath(path));
            }
        }
        catch (IOException)
        {
            // 生成途中ファイルは次回の一時ファイル整理に任せる。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private static AppProblemException BackupPathProblem(string? message = null) => new(new AppProblem(
        "BK-001",
        message ?? "バックアップの保存場所またはファイル名が正しくありません。",
        "絶対パスを使用し、120文字以内でファイル名の末尾を.faqbackupにしてください。"));

    private static AppProblemException BackupWriteProblem() => new(new AppProblem(
        "BK-003",
        "指定したバックアップ先へ書き込めませんでした。",
        "ネットワーク接続と保存先の権限・空き容量を確認するか、別の保存先を選択してください。"));

    private static AppProblemException BackupReadProblem(string message) => new(new AppProblem(
        "BK-004",
        message,
        "FAQを閉じ、データ保存先へアクセスできることを確認してから再実行してください。"));

    private static AppProblemException BackupLinkProblem() => new(new AppProblem(
        "BK-005",
        "バックアップ対象に安全に処理できないリンクが含まれています。",
        "対象フォルダ内のショートカットやシンボリックリンクを取り除いてください。"));

    private static AppProblemException BackupInvalidProblem(string message) => new(new AppProblem(
        "BK-006",
        message,
        "破損していない別の.faqbackupファイルを選択してください。"));

    private static AppProblemException RestoreProblem(string message) => new(new AppProblem(
        "BK-010",
        message,
        "現在のFAQデータは維持されています。保存先の空き容量を確認し、もう一度お試しください。"));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    private sealed record DirectorySwapPlan(string Incoming, string Target, string Rollback);

    private sealed record CompletedDirectorySwap(string Target, string Rollback, bool HadTarget);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record BackupDirectorySettings(string LastSuccessfulDirectory);
}
