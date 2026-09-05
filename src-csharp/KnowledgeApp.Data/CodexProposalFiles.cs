using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeApp.Data;

/// <summary>Fixed, local proposal exchange paths; never reads a database or attachment body.</summary>
public sealed class CodexProposalFiles
{
    public const string ProposalSuffix = ".knowledge-proposal.json";
    public const string DelegationSuffix = ".knowledge-delegation.json";
    public const int MaximumProposalBytes = 1024 * 1024;
    public const int MaximumDelegationBytes = 5 * 1024 * 1024;
    private readonly string _root;

    public CodexProposalFiles(KnowledgeDatabase database)
    {
        _root = Directory.GetParent(Path.GetDirectoryName(database.OpenInfo.DatabasePath)!)!.FullName;
        InboxPath = Path.Combine(_root, "codex-inbox");
        CategoryCatalogPath = Path.Combine(_root, "codex-bridge", "categories.json");
        DelegationsPath = Path.Combine(_root, "codex-bridge", "delegations");
        try
        {
            EnsureDirectory(InboxPath);
            EnsureDirectory(DelegationsPath);
        }
        catch
        {
            throw InboxReadError();
        }
    }

    public string InboxPath { get; }
    public string CategoryCatalogPath { get; }
    public string DelegationsPath { get; }

    public void WriteCategoryCatalog(IReadOnlyList<CategorySummary> categories)
    {
        try
        {
            var byId = categories.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var catalog = new
            {
                FormatVersion = 1,
                GeneratedAt = UtcNow(),
                Categories = categories.Select(item => new
                {
                    item.Id, item.ParentId, item.Name, item.Description, item.Depth,
                    Path = CategoryPath(item, byId)
                }).ToArray()
            };
            AtomicWrite(CategoryCatalogPath, JsonSerializer.SerializeToUtf8Bytes(catalog, CodexJson.Options), true);
        }
        catch
        {
            throw Problem("CDX-010", "Codex用の分類一覧を更新できませんでした。", "データ保存先の空き容量とアクセス権を確認してください。");
        }
    }

    public CodexDelegationResult WriteDelegation(
        string kind,
        IReadOnlyList<ArticleDetail> articles,
        IReadOnlyList<CategorySummary> categories)
    {
        if (!CodexProposalKinds.IsDelegation(kind) || articles is null ||
            kind == CodexProposalKinds.Revise && articles.Count != 1 ||
            kind == CodexProposalKinds.Merge && articles.Count is < 2 or > 10 ||
            articles.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != articles.Count ||
            articles.Any(item => item.DeletedAt is not null || item.MergeInfo is not null))
        {
            throw InvalidProposal("修正は1件、統合は重複のない未削除・未統合FAQを2～10件選択してください。");
        }
        try
        {
            var byId = categories.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var delegationId = Guid.CreateVersion7().ToString();
            var delegated = articles.Select(article =>
            {
                ValidateRequestId(article.Id);
                ValidateTimestamp(article.UpdatedAt, "元FAQの更新日時が正しくありません。");
                SafeRichContentValidator.Validate(article.BodyDoc);
                try { StructuredJsonBoundary.Validate(article.BodyDoc, CodexJson.DelegationBodyDepth); }
                catch (JsonException)
                {
                    throw Problem("CDX-020", "選択したFAQの構造が深すぎるため、Codexへ委譲できません。",
                        "箇条書きなどの入れ子を減らしてから、もう一度委譲してください。");
                }
                if (!byId.TryGetValue(article.CategoryId, out var category))
                {
                    throw Problem("CDX-020", "Codexへ委譲するFAQの分類を確認できませんでした。", "FAQと分類を更新してから、もう一度委譲してください。");
                }
                return new CodexDelegationArticle(
                    article.Id, article.UpdatedAt, article.CategoryId, CategoryPath(category, byId),
                    article.Title, article.Summary, article.BodyDoc, article.Status, article.Importance,
                    article.Attachments.Select(attachment => new CodexDelegationAttachment(
                        attachment.Id, attachment.OriginalName, attachment.AltText, attachment.MediaType)).ToArray());
            }).ToArray();
            var delegation = new CodexDelegation(1, delegationId, UtcNow(), kind, delegated);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(delegation, CodexJson.Options);
            if (bytes.Length > MaximumDelegationBytes)
            {
                throw Problem("CDX-023", "選択したFAQの委譲内容が5MBの上限を超えています。", "統合対象を減らすか、長いFAQを分けて委譲してください。");
            }
            var target = Path.Combine(DelegationsPath, delegationId + DelegationSuffix);
            AtomicWrite(target, bytes, false);
            var action = kind == CodexProposalKinds.Revise ? "推敲・修正" : "統合";
            return new CodexDelegationResult(delegationId,
                $"KnowledgeAppの委譲番号 {delegationId} のFAQを{action}し、確認待ち提案へ送ってください。", target);
        }
        catch (AppProblemException)
        {
            throw;
        }
        catch
        {
            throw DelegationWriteError();
        }
    }

