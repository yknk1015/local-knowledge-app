using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeApp.Data;
using Microsoft.Data.Sqlite;

internal sealed record TableSnapshot(string[] Columns, string[] Rows);

// Every value here is explicitly synthetic. Never copy a user's DB or source archive.
internal sealed class LegacyFixture
{
    internal const string CategoryId = "10000000-0000-4000-8000-000000000001";
    internal const string ChildId = "10000000-0000-4000-8000-000000000002";
    internal const string ArticleId = "20000000-0000-4000-8000-000000000001";
    internal const string RelatedId = "20000000-0000-4000-8000-000000000002";
    internal const string DeletedId = "20000000-0000-4000-8000-000000000003";
    internal const string ImageId = "30000000-0000-4000-8000-000000000001";
    internal const string AdminId = "40000000-0000-4000-8000-000000000001";
    internal const string UserId = "40000000-0000-4000-8000-000000000002";
    internal const string Password = "Synthetic-Legacy\\*2026!";
    internal const string Title = "合成旧版：画像と履歴を保持して復元できますか？";
    internal const string Timestamp = "2026-01-02T03:04:05Z";
    internal const string ImagePath = "attachments/articles/" + ArticleId + "/" + ImageId + ".png";
    internal static readonly string[] SearchTerms = ["合成旧版", "合成症状", "合成原因", "合成対象", "旧版エラー", "合成タグ", "旧版検索語", "旧版手順", "旧版注意"];
    internal static readonly string Body = JsonSerializer.Serialize(new
    {
        type = "doc",
        content = new object[]
        {
            new { type = "paragraph", content = new[] { new { type = "text", text = "■確認方法\n旧版から移行した合成画像と本文を確認します。" } } },
            new { type = "image", attrs = new { attachmentId = ImageId, src = "knowledge-attachment:" + ImageId, alt = "合成旧版の青緑チェック画像" } }
        }
    });

    internal required int Version { get; init; }
    internal required string Root { get; init; }
    internal string DatabasePath => Path.Combine(Root, "data", "knowledge.db");
    internal required string Archive { get; init; }
    internal required Dictionary<string, TableSnapshot> Before { get; init; }
    internal required byte[] Image { get; init; }
    internal bool Bootstrap { get; init; }
    internal required string ExpectedBody { get; init; }
    internal string CaseLabel { get; init; } = "";
    internal string ExistingAdminId { get; init; } = AdminId;
    internal string AdminLogin { get; init; } = "legacy-admin";
    internal bool RepairAudits { get; init; }

