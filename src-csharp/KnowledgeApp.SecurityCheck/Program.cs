using System.Text.Json;
using KnowledgeApp.CSharp;

const string app = HostSecurityPolicy.DocumentUrl;
const string articleId = "10000000-0000-0000-0000-000000000001";
const string imageId = "20000000-0000-0000-0000-000000000002";
var tests = new List<(string Name, Func<bool> Test)>();
void Check(string name, Func<bool> test) => tests.Add((name, test));
bool Document(string? url) => HostSecurityPolicy.IsTrustedDocument(url);
HostResource? Resource(string? url, HostResourceContext context, string method = "GET") =>
    HostSecurityPolicy.ResolveResource(url, method, context);
bool Envelope(string? json) => HostRequestEnvelope.TryParse(json, out _);

Check("launch: no arguments selects separate C# production", () => CandidateLaunchPolicy.TrySelect([], out var production) && production);
Check("launch: old shared-root candidate argument rejected", () => !CandidateLaunchPolicy.TrySelect(["--production-candidate"], out _));
Check("launch: sole explicit rehearsal argument", () => CandidateLaunchPolicy.TrySelect(["--rehearsal"], out var production) && !production);
foreach (var launchArgs in new[] { new[] { "--production-candidate", "extra" }, new[] { "--data-root", "C:/synthetic" }, new[] { "--PRODUCTION-CANDIDATE" }, new[] { "--production-candidate=C:/synthetic" }, new[] { "" } })
    Check("launch: reject unapproved arguments " + string.Join(' ', launchArgs), () => !CandidateLaunchPolicy.TrySelect(launchArgs, out _));
Check("gate: small-scale approval does not fabricate full acceptance", () =>
{
    var report = KnowledgeApp.Migration.MigrationGateReport.Create("synthetic-ui-not-present", false)
        with { ProductionDataOpened = true };
    report.Validate();
    return report.CutoverAllowed && !report.AllFinalChecksPassed && report.RecommendedPriority == "csharp_primary"
        && report.AcceptanceScope == "user-approved-small-scale-cutover"
        && report.DataRootCompatibilityTarget.EndsWith("jp.local.webknowledgesystem.csharp", StringComparison.Ordinal);
});
Check("gate: unapproved production use remains rejected", () =>
{
    var report = KnowledgeApp.Migration.MigrationGateReport.Create("synthetic-ui-not-present", false) with { ProductionDataOpened = true, ProductionUseAuthorized = false };
    try { report.Validate(); return false; } catch (InvalidOperationException) { return true; }
});

Check("response JSON: camelCase contract unchanged", () =>
    HostResponseJson.Serialize(new { RequestId = "synthetic", IsSuccess = true }) ==
        "{\"requestId\":\"synthetic\",\"isSuccess\":true}");
Check("response JSON: historical depth 128 inside typed envelope", () =>
{
    var nested = string.Concat(Enumerable.Repeat("{\"content\":", 127)) + "{\"text\":\"synthetic\"}" + new string('}', 127);
    using var body = JsonDocument.Parse(nested, new JsonDocumentOptions { MaxDepth = 128 });
    var response = HostResponseJson.Serialize(new { Id = "synthetic", Result = new { BodyDoc = body.RootElement } });
    using var parsed = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 144 });
    return parsed.RootElement.GetProperty("result").GetProperty("bodyDoc").GetRawText() == nested;
});
Check("envelope: legacy body depth 128 with typed wrappers", () =>
{
    var nested = string.Concat(Enumerable.Repeat("{\"content\":", 127)) + "{\"text\":\"synthetic\"}" + new string('}', 127);
    return Envelope("{\"id\":\"synthetic\",\"command\":\"save_article\",\"args\":{\"input\":{\"bodyDoc\":" + nested + "}}}");
});
Check("envelope: exact depth 144 is finite", () =>
{
    var nested = new string('[', 142) + "0" + new string(']', 142);
    return Envelope("{\"id\":\"synthetic\",\"command\":\"save_article\",\"args\":{\"body\":" + nested + "}}");
});
Check("envelope: duplicate property beyond former depth64 is rejected", () =>
{
    var nested = string.Concat(Enumerable.Repeat("{\"content\":", 70)) + "{\"text\":\"first\",\"text\":\"second\"}" + new string('}', 70);
    return !Envelope("{\"id\":\"synthetic\",\"command\":\"save_article\",\"args\":{\"input\":{\"bodyDoc\":" + nested + "}}}");
});

