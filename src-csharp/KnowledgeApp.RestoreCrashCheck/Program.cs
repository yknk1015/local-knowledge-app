using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeApp.Data;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = new UTF8Encoding(false);
try { return await CrashCheck.Run(args); }
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }

internal static class CrashCheck
{
    private static readonly List<string> Roots = [];
    private static int _passed;
    private const string OwnedMarker = ".restore-crash-check-owned";
    private const string Incoming = "Synthetic_incoming.faqbackup";

    internal static async Task<int> Run(string[] args)
    {
        if (args.Length != 0) return Child(args);
        try
        {
            var original = LegacyFixture.Create(NewRoot(), 7);
            var incoming = LegacyFixture.Create(NewRoot(), 7,
                mutation: "UPDATE articles SET title='中断試験の別の合成FAQ' WHERE id='20000000-0000-4000-8000-000000000001'");
            var originalHash = Hash(original.Archive); var incomingHash = Hash(incoming.Archive);
            foreach (var step in new[] { "journal-written", "database-restored", "directory-displaced-attachments",
                         "directory-restored-attachments", "directory-displaced-manuals", "directory-restored-manuals",
                         "directory-displaced-settings", "directory-restored-settings", "restore-verified" })
            {
                var state = Prepare(original, incoming);
                await KillAt(state, "restore", step);
                Check(File.Exists(Journal(state.Root)), step + " / durable intent survives actual process termination");
                using (var recovered = KnowledgeDatabase.OpenRehearsalForTest(state.Root))
                    Check(recovered.RecoveredInterruptedRestore, step + " / startup exposes successful rollback notice");
                VerifyRecovered(state, step);
            }
            foreach (var step in new[] { "recovery-verified", "recovery-database-displaced", "recovery-database-restored",
                         "recovery-directory-displaced-attachments", "recovery-directory-restored-attachments",
                         "recovery-directory-displaced-manuals", "recovery-directory-restored-manuals",
                         "recovery-directory-displaced-settings", "recovery-directory-restored-settings", "recovery-complete" })
            {
                var state = Prepare(original, incoming);
                await KillAt(state, "restore", "directory-displaced-attachments");
                await KillAt(state, "recover", step);
                Check(File.Exists(Journal(state.Root)), step + " / intent retained through interrupted recovery");
                using (var recovered = KnowledgeDatabase.OpenRehearsalForTest(state.Root))
                    Check(recovered.RecoveredInterruptedRestore, step + " / startup exposes successful rollback notice");
                VerifyRecovered(state, step);
            }
            foreach (var step in new[] { "database-restored", "directory-displaced-attachments", "directory-restored-manuals" })
            {
                var state = Prepare(original, incoming);
                using (var database = KnowledgeDatabase.OpenRehearsalForTest(state.Root))
                {
                    var fired = false;
                    database.RestoreCheckpointForTest = current =>
                    {
                        if (!fired && current == step) { fired = true; throw new IOException("Synthetic write failure."); }
                    };
                    ExpectProblem(() => database.RestoreFullBackup(state.Root, Path.Combine(state.Root, Incoming)), "BK-010");
                    Check(fired, step + " / in-process fault actually injected");
                    Check(database.GetFullBackupOverview(state.Root).Counts == new BackupCounts(2, 2, 1, 1),
                        step + " / recovered live DB remains usable");
                }
                VerifyRecovered(state, "runtime " + step);
            }
            await CheckInvalidJournal(original, incoming);
            await CheckProductionRecovery(original, incoming);
            await CheckSeparatedLegacyDuringCrash(original, incoming);
            var failure = Prepare(original, incoming);
            using (var database = KnowledgeDatabase.OpenRehearsalForTest(failure.Root))
            {
                database.RestoreCheckpointForTest = step =>
                {
                    if (step == "database-restored")
                    {
                        var safety = SafetyPath(failure.Root);
                        File.WriteAllText(safety, "synthetic corrupt safety", new UTF8Encoding(false));
                        throw new IOException("Synthetic failure plus damaged safety.");
                    }
                };
                ExpectProblem(() => database.RestoreFullBackup(failure.Root, Path.Combine(failure.Root, Incoming)), "BK-012");
                ExpectProblem(() => database.GetFullBackupOverview(failure.Root), "BK-012");
                Check(File.Exists(Journal(failure.Root)), "failed runtime rollback preserves intent and blocks all following DB requests");
            }
            ExpectProblem(() => { using var blocked = KnowledgeDatabase.OpenRehearsalForTest(failure.Root); }, "BK-012");
            Check(Hash(original.Archive) == originalHash && Hash(incoming.Archive) == incomingHash,
                "both original source archives remain byte-for-byte unchanged throughout all process failures");
            Console.WriteLine($"RestoreCrashCheck: {_passed} passed; only explicitly owned synthetic child processes terminated; production data opened=false.");
            return 0;
        }
        finally
        {
            KnowledgeDatabase.RestoreRecoveryCheckpointForTest = null;
            foreach (var root in Roots)
            {
                var validated = FileSystemBoundary.ValidateSyntheticRoot(root);
                if (Directory.Exists(validated)) FileSystemBoundary.DeleteSyntheticRoot(validated);
            }
        }
    }