    internal static LegacyFixture Create(string root, int version, bool emptyV6 = false, string? mutation = null, int? manifestVersion = null, string? bodyOverride = null, string? plainOverride = null, string caseLabel = "", bool initialAdminWithNullAudits = false)
    {
        FileSystemBoundary.ValidateSyntheticRoot(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Fixture root already exists.");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var databasePath = Path.Combine(root, "data", "knowledge.db");
        var image = CreatePng();
        var existingAdminId = initialAdminWithNullAudits ? KnowledgeDatabase.InitialAdminUserId : AdminId;
        var adminLogin = initialAdminWithNullAudits ? "0000" : "legacy-admin";
        Dictionary<string, TableSnapshot> before;
        using (var db = Open(databasePath, readOnly: false))
        {
            for (var migration = 1; migration <= version; migration++) Execute(db, MigrationCatalog.Load(migration));
            Execute(db, "PRAGMA foreign_keys=ON");
            Insert(db, "categories", new() { ["id"] = CategoryId, ["parent_id"] = null, ["name"] = "合成旧版分類", ["normalized_name"] = "合成旧版分類", ["depth"] = 1, ["sort_order"] = 0, ["created_at"] = Timestamp, ["updated_at"] = Timestamp });
            Insert(db, "categories", new() { ["id"] = ChildId, ["parent_id"] = CategoryId, ["name"] = "合成子分類", ["normalized_name"] = "合成子分類", ["depth"] = 2, ["sort_order"] = 3, ["created_at"] = Timestamp, ["updated_at"] = Timestamp });
            if (version >= 3) Execute(db, "UPDATE categories SET description=$value", ("$value", "旧版から保持する合成説明"));
            if (version >= 6 && !emptyV6)
            {
                foreach (var (id, login, role) in new[] { (existingAdminId, adminLogin, "admin"), (UserId, "legacy-user", version >= 9 ? "editor" : "user") })
                    Insert(db, "users", new() { ["id"] = id, ["login_id"] = login, ["normalized_login_id"] = login, ["display_name"] = "合成旧版" + role, ["password_hash"] = Argon2PasswordCodec.Hash(Password), ["role"] = role, ["is_active"] = 1, ["created_at"] = Timestamp, ["updated_at"] = Timestamp, ["last_login_at"] = Timestamp });
            }
            foreach (var id in new[] { ArticleId, RelatedId, DeletedId })
            {
                var main = id == ArticleId;
                var values = new Dictionary<string, object?>
                {
                    ["id"] = id, ["category_id"] = ChildId, ["title"] = main ? Title : id == DeletedId ? "合成削除済み旧版FAQ" : "合成旧版関連FAQ", ["normalized_title"] = main ? Title : "合成旧版関連faq",
                    ["summary"] = "本文・画像・設定・履歴を保持して復元します。", ["body_doc_json"] = main ? bodyOverride ?? Body : "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"合成関連本文\"}]}]}",
                    ["body_format_version"] = 1, ["body_plain_text"] = main ? plainOverride ?? "■確認方法\n旧版から移行した合成画像と本文を確認します。" : "合成関連本文",
                    ["procedure_text"] = main ? "旧版手順を確認します。" : "", ["caution_text"] = main ? "旧版注意を保持します。" : "", ["status"] = "published", ["importance"] = main ? 3 : 1,
                    ["created_at"] = Timestamp, ["updated_at"] = Timestamp, ["deleted_at"] = id == DeletedId ? Timestamp : null
                };
                if (version >= 2) { values["new_badge_until"] = "2099-12-31"; values["updated_badge_until"] = "2099-12-30"; values["is_hidden"] = id == DeletedId ? 1 : 0; }
                if (version >= 6) { values["created_by_user_id"] = emptyV6 || (initialAdminWithNullAudits && id != ArticleId) ? null : existingAdminId; values["updated_by_user_id"] = emptyV6 || (initialAdminWithNullAudits && id == RelatedId) ? null : UserId; }
                Insert(db, "articles", values);
            }
            foreach (var (table, value) in new[] { ("article_symptoms", "合成症状"), ("article_causes", "合成原因"), ("article_targets", "合成対象"), ("article_error_codes", "旧版エラー"), ("article_search_terms", "旧版検索語") })
                Insert(db, table, new() { ["id"] = Guid.NewGuid().ToString(), ["article_id"] = ArticleId, ["value"] = value, ["normalized_value"] = value, ["sort_order"] = 0 });
            Insert(db, "tags", new() { ["id"] = "synthetic-tag", ["name"] = "合成タグ", ["normalized_name"] = "合成タグ", ["created_at"] = Timestamp, ["updated_at"] = Timestamp });
            Insert(db, "article_tags", new() { ["article_id"] = ArticleId, ["tag_id"] = "synthetic-tag" });
            Insert(db, "article_relations", new() { ["source_article_id"] = ArticleId, ["target_article_id"] = RelatedId, ["sort_order"] = 2 });
            Insert(db, "article_attachments", new() { ["id"] = ImageId, ["article_id"] = ArticleId, ["relative_path"] = ArticleId + "/" + ImageId + ".png", ["original_name"] = "合成チェック.png", ["media_type"] = "image/png", ["byte_size"] = image.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(image)), ["alt_text"] = "合成旧版の青緑チェック画像", ["created_at"] = Timestamp });
            Insert(db, "manuals", new() { ["id"] = "synthetic-manual", ["title"] = "旧版互換の合成資料", ["manual_dir"] = "synthetic-manual", ["entry_path"] = "readme.txt", ["last_checked_at"] = Timestamp, ["check_status"] = "ok", ["created_at"] = Timestamp, ["updated_at"] = Timestamp });
            Insert(db, "article_manual_links", new() { ["article_id"] = ArticleId, ["manual_id"] = "synthetic-manual", ["display_name"] = "合成互換資料", ["sort_order"] = 0 });
            Insert(db, "synonym_groups", new() { ["id"] = "synthetic-synonym", ["display_name"] = "合成症状", ["created_at"] = Timestamp, ["updated_at"] = Timestamp });
            foreach (var term in new[] { "合成症状", "合成別称" }) Insert(db, "synonyms", new() { ["id"] = Guid.NewGuid().ToString(), ["group_id"] = "synthetic-synonym", ["term"] = term, ["normalized_term"] = term });
            Insert(db, "search_logs", new() { ["id"] = "synthetic-search", ["query_text"] = "合成旧版", ["normalized_query"] = "合成旧版", ["scope"] = "descendants", ["category_id"] = CategoryId, ["result_count"] = 1, ["created_at"] = Timestamp });
            Insert(db, "view_logs", new() { ["id"] = "synthetic-view", ["article_id"] = ArticleId, ["source_search_log_id"] = "synthetic-search", ["viewed_at"] = Timestamp });
            Insert(db, "app_settings", new() { ["key"] = "appearance", ["value_json"] = "{\"colorTheme\":\"blue\",\"showTopCategoryInTitle\":false,\"showMascot\":false}", ["updated_at"] = Timestamp });
            Insert(db, "app_settings", new() { ["key"] = "synthetic_legacy_setting", ["value_json"] = "{\"preserve\":true}", ["updated_at"] = Timestamp });
            if (version >= 6) Insert(db, "app_settings", new() { ["key"] = "password_policy", ["value_json"] = "{\"allowEmptyPasswords\":true}", ["updated_at"] = Timestamp });
            if (version >= 3) Insert(db, "codex_proposal_receipts", new() { ["request_id"] = "50000000-0000-4000-8000-000000000001", ["article_id"] = RelatedId, ["accepted_at"] = Timestamp });
            if (version >= 4)
            {
                using var proposalBody = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"合成関連本文\"}]}]}");
                var proposal = new CodexFaqProposal { FormatVersion = 1, RequestId = "50000000-0000-4000-8000-000000000001", SeriesId = "50000000-0000-4000-8000-000000000001", CreatedAt = Timestamp, Faq = new CodexFaqDraft { Title = "合成旧版関連FAQ", Summary = "合成履歴を保持します。", BodyDoc = proposalBody.RootElement }, ExistingCategoryCandidates = [new(ChildId, "合成旧版分類 / 合成子分類", "合成テスト専用")] };
                Insert(db, "codex_proposal_history", new() { ["request_id"] = proposal.RequestId, ["series_id"] = proposal.SeriesId, ["proposal_kind"] = "create", ["payload_json"] = JsonSerializer.Serialize(proposal, CodexJson.Options), ["status"] = "accepted", ["received_at"] = Timestamp, ["decided_at"] = Timestamp, ["accepted_article_id"] = RelatedId });
            }
            if (version >= 5) Insert(db, "article_merge_relations", new() { ["source_article_id"] = RelatedId, ["target_article_id"] = ArticleId, ["source_updated_at"] = Timestamp, ["merged_at"] = Timestamp });
            Execute(db, """
                INSERT INTO article_search_documents(article_id,title,summary,body,symptoms,causes,targets,error_codes,tags,search_terms)
                SELECT id,lower(normalized_title),summary,body_plain_text,
                  CASE WHEN id=$id THEN '合成症状' ELSE '' END,CASE WHEN id=$id THEN '合成原因' ELSE '' END,
                  CASE WHEN id=$id THEN '合成対象' ELSE '' END,CASE WHEN id=$id THEN '旧版エラー' ELSE '' END,
                  CASE WHEN id=$id THEN '合成タグ' ELSE '' END,CASE WHEN id=$id THEN '旧版検索語' ELSE '' END FROM articles;
                INSERT INTO article_search_fts(article_id,title,summary,body,symptoms,causes,targets,error_codes,tags,search_terms)
                SELECT d.article_id,d.title,d.summary,d.body || ' ' || a.procedure_text || ' ' || a.caution_text,d.symptoms,d.causes,d.targets,d.error_codes,d.tags,d.search_terms
                FROM article_search_documents d JOIN articles a ON a.id=d.article_id;
                """, ("$id", ArticleId));
            if (mutation is not null) Execute(db, mutation);
            before = Snapshot(db);
        }
        var files = new Dictionary<string, byte[]>
        {
            ["data/knowledge.db"] = File.ReadAllBytes(databasePath), [ImagePath] = image,
            ["manuals/synthetic-manual/readme.txt"] = Encoding.UTF8.GetBytes("旧版互換確認用の合成テキスト。自動表示・実行しません。"),
            ["settings/synthetic-legacy.json"] = Encoding.UTF8.GetBytes("{\"synthetic\":true}")
        };
        var archive = Path.Combine(root, $"Synthetic_legacy_v{version}{(emptyV6 ? "_uninitialized" : "")}.faqbackup");
        WriteArchive(archive, manifestVersion ?? version, files);
        return new() { Version = version, Root = root, Archive = archive, Before = before, Image = image, Bootstrap = version <= 5 || emptyV6, ExpectedBody = bodyOverride ?? Body, CaseLabel = caseLabel, ExistingAdminId = existingAdminId, AdminLogin = adminLogin, RepairAudits = initialAdminWithNullAudits && version == 6 };
    }

    internal static void WriteArchive(string path, int version, Dictionary<string, byte[]> files)
    {
        var manifest = new { backupFormatVersion = 1, appVersion = "0.4.4-synthetic-legacy", schemaVersion = version, richTextFormatVersion = 1, createdAt = Timestamp, displayName = Path.GetFileNameWithoutExtension(path), fileCount = files.Count, totalBytes = files.Values.Sum(bytes => (long)bytes.Length), counts = new { articles = 2, categories = 2, attachments = 1, manuals = 1 }, files = files.Select(entry => new { path = entry.Key, size = entry.Value.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(entry.Value)) }).ToArray() };
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) { using var output = archive.CreateEntry(file.Key, CompressionLevel.Optimal).Open(); output.Write(file.Value); }
        using var manifestOutput = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
        JsonSerializer.Serialize(manifestOutput, manifest);
    }

    internal static SqliteConnection Open(string path, bool readOnly = true)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        db.Open(); return db;
    }
    internal static void Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var item in parameters) command.Parameters.AddWithValue(item.Name, item.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    internal static string Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }
    private static void Insert(SqliteConnection db, string table, Dictionary<string, object?> values)
    {
        var columns = values.Keys.ToArray();
        Execute(db, $"INSERT INTO {Quote(table)}({string.Join(',', columns.Select(Quote))}) VALUES ({string.Join(',', columns.Select((_, i) => "$p" + i))})", values.Values.Select((value, i) => ("$p" + i, value)).ToArray());
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    internal static Dictionary<string, TableSnapshot> Snapshot(SqliteConnection db, Dictionary<string, TableSnapshot>? shape = null)
    {
        var tables = new List<string>();
        if (shape is null)
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> 'schema_migrations' AND (name NOT LIKE 'article_search_fts_%') ORDER BY name";
            using var reader = command.ExecuteReader(); while (reader.Read()) tables.Add(reader.GetString(0));
        }
        else tables.AddRange(shape.Keys);
        var result = new Dictionary<string, TableSnapshot>();
        foreach (var table in tables)
        {
            string[] columns;
            if (shape is not null) columns = shape[table].Columns;
            else
            {
                using var schema = db.CreateCommand(); schema.CommandText = "PRAGMA table_info(" + Quote(table) + ")";
                using var reader = schema.ExecuteReader(); var names = new List<string>(); while (reader.Read()) names.Add(reader.GetString(1)); columns = names.ToArray();
            }
            using var command = db.CreateCommand(); command.CommandText = "SELECT " + string.Join(',', columns.Select(Quote)) + " FROM " + Quote(table);
            using var rowsReader = command.ExecuteReader(); var rows = new List<string>();
            while (rowsReader.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, columns.Length).Select(i => rowsReader.IsDBNull(i) ? null : rowsReader.GetValue(i)).ToArray()));
            result[table] = new(columns, rows.Order(StringComparer.Ordinal).ToArray());
        }
        return result;
    }

    internal static byte[] CreatePng()
    {
        using var output = new MemoryStream(); output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, 96); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 64); header[8] = 8; header[9] = 2;
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < 64; y++) { zlib.WriteByte(0); for (var x = 0; x < 96; x++) zlib.Write((x / 16 + y / 16) % 2 == 0 ? new byte[] { 25, 103, 210 } : new byte[] { 23, 133, 94 }); }
        WriteChunk(output, "IDAT", compressed.ToArray()); WriteChunk(output, "IEND", []); return output.ToArray();
    }
    private static void WriteChunk(Stream output, string name, byte[] bytes)
    {
        var type = Encoding.ASCII.GetBytes(name); Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); output.Write(number); output.Write(type); output.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (var value in type.Concat(bytes)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
    }
}