Check("document: canonical index", () => Document(app));
Check("document: normal search hash", () => Document(app + "#/search"));
Check("document: FAQ hash and query within hash", () => Document(app + "#/articles/" + articleId + "?tab=2"));
Check("document: encoded Japanese hash", () => Document(app + "#/search?q=%E6%97%A5%E6%9C%AC"));
Check("document: default HTTPS port", () => Document("https://app.knowledge.local:443/index.html#/search"));
Check("document: null", () => !Document(null));
Check("document: malformed", () => !Document("not a URI"));
Check("document: wrong scheme", () => !Document("http://app.knowledge.local/index.html"));
Check("document: foreign origin", () => !Document("https://example.invalid/index.html"));
Check("document: deceptive subdomain", () => !Document("https://app.knowledge.local.example.invalid/index.html"));
Check("document: credential authority", () => !Document("https://user:pass@app.knowledge.local/index.html"));
Check("document: different port", () => !Document("https://app.knowledge.local:444/index.html"));
Check("document: wrong path", () => !Document("https://app.knowledge.local/other.html"));
Check("document: root path", () => !Document("https://app.knowledge.local/"));
Check("document: same-origin JavaScript", () => !Document("https://app.knowledge.local/assets/index.js"));
Check("document: staged origin", () => !Document("https://knowledge-staged.local/index.html"));
Check("document: query string", () => !Document(app + "?next=https://example.invalid"));
Check("document: dot path normalization", () => !Document("https://app.knowledge.local/assets/../index.html"));
Check("document: encoded path", () => !Document("https://app.knowledge.local/%69ndex.html"));
Check("document: backslash path", () => !Document("https://app.knowledge.local\\index.html"));
Check("document: control character", () => !Document(app + "\r"));
Check("document: surrounding spaces", () => !Document(" " + app));
Check("document: file navigation", () => !Document("file:///C:/test/index.html"));
Check("document: data navigation", () => !Document("data:text/html,hello"));
Check("document: about blank", () => !Document("about:blank"));

Check("request: trusted source/current document", () => HostSecurityPolicy.MayReceiveRequest(app, app, true));
Check("request: hash routes may differ", () => HostSecurityPolicy.MayReceiveRequest(app + "#/search", app + "#/settings", true));
Check("request: foreign message sender", () => !HostSecurityPolicy.MayReceiveRequest("https://example.invalid/", app, true));
Check("request: changed current document", () => !HostSecurityPolicy.MayReceiveRequest(app, "https://example.invalid/", true));
Check("request: null current document", () => !HostSecurityPolicy.MayReceiveRequest(app, null, true));
Check("request: navigation in progress", () => !HostSecurityPolicy.MayReceiveRequest(app, app, false));
Check("response: same generation", () => HostSecurityPolicy.MaySendResponse(app, app + "#/search", true, 2, 2));
Check("response: new document with same URL", () => !HostSecurityPolicy.MaySendResponse(app, app, true, 2, 3));
Check("response: source changed", () => !HostSecurityPolicy.MaySendResponse(app, "https://example.invalid/", true, 2, 2));
Check("response: document not ready", () => !HostSecurityPolicy.MaySendResponse(app, app, false, 2, 2));

