using Microsoft.Data.Sqlite;

namespace KnowledgeApp.Data;

public sealed partial class KnowledgeDatabase
{
    // No caller-supplied path is accepted. This root is permanently separate
    // from Tauri: existing data enters only through an explicitly selected backup.
    public static KnowledgeDatabase OpenShared()
    {
        var db = OpenProductionAt(SharedDataRoot.Validate(SharedDataRoot.FixedPath));
        db.SharedMode = true;
        db.SavePasswordPolicy(new(false));
        return db;
    }

    public static KnowledgeDatabase OpenProduction() =>
        OpenProductionAt(ProductionDataRoot.ValidateFixedPath(ProductionDataRoot.FixedPath));

    internal static KnowledgeDatabase OpenProductionForTest(string root) =>
        OpenProductionAt(FileSystemBoundary.ValidateSyntheticRoot(root));

    private static KnowledgeDatabase OpenProductionAt(string root)
    {
        KnowledgeDatabase? database = null;
        try
        {
            database = RecoverInterruptedProductionRestore(root);
            if (database is null)
            {
                var initializing = ProductionDataRoot.Prepare(root);
                database = OpenManaged(root, persistent: true, initializing, exclusive: true);
                if (initializing) ProductionDataRoot.CompleteInitialization(root);
            }
            // This is created before authentication updates or catalog refresh.
            // Every production launch retains its own baseline; no existing archive
            // is overwritten and the legacy runtime's separate store is not opened.
            var safetyDirectory = Path.Combine(root, "safety-backups");
            FileSystemBoundary.CreateManagedDirectory(root, safetyDirectory);
            var name = $"KnowledgeApp_before_csharp_{Guid.NewGuid():N}";
            database.StartupSafetyBackup = database.CreateFullBackupUnlocked(root,
                new(Path.Combine(safetyDirectory, name + ".faqbackup"), name, false), rememberDestination: false);
            return database;
        }
        catch
        {
            database?.Dispose();
            throw;
        }
    }

    public BackupResult? StartupSafetyBackup { get; private set; }

    // Kept for the existing synthetic migration check programs; application code
    // uses the production name above and never selects a candidate data root.
    internal BackupResult? CandidateSafetyBackup => StartupSafetyBackup;

    private static void AcquireExclusiveDatabase(SqliteConnection connection)
    {
        // SQLite locking excludes another connection to this C# store, including
        // another Windows session. It never locks or opens the separate Tauri DB.
        // Keep the one connection open for the whole app lifetime.
        // Do not put COMMIT after a potentially rejected BEGIN in one batch.
        // Provider reader disposal drains remaining batch statements, which
        // can restart a lock wait while unwinding the original failure.
        foreach (var sql in new[] { "PRAGMA locking_mode = EXCLUSIVE", "BEGIN EXCLUSIVE", "COMMIT" })
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
