using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeApp.Data;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static readonly JsonDocumentOptions DeepReadOptions = new() { MaxDepth = 256 };
    private static readonly JsonSerializerOptions DeepWriteOptions = new(CodexJson.Options) { MaxDepth = 256 };

    private static void DeepJsonCases()
    {
        Case("deep create stays pending until approval and survives history reload", f =>
        {
            var proposal = DeepProposal(f, 101);
            f.Submit(proposal);
            Check(f.Codex.ListProposals().Proposals.Single().RequestId == proposal.RequestId && f.Count("articles") == 0,
                "Deep proposal bypassed approval");
            var result = f.Accept(proposal).Article;
            Check(result.Status == "draft" && JsonElement.DeepEquals(result.BodyDoc, proposal.Faq.BodyDoc), "Deep create changed body/status");
            var authentication = new AuthenticationService(f.Database);
            var service = new CodexProposalService(f.Database, authentication, new ArticleAttachmentService(f.Root));
            Expect("AUTH-002", () => service.ListProposals());
            authentication.Login("0000", "");
            var history = service.ListProposals().History.Single();
            Check(history.AcceptedArticleId == result.Id && JsonElement.DeepEquals(history.Proposal.Faq.BodyDoc, proposal.Faq.BodyDoc),
                "Deep persisted history could not be reloaded");
        });
        Case("deep revision preserves images and protected metadata through approval", f =>
        {
            var source = DeepImageArticle(f, 101);
            var image = source.Attachments.Single();
            // The view contract exposes a managed HTTPS asset URL, not a path.
            // This test generated a PNG and knows its isolated storage location.
            var imagePath = Path.Combine(f.Root, "attachments", "articles", source.Id, image.Id + ".png");
            var imageBytes = File.ReadAllBytes(imagePath);
            var before = DeepSnapshot(f);
            var delegation = f.Codex.CreateDelegation(new("revise", [source.Id]));
            Check(DeepSnapshot(f) == before, "Delegation modified the source database");
            VerifyDeepDelegation(delegation, source, 104);
            var proposal = f.Revision(source, delegation.DelegationId) with
            {
                Faq = f.Proposal().Faq with { BodyDoc = DeepBody(101, "深い本文の修正後です。", image.Id) }
            };
            f.Submit(proposal);
            Check(f.Codex.ListProposals().Proposals.Count == 1 && JsonElement.DeepEquals(f.View.GetArticle(source.Id).BodyDoc, source.BodyDoc),
                "Deep revision changed the source before approval");
            var updated = f.Accept(proposal).Article;
            Check(JsonElement.DeepEquals(updated.BodyDoc, proposal.Faq.BodyDoc) && updated.Attachments.Single() == image,
                "Deep image revision was not preserved");
            Check(updated.Status == source.Status && updated.CategoryId == source.CategoryId && updated.CreatedAt == source.CreatedAt &&
                updated.CreatedByUserId == source.CreatedByUserId && updated.IsHidden == source.IsHidden,
                "Deep revision changed protected metadata");
            Check(File.ReadAllBytes(imagePath).SequenceEqual(imageBytes), "Image file changed");
            Check(f.Codex.ListProposals().History.Single().Status == "accepted", "Deep revision history missing");
        });
        Case("deep merge remains a draft and does not modify either selected source", f =>
        {
            var first = f.Article("深い合成統合元1", body: DeepBody(101, "統合元の合成回答1"));
            var second = DeepImageArticle(f, 105);
            var proposal = f.Merge(first, second);
            proposal = proposal with { Faq = proposal.Faq with { BodyDoc = DeepBody(109, "統合後の合成回答") } };
            f.Submit(proposal);
            Check(f.Codex.ListProposals().Proposals.Count == 1 && f.Count("articles") == 2, "Deep merge bypassed approval");
            var result = f.Accept(proposal).Article;
            Check(result.Status == "draft" && result.Attachments.Count == 0 && JsonElement.DeepEquals(result.BodyDoc, proposal.Faq.BodyDoc),
                "Deep merge output invalid");
            Check(JsonElement.DeepEquals(f.View.GetArticle(first.Id).BodyDoc, first.BodyDoc) &&
                JsonElement.DeepEquals(f.View.GetArticle(second.Id).BodyDoc, second.BodyDoc) && f.Count("article_merge_relations") == 0,
                "Deep merge altered a source");
            Check(f.Codex.GetMergePublicationContext(result.Id)?.SourceArticles.Count == 2, "Deep history publication context failed");
        });
        Case("deep Rust-style stored history and equivalent escaped property order are unchanged", f =>
        {
            var proposal = DeepProposal(f, 125);
            f.Submit(proposal);
            f.Codex.ListProposals();
            var equivalent = JsonNode.Parse(JsonSerializer.Serialize(proposal, CodexJson.Options), documentOptions: DeepReadOptions)!.AsObject();
            var reordered = new JsonObject();
            foreach (var property in equivalent.Reverse()) reordered.Add(property.Key, property.Value?.DeepClone());
            var rustStyle = reordered.ToJsonString(new JsonSerializerOptions(DeepWriteOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Check(rustStyle.Contains("合成", StringComparison.Ordinal), "Fixture did not use literal Unicode");
            f.Sql("UPDATE codex_proposal_history SET payload_json = $value WHERE request_id = $id", proposal.RequestId, rustStyle);
            var before = DeepSnapshot(f);
            var inbox = f.Codex.ListProposals();
            Check(inbox.Rejected.Count == 0 && inbox.Proposals.Single().RequestId == proposal.RequestId, "Deep semantic equality failed");
            Check(DeepSnapshot(f) == before && f.Count("codex_proposal_history") == 1, "Equivalent history was rewritten");
            Check(JsonElement.DeepEquals(f.Accept(proposal).Article.BodyDoc, proposal.Faq.BodyDoc), "Deep stored proposal approval failed");
        });
        Case("deep rejected proposal reopens only the latest request in its series", f =>
        {
            var older = DeepProposal(f, 101);
            f.Submit(older);
            f.Codex.ListProposals();
            f.Codex.RejectProposal(older.RequestId);
            var newer = DeepProposal(f, 105) with { SeriesId = older.SeriesId };
            f.Submit(newer);
            f.Codex.ListProposals();
            f.Codex.RejectProposal(newer.RequestId);
            var before = DeepSnapshot(f);
            Expect("CDX-015", () => f.Codex.ReopenRejectedProposal(older.RequestId));
            Check(DeepSnapshot(f) == before, "Old deep rejected request changed history");
            f.Codex.ReopenRejectedProposal(newer.RequestId);
            Check(f.Codex.ListProposals().Proposals.Single().RequestId == newer.RequestId, "Latest deep rejected proposal failed to reopen");
            Check(JsonElement.DeepEquals(f.Accept(newer).Article.BodyDoc, newer.Faq.BodyDoc) && f.Count("codex_proposal_history") == 2,
                "Deep reopen duplicated history or changed its body");
        });
        Case("deep same-ID body replacement is refused without replacing pending payload", f =>
        {
            var original = DeepProposal(f, 101);
            f.Submit(original);
            f.Codex.ListProposals();
            var before = DeepSnapshot(f);
            f.Submit(original with { Faq = original.Faq with { BodyDoc = DeepBody(101, "差し替えた合成本文") } });
            var inbox = f.Codex.ListProposals();
            Check(inbox.Rejected.Count == 1 && DeepSnapshot(f) == before, "Tampered deep payload replaced stored history");
            Check(JsonElement.DeepEquals(f.Accept(original).Article.BodyDoc, original.Faq.BodyDoc), "Approval used a replaced inbox payload");
        });
        Case("deep revision conflict leaves edited source and pending history unchanged", f =>
        {
            var source = f.Article("深い競合元", body: DeepBody(101, "合成の元本文"));
            var proposal = f.DelegateRevision(source) with { Faq = f.Proposal().Faq with { BodyDoc = DeepBody(105, "提案本文") } };
            f.Submit(proposal);
            f.Codex.ListProposals();
            f.Editing.SaveArticle(f.Input(source) with { BodyDoc = DeepBody(109, "利用者が先に変更した本文") });
            var before = DeepSnapshot(f);
            Expect("CDX-012", () => f.Accept(proposal));
            Check(DeepSnapshot(f) == before && f.Count("codex_proposal_receipts") == 0, "Deep conflict partially committed");
        });
        Case("deep merge conflict is atomic", f =>
        {
            var first = f.Article("深い統合競合1", body: DeepBody(101, "合成本文1"));
            var second = f.Article("深い統合競合2", body: DeepBody(105, "合成本文2"));
            var proposal = f.Merge(first, second);
            proposal = proposal with { Faq = proposal.Faq with { BodyDoc = DeepBody(109, "統合案") } };
            f.Submit(proposal);
            f.Codex.ListProposals();
            f.Editing.SaveArticle(f.Input(second) with { Title = "利用者による合成変更" });
            var before = DeepSnapshot(f);
            Expect("CDX-012", () => f.Accept(proposal));
            Check(DeepSnapshot(f) == before && f.Count("articles") == 2, "Deep merge conflict changed the database");
        });
        foreach (var change in new[] { "alt", "position", "removal" })
        {
            Case("deep revision image " + change + " change is rejected atomically", f =>
            {
                var source = DeepImageArticle(f, 101);
                var proposal = f.DelegateRevision(source);
                var body = JsonNode.Parse(source.BodyDoc.GetRawText(), documentOptions: DeepReadOptions)!.AsObject();
                var content = body["content"]!.AsArray();
                if (change == "alt") content[1]!["attrs"]!["alt"] = "変更した代替テキスト";
                else
                {
                    var image = content[1]!.DeepClone();
                    content.RemoveAt(1);
                    if (change == "position") content.Insert(0, image);
                }
                proposal = proposal with { Faq = proposal.Faq with { BodyDoc = JsonSerializer.SerializeToElement(body, DeepWriteOptions) } };
                f.Submit(proposal);
                var before = DeepSnapshot(f);
                Check(f.Codex.ListProposals().Rejected.Count == 1, "Deep changed image was not rejected at reception");
                Expect("CDX-021", () => f.Accept(proposal));
                Check(DeepSnapshot(f) == before && JsonElement.DeepEquals(f.View.GetArticle(source.Id).BodyDoc, source.BodyDoc),
                    "Changed deep image altered source/history");
            });
        }
        Case("proposal boundary accepts all 127 containers through file history and approval", f =>
        {
            var proposal = DeepProposal(f, 125);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(proposal, CodexJson.Options);
            using var document = JsonDocument.Parse(bytes, DeepReadOptions);
            Check(DeepContainerCount(document.RootElement) == 127, "Proposal boundary fixture depth incorrect");
            f.Submit(proposal);
            Check(f.Codex.ListProposals().Proposals.Single().RequestId == proposal.RequestId, "127-container proposal rejected");
            Check(JsonElement.DeepEquals(f.Accept(proposal).Article.BodyDoc, proposal.Faq.BodyDoc), "Boundary body changed");
        });
        foreach (var depth in new[] { 126, 127, 128, 129 })
        {
            Case($"proposal {depth + 2}-container input is refused with database unchanged", f =>
            {
                var proposal = DeepProposal(f, depth);
                var before = DeepSnapshot(f);
                File.WriteAllText(f.ProposalPath(proposal.RequestId), JsonSerializer.Serialize(proposal, DeepWriteOptions), Utf8);
                Expect("CDX-002", () => f.Files.ReadProposal(proposal.RequestId));
                Expect("CDX-002", () => CodexProposalFiles.ValidateProposal(proposal));
                Check(f.Codex.ListProposals().Rejected.Count == 1 && DeepSnapshot(f) == before, "Overdeep proposal changed the database");
            });
        }
        Case("delegation 127-container boundary roundtrips and rejected output preserves existing files", f =>
        {
            var source = f.Article("委譲上限の合成FAQ", body: DeepBody(124, "安全な合成本文"));
            var delegation = f.Codex.CreateDelegation(new("revise", [source.Id]));
            VerifyDeepDelegation(delegation, source, 127);
            var originalBytes = File.ReadAllBytes(delegation.FilePath);
            var proposal = f.Revision(source, delegation.DelegationId) with { Faq = f.Proposal().Faq with { BodyDoc = source.BodyDoc } };
            f.Submit(proposal);
            Check(f.Codex.ListProposals().Proposals.Count == 1, "Boundary delegation could not be reread");
            f.Accept(proposal);
            var tooDeep = f.Article("通常保存可能で委譲だけ上限超過", body: DeepBody(125, "通常保存本文は保持する"));
            var before = DeepSnapshot(f);
            try { f.Codex.CreateDelegation(new("revise", [tooDeep.Id])); throw new InvalidOperationException("Overdeep delegation succeeded"); }
            catch (AppProblemException problem)
            {
                Check(problem.Problem.Code == "CDX-020" && problem.Problem.Message.Contains("構造が深すぎる", StringComparison.Ordinal),
                    "Delegation depth refusal was not understandable");
            }
            Check(DeepSnapshot(f) == before && File.ReadAllBytes(delegation.FilePath).SequenceEqual(originalBytes) &&
                Directory.EnumerateFiles(f.Files.DelegationsPath).Count() == 1, "Overdeep delegation changed source/existing files or left a partial");
        });
        Case("deep proposal keeps its one MiB byte limit", f =>
        {
            var proposal = f.Proposal();
            proposal = proposal with { Faq = proposal.Faq with { BodyDoc = DeepBody(101, new string('a', CodexProposalFiles.MaximumProposalBytes)) } };
            f.Submit(proposal);
            var before = DeepSnapshot(f);
            Expect("CDX-002", () => f.Files.ReadProposal(proposal.RequestId));
            Check(f.Codex.ListProposals().Rejected.Count == 1 && DeepSnapshot(f) == before, "Deep oversize proposal changed database");
        });
        Case("deep delegation keeps five MiB byte limit and existing delegation unchanged", f =>
        {
            var previous = f.Article("既存合成委譲", body: DeepBody(77, "既存本文"));
            var delegation = f.Codex.CreateDelegation(new("revise", [previous.Id]));
            var bytes = File.ReadAllBytes(delegation.FilePath);
            var large = f.Article("大容量合成委譲", body: DeepBody(101, new string('a', CodexProposalFiles.MaximumDelegationBytes)));
            var before = DeepSnapshot(f);
            Expect("CDX-023", () => f.Codex.CreateDelegation(new("revise", [large.Id])));
            Check(DeepSnapshot(f) == before && File.ReadAllBytes(delegation.FilePath).SequenceEqual(bytes) &&
                Directory.EnumerateFiles(f.Files.DelegationsPath).Count() == 1, "Deep oversize delegation changed persistent data");
        });
        foreach (var change in new[] { "duplicate", "unknown", "unicode", "unicode-value", "invalid-utf8", "script", "unsafe-link" })
        {
            Case("deep " + change + " proposal remains invalid and does not hide valid input", f =>
            {
                var invalid = DeepProposal(f, 101);
                var bytes = JsonSerializer.Serialize(invalid, new JsonSerializerOptions(DeepWriteOptions) { WriteIndented = false });
                bytes = change switch
                {
                    "duplicate" => bytes.Replace("\"type\":\"text\"", "\"type\":\"text\",\"type\":\"text\"", StringComparison.Ordinal),
                    "unknown" => bytes.Replace("\"type\":\"text\"", "\"type\":\"text\",\"unknown\":0", StringComparison.Ordinal),
                    "unicode" => bytes.Replace("\"type\":\"text\"", "\"type\":\"text\",\"attrs\":{\"\\uD800\":0}", StringComparison.Ordinal),
                    "unicode-value" => bytes.Replace(JsonSerializer.Serialize("安全な合成の深い本文です。"), "\"\\uD800\"", StringComparison.Ordinal),
                    "invalid-utf8" => bytes.Replace(JsonSerializer.Serialize("安全な合成の深い本文です。"), "\"synthetic_bad_utf8\"", StringComparison.Ordinal),
                    "script" => bytes.Replace("\"type\":\"text\"", "\"type\":\"script\"", StringComparison.Ordinal),
                    _ => bytes.Replace("\"type\":\"text\"", "\"type\":\"text\",\"marks\":[{\"type\":\"link\",\"attrs\":{\"href\":\"javascript:alert(1)\"}}]", StringComparison.Ordinal)
                };
                if (change == "invalid-utf8")
                {
                    var invalidBytes = Utf8.GetBytes(bytes);
                    var marker = Utf8.GetBytes("synthetic_bad_utf8");
                    var offset = invalidBytes.AsSpan().IndexOf(marker);
                    Check(offset >= 0, "Invalid UTF-8 marker missing");
                    invalidBytes[offset] = 0xC0; // Invalid UTF-8 leading byte; no user file is used.
                    File.WriteAllBytes(f.ProposalPath(invalid.RequestId), invalidBytes);
                }
                else File.WriteAllText(f.ProposalPath(invalid.RequestId), bytes, Utf8);
                var before = DeepSnapshot(f);
                Expect("CDX-002", () => f.Files.ReadProposal(invalid.RequestId));
                Check(DeepSnapshot(f) == before, "Unsafe deep proposal altered the database");
                var valid = DeepProposal(f, 105);
                f.Submit(valid);
                var inbox = f.Codex.ListProposals();
                Check(inbox.Rejected.Count == 1 && inbox.Proposals.Single().RequestId == valid.RequestId && f.Count("articles") == 0,
                    "Unsafe deep file hid valid pending input");
            });
        }
        Case("overdeep delegated file is rejected before proposal history changes", f =>
        {
            var source = f.Article("委譲読込境界の合成FAQ", body: DeepBody(124, "元本文"));
            var delegation = f.Codex.CreateDelegation(new("revise", [source.Id]));
            var proposal = f.Revision(source, delegation.DelegationId) with { Faq = f.Proposal().Faq with { BodyDoc = source.BodyDoc } };
            var node = JsonNode.Parse(File.ReadAllBytes(delegation.FilePath), documentOptions: DeepReadOptions)!;
            node["articles"]![0]!["bodyDoc"] = JsonNode.Parse(DeepBody(125, "全体128の本文").GetRawText(), documentOptions: DeepReadOptions);
            File.WriteAllText(delegation.FilePath, node.ToJsonString(DeepWriteOptions), Utf8);
            f.Submit(proposal);
            var before = DeepSnapshot(f);
            Expect("CDX-002", () => f.Files.ValidateDelegatedSources(proposal));
            Check(f.Codex.ListProposals().Rejected.Count == 1 && DeepSnapshot(f) == before, "Overdeep delegated file changed history/source");
        });
    }

    private static CodexFaqProposal DeepProposal(Fixture fixture, int depth)
    {
        var proposal = fixture.Proposal();
        return proposal with { Faq = proposal.Faq with { BodyDoc = DeepBody(depth, "安全な合成の深い本文です。") } };
    }

    private static ArticleDetail DeepImageArticle(Fixture fixture, int depth)
    {
        var source = fixture.ImageArticle();
        return fixture.Editing.SaveArticle(fixture.Input(source) with
        {
            BodyDoc = DeepBody(depth, "画像を含む深い合成本文です。", source.Attachments.Single().Id)
        });
    }

    private static JsonElement DeepBody(int depth, string text, string? imageId = null)
    {
        Check(depth >= 5, "Invalid requested test depth");
        var layers = (depth - 5) / 4;
        var extra = (depth - 5) % 4;
        var attributes = extra switch
        {
            1 => ",\"attrs\":{}",
            2 => ",\"marks\":[{\"type\":\"bold\"}]",
            3 => ",\"marks\":[{\"type\":\"bold\",\"attrs\":{}}]",
            _ => string.Empty
        };
        var node = "{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + attributes + "}]}";
        for (var i = 0; i < layers; i++)
        {
            var leadingParagraph = i == 0 ? string.Empty : "{\"type\":\"paragraph\"},";
            node = "{\"type\":\"bulletList\",\"content\":[{\"type\":\"listItem\",\"content\":[" + leadingParagraph + node + "]}]}";
        }
        var image = imageId is null ? string.Empty : ",{\"type\":\"image\",\"attrs\":{\"src\":\"knowledge-attachment:" + imageId +
            "\",\"attachmentId\":\"" + imageId + "\",\"alt\":\"合成画像\",\"title\":null}}";
        using var document = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[" + node + image + "]}", DeepReadOptions);
        Check(DeepContainerCount(document.RootElement) == depth, "Synthetic body depth does not match requested boundary");
        return document.RootElement.Clone();
    }

    private static int DeepContainerCount(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => 1 + value.EnumerateObject().Select(item => DeepContainerCount(item.Value)).DefaultIfEmpty().Max(),
        JsonValueKind.Array => 1 + value.EnumerateArray().Select(DeepContainerCount).DefaultIfEmpty().Max(),
        _ => 0
    };

    private static void VerifyDeepDelegation(CodexDelegationResult delegation, ArticleDetail source, int expectedDepth)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(delegation.FilePath), DeepReadOptions);
        Check(DeepContainerCount(document.RootElement) == expectedDepth, "Delegation container depth incorrect");
        var item = document.RootElement.GetProperty("articles").EnumerateArray().Single();
        SetEqual(item.EnumerateObject().Select(property => property.Name),
            ["articleId", "sourceUpdatedAt", "categoryId", "categoryPath", "title", "summary", "bodyDoc", "status", "importance", "attachments"]);
        Check(item.GetProperty("articleId").GetString() == source.Id && JsonElement.DeepEquals(item.GetProperty("bodyDoc"), source.BodyDoc),
            "Delegation body or selected identity changed");
        Check(!document.RootElement.GetRawText().Contains("assetPath", StringComparison.Ordinal) &&
            !document.RootElement.GetRawText().Contains("createdByUserId", StringComparison.Ordinal), "Delegation exposed private metadata");
    }

    private static string DeepSnapshot(Fixture fixture)
    {
        // This diagnostic can open only this test's freshly generated temporary
        // database; it is not a host command or an arbitrary production endpoint.
        var path = Path.GetFullPath(fixture.Database.OpenInfo.DatabasePath);
        Check(path.StartsWith(fixture.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Snapshot escaped synthetic root");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name";
        var names = new List<string>();
        using (var reader = tables.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        var result = new List<string>();
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = Enumerable.Range(0, reader.FieldCount).Select(index => reader.GetValue(index) switch
                {
                    DBNull => "<NULL>",
                    byte[] bytes => "blob:" + Convert.ToHexString(bytes),
                    var value => Convert.ToString(value, CultureInfo.InvariantCulture)
                }).ToArray();
                rows.Add(JsonSerializer.Serialize(values));
            }
            result.Add(name + ":" + string.Join("\n", rows.Order(StringComparer.Ordinal)));
        }
        return Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(string.Join("\n", result))));
    }
}