    public (IReadOnlyList<CodexFaqProposal> Proposals, IReadOnlyList<RejectedCodexProposal> Rejected) ListProposals()
    {
        var proposals = new List<CodexFaqProposal>();
        var rejected = new List<RejectedCodexProposal>();
        string[] entries;
        try
        {
            EnsureSafePath(InboxPath);
            entries = Directory.GetFileSystemEntries(InboxPath);
        }
        catch
        {
            throw InboxReadError();
        }
        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(ProposalSuffix, StringComparison.Ordinal)) continue;
            try
            {
                proposals.Add(ReadAndValidate(path));
            }
            catch (AppProblemException exception)
            {
                rejected.Add(new RejectedCodexProposal(name, exception.Problem.Message));
            }
        }
        return (proposals.OrderByDescending(item => item.CreatedAt, StringComparer.Ordinal).ToArray(),
            rejected.OrderBy(item => item.FileName, StringComparer.Ordinal).ToArray());
    }

    public CodexFaqProposal ReadProposal(string requestId)
    {
        ValidateRequestId(requestId);
        return ReadAndValidate(Path.Combine(InboxPath, requestId + ProposalSuffix));
    }

    public void DiscardProposal(string requestId)
    {
        ValidateRequestId(requestId);
        var path = Path.Combine(InboxPath, requestId + ProposalSuffix);
        try
        {
            EnsureSafePath(path);
            if (!File.Exists(path)) throw ProposalNotFound();
            RequireRegularFile(path);
            File.Delete(path);
        }
        catch (AppProblemException)
        {
            throw;
        }
        catch
        {
            throw Problem("CDX-008", "Codexの提案を破棄できませんでした。", "ファイルが他のアプリで使用中でないか確認し、もう一度お試しください。");
        }
    }

    public void DiscardProposalBestEffort(string requestId)
    {
        try { DiscardProposal(requestId); }
        catch { /* The transactional receipt remains the duplicate-import boundary. */ }
    }

    public void ValidateDelegatedSources(CodexFaqProposal proposal)
    {
        ValidateProposal(proposal);
        if (proposal.ProposalKind == CodexProposalKinds.Create) return;
        var delegationId = proposal.SeriesId!;
        var path = Path.Combine(DelegationsPath, delegationId + DelegationSuffix);
        if (!File.Exists(path))
        {
            throw Problem("CDX-022", "Codex提案に対応する委譲情報が見つかりません。", "KnowledgeAppから新しい委譲番号を作成し、Codexへ再依頼してください。");
        }
        CodexDelegation delegation;
        try
        {
            var bytes = ReadBounded(path, MaximumDelegationBytes);
            RejectDuplicateProperties(bytes);
            delegation = JsonSerializer.Deserialize<CodexDelegation>(bytes, CodexJson.Options)
                ?? throw new JsonException();
        }
        catch
        {
            throw InvalidProposal("Codex委譲ファイルを安全に読み取れません。形式と5MBの上限を確認してください。");
        }
        if (delegation.FormatVersion != 1 || delegation.DelegationId != delegationId ||
            delegation.Kind != proposal.ProposalKind || delegation.Articles is null ||
            delegation.Articles.Count != proposal.SourceArticles.Count ||
            delegation.Articles.Any(item => item is null))
        {
            throw InvalidProposal("Codex委譲番号・種類・元FAQが提案と一致しません。");
        }
        ValidateTimestamp(delegation.CreatedAt, "Codex委譲ファイルの作成日時が正しくありません。");
        var delegated = delegation.Articles.Select(item => (item.ArticleId, item.SourceUpdatedAt)).ToHashSet();
        var proposed = proposal.SourceArticles.Select(item => (item.ArticleId, item.SourceUpdatedAt)).ToHashSet();
        if (delegated.Count != delegation.Articles.Count || !delegated.SetEquals(proposed))
        {
            throw InvalidProposal("Codex提案の元FAQが、KnowledgeAppで委譲した対象と一致しません。");
        }
        foreach (var article in delegation.Articles)
        {
            ValidateRequestId(article.CategoryId);
            if (article.CategoryPath is null || article.Title is null || article.Summary is null ||
                !ArticleStatuses.IsValid(article.Status) ||
                article.Importance is < 1 or > 3 || article.Attachments is null ||
                article.Attachments.Any(item => item is null || item.OriginalName is null ||
                    item.AltText is null || item.MediaType is null))
            {
                throw InvalidProposal("Codex委譲ファイルの項目形式が正しくありません。");
            }
            foreach (var attachment in article.Attachments) ValidateRequestId(attachment.AttachmentId);
            try { SafeRichContentValidator.Validate(article.BodyDoc); }
            catch { throw InvalidProposal("Codex委譲ファイルの回答形式が正しくありません。"); }
        }
        if (proposal.ProposalKind == CodexProposalKinds.Revise)
        {
            ValidatePreservedImages(delegation.Articles[0].BodyDoc, proposal.Faq.BodyDoc);
        }
    }

    public static void ValidateProposal(CodexFaqProposal proposal)
    {
        if (proposal is null || proposal.Faq is null || proposal.SourceArticles is null ||
            proposal.ExistingCategoryCandidates is null || !CodexProposalKinds.IsValid(proposal.ProposalKind))
        {
            throw InvalidProposal("Codex提案のJSON形式が正しくありません。");
        }
        if (proposal.FormatVersion is not (1 or 2))
            throw InvalidProposal("このCodex提案は現在のアプリで扱えない形式です。");
        ValidateRequestId(proposal.RequestId);
        if (proposal.FormatVersion == 1)
        {
            if (proposal.SeriesId is not null || proposal.ProposalKind != CodexProposalKinds.Create || proposal.SourceArticles.Count != 0)
                throw InvalidProposal("形式第1版のCodex提案には既存FAQの委譲情報を含められません。");
        }
        else
        {
            ValidateRequestId(proposal.SeriesId);
        }
        ValidateTimestamp(proposal.CreatedAt, "Codex提案の作成日時が正しくありません。");
        if (proposal.ProposalKind == CodexProposalKinds.Create && proposal.SourceArticles.Count != 0 ||
            proposal.ProposalKind == CodexProposalKinds.Revise && proposal.SourceArticles.Count != 1 ||
            proposal.ProposalKind == CodexProposalKinds.Merge && proposal.SourceArticles.Count is < 2 or > 10)
            throw InvalidProposal("新規提案は元FAQなし、修正は1件、統合は2～10件を指定してください。");
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in proposal.SourceArticles)
        {
            if (source is null) throw InvalidProposal("元FAQの形式が正しくありません。");
            ValidateRequestId(source.ArticleId);
            ValidateTimestamp(source.SourceUpdatedAt, "元FAQの更新日時が正しくありません。");
            if (!sourceIds.Add(source.ArticleId)) throw InvalidProposal("同じ元FAQが重複しています。");
        }
        ValidateLimitedText(proposal.Faq.Title?.Trim(), 200, "Codex提案のタイトル");
        if (proposal.Faq.Summary is null || RuneCount(proposal.Faq.Summary) > 500)
            throw InvalidProposal("Codex提案の概要は500文字以内である必要があります。");
        if (proposal.Faq.Importance is < 1 or > 3)
            throw InvalidProposal("Codex提案の重要度は1～3である必要があります。");
        try
        {
            // Validate already-parsed JsonElements as well as file input, before
            // the recursive image walk. The two outer proposal objects count.
            StructuredJsonBoundary.Validate(proposal.Faq.BodyDoc, CodexJson.ProposalBodyDepth);
            if (proposal.ProposalKind != CodexProposalKinds.Revise && ContainsImage(proposal.Faq.BodyDoc))
                throw InvalidProposal("新規・統合提案から画像は取り込めません。下書き取込後に追加してください。");
            if (string.IsNullOrWhiteSpace(SafeRichContentValidator.Validate(proposal.Faq.BodyDoc).PlainText))
                throw InvalidProposal("Codex提案の回答が空です。");
        }
        catch (JsonException)
        {
            throw InvalidProposal("Codex提案のJSON構造が深すぎるか、重複した項目を含んでいます。");
        }
        catch (AppProblemException exception) when (exception.Problem.Code != "CDX-002")
        {
            throw InvalidProposal("Codex提案の回答に扱えない形式、画像またはリンクが含まれています。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw InvalidProposal("Codex提案の回答形式が正しくありません。");
        }
        if (proposal.ExistingCategoryCandidates.Count > 3)
            throw InvalidProposal("既存分類の候補は3件以内である必要があります。");
        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in proposal.ExistingCategoryCandidates)
        {
            if (candidate is null) throw InvalidProposal("既存分類候補の形式が正しくありません。");
            ValidateRequestId(candidate.CategoryId);
            if (!categoryIds.Add(candidate.CategoryId)) throw InvalidProposal("同じ既存分類候補が重複しています。");
            ValidateLimitedText(candidate.CategoryPath, 1000, "分類候補の階層");
            ValidateLimitedText(candidate.Reason, 500, "分類候補の理由");
        }
        if (proposal.NewCategoryProposal is { } category)
        {
            if (category.ParentCategoryId is not null) ValidateRequestId(category.ParentCategoryId);
            if (category.ParentCategoryPath is not null && RuneCount(category.ParentCategoryPath) > 1000)
                throw InvalidProposal("新規分類案の親分類階層が長すぎます。");
            ValidateLimitedText(category.Name, 100, "新規分類案の名前");
            if (category.Description is null || RuneCount(category.Description) > 500)
                throw InvalidProposal("新規分類案の説明は500文字以内である必要があります。");
            ValidateLimitedText(category.Reason, 500, "新規分類案の理由");
        }
        if (proposal.ProposalKind == CodexProposalKinds.Revise)
        {
            if (proposal.ExistingCategoryCandidates.Count != 0 || proposal.NewCategoryProposal is not null)
                throw InvalidProposal("既存FAQの修正提案では所属分類を変更できません。");
        }
        else if (proposal.ExistingCategoryCandidates.Count == 0 && proposal.NewCategoryProposal is null)
            throw InvalidProposal("既存分類候補または新規分類案が必要です。");
    }

    public static void ValidateRequestId(string? requestId)
    {
        if (requestId is null || !Guid.TryParseExact(requestId, "D", out _))
            throw InvalidProposal("Codex提案の受付番号が正しくありません。");
    }

    public static void ValidatePreservedImages(JsonElement source, JsonElement proposal)
    {
        var original = CollectImages(source);
        var revised = CollectImages(proposal);
        if (original.Count != revised.Count || original.Where((item, index) =>
            item.Path != revised[index].Path || !JsonElement.DeepEquals(item.Node, revised[index].Node)).Any())
        {
            throw Problem("CDX-021", "Codex修正案で既存画像の参照または位置が変更されています。",
                "元FAQの画像ノードの参照・位置・順序・代替テキストを変更せずに修正案を作り直してください。");
        }
    }

    private CodexFaqProposal ReadAndValidate(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw ProposalNotFound();
        byte[] bytes;
        try { bytes = ReadBounded(path, MaximumProposalBytes); }
        catch (AppProblemException) { throw; }
        catch { throw InboxReadError(); }
        CodexFaqProposal proposal;
        try
        {
            RejectDuplicateProperties(bytes);
            proposal = JsonSerializer.Deserialize<CodexFaqProposal>(bytes, CodexJson.Options) ?? throw new JsonException();
        }
        catch
        {
            throw InvalidProposal("Codex提案のJSON形式が正しくありません。");
        }
        var filename = Path.GetFileName(path);
        if (!filename.EndsWith(ProposalSuffix, StringComparison.Ordinal) ||
            filename[..^ProposalSuffix.Length] != proposal.RequestId)
            throw InvalidProposal("Codex提案の受付番号とファイル名が一致しません。");
        ValidateProposal(proposal);
        return proposal;
    }

    private byte[] ReadBounded(string path, int maximum)
    {
        EnsureSafePath(path);
        RequireRegularFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        EnsureSafePath(path);
        if (stream.Length > maximum)
            throw InvalidProposal($"Codexファイルが{maximum / 1024 / 1024}MBの上限を超えています。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw InvalidProposal("Codexファイルが読み取り中に変更されました。");
        return bytes;
    }

    private void AtomicWrite(string target, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(target)!;
        EnsureDirectory(directory);
        EnsureSafePath(target);
        var partial = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.partial");
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            EnsureSafePath(target);
            EnsureSafePath(partial);
            File.Move(partial, target, overwrite);
        }
        finally
        {
            try
            {
                EnsureSafePath(partial);
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch { /* Never obscure the original write result. */ }
        }
    }

    private void EnsureDirectory(string path)
    {
        EnsureSafePath(path);
        Directory.CreateDirectory(path);
        EnsureSafePath(path);
    }

    private void EnsureSafePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(fullPath, _root, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw InvalidProposal("Codex連携の固定保存先以外は使用できません。");
        var current = fullPath;
        while (true)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw InvalidProposal("リンクまたは再解析ポイントのCodexファイルは使用できません。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (string.Equals(current, _root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ?? throw InvalidProposal("Codex連携の保存先が正しくありません。");
        }
    }

    private static void RequireRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw InvalidProposal("通常ファイルではないCodex提案は読み込めません。");
    }

    private static string CategoryPath(CategorySummary category, IReadOnlyDictionary<string, CategorySummary> byId)
    {
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        CategorySummary? current = category;
        while (current is not null)
        {
            if (!visited.Add(current.Id) || visited.Count > 5)
                throw InvalidProposal("分類の階層情報が正しくありません。");
            names.Add(current.Name);
            if (current.ParentId is null) break;
            if (!byId.TryGetValue(current.ParentId, out current))
                throw InvalidProposal("分類の親情報が見つかりません。");
        }
        names.Reverse();
        return string.Join(" > ", names);
    }

    private static bool ContainsImage(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String && type.GetString() == "image" ||
            value.EnumerateObject().Any(item => ContainsImage(item.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsImage),
        _ => false
    };

    private static List<(string Path, JsonElement Node)> CollectImages(JsonElement document)
    {
        var result = new List<(string Path, JsonElement Node)>();
        Visit(document, "$", result);
        return result;

        static void Visit(JsonElement node, string path, List<(string Path, JsonElement Node)> images)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "image")
                images.Add((path, node));
            if (node.TryGetProperty("content", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var child in children.EnumerateArray()) Visit(child, $"{path}.content[{index++}]", images);
            }
        }
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, CodexJson.DocumentOptions);
        Visit(document.RootElement);
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException();
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Visit(child);
        }
    }

    private static void ValidateTimestamp(string? value, string message)
    {
        if (value is null || !Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw InvalidProposal(message);
    }

    private static void ValidateLimitedText(string? value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || RuneCount(value) > maximum)
            throw InvalidProposal($"{label}は1～{maximum}文字である必要があります。");
    }

    private static int RuneCount(string value) => value.EnumerateRunes().Count();
    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static AppProblemException Problem(string code, string message, string action) => new(new AppProblem(code, message, action));
    private static AppProblemException InvalidProposal(string message) => Problem("CDX-002", message,
        "Codexへもう一度下書き作成を依頼してください。データベースは変更されていません。");
    private static AppProblemException ProposalNotFound() => Problem("CDX-003", "指定したCodex提案が見つかりません。", "提案一覧を更新し、もう一度選択してください。");
    private static AppProblemException InboxReadError() => Problem("CDX-001", "Codexからの提案を確認できませんでした。", "提案フォルダを確認してから、もう一度お試しください。");
    private static AppProblemException DelegationWriteError() => Problem("CDX-020", "Codexへの委譲ファイルを作成できませんでした。", "利用者データフォルダの空き容量とアクセス権を確認してください。");

    private sealed record CodexDelegation(
        [property: System.Text.Json.Serialization.JsonRequired] uint FormatVersion,
        [property: System.Text.Json.Serialization.JsonRequired] string DelegationId,
        [property: System.Text.Json.Serialization.JsonRequired] string CreatedAt,
        [property: System.Text.Json.Serialization.JsonRequired] string Kind,
        [property: System.Text.Json.Serialization.JsonRequired] IReadOnlyList<CodexDelegationArticle> Articles);

    private sealed record CodexDelegationArticle(
        [property: System.Text.Json.Serialization.JsonRequired] string ArticleId,
        [property: System.Text.Json.Serialization.JsonRequired] string SourceUpdatedAt,
        [property: System.Text.Json.Serialization.JsonRequired] string CategoryId,
        [property: System.Text.Json.Serialization.JsonRequired] string CategoryPath,
        [property: System.Text.Json.Serialization.JsonRequired] string Title,
        [property: System.Text.Json.Serialization.JsonRequired] string Summary,
        [property: System.Text.Json.Serialization.JsonRequired] JsonElement BodyDoc,
        [property: System.Text.Json.Serialization.JsonRequired] string Status,
        [property: System.Text.Json.Serialization.JsonRequired] long Importance,
        [property: System.Text.Json.Serialization.JsonRequired] IReadOnlyList<CodexDelegationAttachment> Attachments);

    private sealed record CodexDelegationAttachment(
        [property: System.Text.Json.Serialization.JsonRequired] string AttachmentId,
        [property: System.Text.Json.Serialization.JsonRequired] string OriginalName,
        [property: System.Text.Json.Serialization.JsonRequired] string AltText,
        [property: System.Text.Json.Serialization.JsonRequired] string MediaType);
}