Check("resource: index document", () => Resource(app, HostResourceContext.Document) is { Root: HostResourceRoot.Ui, RelativePath: "index.html" });
Check("resource: index not executable script", () => Resource(app, HostResourceContext.Script) is null);
Check("resource: hashed dynamic JavaScript", () => Resource("https://app.knowledge.local/assets/ArticleEditorPage-aB_123.js", HostResourceContext.Script)?.ContentType == "text/javascript; charset=utf-8");
Check("resource: module preload", () => Resource("https://app.knowledge.local/assets/index-aB123.js", HostResourceContext.Other) is not null);
Check("resource: dynamic CSS", () => Resource("https://app.knowledge.local/assets/ArticleEditorPage-aB123.css", HostResourceContext.Stylesheet) is not null);
Check("resource: mascot WebP", () => Resource("https://app.knowledge.local/assets/faq-owl-aB123.webp", HostResourceContext.Image) is not null);
Check("resource: static font", () => Resource("https://app.knowledge.local/assets/font-aB123.woff2", HostResourceContext.Font) is not null);
Check("resource: asset HEAD", () => Resource("https://app.knowledge.local/assets/index-aB123.js", HostResourceContext.Script, "HEAD") is not null);
Check("resource: managed PNG", () => Resource($"https://knowledge-attachments.local/{articleId}/{imageId}.png", HostResourceContext.Image) is { Root: HostResourceRoot.Attachments });
Check("resource: staging JPEG", () => Resource($"https://knowledge-staged.local/{imageId}.jpg", HostResourceContext.Image) is { Root: HostResourceRoot.StagedImages });
Check("resource: managed WebP", () => Resource($"https://knowledge-attachments.local/{articleId}/{imageId}.webp", HostResourceContext.Image) is not null);
Check("resource: staging GIF", () => Resource($"https://knowledge-staged.local/{imageId}.gif", HostResourceContext.Image) is not null);
Check("resource: foreign network request", () => Resource("https://example.invalid/image.png", HostResourceContext.Image) is null);
Check("resource: app fetch disabled", () => Resource("https://app.knowledge.local/assets/index.js", HostResourceContext.Blocked) is null);
Check("resource: POST disabled", () => Resource(app, HostResourceContext.Document, "POST") is null);
Check("resource: local file disabled", () => Resource("file:///C:/test.png", HostResourceContext.Image) is null);
Check("resource: metadata disabled", () => Resource($"https://knowledge-staged.local/{imageId}.json", HostResourceContext.Image) is null);
Check("resource: HTML disabled", () => Resource($"https://knowledge-staged.local/{imageId}.html", HostResourceContext.Document) is null);
Check("resource: image cannot become document", () => Resource($"https://knowledge-staged.local/{imageId}.png", HostResourceContext.Document) is null);
Check("resource: image cannot become script", () => Resource($"https://knowledge-staged.local/{imageId}.png", HostResourceContext.Script) is null);
Check("resource: arbitrary image filename disabled", () => Resource("https://knowledge-staged.local/private.png", HostResourceContext.Image) is null);
Check("resource: invalid article ID", () => Resource($"https://knowledge-attachments.local/private/{imageId}.png", HostResourceContext.Image) is null);
Check("resource: missing article directory", () => Resource($"https://knowledge-attachments.local/{imageId}.png", HostResourceContext.Image) is null);
Check("resource: staging subdirectory disabled", () => Resource($"https://knowledge-staged.local/{articleId}/{imageId}.png", HostResourceContext.Image) is null);
Check("resource: traversal disabled", () => Resource("https://app.knowledge.local/assets/../index.html", HostResourceContext.Document) is null);
Check("resource: encoded traversal disabled", () => Resource("https://app.knowledge.local/assets/%2e%2e/index.html", HostResourceContext.Document) is null);
Check("resource: encoded separator disabled", () => Resource("https://app.knowledge.local/assets%2findex.js", HostResourceContext.Script) is null);
Check("resource: alternate stream disabled", () => Resource("https://app.knowledge.local/assets/index.js:secret", HostResourceContext.Script) is null);
Check("resource: static source map disabled", () => Resource("https://app.knowledge.local/assets/index.js.map", HostResourceContext.Other) is null);
Check("resource: database disabled", () => Resource("https://app.knowledge.local/data/knowledge.db", HostResourceContext.Other) is null);
Check("resource: resource credentials disabled", () => Resource("https://user@app.knowledge.local/assets/index.js", HostResourceContext.Script) is null);
Check("resource: script content in image context disabled", () => Resource("https://app.knowledge.local/assets/index.js", HostResourceContext.Image) is null);

