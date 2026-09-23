using System.Diagnostics;
using System.Text.Json;
using KnowledgeApp.Data;
using KnowledgeApp.Shared;

var root = Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var external = Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}");
var checks = 0;
try
{
    CheckApplicationDirectories();
    var bootstrapRoot = Path.Combine(Path.GetTempPath(), $"knowledgeapp-data-check-{Guid.NewGuid():D}");
    try
    {
        Directory.CreateDirectory(bootstrapRoot); // installer first creates an empty ACL-protected root
        using var bootstrap = KnowledgeDatabase.OpenProductionForTest(bootstrapRoot);
        bootstrap.SharedMode = true;
        Check(!bootstrap.SharedAdministratorReady(), "shared bootstrap requires initial password");
        bootstrap.InitializeSharedAdministrator("Synthetic-bootstrap!");
        Check(bootstrap.SharedAdministratorReady(), "local console setup enables first shared administrator");
        Expect(() => bootstrap.InitializeSharedAdministrator("Never-overwrite!"));
        Directory.CreateDirectory(Path.Combine(bootstrapRoot, "device-settings")); // server.json is written only after initialization
    }
    finally { FileSystemBoundary.DeleteSyntheticRoot(bootstrapRoot); }
    using var db = KnowledgeDatabase.OpenSynthetic(root);
    Check(CodexLocationService.ResolveExchangeRoot(root) == root, "unconfigured default exchange remains readable outside application directories");
    var auth = new AuthenticationService(db);
    auth.Login("0000", "");
    auth.ResetUserPassword(KnowledgeDatabase.InitialAdminUserId, "Synthetic-admin!");
    auth.Login("0000", "Synthetic-admin!");
    var editor = auth.CreateUser("editor", "合成編集者", "Synthetic-editor!", UserRoles.Editor);
    var viewer = auth.CreateUser("viewer", "合成閲覧者", "Synthetic-viewer!", UserRoles.Viewer);
    var category = new ClassificationSearchService(db, auth).CreateCategory("共有合成分類", "", null);
    var editing = new ArticleEditingService(db, auth);
    var published = editing.SaveArticle(Input(null, category.Id, "共有公開FAQ", "published"));
    var draft = editing.SaveArticle(Input(null, category.Id, "非公開FAQ", "draft"));
    db.SharedMode = true;
    db.SavePasswordPolicy(new(false));
    var serverId = Guid.NewGuid().ToString("D");
    var sessions = new ServerSessions(db, root, serverId);
    var a = sessions.Create(); var e = sessions.Create(); var v = sessions.Create();
    await Ok(a, "login", new { input = new { loginId = "0000", password = "Synthetic-admin!" } });
    await Ok(e, "login", new { input = new { loginId = "editor", password = "Synthetic-editor!" } });
    await Ok(v, "login", new { input = new { loginId = "viewer", password = "Synthetic-viewer!" } });
    await Ok(v, "get_article", new { id = published.Id });
    await Deny(v, "get_article", new { id = draft.Id });
    foreach (var command in new[] { "save_article", "delete_article", "restore_article", "save_tag", "create_category", "create_codex_delegation", "list_codex_proposals", "get_storage_folders", "create_full_backup" })
        await Deny(v, command, new { id = published.Id, input = Input(published.Id, category.Id, "拒否される変更", "published") });
    await Deny(e, "delete_article", new { id = published.Id });
    await Deny(e, "create_category", new { input = new { name = "禁止", description = "", parentId = (string?)null } });
    await Deny(e, "save_storage_folder", new { input = new { purpose = "backup-export", path = external } });
    var update = Input(published.Id, category.Id, "先に保存", "published") with { ExpectedRevision = published.Revision };
    await Ok(e, "save_article", new { input = update });
    await Deny(a, "save_article", new { input = update with { Title = "古い内容" } }, "ART-010");
    Check(db.GetArticle(published.Id).Title == "先に保存", "stale write cannot overwrite");
    var idempotency = Guid.NewGuid().ToString("D");
    var request = Request("save_article", new { input = Input(null, category.Id, "再送合成FAQ", "published") }, idempotency);
    var first = await sessions.Execute(e, request); var second = await sessions.Execute(e, request);
    Check(first.Ok && ReferenceEquals(first, second), "same request reuses completed response");
    await Deny(e, "save_article", new { input = Input(null, category.Id, "別内容", "published") }, id: idempotency);
    var ownLog = (string)(await Ok(v, "record_search_log", new { input = new { query = "閲覧者だけの検索", categoryId = (string?)null, scope = "all", resultCount = 1 } }))!;
    await Deny(e, "record_article_view", new { input = new { articleId = published.Id, sourceSearchLogId = ownLog } });
    await Ok(v, "record_article_view", new { input = new { articleId = published.Id, sourceSearchLogId = ownLog } });
    var history = (SearchLogPage)(await Ok(e, "list_search_logs", new { input = new { query = "", page = 1, zeroResultsOnly = false } }))!;
    Check(history.Items.All(item => item.Id != ownLog), "history belongs to its user");
    await Ok(a, "set_user_role", new { input = new { id = editor.Id, role = "viewer" } });
    await Deny(e, "save_article", new { input = update }, "AUTH-002");
    await Deny(a, "set_user_role", new { input = new { id = KnowledgeDatabase.InitialAdminUserId, role = "viewer" } }, "USR-002");
    Directory.CreateDirectory(external);
    var deviceOnly = new StorageSettingsService(external, () => { });
    Check(deviceOnly.GetFolders().Count == 6 && !Directory.Exists(Path.Combine(external, "data")), "shared device settings do not create or require a local DB");
    var folders = new StorageSettingsService(root, auth);
    var path = Path.Combine(external, "backups"); Directory.CreateDirectory(path);
    folders.Save(new("backup-export", path));
    Check(new StorageSettingsService(root, auth).ResolveForDialog("backup-export") == path, "path survives service reconstruction");
    Directory.CreateDirectory(Path.Combine(path, ".git"));
    Expect(() => folders.ResolveForDialog("backup-export"));
    Directory.Delete(Path.Combine(path, ".git"));
    folders.Save(new("backup-export", null));
    Check(folders.ResolveForDialog("backup-export", path) != path, "explicit reset ignores old last-used path");
    Expect(() => folders.Save(new("csv-export", Path.Combine(root, "data"))));
    auth.SetUserRole(editor.Id, UserRoles.Editor);
    await Ok(e, "login", new { input = new { loginId = "editor", password = "Synthetic-editor!" } });
    var profile = await Ok(e, "shared_codex_exchange", new { });
    Check(profile is not null, "private exchange profile exists");
    var delegation = (CodexDelegationResult)(await Ok(e, "create_codex_delegation", new { input = new { kind = "revise", articleIds = new[] { published.Id } } }))!;
    Check(sessions.ReadDelegation(e, delegation.DelegationId).Length > 0, "owner can download explicit delegation");
    Expect(() => sessions.ReadDelegation(a, delegation.DelegationId));
    await Deny(v, "shared_codex_exchange", new { });
    // Full archive transfer uses session capabilities and keeps verified local backups after logout.
    var exported = (JsonElement)(await Ok(a, "create_full_backup", new { input = new { destinationPath = "ignored-by-server", displayName = "共有合成バックアップ", overwrite = false } }))!;
    var fileId = exported.GetProperty("destinationPath").GetString()![12..];
    byte[] archiveBytes;
    using (var stream = sessions.Download(a, fileId))
    { using var memory = new MemoryStream(); stream.CopyTo(memory); archiveBytes = memory.ToArray(); }
    Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(archiveBytes)) == exported.GetProperty("transferSha256").GetString(), "download checksum matches retained archive");
    Expect(() => sessions.Download(e, fileId));
    var otherAdmin = auth.CreateUser("other-admin", "別の合成管理者", "Synthetic-other!", UserRoles.Admin);
    var other = sessions.Create(); await Ok(other, "login", new { input = new { loginId = "other-admin", password = "Synthetic-other!" } });
    Expect(() => sessions.Download(other, fileId));
    var uploaded = await sessions.Upload(a, "backup", new MemoryStream(archiveBytes), archiveBytes.Length, CancellationToken.None);
    await Deny(other, "inspect_backup", new { path = "shared-file:" + uploaded });
    var previewJson = JsonSerializer.SerializeToElement(await Ok(a, "inspect_backup", new { path = "shared-file:" + uploaded }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    await Ok(a, "restore_backup", new { input = new { path = "shared-file:" + uploaded, confirmationToken = previewJson.GetProperty("confirmationToken").GetString() } });
    await Deny(v, "get_article", new { id = published.Id }, "AUTH-002");
    auth.Login("0000", "Synthetic-admin!");
    await HttpsCheck.Run(sessions, serverId, published.Id, draft.Id);
    var locationService = new CodexLocationService(db, auth);
    var location = locationService.Change(new(external, true));
    Check(location.Generation == 1 && Directory.Exists(location.Root), "custom Codex exchange created");
    ExchangePermissions.Validate(location.Root);
    Check(locationService.Get() == location, "owned exchange persists");
    var oldLocation = location.Root;
    var standardLocation = locationService.Change(new(null, true));
    Check(standardLocation.Generation == 2 && Directory.Exists(oldLocation), "Codex reset increments generation and retains originals");
    Check(locationService.Get() == standardLocation, "configured standard exchange remains readable after reset");
    var clients = new List<string>();
    for (var i = 0; i < 50; i++)
    {
        auth.CreateUser($"load-{i}", $"合成利用者{i}", "Synthetic-load!", UserRoles.Viewer);
        clients.Add(sessions.Create());
    }
    var timer = Stopwatch.StartNew();
    await Task.WhenAll(clients.Select((token, i) => Ok(token, "login", new { input = new { loginId = $"load-{i}", password = "Synthetic-load!" } })));
    await Task.WhenAll(clients.Select(token => Ok(token, "get_article", new { id = published.Id })));
    timer.Stop();
    Console.WriteLine($"PASS: 50 synthetic accounts login and read, elapsed {timer.ElapsedMilliseconds} ms (not a hardware capacity guarantee)");
    Console.WriteLine($"PASS: {checks} shared role, session, revision, replay, ownership and path checks");

    async Task<object?> Ok(string token, string command, object args)
    {
        var result = await sessions.Execute(token, Request(command, args));
        if (!result.Ok) throw new Exception($"{command}: {result.Error?.Code} {result.Error?.Message}");
        Interlocked.Increment(ref checks); return result.Result;
    }
    async Task Deny(string token, string command, object args, string? code = null, string? id = null)
    {
        try
        {
            var result = await sessions.Execute(token, Request(command, args, id));
            if (!result.Ok && (code is null || result.Error?.Code == code)) { checks++; return; }
        }
        catch (AppProblemException ex) when (code is null || ex.Problem.Code == code) { checks++; return; }
        throw new Exception($"Expected rejection: {command} {code}");
    }
}
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
finally { FileSystemBoundary.DeleteSyntheticRoot(root); FileSystemBoundary.DeleteSyntheticRoot(external); }
return 0;
void CheckApplicationDirectories()
{
    // Deliberately nonexistent locations: all checks operate on strings alone,
    // including UNC input, so this cannot connect to a share or read user files.
    const string cache = @"C:\synthetic-startup\cache\payload";
    const string executable = @"D:\synthetic-portable\app\KnowledgeApp.CSharp.exe";
    foreach (var path in new[]
    {
        cache, cache + @"\backups", @"C:\synthetic-startup\cache",
        @"D:\synthetic-portable\app", @"D:\synthetic-portable\app\exports", @"D:\synthetic-portable",
        @"d:/SYNTHETIC-portable/app/", @"C:\"
    }) Check(ApplicationDirectoryBoundary.Overlaps(path, cache, executable), "app cache/executable same, child and ancestor are rejected");
    foreach (var path in new[]
    {
        @"C:\synthetic-startup\cache\payload-other", @"C:\synthetic-startup\exports",
        @"D:\synthetic-portable\app-other", @"D:\synthetic-portable\exports", @"\\synthetic.invalid\share\exports"
    }) Check(!ApplicationDirectoryBoundary.Overlaps(path, cache, executable), "sibling/unrelated/UNC text is not an application overlap");
    Check(ApplicationDirectoryBoundary.Overlaps(cache, cache, null), "unknown executable still protects known application directory");
    Check(!ApplicationDirectoryBoundary.Overlaps(@"D:\synthetic-portable\exports", cache, null), "unknown executable does not invent another path");
    Check(ApplicationDirectoryBoundary.Overlaps(cache + @"\child", cache, cache + @"\KnowledgeApp.CSharp.exe"), "folder deployment protects coincident runtime and executable directories");
}
void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }
void Expect(Action action) { try { action(); } catch (Exception ex) when (ex is AppProblemException or IOException) { checks++; return; } throw new Exception("Expected refusal"); }
static SharedRequest Request(string command, object args, string? id = null) => new(id ?? Guid.NewGuid().ToString("D"), command, JsonSerializer.SerializeToElement(args, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
static SaveArticleInput Input(string? id, string category, string title, string status) => new(id, category, title, "合成データの概要", JsonSerializer.SerializeToElement(new { type = "doc", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = "合成データの回答" } } } } }), status, 1, null, null, false, [], [], [], [], [], [], [], [], []);
