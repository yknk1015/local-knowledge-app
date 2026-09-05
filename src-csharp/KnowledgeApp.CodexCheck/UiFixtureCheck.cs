using System.Globalization;
using KnowledgeApp.Data;
using Microsoft.Data.Sqlite;

internal static class UiFixtureCheck
{
    private const string RootPrefix = "knowledgeapp-csharp-search-";
    private static readonly string[] SourceIds =
    [
        "72000000-0000-7000-8000-000000000001",
        "72000000-0000-7000-8000-000000000002",
        "72000000-0000-7000-8000-000000000003"
    ];

    internal static void Run()
    {
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.Combine(temporaryRoot, RootPrefix + Guid.NewGuid().ToString("D"));
        ValidateRoot(root, temporaryRoot);
        KnowledgeDatabase? database = null;
        try
        {
            database = KnowledgeDatabase.OpenSynthetic(root);
            Check(database.OpenInfo.Created, "UI fixture did not use a newly created synthetic database");
            database.SeedSyntheticCategorySearchFixture();
            var authentication = new AuthenticationService(database);
            Check(authentication.GetCurrentUser() is null, "UI fixture started with a logged-in session");
            var originalVersions = SourceVersions(root, database.OpenInfo.DatabasePath);
            Check(originalVersions.Count == 3, "UI fixture source FAQs were not seeded");
            Check(CountHistory(root, database.OpenInfo.DatabasePath) == 0, "UI fixture started with proposal history");

            // The same explicit hook is called by the prototype before login.
            // In particular, the revision fixture must not contain category choices.
            CodexSyntheticUiFixture.Seed(database);
            Check(authentication.GetCurrentUser() is null, "UI fixture changed the authentication session");
            Check(CountLogins(root, database.OpenInfo.DatabasePath) == 0, "UI fixture authenticated a user internally");
            Check(CountHistory(root, database.OpenInfo.DatabasePath) == 0, "UI fixture prematurely recorded proposal history");

            var files = new CodexProposalFiles(database);
            var proposalPaths = Directory.GetFileSystemEntries(files.InboxPath).Order(StringComparer.Ordinal).ToArray();
            var delegationPaths = Directory.GetFileSystemEntries(files.DelegationsPath).Order(StringComparer.Ordinal).ToArray();
            Check(proposalPaths.Length == 3 && proposalPaths.All(path =>
                    Path.GetFileName(path).EndsWith(CodexProposalFiles.ProposalSuffix, StringComparison.Ordinal)),
                "UI fixture must create exactly three proposal files without partial files");
            Check(delegationPaths.Length == 2 && delegationPaths.All(path =>
                    Path.GetFileName(path).EndsWith(CodexProposalFiles.DelegationSuffix, StringComparison.Ordinal)),
                "UI fixture must create exactly two delegation files without partial files");

            var inbox = files.ListProposals();
            Check(inbox.Rejected.Count == 0 && inbox.Proposals.Count == 3, "UI fixture generated a rejected proposal");
            Check(inbox.Proposals.Select(proposal => proposal.ProposalKind).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "create", "merge", "revise" }, StringComparer.Ordinal),
                "UI fixture must contain one create, one revise, and one merge proposal");
            Check(inbox.Proposals.Single(proposal => proposal.ProposalKind == "create").Faq.Title == "合成Codex新規案" &&
                  inbox.Proposals.Single(proposal => proposal.ProposalKind == "revise").Faq.Title == "合成Codex修正案" &&
                  inbox.Proposals.Single(proposal => proposal.ProposalKind == "merge").Faq.Title == "合成Codex統合案",
                "UI fixture titles do not identify the synthetic proposals");
            var revision = inbox.Proposals.Single(proposal => proposal.ProposalKind == "revise");
            Check(revision.ExistingCategoryCandidates.Count == 0 && revision.NewCategoryProposal is null,
                "Revision UI fixture incorrectly contains category choices");
            foreach (var proposal in inbox.Proposals)
            {
                CodexProposalFiles.ValidateProposal(proposal);
                files.ValidateDelegatedSources(proposal);
            }
            Check(CountHistory(root, database.OpenInfo.DatabasePath) == 0,
                "Reading UI fixture files should not receive proposals into DB history");

            CodexSyntheticUiFixture.Seed(database);
            Check(proposalPaths.SequenceEqual(
                    Directory.GetFileSystemEntries(files.InboxPath).Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
                  delegationPaths.SequenceEqual(
                    Directory.GetFileSystemEntries(files.DelegationsPath).Order(StringComparer.Ordinal), StringComparer.Ordinal),
                "Calling the UI fixture twice changed or added exchange files");
            var currentVersions = SourceVersions(root, database.OpenInfo.DatabasePath);
            Check(originalVersions.Count == currentVersions.Count &&
                  originalVersions.All(pair => currentVersions.TryGetValue(pair.Key, out var version) && version == pair.Value),
                "UI fixture changed a source FAQ version");
            Check(authentication.GetCurrentUser() is null && CountLogins(root, database.OpenInfo.DatabasePath) == 0,
                "Repeated UI fixture seeding authenticated a user");
            Check(CountHistory(root, database.OpenInfo.DatabasePath) == 0,
                "Repeated UI fixture seeding created proposal history");
        }
        finally
        {
            database?.Dispose();
            ValidateRoot(root, temporaryRoot);
            if (Directory.Exists(root))
            {
                EnsureNoReparseEntries(new DirectoryInfo(root));
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Dictionary<string, string> SourceVersions(string root, string databasePath)
    {
        using var connection = OpenReadOnly(root, databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, updated_at FROM articles WHERE id IN ($first, $second, $third)";
        command.Parameters.AddWithValue("$first", SourceIds[0]);
        command.Parameters.AddWithValue("$second", SourceIds[1]);
        command.Parameters.AddWithValue("$third", SourceIds[2]);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    private static long CountHistory(string root, string databasePath)
    {
        using var connection = OpenReadOnly(root, databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM codex_proposal_history";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long CountLogins(string root, string databasePath)
    {
        using var connection = OpenReadOnly(root, databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users WHERE last_login_at IS NOT NULL";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection OpenReadOnly(string root, string databasePath)
    {
        Check(string.Equals(Path.GetFullPath(databasePath), Path.Combine(root, "data", "knowledge.db"),
                StringComparison.OrdinalIgnoreCase), "UI fixture read escaped its synthetic database path");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void ValidateRoot(string root, string temporaryRoot)
    {
        var full = Path.GetFullPath(root);
        var name = Path.GetFileName(full);
        Check(string.Equals(Path.GetDirectoryName(full), temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
              name.StartsWith(RootPrefix, StringComparison.Ordinal) &&
              Guid.TryParseExact(name[RootPrefix.Length..], "D", out _),
            "UI fixture root is not a dedicated synthetic temporary directory");
    }

    private static void EnsureNoReparseEntries(DirectoryInfo directory)
    {
        Check((directory.Attributes & FileAttributes.ReparsePoint) == 0, "UI fixture cleanup refuses linked directories");
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            Check((entry.Attributes & FileAttributes.ReparsePoint) == 0, "UI fixture cleanup refuses linked entries");
            if (entry is DirectoryInfo child) EnsureNoReparseEntries(child);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