const string valid = "{\"id\":\"csharp-123-ab_cd\",\"command\":\"get_article\",\"args\":{\"id\":\"synthetic\"}}";
Check("envelope: normal request", () => Envelope(valid));
Check("envelope: cloned arguments survive parser disposal", () => HostRequestEnvelope.TryParse(valid, out var request) && request!.Args.GetProperty("id").GetString() == "synthetic");
Check("envelope: empty arguments", () => Envelope("{\"id\":\"1\",\"command\":\"migration_probe\",\"args\":{}}"));
Check("envelope: unknown member", () => !Envelope("{\"id\":\"1\",\"command\":\"get_article\",\"args\":{},\"extra\":true}"));
Check("envelope: missing args", () => !Envelope("{\"id\":\"1\",\"command\":\"get_article\"}"));
Check("envelope: wrong property case", () => !Envelope("{\"Id\":\"1\",\"command\":\"get_article\",\"args\":{}}"));
Check("envelope: duplicate top member", () => !Envelope("{\"id\":\"1\",\"id\":\"2\",\"command\":\"get_article\",\"args\":{}}"));
Check("envelope: escaped duplicate", () => !Envelope("{\"id\":\"1\",\"\\u0069d\":\"2\",\"command\":\"get_article\",\"args\":{}}"));
Check("envelope: duplicate argument", () => !Envelope("{\"id\":\"1\",\"command\":\"get_article\",\"args\":{\"id\":\"A\",\"id\":\"B\"}}"));
Check("envelope: nested duplicate in array", () => !Envelope("{\"id\":\"1\",\"command\":\"get_article\",\"args\":{\"input\":[{\"x\":1,\"x\":2}]}}"));
Check("envelope: array args", () => !Envelope("{\"id\":\"1\",\"command\":\"get_article\",\"args\":[]}"));
Check("envelope: null args", () => !Envelope("{\"id\":\"1\",\"command\":\"get_article\",\"args\":null}"));
Check("envelope: numeric request ID", () => !Envelope("{\"id\":1,\"command\":\"get_article\",\"args\":{}}"));
Check("envelope: empty request ID", () => !Envelope("{\"id\":\"\",\"command\":\"get_article\",\"args\":{}}"));
Check("envelope: unsafe request ID", () => !Envelope("{\"id\":\"../test\",\"command\":\"get_article\",\"args\":{}}"));
Check("envelope: unsafe command", () => !Envelope("{\"id\":\"1\",\"command\":\"get article\",\"args\":{}}"));
Check("envelope: null document", () => !Envelope("null"));
Check("envelope: non-JSON", () => !Envelope("hello"));
Check("envelope: trailing JSON", () => !Envelope(valid + "{}"));
Check("envelope: trailing comma", () => !Envelope("{\"id\":\"1\",\"command\":\"get_article\",\"args\":{},}"));
Check("envelope: null string", () => !Envelope(null));
Check("envelope: maximum depth", () => !Envelope("{\"id\":\"1\",\"command\":\"get_article\",\"args\":{\"a\":" + new string('[', 143) + "0" + new string(']', 143) + "}}"));
Check("envelope: character limit", () => !Envelope(new string(' ', HostRequestEnvelope.MaximumMessageBytes + 1)));
Check("envelope: UTF-8 byte limit", () => !Envelope("{\"id\":\"1\",\"command\":\"get_article\",\"args\":{\"text\":\"" + new string('あ', HostRequestEnvelope.MaximumMessageBytes / 3 + 1) + "\"}}"));
Check("envelope: exact byte limit", () =>
{
    const string prefix = "{\"id\":\"1\",\"command\":\"get_article\",\"args\":{\"text\":\"";
    const string suffix = "\"}}";
    return Envelope(prefix + new string('a', HostRequestEnvelope.MaximumMessageBytes - prefix.Length - suffix.Length) + suffix);
});
Check("envelope: normal image paste payload", () => Envelope(JsonSerializer.Serialize(new { id = "1", command = "stage_article_image_bytes", args = new { input = new { bytesBase64 = "iVBORw0KGgo=", originalName = "合成画像.png" } } })));

