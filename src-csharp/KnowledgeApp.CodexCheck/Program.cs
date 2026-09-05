using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeApp.Data;
using Microsoft.Data.Sqlite;

// All files and SQL in this executable are synthetic fixtures under fresh,
// validated OS temporary roots. No production data root is opened or searched.
internal static partial class Program
{
    private static int _passed;
    private static readonly List<string> Failures = [];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static int Main()
    {
        Console.OutputEncoding = Utf8;
        Case("prototype UI fixture starts safely without authentication", _ => UiFixtureCheck.Run());
        Case("unauthenticated contracts", f =>
        {
            f.Authentication.Logout();
            Expect("AUTH-002", () => f.Codex.ListProposals());
            Expect("AUTH-002", () => f.Codex.CreateDelegation(new("revise", [Guid.NewGuid().ToString()])));
            Expect("AUTH-002", () => f.Codex.AcceptProposal(new(Guid.NewGuid().ToString(), null, false)));
            Expect("AUTH-002", () => f.Codex.RejectProposal(Guid.NewGuid().ToString()));
            Expect("AUTH-002", () => f.Codex.ReopenRejectedProposal(Guid.NewGuid().ToString()));
            Expect("AUTH-002", () => f.Codex.GetMergePublicationContext(Guid.NewGuid().ToString()));
            Expect("AUTH-002", () => f.Codex.MarkMergeSources(Guid.NewGuid().ToString()));
            Expect("AUTH-002", () => f.Codex.ClearArticleMerge(Guid.NewGuid().ToString()));
        });
        Case("catalog metadata-only boundary", f =>
        {
            f.Article("PRIVATE_SYNTHETIC_FAQ_MARKER");
            f.Codex.ListProposals();
            using var catalog = JsonDocument.Parse(File.ReadAllText(f.Files.CategoryCatalogPath, Utf8));
            var root = catalog.RootElement;
            Check(root.GetProperty("formatVersion").GetUInt32() == 1, "Catalog version");
            var item = root.GetProperty("categories").EnumerateArray().Single(x => x.GetProperty("id").GetString() == f.Category.Id);
            SetEqual(item.EnumerateObject().Select(x => x.Name), ["id", "parentId", "name", "description", "depth", "path"]);
            Check(!root.GetRawText().Contains("PRIVATE_SYNTHETIC_FAQ_MARKER", StringComparison.Ordinal), "FAQ leaked into catalog");
            Check(!root.GetRawText().Contains("articleCount", StringComparison.Ordinal), "Count leaked into catalog");
        });
        Case("create approval is draft and audited", f =>
        {
            var user = f.Authentication.CreateUser("synthetic-user", "合成一般担当者", "synthetic-password", UserRoles.User);
            f.Authentication.Logout();
            f.Authentication.Login("synthetic-user", "synthetic-password");
            var proposal = f.Proposal();
            f.Submit(proposal);
            Check(f.Codex.ListProposals().Proposals.Count == 1 && f.Count("articles") == 0, "Approval bypass");
            var result = f.Accept(proposal);
            Check(result.Article.Status == "draft" && !result.Article.IsHidden && result.Article.NewBadgeUntil is null &&
                result.Article.UpdatedBadgeUntil is null, "Create status was not fixed");
            Check(result.Article.CreatedByUserId == user.Id && result.Article.UpdatedByUserId == user.Id, "Actor missing");
            Check(f.Scalar("SELECT management_code FROM articles WHERE id = $id", result.Article.Id)?.StartsWith("FAQ-", StringComparison.Ordinal) == true,
                "Management ID missing");
            Check(f.Count("codex_proposal_receipts") == 1 && f.Count("article_search_fts") == 1, "Receipt or FTS missing");
            Check(f.Codex.ListProposals().History.Single().AcceptedArticleId == result.Article.Id, "History target missing");
        });
        Case("legacy version one create", f =>
        {
            var proposal = f.Proposal() with { FormatVersion = 1, SeriesId = null };
            f.Submit(proposal);
            Check(f.Codex.ListProposals().Proposals.Count == 1, "Legacy proposal rejected");
            f.Accept(proposal);
            Check(f.Count("articles") == 1, "Legacy create failed");
        });
        Case("new category and FAQ commit together", f =>
        {
            var proposal = f.Proposal() with
            {
                ExistingCategoryCandidates = [],
                NewCategoryProposal = new() { Name = "合成新規分類", Description = "説明", Reason = "新しい分類が適切です", ParentCategoryId = f.Category.Id }
            };
            f.Submit(proposal);
            f.Codex.ListProposals();
            var result = f.Codex.AcceptProposal(new(proposal.RequestId, null, true));
            Check(result.CreatedCategory?.Depth == 2 && result.Article.CategoryId == result.CreatedCategory.Id, "Atomic category result");
            Check(f.Count("categories") == 2 && f.Count("articles") == 1 && f.Count("codex_proposal_receipts") == 1, "Atomic create counts");
        });
        Case("duplicate category rolls back FAQ and receipt", f =>
        {
            var proposal = f.Proposal() with { NewCategoryProposal = new() { Name = f.Category.Name, Reason = "合成競合" } };
            f.Submit(proposal);
            f.Codex.ListProposals();
            Expect("CAT-004", () => f.Codex.AcceptProposal(new(proposal.RequestId, null, true)));
            Check(f.Count("categories") == 1 && f.Count("articles") == 0 && f.Count("codex_proposal_receipts") == 0, "Category conflict partially committed");
        });
        Case("sixth category depth rolls back", f =>
        {
            var parent = f.Category;
            for (var depth = 2; depth <= 5; depth++) parent = f.Classification.CreateCategory($"合成階層{depth}", "", parent.Id);
            var proposal = f.Proposal() with { NewCategoryProposal = new() { Name = "合成階層6", ParentCategoryId = parent.Id, Reason = "合成上限" } };
            f.Submit(proposal);
            f.Codex.ListProposals();
            Expect("CAT-003", () => f.Codex.AcceptProposal(new(proposal.RequestId, null, true)));
            Check(f.Count("categories") == 5 && f.Count("articles") == 0 && f.Count("codex_proposal_receipts") == 0, "Depth conflict partially committed");
        });
        Case("missing selected category refuses approval", f =>
        {
            var proposal = f.Proposal();
            f.Submit(proposal);
            f.Codex.ListProposals();
            Expect("CDX-005", () => f.Codex.AcceptProposal(new(proposal.RequestId, Guid.NewGuid().ToString(), false)));
            Check(f.Count("articles") == 0 && f.Count("codex_proposal_receipts") == 0, "Missing category committed");
        });
        Case("missing proposed category refuses approval", f =>
        {
            var proposal = f.Proposal();
            f.Submit(proposal);
            f.Codex.ListProposals();
            Expect("CDX-004", () => f.Codex.AcceptProposal(new(proposal.RequestId, null, true)));
        });
        Case("receipt prevents repeated acceptance", f =>
        {
            var proposal = f.Proposal();
            f.Submit(proposal);
            f.Codex.ListProposals();
            f.Accept(proposal);
            f.Submit(proposal);
            Check(f.Codex.ListProposals().Proposals.Count == 0, "Accepted residual file shown");
            Expect("CDX-006", () => f.Accept(proposal));
            Check(f.Count("articles") == 1 && f.Count("codex_proposal_receipts") == 1, "Duplicate approval committed");
        });
        Case("equivalent JSON ordering and escaping is same request", f =>
        {
            var proposal = f.Proposal();
            f.Submit(proposal);
            f.Codex.ListProposals();
            // Simulates a semantically identical payload produced by another serializer.
            var node = JsonNode.Parse(JsonSerializer.Serialize(proposal, CodexJson.Options))!.AsObject();
            var reordered = new JsonObject();
            foreach (var property in node.Reverse()) reordered.Add(property.Key, property.Value?.DeepClone());
            f.Sql("UPDATE codex_proposal_history SET payload_json = $value WHERE request_id = $id", proposal.RequestId, reordered.ToJsonString());
            Check(f.Codex.ListProposals().Proposals.Count == 1 && f.Count("codex_proposal_history") == 1, "Equivalent payload misdetected as tampering");
        });
        Case("same request different content is rejected", f =>
        {
            var proposal = f.Proposal();
            f.Submit(proposal);
            f.Codex.ListProposals();
            f.Submit(proposal with { Faq = proposal.Faq with { Title = "差し替えられた合成タイトル" } });
            var inbox = f.Codex.ListProposals();
            Check(inbox.Rejected.Count == 1 && inbox.Proposals.Single().Faq.Title == proposal.Faq.Title,
                "Tampering replaced the reviewed history payload");
            Check(f.Count("articles") == 0 && f.Count("codex_proposal_history") == 1, "Tampered payload committed");
            Check(f.Accept(proposal).Article.Title == proposal.Faq.Title, "Acceptance used tampered file instead of stored payload");
        });
        Case("latest rejected request reopens without new identity", f =>
        {
            var proposal = f.Proposal();
            f.Submit(proposal);
            f.Codex.ListProposals();
            f.Codex.RejectProposal(proposal.RequestId);
            var history = f.Codex.ListProposals().History.Single();
            Check(history.Status == "rejected" && history.CanReopen && !File.Exists(f.ProposalPath(proposal.RequestId)), "Rejected history lost");
            f.Codex.ReopenRejectedProposal(proposal.RequestId);
            Check(f.Codex.ListProposals().Proposals.Single().RequestId == proposal.RequestId, "Reopen changed identity");
            f.Accept(proposal);
            Check(f.Count("articles") == 1 && f.Count("codex_proposal_history") == 1, "Reopen created duplicate history");
        });
        Case("older rejected request cannot reopen or reaccept", f =>
        {
            var older = f.Proposal();
            f.Submit(older);
            f.Codex.ListProposals();
            f.Codex.RejectProposal(older.RequestId);
            var newer = f.Proposal() with { SeriesId = older.SeriesId };
            f.Submit(newer);
            f.Codex.ListProposals();
            Check(!f.Codex.ListProposals().History.Single().CanReopen, "Old rejected proposal reopen enabled");
            Expect("CDX-015", () => f.Codex.ReopenRejectedProposal(older.RequestId));
            f.Submit(older);
            Expect("CDX-003", () => f.Accept(older));
            Check(f.Count("articles") == 0, "Rejected proposal bypassed reopening");
        });
        Case("pending payload survives inbox removal", f =>
        {
            var proposal = f.Proposal();
            f.Submit(proposal);
            f.Codex.ListProposals();
            File.Delete(f.ProposalPath(proposal.RequestId));
            f.Accept(proposal);
            Check(f.Count("articles") == 1, "Pending history not used");
        });
        InvalidCase("unknown top-level JSON", node => node["unknown"] = "not allowed");
        InvalidCase("unknown nested JSON", node => node["faq"]!["unknown"] = "not allowed");
        InvalidCase("unknown format version", node => node["formatVersion"] = 999);
        InvalidCase("empty proposal title", node => node["faq"]!["title"] = " ");
        InvalidCase("importance out of bounds", node => node["faq"]!["importance"] = 4);
        InvalidCase("dangerous rich-text node", node => node["faq"]!["bodyDoc"] = JsonNode.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"iframe\"}]}"));
        InvalidCase("unsafe clickable URL", node => node["faq"]!["bodyDoc"] = JsonNode.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"unsafe\",\"marks\":[{\"type\":\"link\",\"attrs\":{\"href\":\"javascript:alert(1)\"}}]}]}]}"));
        InvalidCase("new proposal cannot import managed image", node => node["faq"]!["bodyDoc"] = JsonNode.Parse(ImageDocument(Guid.NewGuid().ToString()).GetRawText()));
        Case("filename ID mismatch rejected without hiding valid proposal", f =>
        {
            var invalid = f.Proposal();
            File.WriteAllText(f.ProposalPath(Guid.NewGuid().ToString()), JsonSerializer.Serialize(invalid, CodexJson.Options), Utf8);
            var valid = f.Proposal();
            f.Submit(valid);
            var inbox = f.Codex.ListProposals();
            Check(inbox.Rejected.Count == 1 && inbox.Proposals.Single().RequestId == valid.RequestId, "Mismatch handling");
        });
        Case("one MiB proposal limit", f =>
        {
            var proposal = f.Proposal();
            File.WriteAllText(f.ProposalPath(proposal.RequestId), new string(' ', 1024 * 1024 + 1), Utf8);
            Check(f.Codex.ListProposals().Rejected.Count == 1 && f.Count("codex_proposal_history") == 0, "Oversize proposal stored");
        });
        Case("partial and unrelated file extensions ignored", f =>
        {
            var bytes = JsonSerializer.Serialize(f.Proposal(), CodexJson.Options);
            File.WriteAllText(Path.Combine(f.Files.InboxPath, "synthetic.partial"), bytes, Utf8);
            File.WriteAllText(Path.Combine(f.Files.InboxPath, "synthetic.txt"), bytes, Utf8);
            Check(f.Codex.ListProposals().Proposals.Count == 0 && f.Count("codex_proposal_history") == 0, "Partial file ingested");
        });
        Case("explicit delegation contains only selected safe fields", f =>
        {
            var selected = f.Article("選択済み合成FAQ");
            f.Article("UNSELECTED_SYNTHETIC_FAQ_MARKER");
            var delegation = f.Codex.CreateDelegation(new("revise", [selected.Id]));
            using var json = JsonDocument.Parse(File.ReadAllText(delegation.FilePath, Utf8));
            var root = json.RootElement;
            Check(root.GetProperty("articles").GetArrayLength() == 1 && root.GetProperty("delegationId").GetString() == delegation.DelegationId, "Delegation identity");
            var article = root.GetProperty("articles")[0];
            SetEqual(article.EnumerateObject().Select(x => x.Name), ["articleId", "sourceUpdatedAt", "categoryId", "categoryPath", "title", "summary", "bodyDoc", "status", "importance", "attachments"]);
            Check(!root.GetRawText().Contains("UNSELECTED_SYNTHETIC_FAQ_MARKER", StringComparison.Ordinal), "Unselected FAQ delegated");
            Check(!root.GetRawText().Contains(f.Root, StringComparison.OrdinalIgnoreCase), "Local path delegated");
            Check(delegation.Prompt.Contains(delegation.DelegationId, StringComparison.Ordinal), "Prompt lost delegation number");
        });
        Case("delegation count and duplicate boundaries", f =>
        {
            var first = f.Article("合成委譲1");
            var second = f.Article("合成委譲2");
            Expect("CDX-020", () => f.Codex.CreateDelegation(new("revise", [])));
            Expect("CDX-020", () => f.Codex.CreateDelegation(new("revise", [first.Id, second.Id])));
            Expect("CDX-020", () => f.Codex.CreateDelegation(new("merge", [first.Id])));
            Expect("CDX-020", () => f.Codex.CreateDelegation(new("merge", [first.Id, first.Id])));
            Expect("CDX-020", () => f.Codex.CreateDelegation(new("merge", Enumerable.Repeat(first.Id, 11).ToArray())));
        });
        Case("deleted article cannot be delegated", f =>
        {
            var article = f.Article("合成削除");
            f.Editing.DeleteArticle(article.Id);
            Expect("CDX-020", () => f.Codex.CreateDelegation(new("revise", [article.Id])));
        });
        Case("missing delegation rejected at reception and acceptance", f =>
        {
            var source = f.Article("合成委譲元");
            var proposal = f.Revision(source, Guid.NewGuid().ToString());
            f.Submit(proposal);
            Check(f.Codex.ListProposals().Rejected.Count == 1 && f.Count("codex_proposal_history") == 0, "Unknown delegation recorded");
            Expect("CDX-022", () => f.Accept(proposal));
        });
        Case("delegation source replacement rejected at reception", f =>
        {
            var first = f.Article("合成委譲元1");
            var second = f.Article("合成委譲元2");
            var delegation = f.Codex.CreateDelegation(new("revise", [first.Id]));
            f.Submit(f.Revision(second, delegation.DelegationId));
            Check(f.Codex.ListProposals().Rejected.Count == 1 && f.Count("codex_proposal_history") == 0, "Source replacement accepted");
        });
        Case("delegation five MiB limit", f =>
        {
            // Valid plain-text budget, but deliberately verbose structured JSON.
            var nodes = Enumerable.Range(0, 2_000).Select(_ => new
            {
                type = "paragraph", content = new[] { new { type = "text", text = new string('界', 45), marks = new[] { new { type = "bold" }, new { type = "italic" } } } }
            }).ToArray();
            var body = JsonSerializer.SerializeToElement(new { type = "doc", content = nodes });
            var ids = Enumerable.Range(0, 10).Select(i => f.Article($"合成大容量{i}", body: body).Id).ToArray();
            Expect("CDX-023", () => f.Codex.CreateDelegation(new("merge", ids)));
            Check(!Directory.EnumerateFiles(f.Files.DelegationsPath, "*.knowledge-delegation.json").Any(), "Oversize delegation committed");
        });
        Case("revision preserves state audit metadata and legacy fields", f =>
        {
            var source = f.Article("合成旧タイトル", hidden: true, badge: "2099-12-31");
            f.Sql("UPDATE articles SET procedure_text = $value WHERE id = $id", source.Id, "合成旧手順");
            var proposal = f.DelegateRevision(source);
            f.Submit(proposal);
            f.Codex.ListProposals();
            var updated = f.Accept(proposal).Article;
            Check(updated.Title == proposal.Faq.Title && updated.Status == source.Status && updated.CategoryId == source.CategoryId && updated.IsHidden &&
                updated.NewBadgeUntil == source.NewBadgeUntil && updated.UpdatedBadgeUntil == source.UpdatedBadgeUntil && updated.CreatedAt == source.CreatedAt &&
                updated.CreatedByUserId == source.CreatedByUserId, "Revision modified protected state");
            Check(f.Scalar("SELECT procedure_text FROM articles WHERE id = $id", source.Id) == "合成旧手順", "Revision removed legacy data");
            Check(f.Count("articles") == 1 && f.Count("codex_proposal_receipts") == 1, "Revision created extra article");
        });
        Case("revision version conflict is atomic", f =>
        {
            var source = f.Article("合成競合元");
            var proposal = f.DelegateRevision(source);
            f.Submit(proposal);
            f.Codex.ListProposals();
            var changed = f.Editing.SaveArticle(f.Input(source) with { Title = "利用者の合成変更" });
            Expect("CDX-012", () => f.Accept(proposal));
            Check(f.View.GetArticle(source.Id).Title == changed.Title && f.Count("codex_proposal_receipts") == 0, "Stale revision overwrote source");
        });
        Case("revision deletion conflict is atomic", f =>
        {
            var source = f.Article("合成削除競合元");
            var proposal = f.DelegateRevision(source);
            f.Submit(proposal);
            f.Codex.ListProposals();
            f.Editing.DeleteArticle(source.Id);
            Expect("CDX-012", () => f.Accept(proposal));
            Check(f.Count("codex_proposal_receipts") == 0 && f.View.GetArticle(source.Id).DeletedAt is not null, "Deleted source revision committed");
        });
        Case("revision images preserve file and metadata", f =>
        {
            var source = f.ImageArticle();
            var image = source.Attachments.Single();
            var proposal = f.DelegateRevision(source) with { Faq = f.Proposal().Faq with { BodyDoc = source.BodyDoc } };
            f.Submit(proposal);
            f.Codex.ListProposals();
            var updated = f.Accept(proposal).Article;
            Check(updated.Attachments.Single() == image && JsonElement.DeepEquals(updated.BodyDoc, source.BodyDoc), "Image changed during revision");
            var delegation = File.ReadAllText(Path.Combine(f.Files.DelegationsPath, proposal.SeriesId + ".knowledge-delegation.json"), Utf8);
            Check(!delegation.Contains(image.AssetPath, StringComparison.Ordinal), "Image file path delegated");
        });
        Case("revision cannot remove image", f =>
        {
            var source = f.ImageArticle();
            var proposal = f.DelegateRevision(source);
            f.Submit(proposal);
            f.Codex.ListProposals();
            Expect("CDX-021", () => f.Accept(proposal));
            Check(f.View.GetArticle(source.Id).Attachments.Count == 1 && f.Count("codex_proposal_receipts") == 0, "Image removal committed");
        });
        Case("revision cannot change image alternative text", f =>
        {
            var source = f.ImageArticle();
            var proposal = f.DelegateRevision(source);
            proposal = proposal with { Faq = proposal.Faq with { BodyDoc = ImageDocument(source.Attachments.Single().Id, "変更した代替文") } };
            f.Submit(proposal);
            f.Codex.ListProposals();
            Expect("CDX-021", () => f.Accept(proposal));
        });
        Case("revision cannot move existing image node", f =>
        {
            var source = f.ImageArticle();
            var proposal = f.DelegateRevision(source);
            var changed = JsonNode.Parse(source.BodyDoc.GetRawText())!.AsObject();
            var content = changed["content"]!.AsArray();
            var image = content[1]!.DeepClone();
            content.RemoveAt(1);
            content.Insert(0, image);
            proposal = proposal with { Faq = proposal.Faq with { BodyDoc = JsonSerializer.SerializeToElement(changed) } };
            f.Submit(proposal);
            f.Codex.ListProposals();
            Expect("CDX-021", () => f.Accept(proposal));
        });
        Case("merge draft publication marking and clear flow", f =>
        {
            var first = f.Article("合成統合元1");
            var second = f.Article("合成統合元2");
            var target = f.AcceptMerge(first, second);
            Check(target.Status == "draft" && f.View.GetArticle(first.Id).UpdatedAt == first.UpdatedAt && f.View.GetArticle(second.Id).UpdatedAt == second.UpdatedAt,
                "Merge approval changed sources");
            Check(f.Codex.GetMergePublicationContext(target.Id)?.CanMarkMerged == true, "Merge context missing");
            Expect("CDX-016", () => f.Editing.SaveArticle(f.Input(target) with { Status = "published", NewBadgeUntil = null }));
            target = f.Editing.SaveArticle(f.Input(target) with { Status = "published", NewBadgeUntil = "2099-12-31" });
            Check(f.Count("article_merge_relations") == 0, "Publication auto-marked sources");
            Check(f.Codex.MarkMergeSources(target.Id).MarkedCount == 2, "Merge marking count");
            Check(f.Codex.GetMergePublicationContext(target.Id)?.AllSourcesMerged == true, "All-sources flag");
            Check(f.Codex.MarkMergeSources(target.Id).MarkedCount == 0, "Repeated marking changed relations");
            Check(f.Search("合成統合元").Total == 0, "Merged source remains in normal search");
            Expect("CDX-020", () => f.Codex.CreateDelegation(new("revise", [first.Id])));
            var cleared = f.Codex.ClearArticleMerge(first.Id);
            Check(cleared.MergeInfo is null && cleared.Status == first.Status && cleared.Title == first.Title && cleared.UpdatedAt == first.UpdatedAt,
                "Clear changed source content/state");
            Check(f.Search("合成統合元").Total == 1, "Cleared source missing from search");
            Expect("CDX-018", () => f.Codex.ClearArticleMerge(first.Id));
        });
        Case("stale merge approval rolls back", f =>
        {
            var first = f.Article("合成元1");
            var second = f.Article("合成元2");
            var proposal = f.Merge(first, second);
            f.Submit(proposal);
            f.Codex.ListProposals();
            f.Editing.SaveArticle(f.Input(second) with { Title = "合成変更" });
            Expect("CDX-012", () => f.Accept(proposal));
            Check(f.Count("articles") == 2 && f.Count("codex_proposal_receipts") == 0 && f.Count("article_merge_relations") == 0, "Stale merge partially committed");
        });
        Case("one stale source rolls back all merge marking", f =>
        {
            var first = f.Article("合成元1");
            var second = f.Article("合成元2");
            var target = f.AcceptMerge(first, second);
            target = f.Editing.SaveArticle(f.Input(target) with { Status = "published", NewBadgeUntil = "2099-12-31" });
            f.Editing.SaveArticle(f.Input(second) with { Title = "承認後の合成変更" });
            Expect("CDX-012", () => f.Codex.MarkMergeSources(target.Id));
            Check(f.Count("article_merge_relations") == 0 && f.View.GetArticle(first.Id).MergeInfo is null, "Partial merge marking committed");
        });
        Case("merge target must stay visible until all sources cleared", f =>
        {
            var first = f.Article("合成元1");
            var second = f.Article("合成元2");
            var target = f.AcceptMerge(first, second);
            Expect("CDX-016", () => f.Codex.MarkMergeSources(target.Id));
            target = f.Editing.SaveArticle(f.Input(target) with { Status = "published", NewBadgeUntil = "2099-12-31" });
            f.Codex.MarkMergeSources(target.Id);
            Expect("ART-009", () => f.Editing.SaveArticle(f.Input(target) with { IsHidden = true }));
            Expect("ART-009", () => f.Editing.SaveArticle(f.Input(target) with { Status = "draft" }));
            Expect("ART-009", () => f.Editing.DeleteArticle(target.Id));
            f.Codex.ClearArticleMerge(first.Id);
            f.Codex.ClearArticleMerge(second.Id);
            Check(f.Editing.DeleteArticle(target.Id).DeletedAt is not null, "Cleared target cannot be deleted");
        });
        Case("full backup excludes bridge files and restore regenerates catalog", f =>
        {
            var source = f.Article("合成バックアップ元");
            f.Codex.CreateDelegation(new("revise", [source.Id]));
            var proposal = f.Proposal();
            f.Submit(proposal);
            f.Codex.ListProposals();
            var destination = Path.Combine(f.Root, "synthetic-roundtrip.faqbackup");
            f.Backup.CreateFullBackup(new(destination, "synthetic-roundtrip", false));
            using (var archive = ZipFile.OpenRead(destination))
            {
                Check(!archive.Entries.Any(entry => entry.FullName.Contains("codex-", StringComparison.Ordinal)), "Codex files included in backup");
            }
            f.Classification.CreateCategory("復元で消える合成分類", "", null);
            f.Codex.ListProposals();
            f.Backup.RestoreBackup(destination);
            Check(f.Authentication.GetCurrentUser() is null, "Restore kept old session");
            using var catalog = JsonDocument.Parse(File.ReadAllText(f.Files.CategoryCatalogPath, Utf8));
            Check(!catalog.RootElement.GetRawText().Contains("復元で消える合成分類", StringComparison.Ordinal), "Restore left stale catalog");
            f.Authentication.Login("0000", "");
            Check(f.Codex.ListProposals().Proposals.Count == 1, "Backup lost existing proposal history");
        });

        DeepJsonCases();
        Console.WriteLine($"CodexCheck: {_passed} passed, {Failures.Count} failed, {_passed + Failures.Count} scenarios.");
        foreach (var failure in Failures) Console.WriteLine(failure);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void InvalidCase(string name, Action<JsonObject> mutate) => Case(name, f =>
    {
        var invalid = f.Proposal();
        var node = JsonNode.Parse(JsonSerializer.Serialize(invalid, CodexJson.Options))!.AsObject();
        mutate(node);
        File.WriteAllText(f.ProposalPath(invalid.RequestId), node.ToJsonString(), Utf8);
        var valid = f.Proposal();
        f.Submit(valid);
        var inbox = f.Codex.ListProposals();
        Check(inbox.Rejected.Count == 1 && inbox.Proposals.Single().RequestId == valid.RequestId, "Invalid proposal hid valid proposal");
        Check(f.Count("articles") == 0 && f.Count("codex_proposal_history") == 1, "Invalid proposal stored");
    });

    private static void Case(string name, Action<Fixture> test)
    {
        try
        {
            using var fixture = new Fixture();
            test(fixture);
            _passed++;
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception error)
        {
            var reason = error is AppProblemException problem ? problem.Problem.Code : error.Message;
            Failures.Add($"FAIL: {name}: {reason}");
            Console.WriteLine(Failures[^1]);
        }
    }

    private static void Expect(string expected, Action action)
    {
        try { action(); }
        catch (AppProblemException problem) when (problem.Problem.Code == expected) { return; }
        catch (AppProblemException problem) { throw new InvalidOperationException($"Expected {expected}; actual {problem.Problem.Code}"); }
        throw new InvalidOperationException($"Expected {expected}; operation succeeded");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void SetEqual(IEnumerable<string> actual, IEnumerable<string> expected) =>
        Check(actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected), "Unexpected exposed fields");

    private static JsonElement Body(string text) => JsonSerializer.SerializeToElement(new
    {
        type = "doc", content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } }
    });

    private static JsonElement ImageDocument(string id, string alt = "合成画像") => JsonSerializer.SerializeToElement(new
    {
        type = "doc", content = new object[]
        {
            new { type = "paragraph", content = new[] { new { type = "text", text = "合成画像の説明です。" } } },
            new { type = "image", attrs = new { src = "knowledge-attachment:" + id, alt, title = (string?)null, attachmentId = id } }
        }
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        internal string Root { get; }
        internal KnowledgeDatabase Database { get; }
        internal AuthenticationService Authentication { get; }
        internal ClassificationSearchService Classification { get; }
        internal ArticleEditingService Editing { get; }
        internal ArticleViewService View { get; }
        internal CodexProposalService Codex { get; }
        internal CodexProposalFiles Files { get; }
        internal BackupService Backup { get; }
        internal CategorySummary Category { get; }

        internal Fixture()
        {
            Root = Path.Combine(_temporaryRoot, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
            Database = KnowledgeDatabase.OpenSynthetic(Root);
            Authentication = new(Database);
            Authentication.Login("0000", "");
            Classification = new(Database, Authentication);
            var attachments = new ArticleAttachmentService(Root);
            Editing = new(Database, Authentication, attachments);
            View = new(Database, Authentication, attachments);
            Codex = new(Database, Authentication, attachments);
            Files = new(Database);
            Backup = new(Database, Authentication, Root, Codex.RefreshCategoryCatalogBestEffort);
            Category = Classification.CreateCategory("合成分類", "合成の説明", null);
            Directory.CreateDirectory(Files.InboxPath);
            Directory.CreateDirectory(Files.DelegationsPath);
        }

        internal CodexFaqProposal Proposal() => new()
        {
            FormatVersion = 2, RequestId = Guid.NewGuid().ToString(), SeriesId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Faq = new() { Title = "合成のFAQはどのように確認しますか？", Summary = "合成データだけで確認します。", BodyDoc = Body("合成の回答です。"), Importance = 2 },
            ExistingCategoryCandidates = [new(Category.Id, Category.Name, "内容が一致します。")]
        };

        internal CodexFaqProposal Revision(ArticleDetail source, string delegationId) => Proposal() with
        {
            SeriesId = delegationId, ProposalKind = "revise", SourceArticles = [new(source.Id, source.UpdatedAt)], ExistingCategoryCandidates = []
        };

        internal CodexFaqProposal DelegateRevision(ArticleDetail source) =>
            Revision(source, Codex.CreateDelegation(new("revise", [source.Id])).DelegationId);

        internal CodexFaqProposal Merge(ArticleDetail first, ArticleDetail second) => Proposal() with
        {
            SeriesId = Codex.CreateDelegation(new("merge", [first.Id, second.Id])).DelegationId,
            ProposalKind = "merge", SourceArticles = [new(first.Id, first.UpdatedAt), new(second.Id, second.UpdatedAt)]
        };

        internal ArticleDetail AcceptMerge(ArticleDetail first, ArticleDetail second)
        {
            var proposal = Merge(first, second);
            Submit(proposal);
            Codex.ListProposals();
            return Accept(proposal).Article;
        }

        internal AcceptCodexProposalResult Accept(CodexFaqProposal proposal) => Codex.AcceptProposal(new(proposal.RequestId, Category.Id, false));
        internal string ProposalPath(string requestId) => Path.Combine(Files.InboxPath, requestId + ".knowledge-proposal.json");
        internal void Submit(CodexFaqProposal proposal) => File.WriteAllText(ProposalPath(proposal.RequestId), JsonSerializer.Serialize(proposal, CodexJson.Options), Utf8);

        internal ArticleDetail Article(string title, bool hidden = false, string? badge = null, JsonElement? body = null) => Editing.SaveArticle(new(
            null, Category.Id, title, "合成の概要", body ?? Body("合成の回答"), "published", 2, badge, badge, hidden,
            [], [], [], [], [], [], [], [], []));

        internal SaveArticleInput Input(ArticleDetail article) => new(
            article.Id, article.CategoryId, article.Title, article.Summary, article.BodyDoc, article.Status, article.Importance,
            article.NewBadgeUntil, article.UpdatedBadgeUntil, article.IsHidden,
            article.Symptoms, article.Causes, article.Targets, article.ErrorCodes, article.Procedures, article.Cautions, article.Tags, article.SearchTerms,
            article.RelatedArticles.Select(x => x.Id).ToArray());

        internal ArticleDetail ImageArticle()
        {
            // A fixed, non-user 1x1 PNG fixture; no repository or clipboard image is read.
            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a/8sAAAAASUVORK5CYII=";
            var image = Editing.StageArticleImageBase64(new("synthetic.png", png));
            return Article("合成画像FAQ", body: ImageDocument(image.Id));
        }

        internal SearchArticlePage Search(string query) => Classification.SearchArticles(new(query, null, "all", false, 1, "updatedDesc"));

        internal long Count(string table)
        {
            string[] allowed = ["articles", "categories", "codex_proposal_history", "codex_proposal_receipts", "article_merge_relations", "article_search_fts"];
            Check(allowed.Contains(table, StringComparer.Ordinal), "Unknown test table");
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM " + table;
            return (long)command.ExecuteScalar()!;
        }

        internal string? Scalar(string sql, string id)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as string;
        }

        internal void Sql(string sql, string id, string value)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        private SqliteConnection OpenConnection()
        {
            var path = Path.GetFullPath(Database.OpenInfo.DatabasePath);
            Check(path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Test DB escaped synthetic root");
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();
            return connection;
        }

        public void Dispose()
        {
            Database.Dispose();
            var root = Path.GetFullPath(Root);
            var relative = Path.GetRelativePath(_temporaryRoot, root);
            Check(relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative) &&
                !relative.Contains(Path.DirectorySeparatorChar) && !relative.Contains(Path.AltDirectorySeparatorChar) &&
                Path.GetFileName(root).StartsWith("knowledgeapp-data-check-", StringComparison.Ordinal), "Invalid synthetic cleanup root");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
