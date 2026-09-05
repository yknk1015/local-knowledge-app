using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    internal const string RestoreJournalName = "restore-pending.json";
    private bool _restoreRecoveryRequired;
    internal static Action<string>? RestoreRecoveryCheckpointForTest { get; set; }
    public bool RecoveredInterruptedRestore { get; private set; }

    private void ThrowIfRestoreRecoveryRequired()
    {
        if (_restoreRecoveryRequired) throw RestoreRecoveryProblem();
    }

    private static string RestoreJournalPath(string root) =>
        FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, RestoreJournalName));

    private static void BeginRestoreJournal(string root, string safetyPath)
    {
        var journal = RestoreJournalPath(root);
        if (File.Exists(journal) || Directory.Exists(journal)) throw RestoreRecoveryProblem();
        var safety = FileSystemBoundary.ValidateManagedPath(root, safetyPath);
        var record = new RestoreJournal(1, Path.GetFileName(safety), HashFile(safety));
        ValidateRestoreJournal(record);
        var temporary = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, $"restore-intent-{Guid.NewGuid():D}.partial"));
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, record, StrictBackupJsonOptions);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, journal, overwrite: false);
        }
        finally { TryDeleteFile(temporary); }
    }

    // Invoked only by the fixed/synthetic Open* entry points, before they inspect,
    // migrate, bootstrap or open the current DB. Recovery never follows a path from
    // the journal: it resolves a constrained leaf below this root's safety-backups.
    private static bool RecoverInterruptedRestore(string root)
    {
        try
        {
            var journal = RestoreJournalPath(root);
            if (!File.Exists(journal) && !Directory.Exists(journal)) return false;
            if (string.Equals(root, RehearsalDataRoot.FixedPath, StringComparison.OrdinalIgnoreCase))
            {
                RehearsalDataRoot.ValidateFixedPath(root);
                var marker = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, RehearsalDataRoot.InitializedMarker));
                if (!FileSystemBoundary.ReadBoundedFile(marker, 128).AsSpan().SequenceEqual(RehearsalDataRoot.MarkerContent) ||
                    File.Exists(Path.Combine(root, RehearsalDataRoot.InitializingMarker))) throw RestoreRecoveryProblem();
            }
            else FileSystemBoundary.ValidateSyntheticRoot(root);

            RecoverRestoreFromSafetyArchive(root, snapshot => ReplaceOfflineRestoreDatabase(root, snapshot),
                () => { }, RestoreRecoveryCheckpointForTest);
            return true;
        }
        catch (Exception)
        {
            throw RestoreRecoveryProblem();
        }
    }

    // Keep the same SQLite-exclusive connection alive from recovery through normal
    // production use. Never rename/delete/copy a live production DB or its sidecars.
    // A missing/corrupt DB is refused rather than recreated from an unowned path.
    private static KnowledgeDatabase? RecoverInterruptedProductionRestore(string root)
    {
        KnowledgeDatabase? recovered = null;
        SqliteConnection? connection = null;
        try
        {
            if (string.Equals(root, ProductionDataRoot.FixedPath, StringComparison.OrdinalIgnoreCase))
                ProductionDataRoot.ValidateFixedPath(root);
            else FileSystemBoundary.ValidateSyntheticRoot(root);
            var journal = RestoreJournalPath(root);
            if (!File.Exists(journal) && !Directory.Exists(journal)) return null;
            ProductionDataRoot.ValidateExistingLayout(root);
            RecoverRestoreFromSafetyArchive(root, snapshot =>
            {
                // The callback runs only after the safety archive's identity,
                // manifest, schema, users, body, references and settings pass.
                var databasePath = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "data", "knowledge.db"));
                ValidatePersistentDatabaseBeforeOpen(root, databasePath);
                connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite,
                    Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5
                }.ToString());
                connection.Open();
                ExecuteNonQuery(connection, "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;");
                AcquireExclusiveDatabase(connection);
                RequireCurrentRehearsalVersion(ValidateExisting(connection));
                ValidatePersistentUsers(connection);
                recovered = new KnowledgeDatabase(connection,
                    new SyntheticDatabaseOpenInfo(databasePath, MigrationCatalog.CurrentVersion,
                        MigrationCatalog.CurrentVersion, false, null), persistentRehearsal: true);
                RestoreRecoveryCheckpointForTest?.Invoke("production-recovery-exclusive");
                recovered.RestoreDatabaseFromSnapshot(snapshot);
            }, () => recovered!.FlushRestoredDatabase(), RestoreRecoveryCheckpointForTest);
            recovered!.RecoveredInterruptedRestore = true;
            return recovered;
        }
        catch
        {
            recovered?.Dispose();
            connection?.Dispose();
            throw RestoreRecoveryProblem();
        }
    }

    private static void RecoverRestoreFromSafetyArchive(string root, Action<string> restoreDatabase,
        Action flushDatabase, Action<string>? checkpoint)
    {
        var journal = ReadRestoreJournal(root);
        var safety = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "safety-backups", journal.SafetyFile));
        var stagingRoot = Path.Combine(root, "restore-staging");
        FileSystemBoundary.CreateManagedDirectory(root, stagingRoot);
        var staging = Path.Combine(stagingRoot, $"recovery-{Guid.NewGuid():D}");
        FileSystemBoundary.CreateManagedDirectory(root, staging);
        try
        {
            // Digest + full archive/schema/content validation all finish before any
            // current DB or managed folder changes. The verified archive is retained.
            var manifest = ExtractVerifiedBackupArchive(safety, staging, journal.SafetySha256, null);
            var snapshot = Path.Combine(staging, "data", "knowledge.db");
            PrepareBackupSnapshotForRestore(snapshot, manifest);
            foreach (var relative in new[] { Path.Combine("attachments", "articles"), "manuals", "settings" })
                FileSystemBoundary.CreateManagedDirectory(root, Path.Combine(staging, relative));
            checkpoint?.Invoke("recovery-verified");
            restoreDatabase(snapshot);
            checkpoint?.Invoke("recovery-database-restored");
            ReplaceManagedBackupDirectories(root, staging, step => checkpoint?.Invoke("recovery-" + step));
            VerifyRestoredManagedFiles(root, manifest);
            flushDatabase();
            checkpoint?.Invoke("recovery-complete");
            CompleteRestoreJournal(root);
        }
        finally { TryDeleteGeneratedDirectory(staging, stagingRoot, "recovery-"); }
    }

    private static RestoreJournal ReadRestoreJournal(string root)
    {
        var bytes = FileSystemBoundary.ReadBoundedFile(RestoreJournalPath(root), 4096);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw RestoreRecoveryProblem();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw RestoreRecoveryProblem();
        var record = JsonSerializer.Deserialize<RestoreJournal>(bytes, StrictBackupJsonOptions)
            ?? throw RestoreRecoveryProblem();
        ValidateRestoreJournal(record);
        return record;
    }

    private static void ValidateRestoreJournal(RestoreJournal record)
    {
        if (record.Version != 1 || record.SafetyFile is null || record.SafetyFile.Length > 150 ||
            !record.SafetyFile.StartsWith("KnowledgeApp_before_restore_", StringComparison.Ordinal) ||
            !record.SafetyFile.EndsWith(".faqbackup", StringComparison.Ordinal) ||
            record.SafetyFile.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')) ||
            !IsSha256(record.SafetySha256)) throw RestoreRecoveryProblem();
    }

    private static void ReplaceOfflineRestoreDatabase(string root, string snapshot)
    {
        var target = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "data", "knowledge.db"));
        FileSystemBoundary.CreateManagedDirectory(root, Path.GetDirectoryName(target)!);
        // No live connection exists. Move the interrupted DB and all sidecars into
        // this attempt's validated staging folder; a repeated recovery starts from
        // the same immutable archive even if killed between any of these moves.
        var displaced = Path.Combine(Path.GetDirectoryName(snapshot)!, "interrupted");
        FileSystemBoundary.CreateManagedDirectory(root, displaced);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal", "" })
        {
            var path = FileSystemBoundary.ValidateManagedPath(root, target + suffix);
            if (File.Exists(path)) File.Move(path, Path.Combine(displaced, "knowledge.db" + suffix), overwrite: false);
        }
        RestoreRecoveryCheckpointForTest?.Invoke("recovery-database-displaced");
        File.Copy(snapshot, target, overwrite: false);
        using (var file = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            file.Flush(flushToDisk: true);
        ValidateBackupSnapshot(target, MigrationCatalog.CurrentVersion);
    }

    private void FlushRestoredDatabase()
    {
        ExecuteNonQuery(_connection, "PRAGMA synchronous = FULL; PRAGMA wal_checkpoint(FULL);");
        QuickCheck(_connection);
    }

    private static void VerifyRestoredManagedFiles(string root, TransferBackupManifest manifest)
    {
        foreach (var entry in manifest.Files.Where(file => !string.Equals(file.Path, "data/knowledge.db", StringComparison.OrdinalIgnoreCase)))
        {
            var path = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (HashBoundedBackupEntry(input, entry.Size) != entry.Sha256) throw RestoreRecoveryProblem();
        }
    }

    private static void CompleteRestoreJournal(string root) => File.Delete(RestoreJournalPath(root));

    private static AppProblemException RestoreRecoveryProblem() => new(new AppProblem(
        "BK-012", "中断した復元の安全な復旧を完了できないため、FAQの利用を停止しました。",
        "アプリを閉じ、保存先の空き容量・権限を確認して再起動してください。安全バックアップとrestore-pending.jsonは削除せず、解決しない場合は管理者へ相談してください。"));

    private sealed record RestoreJournal(int Version, string SafetyFile, string SafetySha256);
}