Check("CSP: no outbound connections", () => HostSecurityPolicy.ContentSecurityPolicy.Contains("connect-src 'none'", StringComparison.Ordinal));
Check("CSP: no frames or objects", () => HostSecurityPolicy.ContentSecurityPolicy.Contains("frame-src 'none'", StringComparison.Ordinal) && HostSecurityPolicy.ContentSecurityPolicy.Contains("object-src 'none'", StringComparison.Ordinal));
Check("CSP: trusted images and local preview preserved", () => HostSecurityPolicy.ContentSecurityPolicy.Contains("https://knowledge-attachments.local https://knowledge-staged.local blob:", StringComparison.Ordinal));
Check("CSP: scripts have no inline/eval bypass", () => HostSecurityPolicy.ContentSecurityPolicy.Contains("script-src 'self';", StringComparison.Ordinal) && !HostSecurityPolicy.ContentSecurityPolicy.Contains("unsafe-eval", StringComparison.Ordinal));
Check("bridge script: top-level/origin/path/query guard", () =>
{
    var script = HostSecurityPolicy.BridgeInitializationScript("{}");
    return script.Contains("window.top === window", StringComparison.Ordinal) &&
        script.Contains("location.origin === 'https://app.knowledge.local'", StringComparison.Ordinal) &&
        script.Contains("location.pathname === '/index.html'", StringComparison.Ordinal) &&
        script.Contains("location.search === ''", StringComparison.Ordinal);
});

Check("shutdown: first close begins cleanup", () =>
{
    var state = new HostShutdownPolicy();
    return state.RequestClose(0) == HostCloseDecision.BeginShutdown && state.IsClosing && !state.CanClose;
});
Check("shutdown: active critical operation blocks close", () =>
{
    var state = new HostShutdownPolicy();
    return state.RequestClose(1) == HostCloseDecision.RejectCriticalOperation && !state.IsClosing && !state.CanClose;
});
Check("shutdown: multiple critical operations block close", () => new HostShutdownPolicy().RequestClose(17) == HostCloseDecision.RejectCriticalOperation);
Check("shutdown: retry after operation completes", () =>
{
    var state = new HostShutdownPolicy();
    state.RequestClose(1);
    return state.RequestClose(0) == HostCloseDecision.BeginShutdown;
});
Check("shutdown: duplicate close does not begin cleanup twice", () =>
{
    var state = new HostShutdownPolicy();
    state.RequestClose(0);
    return state.RequestClose(0) == HostCloseDecision.IgnoreDuplicate && !state.CanClose;
});
Check("shutdown: final close allowed after cleanup", () =>
{
    var state = new HostShutdownPolicy();
    state.RequestClose(0);
    state.CompleteShutdown();
    return state.RequestClose(0) == HostCloseDecision.AllowClose && state.CanClose;
});
Check("shutdown: completion cannot bypass initial close", () =>
{
    try { new HostShutdownPolicy().CompleteShutdown(); return false; }
    catch (InvalidOperationException) { return true; }
});
Check("shutdown: duplicate completion is harmless", () =>
{
    var state = new HostShutdownPolicy();
    state.RequestClose(0);
    state.CompleteShutdown();
    state.CompleteShutdown();
    return state.CanClose;
});
Check("shutdown: no browser created needs no exit signal", () => HostShutdownPolicy.BrowserResourcesReleased(true, false, null, new HashSet<uint>()));
Check("shutdown: pending initialization must not delete", () => !HostShutdownPolicy.BrowserResourcesReleased(false, false, null, new HashSet<uint>()));
Check("shutdown: pending known browser waits", () => !HostShutdownPolicy.BrowserResourcesReleased(true, true, 10, new HashSet<uint>()));
Check("shutdown: expected browser exited", () => HostShutdownPolicy.BrowserResourcesReleased(true, true, 10, new HashSet<uint> { 10 }));
Check("shutdown: another process exit is insufficient", () => !HostShutdownPolicy.BrowserResourcesReleased(true, true, 10, new HashSet<uint> { 20 }));
Check("shutdown: process exit before initialization completes", () => !HostShutdownPolicy.BrowserResourcesReleased(false, true, 10, new HashSet<uint> { 10 }));
Check("shutdown: failed browser creation without exit signal retains data", () => !HostShutdownPolicy.BrowserResourcesReleased(true, true, null, new HashSet<uint>()));
Check("shutdown: private environment exit without published process ID", () => HostShutdownPolicy.BrowserResourcesReleased(true, true, null, new HashSet<uint> { 10 }));
Check("shutdown: deadline pending", () => HostShutdownPolicy.DecideWait(false, false) == HostWaitDecision.Pending);
Check("shutdown: deadline expires", () => HostShutdownPolicy.DecideWait(false, true) == HostWaitDecision.TimedOut);
Check("shutdown: exit before deadline", () => HostShutdownPolicy.DecideWait(true, false) == HostWaitDecision.Completed);
Check("shutdown: exit and deadline together permit completed operation", () => HostShutdownPolicy.DecideWait(true, true) == HostWaitDecision.Completed);
foreach (var initializationCompleted in new[] { false, true })
foreach (var browserReleased in new[] { false, true })
foreach (var databaseDisposed in new[] { false, true })
{
    Check($"shutdown: cleanup gate init={initializationCompleted} browser={browserReleased} DB={databaseDisposed}", () =>
        HostShutdownPolicy.MayDeleteTemporaryData(initializationCompleted, browserReleased, databaseDisposed) ==
        (initializationCompleted && browserReleased && databaseDisposed));
}
Check("shutdown: failed deletion reports residual temporary data", () => HostShutdownPolicy.NeedsResidualDataNotice(true, false));
Check("shutdown: successful deletion needs no warning", () => !HostShutdownPolicy.NeedsResidualDataNotice(true, true));
Check("shutdown: no temporary root created needs no warning", () => !HostShutdownPolicy.NeedsResidualDataNotice(false, false));
Check("shutdown: empty successful cleanup needs no warning", () => !HostShutdownPolicy.NeedsResidualDataNotice(false, true));