    private static State Prepare(LegacyFixture original, LegacyFixture incoming)
    {
        var root = NewRoot(); var nonce = Guid.NewGuid().ToString("D");
        using (var database = KnowledgeDatabase.OpenRehearsalForTest(root))
            database.RestoreFullBackup(root, original.Archive);
        File.WriteAllText(Path.Combine(root, OwnedMarker), nonce, new UTF8Encoding(false));
        File.Copy(incoming.Archive, Path.Combine(root, Incoming));
        Directory.CreateDirectory(Path.Combine(root, "codex-inbox"));
        File.WriteAllText(Path.Combine(root, "codex-inbox", "synthetic-unselected.txt"), "never touch unrelated synthetic bridge files", new UTF8Encoding(false));
        using var connection = LegacyFixture.Open(Path.Combine(root, "data", "knowledge.db"));
        var before = LegacyFixture.Snapshot(connection);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in new[] { "attachments", "manuals", "settings", "codex-inbox" })
            foreach (var file in Directory.GetFiles(Path.Combine(root, relative), "*", SearchOption.AllDirectories))
                hashes.Add(Path.GetRelativePath(root, file), Hash(file));
        return new(root, nonce, before, hashes);
    }

    private static async Task KillAt(State state, string mode, string step)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(typeof(CrashCheck).Assembly.Location);
        foreach (var argument in new[] { "--owned-child", state.Root, state.Nonce, mode, step }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new Exception("Could not start synthetic child.");
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (line != "CHECKPOINT " + step)
                throw new Exception($"Synthetic checkpoint not reached: {line}; {await error.WaitAsync(TimeSpan.FromSeconds(5))}");
            // This exact Process object was created immediately above and has
            // authenticated its private, freshly-created root/nonce before waiting.
            child.Kill(entireProcessTree: false);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(child.ExitCode != 0, mode + " " + step + " / owned process killed without finally/Dispose");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: false); await child.WaitForExitAsync(); }
        }
    }

    private static int Child(string[] args)
    {
        if (args.Length != 5 || args[0] != "--owned-child" || args[3] is not ("restore" or "recover" or "production-restore" or "production-recover")) return 2;
        var root = FileSystemBoundary.ValidateSyntheticRoot(args[1]);
        var marker = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, OwnedMarker));
        if (!Guid.TryParseExact(args[2], "D", out _) || File.ReadAllText(marker, Encoding.UTF8) != args[2]) return 3;
        void Checkpoint(string step)
        {
            if (step != args[4]) return;
            Console.WriteLine("CHECKPOINT " + step); Console.Out.Flush();
            Console.ReadLine();
            throw new Exception("Synthetic crash handshake was resumed instead of terminated.");
        }
        if (args[3] is "recover" or "production-recover") KnowledgeDatabase.RestoreRecoveryCheckpointForTest = Checkpoint;
        using var database = args[3].StartsWith("production-", StringComparison.Ordinal)
            ? KnowledgeDatabase.OpenProductionForTest(root) : KnowledgeDatabase.OpenRehearsalForTest(root);
        if (args[3] is "restore" or "production-restore")
        {
            database.RestoreCheckpointForTest = Checkpoint;
            database.RestoreFullBackup(root, Path.Combine(root, Incoming));
        }
        return 4;
    }

    private static async Task CheckProductionRecovery(LegacyFixture original, LegacyFixture incoming)
    {
        foreach (var step in new[] { "database-restored", "directory-displaced-attachments", "directory-restored-settings" })
        {
            var state = Prepare(original, incoming);
            await KillAt(state, "production-restore", step);
            using (var recovered = KnowledgeDatabase.OpenProductionForTest(state.Root))
            {
                Check(recovered.RecoveredInterruptedRestore && recovered.CandidateSafetyBackup is not null,
                    "production " + step + " / rollback notice and launch baseline are available");
                Check(recovered.GetFullBackupOverview(state.Root).Counts == new BackupCounts(2, 2, 1, 1),
                    "production " + step + " / recovered instance remains usable on the same exclusive connection");
                ExpectLocked(state.Root, "production " + step + " / another SQLite runtime cannot read/write while recovered instance lives");
            }
            VerifyRecovered(state, "production " + step);
        }
        foreach (var step in new[] { "production-recovery-exclusive", "recovery-database-restored",
                     "recovery-directory-displaced-attachments", "recovery-complete" })
        {
            var state = Prepare(original, incoming);
            await KillAt(state, "production-restore", "directory-displaced-attachments");
            await KillAt(state, "production-recover", step);
            using (var recovered = KnowledgeDatabase.OpenProductionForTest(state.Root))
                Check(recovered.RecoveredInterruptedRestore, "production repeated " + step + " / repeated process interruption recovers under exclusive connection");
            VerifyRecovered(state, "production repeated " + step);
        }
        var occupied = Prepare(original, incoming);
        await KillAt(occupied, "production-restore", "database-restored");
        var markerHash = Hash(Journal(occupied.Root));
        using (var competing = LegacyFixture.Open(Path.Combine(occupied.Root, "data", "knowledge.db"), readOnly: false))
        {
            LegacyFixture.Execute(competing, "BEGIN EXCLUSIVE");
            ExpectProblem(() => { using var rejected = KnowledgeDatabase.OpenProductionForTest(occupied.Root); }, "BK-012");
            Check(Hash(Journal(occupied.Root)) == markerHash, "production recovery rejects occupied SQLite without clearing recovery intent");
            LegacyFixture.Execute(competing, "ROLLBACK");
        }
        using (KnowledgeDatabase.OpenProductionForTest(occupied.Root)) { }
        VerifyRecovered(occupied, "production after competing connection exits");

        foreach (var invalid in new[] { "missing", "corrupt" })
        {
            var state = Prepare(original, incoming);
            await KillAt(state, "production-restore", "database-restored");
            var path = Path.Combine(state.Root, "data", "knowledge.db");
            // Preserve the generated interrupted DB for the test; never replace a
            // real DB and never let the production path bootstrap missing data.
            await MoveAfterOwnedProcessExit(path, path + ".synthetic-retained");
            if (invalid == "corrupt")
            {
                // A valid WAL may legitimately recover a damaged main page. Make
                // this case truly unreadable while retaining every generated file.
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                    if (File.Exists(path + suffix)) await MoveAfterOwnedProcessExit(path + suffix, path + suffix + ".synthetic-retained");
                File.WriteAllText(path, "invalid synthetic DB", new UTF8Encoding(false));
            }
            ExpectProblem(() => { using var blocked = KnowledgeDatabase.OpenProductionForTest(state.Root); }, "BK-012");
            Check(File.Exists(Journal(state.Root)) && (invalid == "missing" ? !File.Exists(path) : new FileInfo(path).Length == 20),
                "production " + invalid + " DB / refuses without reinitializing or clearing intent");
        }
    }

    private static async Task MoveAfterOwnedProcessExit(string source, string destination)
    {
        // Windows can briefly retain image/SQLite handles after TerminateProcess
        // signals exit. Retry only sharing violations on this generated test file;
        // there is no retry or process termination in the product recovery path.
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(source, destination); return; }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 && attempt < 20)
            { await Task.Delay(50); }
        }
    }

    private static async Task CheckSeparatedLegacyDuringCrash(LegacyFixture legacy, LegacyFixture incoming)
    {
        var before = Directory.GetFiles(legacy.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(legacy.Root, path), Hash, StringComparer.OrdinalIgnoreCase);
        foreach (var step in new[] { "database-restored", "directory-displaced-attachments", "restore-verified" })
        {
            // Prepare transfers only the explicitly supplied synthetic backup to a
            // different owned root. No real fixed path or undisclosed FAQ is read.
            var state = Prepare(legacy, incoming);
            using var oldRuntime = LegacyFixture.Open(legacy.DatabasePath);
            await KillAt(state, "production-restore", step);
            Check(File.Exists(Journal(state.Root)) && !File.Exists(Journal(legacy.Root)),
                "separate legacy " + step + " / restore intent exists only in the C# root");
            Check(LegacyFixture.Scalar(oldRuntime, "SELECT title FROM articles WHERE id='20000000-0000-4000-8000-000000000001'") == LegacyFixture.Title,
                "separate legacy " + step + " / old runtime can still read its unchanged FAQ while C# is interrupted");
            using (var recovered = KnowledgeDatabase.OpenProductionForTest(state.Root))
            {
                Check(recovered.RecoveredInterruptedRestore && recovered.StartupSafetyBackup is not null,
                    "separate legacy " + step + " / C# rolls back and protects its own store on restart");
                Check(LegacyFixture.Scalar(oldRuntime, "SELECT count(*) FROM articles WHERE deleted_at IS NULL") == "2",
                    "separate legacy " + step + " / recovered C# exclusive lease never locks the old runtime's DB");
            }
            VerifyRecovered(state, "separate legacy " + step);
            var after = Directory.GetFiles(legacy.Root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(legacy.Root, path), Hash, StringComparer.OrdinalIgnoreCase);
            Check(before.Count == after.Count && before.All(file => after.TryGetValue(file.Key, out var hash) && hash == file.Value),
                "separate legacy " + step + " / all old DB, backup and managed bytes remain exactly unchanged");
        }
    }

    private static void ExpectLocked(string root, string label)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "data", "knowledge.db"), Mode = SqliteOpenMode.ReadWrite,
                Pooling = false, DefaultTimeout = 1
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM articles";
            command.ExecuteScalar();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { Check(true, label); return; }
        throw new Exception("Exclusive database lease was lost: " + label);
    }

    private static async Task CheckInvalidJournal(LegacyFixture original, LegacyFixture incoming)
    {
        foreach (var mutation in new[] { "truncated", "oversized", "duplicate", "unknown", "path-traversal", "missing-safety", "changed-safety" })
        {
            var state = Prepare(original, incoming);
            await KillAt(state, "restore", "database-restored");
            var journal = Journal(state.Root); var safety = SafetyPath(state.Root);
            var text = File.ReadAllText(journal, Encoding.UTF8);
            switch (mutation)
            {
                case "truncated": text = "{"; break;
                case "oversized": text = new string(' ', 4097); break;
                case "duplicate": text = text.Replace("{", "{\"version\":1,", StringComparison.Ordinal); break;
                case "unknown": text = text.Replace("{", "{\"extra\":true,", StringComparison.Ordinal); break;
                case "path-traversal": text = text.Replace(Path.GetFileName(safety), "../Synthetic_incoming.faqbackup", StringComparison.Ordinal); break;
                case "missing-safety": File.Move(safety, safety + ".test-retained"); break;
                case "changed-safety": File.AppendAllText(safety, "synthetic mutation", new UTF8Encoding(false)); break;
            }
            File.WriteAllText(journal, text, new UTF8Encoding(false));
            var databasePath = Path.Combine(state.Root, "data", "knowledge.db");
            var beforeHash = Hash(databasePath);
            ExpectProblem(() => { using var blocked = KnowledgeDatabase.OpenRehearsalForTest(state.Root); }, "BK-012");
            Check(Hash(databasePath) == beforeHash && File.Exists(journal), mutation + " / fails closed before current DB mutation; journal retained");
        }
    }

    private static void VerifyRecovered(State state, string label)
    {
        using var connection = LegacyFixture.Open(Path.Combine(state.Root, "data", "knowledge.db"));
        var after = LegacyFixture.Snapshot(connection, state.Before);
        Check(state.Before.All(table => table.Value.Columns.SequenceEqual(after[table.Key].Columns) &&
            table.Value.Rows.SequenceEqual(after[table.Key].Rows)), label + " / every table, auth hash, setting, ID and history restored exactly");
        Check(state.Files.All(file => File.Exists(Path.Combine(state.Root, file.Key)) && Hash(Path.Combine(state.Root, file.Key)) == file.Value),
            label + " / image, settings, legacy files and unrelated bridge file retained exactly");
        Check(!File.Exists(Journal(state.Root)), label + " / only completed recovery clears intent");
        Check(Directory.GetFiles(Path.Combine(state.Root, "safety-backups"), "*.faqbackup").Length >= 2,
            label + " / verified pre-restore safety archives retained");
        connection.Dispose();
        using var reopened = KnowledgeDatabase.OpenRehearsalForTest(state.Root);
        Check(!reopened.RecoveredInterruptedRestore, label + " / completed rollback notice is not repeated on later normal startup");
        Check(reopened.GetFullBackupOverview(state.Root).Counts == new BackupCounts(2, 2, 1, 1), label + " / subsequent normal restart remains usable");
    }

    private static string SafetyPath(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Journal(root)));
        return Path.Combine(root, "safety-backups", document.RootElement.GetProperty("safetyFile").GetString()!);
    }
    private static string Journal(string root) => Path.Combine(root, KnowledgeDatabase.RestoreJournalName);
    private static string Hash(string path) { using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    private static string NewRoot() { var root = Path.Combine(Path.GetTempPath(), "knowledgeapp-data-check-" + Guid.NewGuid().ToString("D")); Roots.Add(root); return root; }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); _passed++; Console.WriteLine("PASS " + label); }
    private static void ExpectProblem(Action action, string code)
    {
        try { action(); }
        catch (AppProblemException exception) when (exception.Problem.Code == code) { Check(true, "expected " + code); return; }
        throw new Exception("Expected " + code);
    }
    private sealed record State(string Root, string Nonce, Dictionary<string, TableSnapshot> Before, Dictionary<string, string> Files);
}