var expectedProfile = Path.Combine(Path.GetTempPath(), "knowledgeapp-security-check-00000000-0000-0000-0000-000000000000", "temp", "webview2-profile");
Check("profile: expected absolute directory", () => HostShutdownPolicy.IsExpectedProfileDirectory(expectedProfile, expectedProfile));
Check("profile: trailing directory separator", () => HostShutdownPolicy.IsExpectedProfileDirectory(expectedProfile + Path.DirectorySeparatorChar, expectedProfile));
Check("profile: normalized dot path", () => HostShutdownPolicy.IsExpectedProfileDirectory(Path.Combine(expectedProfile, "."), expectedProfile));
Check("profile: case-insensitive Windows directory identity", () => HostShutdownPolicy.IsExpectedProfileDirectory(expectedProfile.ToUpperInvariant(), expectedProfile));
Check("profile: different root rejected", () => !HostShutdownPolicy.IsExpectedProfileDirectory(Path.Combine(Path.GetTempPath(), "other-profile"), expectedProfile));
Check("profile: sibling sharing prefix rejected", () => !HostShutdownPolicy.IsExpectedProfileDirectory(expectedProfile + "-other", expectedProfile));
Check("profile: null actual rejected", () => !HostShutdownPolicy.IsExpectedProfileDirectory(null, expectedProfile));
Check("profile: empty actual rejected", () => !HostShutdownPolicy.IsExpectedProfileDirectory("", expectedProfile));
Check("profile: relative actual rejected", () => !HostShutdownPolicy.IsExpectedProfileDirectory("temp/webview2-profile", expectedProfile));
Check("profile: relative expected rejected", () => !HostShutdownPolicy.IsExpectedProfileDirectory(expectedProfile, "temp/webview2-profile"));
Check("profile: control character rejected", () => !HostShutdownPolicy.IsExpectedProfileDirectory(expectedProfile + "\0", expectedProfile));
Check("profile: URL instead of directory rejected", () => !HostShutdownPolicy.IsExpectedProfileDirectory("https://app.knowledge.local/profile", expectedProfile));

var failures = new List<string>();
foreach (var (name, test) in tests)
{
    try
    {
        if (!test()) failures.Add(name);
    }
    catch (Exception exception)
    {
        failures.Add($"{name} ({exception.GetType().Name})");
    }
}
foreach (var failure in failures) Console.Error.WriteLine($"FAIL: {failure}");
Console.WriteLine($"Host security checks: {tests.Count - failures.Count}/{tests.Count} passed.");
Console.WriteLine("Pure synthetic requests only; no DB, mail, browser, network, or user files were accessed.");
return failures.Count == 0 ? 0 : 1;
